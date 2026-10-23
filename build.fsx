#!/usr/bin/env -S dotnet fsi --exec --
// Same FAKE libraries as FastCoref.fs; 6.1.3 is provisioned locally, unlike 5.23.1.
#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"

// Source loading keeps local checks and construction examples usable before Build.
#load "src/Rlcd/Domain.fs"
#load "src/Rlcd/Questions.fs"
#load "src/Rlcd/ModelBundle.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO.FileSystemOperators

open Rlcd

fsi.CommandLineArgs
|> Array.skip 1
|> Array.toList
|> Context.FakeExecutionContext.Create false __SOURCE_FILE__
|> Context.RuntimeContext.Fake
|> Context.setExecutionContext

let repository = __SOURCE_DIRECTORY__
let solution = "Rlcd.slnx"

let dotnetPath =
    ProcessUtils.tryFindFileOnPath "dotnet"
    |> Option.defaultWith (fun () -> invalidOp "The provisioned dotnet SDK must be on PATH.")

let dotnetOptions (options: DotNet.Options) =
    { options with
        DotNetCliPath = dotnetPath
        WorkingDirectory = repository
    }

[<RequireQualifiedAccess>]
module Models =
    type ArtifactIdentity =
        | Sha256 of string
        | GitBlob of string

    type ArtifactPin =
        {
            Filename: string
            SizeBytes: int64
            Identity: ArtifactIdentity
        }

    type ArtifactReceipt =
        {
            Filename: string
            SizeBytes: int64
            Sha256: string
        }

    type IArtifactSource =
        abstract Fetch: artifact:ArtifactPin * destination:string -> unit

    let private manifestName = "laya-english.manifest.json"

    let private pins: ArtifactPin list =
        [
            {
                Filename = "laya.onnx"
                SizeBytes = 1688711336L
                Identity = Sha256 "ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e"
            }
            {
                Filename = "tokenizer.json"
                SizeBytes = 3583228L
                Identity = GitBlob "2f4d8583e507b7466d2490e2d6c045647a822698"
            }
            {
                Filename = "tokenizer_config.json"
                SizeBytes = 124L
                Identity = GitBlob "8e705ec8ffe005917120c14d5b530edce3221217"
            }
            {
                Filename = "rl_agent_config.json"
                SizeBytes = 745L
                Identity = GitBlob "3e4fcbf12cf36164ce18a1398aa9f35f58375ae0"
            }
        ]

    let private require condition message =
        if not condition then raise (InvalidDataException message)

    let private rejectLink (entry: FileSystemInfo) =
        require
            (isNull entry.LinkTarget
             && (not entry.Exists || not (entry.Attributes.HasFlag FileAttributes.ReparsePoint)))
            $"Model cache symlink/reparse path is unsupported: {entry.FullName}"

    let rec private guardDirectory (directory: DirectoryInfo) =
        if not (isNull directory) then
            rejectLink directory
            require (not (File.Exists directory.FullName))
                $"Model cache directory is a file: {directory.FullName}"
            guardDirectory directory.Parent

    let private guardFile path =
        let file = FileInfo path
        guardDirectory file.Directory
        rejectLink file
        require (not (Directory.Exists path)) $"Model cache file is a directory: {path}"

    let private externalDirectory path =
        let directory = Path.GetFullPath path |> Path.TrimEndingDirectorySeparator
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal

        if
            directory.Equals(repository, comparison)
            || directory.StartsWith(repository + string Path.DirectorySeparatorChar, comparison)
        then
            invalidArg "modelDirectory" "Provision model artifacts outside the repository."

        guardDirectory (DirectoryInfo directory)
        directory

    let private inspect (pin: ArtifactPin) path : ArtifactReceipt =
        guardFile path
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        require (stream.Length = pin.SizeBytes)
            $"{pin.Filename}: byte count mismatch; expected {pin.SizeBytes}, got {stream.Length}."
        use sha256 = IncrementalHash.CreateHash HashAlgorithmName.SHA256
        use gitBlob = IncrementalHash.CreateHash HashAlgorithmName.SHA1
        let length = pin.SizeBytes.ToString(Globalization.CultureInfo.InvariantCulture)
        gitBlob.AppendData(Encoding.UTF8.GetBytes $"blob {length}\000")
        let buffer = Array.zeroCreate<byte> 81920
        let mutable count = stream.Read(buffer, 0, buffer.Length)
        let mutable total = 0L
        while count > 0 do
            total <- total + int64 count
            require (total <= pin.SizeBytes) $"{pin.Filename}: byte count changed while hashing."
            sha256.AppendData(buffer, 0, count)
            gitBlob.AppendData(buffer, 0, count)
            count <- stream.Read(buffer, 0, buffer.Length)
        require (total = pin.SizeBytes) $"{pin.Filename}: byte count changed while hashing."
        let digest = sha256.GetHashAndReset() |> Convert.ToHexStringLower
        match pin.Identity with
        | Sha256 expected ->
            require (digest = expected)
                $"{pin.Filename}: SHA-256 mismatch; expected {expected}, got {digest}."
        | GitBlob expected ->
            let actual = gitBlob.GetHashAndReset() |> Convert.ToHexStringLower
            require (actual = expected)
                $"{pin.Filename}: Git blob SHA-1 mismatch; expected {expected}, got {actual}."
            Trace.tracefn "Git blob verified: %s %s" pin.Filename actual
        guardFile path
        { Filename = pin.Filename; SizeBytes = total; Sha256 = digest }

    let private cached pin path =
        guardFile path
        if File.Exists path then
            try
                let receipt = inspect pin path
                Trace.tracefn "Reusing verified: %s (%d bytes, SHA-256 %s)"
                    receipt.Filename receipt.SizeBytes receipt.Sha256
                Some receipt
            with :? InvalidDataException as error ->
                Trace.traceImportantfn
                    "Corrupt cache: %s Repairing from pinned source." error.Message
                None
        else
            None

    let private acquireFile (source: IArtifactSource) staging directory (pin: ArtifactPin) =
        let destination = directory @@ pin.Filename
        match cached pin destination with
        | Some receipt -> receipt
        | None ->
            let temporary = staging @@ pin.Filename
            Trace.tracefn "Downloading pinned: %s (%d bytes)" pin.Filename pin.SizeBytes
            source.Fetch(pin, temporary)
            let receipt = inspect pin temporary
            guardFile destination
            File.Move(temporary, destination, true)
            Trace.tracefn "Published verified: %s (%d bytes, SHA-256 %s)"
                receipt.Filename receipt.SizeBytes receipt.Sha256
            receipt

    let private validatePins (requested: ArtifactPin list) =
        let names = requested |> List.map _.Filename
        require (names.Length = (List.distinct names).Length) "Duplicate model cache filenames."
        let allowed = pins |> List.map _.Filename |> Set.ofList
        for pin in requested do
            require (Set.contains pin.Filename allowed && pin.SizeBytes > 0L)
                "Unsupported model cache filename or byte count."
            let length, digest =
                match pin.Identity with
                | Sha256 digest -> 64, digest
                | GitBlob digest -> 40, digest
            require (digest.Length = length && Seq.forall Char.IsAsciiHexDigitLower digest)
                "Invalid pinned model cache identity."

    let private lockCache path =
        guardFile path
        try
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        with :? IOException as error ->
            raise (IOException(
                $"Model cache lock unavailable: {path}. Another writer may be active. "
                + "After a crash, remove this lock only after confirming no writer remains.",
                error
            ))

    let acquire
        (source: IArtifactSource)
        (requested: ArtifactPin list)
        (publish: ArtifactReceipt list -> string -> unit)
        directory
        =
        validatePins requested
        let directory = externalDirectory directory
        Directory.CreateDirectory directory |> ignore
        for pin in requested do guardFile (directory @@ pin.Filename)
        guardFile (directory @@ manifestName)
        let lockPath = directory @@ ".laya-english.download.lock"
        let held = lockCache lockPath
        let staging = directory @@ $".download-{Guid.NewGuid():N}"
        try
            let manifest = directory @@ manifestName
            if File.Exists manifest then ModelBundle.readManifest manifest |> ignore
            Directory.CreateDirectory staging |> ignore
            let receipts = requested |> List.map (acquireFile source staging directory)
            publish receipts staging
        finally
            try
                if Directory.Exists staging then Directory.Delete(staging, true)
            finally
                held.Dispose()
                File.Delete lockPath

    let private source =
        { new IArtifactSource with
            member _.Fetch(pin, destination) =
                let url =
                    "https://huggingface.co/mariojcr/laya-onnx/resolve/"
                    + "aeebb05497cc473360caf4de1653107cf08d6b10/english/" + pin.Filename
                // Native curl streams to disk; no async-to-sync bridge or buffered model body.
                CreateProcess.fromRawCommand "curl"
                    [
                        "--disable"; "--fail"; "--silent"; "--show-error"; "--location"
                        "--proto"; "=https"; "--proto-redir"; "=https"; "--max-redirs"; "5"
                        "--connect-timeout"; "30"; "--max-time"; "1800"
                        "--speed-time"; "60"; "--speed-limit"; "1024"
                        "--max-filesize"; string pin.SizeBytes
                        "--output"; destination; url
                    ]
                |> CreateProcess.withTimeout (TimeSpan.FromMinutes 31.0)
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore }

    let manifestJson (receipts: ArtifactReceipt list) =
        let artifact filename =
            let receipt = receipts |> List.find (fun receipt -> receipt.Filename = filename)
            {| path = receipt.Filename; sizeBytes = receipt.SizeBytes; sha256 = receipt.Sha256 |}
        let tensor name elementType dimensions =
            {| name = name; elementType = elementType; dimensions = dimensions |}
        {|
            schemaVersion = "laya-decision-v1"
            bundleId = "laya-english"
            precision = "float32"
            graph = artifact "laya.onnx"
            sidecars =
                {|
                    tokenizer = artifact "tokenizer.json"
                    tokenizerConfig = artifact "tokenizer_config.json"
                    agentConfig = artifact "rl_agent_config.json"
                |}
            limits = {| maxSequenceLength = 512; maxHeadLength = 192; maxOptionTokens = 48 |}
            inputs =
                [
                    tensor "input_ids" "int64" [ "questions"; "sequence_length" ]
                    tensor "attention_mask" "int64" [ "questions"; "sequence_length" ]
                    tensor "marker_pos" "int64" [ "questions"; "option_count" ]
                    tensor "marker_mask" "bool" [ "questions"; "option_count" ]
                    tensor "qtype" "int64" [ "questions" ]
                ]
            outputs =
                [
                    tensor "logits" "float32" [ "questions"; "option_count" ]
                    tensor "act_logits" "float32" [ "questions"; "2" ]
                ]
            customOperatorLibraries = Array.empty<string>
            externalData = Array.empty<string>
            testedShapes = Array.empty<obj>
            provenance =
                {|
                    repository = "mariojcr/laya-onnx"
                    revision = "aeebb05497cc473360caf4de1653107cf08d6b10"
                    sourceRepository = "convaiinnovations/laya"
                    sourceRevision = (null: string)
                |}
        |}
        |> fun manifest ->
            JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true))

    let private report status integrityVerified detail =
        {|
            schemaVersion = "rlcd-artifact-verification-v1"
            status = status
            integrityVerified = integrityVerified
            nativeCertified = false
            detail = detail
        |}
        |> JsonSerializer.Serialize
        |> printfn "%s"

    let private directory () =
        let defaultDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            @@ ".cache/rlcd/laya-english"

        Environment.environVarOrDefault "RLCD_MODELS_DIR" defaultDirectory
        |> externalDirectory

    let private publishManifest directory receipts staging =
        let destination = directory @@ manifestName
        guardFile destination
        let json = manifestJson receipts
        // Never overwrite an unrelated/user-maintained manifest, even in the cache.
        if File.Exists destination then
            require (FileInfo(destination).Length <= 1048576L)
                "Existing cache manifest is not managed by DownloadModels; refusing to overwrite."
            require (File.ReadAllText destination = json)
                "Existing cache manifest differs; refusing to overwrite. Inspect it before removal."
        let temporary = staging @@ manifestName
        File.WriteAllText(temporary, json, UTF8Encoding false)
        temporary |> ModelBundle.readManifest |> ModelBundle.verify directory |> ignore
        if not (File.Exists destination) then
            guardFile destination
            File.Move(temporary, destination)
        Trace.tracefn "Verified cache manifest: %s" destination

    let download () =
        let modelDirectory = directory ()
        Trace.tracefn "Model directory: %s" modelDirectory
        modelDirectory |> acquire source pins (publishManifest modelDirectory)
        report "integrity-verified" true
            "Pinned model bundle acquired and verified; native/model parity remains unverified."

    let private run integrityOnly =
        try
            let modelDirectory = directory ()
            let manifest =
                Environment.environVarOrDefault
                    "RLCD_MODEL_MANIFEST"
                    (modelDirectory @@ manifestName)
                |> Path.GetFullPath

            Trace.tracefn "Model directory: %s\nManifest: %s" modelDirectory manifest
            let verified = manifest |> ModelBundle.readManifest |> ModelBundle.verify modelDirectory

            if integrityOnly then
                report
                    "integrity-verified"
                    true
                    $"{verified.BundleId}: native certification was not requested."
            else
                let detail =
                    "Artifact integrity passed. Independent model parity/certification is unavailable. "
                    + "Run CheckInference separately to check real CPU model callability; "
                    + "that check does not certify independent parity. No runtime acquisition is performed."
                report "blocked" true detail
                invalidOp detail
        with
        | :? ArgumentException as error ->
            report "invalid-arguments" false error.Message
            reraise ()
        | (:? InvalidDataException | :? IOException | :? UnauthorizedAccessException) as error ->
            report "blocked" false error.Message
            reraise ()

    let check () = run true
    let verify () = run false

[<RequireQualifiedAccess>]
module Example =
    let private collect results =
        let prepend result rest =
            match result, rest with
            | Ok value, Ok values -> Ok(value :: values)
            | Error errors, Error remaining -> Error(errors @ remaining)
            | Error errors, Ok _
            | Ok _, Error errors -> Error errors

        List.foldBack prepend results (Ok [])

    let triage state =
        let category =
            [
                ChoiceOption.create "billing" "Billing" (Some "Charges, invoices, and refunds")
                ChoiceOption.create
                    "technical"
                    "Technical"
                    (Some "Product faults and access problems")
            ]
            |> collect
            |> Result.bind (Question.choice "category" "Choose the ticket category.")

        let urgency =
            [
                ChoiceOption.create "routine" "Routine" (Some "Work can continue")
                ChoiceOption.create "blocked" "Blocked" (Some "Work cannot continue")
                ChoiceOption.create "critical" "Critical" (Some "A widespread service interruption")
            ]
            |> collect
            |> Result.bind (
                Question.score "urgency" "Rate the urgency in the stated ordinal order."
            )

        [
            category
            urgency
            Question.boolean "follow-up" "Does the customer need a follow-up reply?" None
        ]
        |> collect
        |> Result.bind (fun questions ->
            DecisionRequest.create
                {
                    State = state
                    Questions = questions
                    Options = RequestOptions.defaults
                })

    let private summarizeQuestion question =
        {|
            id = question |> Question.id |> QuestionId.value
            kind = question |> Question.kind |> string
            instructions = Question.instructions question
            options =
                question
                |> Question.options
                |> List.mapi (fun position option ->
                    {|
                        position = position
                        id = option |> ChoiceOption.id |> OptionId.value
                        label = ChoiceOption.label option
                        criterion = option |> ChoiceOption.criterion |> Option.toObj
                    |})
        |}

    let private print value = JsonSerializer.Serialize value |> printfn "%s"

    let run () =
        // An explicitly empty value is invalid input, not a request for the default.
        let state =
            System.Environment.GetEnvironmentVariable "RLCD_EXAMPLE_STATE"
            |> Option.ofObj
            |> Option.defaultValue
                "The customer was charged twice and asks when the refund will arrive."

        match triage state with
        | Error errors ->
            print
                {|
                    status = "invalid-request"
                    inferenceAvailable = false
                    errors = errors |> List.map (sprintf "%A")
                |}
            invalidArg "RLCD_EXAMPLE_STATE" "The example state must be meaningful text."
        | Ok request ->
            let options = DecisionRequest.options request
            let budget = RequestOptions.budget options
            print
                {|
                    status = "constructed"
                    inferenceAvailable = false
                    state = DecisionRequest.state request
                    questions = request |> DecisionRequest.questions |> List.map summarizeQuestion
                    budget =
                        {|
                            sequenceLength = TokenBudget.sequenceLength budget
                            headLength = TokenBudget.headLength budget
                            optionTokens = TokenBudget.optionTokens budget
                        |}
                    truncationPolicy = options |> RequestOptions.truncation |> string
                |}

// FAKE 6.1.3's internal binlog reader predates SDK 10's format; keep console diagnostics.
Target.create "Restore" (fun _ ->
    DotNet.restore
        (fun options ->
            { options with
                Common = { dotnetOptions options.Common with CustomParams = Some "--locked-mode" }
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        solution)

// Explicit opt-in for intentional package changes; ordinary Restore remains locked.
Target.create "UpdateLocks" (fun _ ->
    DotNet.restore
        (fun options ->
            { options with
                Common =
                    { dotnetOptions options.Common with
                        CustomParams = Some "--force-evaluate -p:RestoreLockedMode=false" }
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        solution)

Target.create "Build" (fun _ ->
    DotNet.build
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoRestore = true
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        solution)

// Loads the real native library and calls its API without requiring a model.
Target.create "CheckRuntime" (fun _ ->
    DotNet.test
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoBuild = true
                NoRestore = true
                Filter = Some "Category=NativeRuntime"
                Logger = Some "console;verbosity=detailed"
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        "tests/Rlcd.Tests/Rlcd.Tests.fsproj")

// Explicit real-bundle callability only; missing inputs fail, and this is not independent parity.
Target.create "CheckInference" (fun _ ->
    DotNet.test
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoBuild = true
                NoRestore = true
                Filter = Some "Category=RealInference"
                Logger = Some "console;verbosity=detailed"
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        "tests/Rlcd.Tests/Rlcd.Tests.fsproj")

Target.create "Tests" (fun _ ->
    DotNet.test
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoBuild = true
                NoRestore = true
                Filter = Some "Category!=RealInference"
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        solution)

Target.create "Package" (fun _ ->
    DotNet.pack
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoBuild = true
                NoRestore = true
                MSBuildParams = { options.MSBuildParams with DisableInternalBinLog = true }
            })
        solution)

// Local-only inputs: RLCD_MODELS_DIR and RLCD_MODEL_MANIFEST; neither target downloads.
Target.create "CheckModels" (fun _ -> Models.check ())
// Explicit acquisition only: no dependency from All, Setup, or inference.
Target.create "DownloadModels" (fun _ -> Models.download ())
// Integrity may pass; independent model parity/certification remains blocked, not CPU callability.
Target.create "VerifyModels" (fun _ -> Models.verify ())
// Optional RLCD_EXAMPLE_STATE overrides the sample text; this constructs requests, not inference.
Target.create "Example" (fun _ -> Example.run ())

Target.create "Setup" ignore
Target.create "All" ignore

"Restore" ==> "Build" ==> "Tests" ==> "All" |> ignore
"Build" ==> "CheckRuntime" |> ignore
"Build" ==> "CheckInference" |> ignore
"Build" ==> "Package" ==> "All" |> ignore
"Tests" ?=> "Package" |> ignore
"Setup" <== [ "Restore"; "CheckModels" ] |> ignore
// Order these when both are selected, without making CheckModels restore on its own.
"Restore" ?=> "CheckModels" |> ignore

Target.runOrDefault "All"
