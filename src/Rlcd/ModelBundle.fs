namespace Rlcd

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

[<RequireQualifiedAccess>]
module private BundleJson =
    let require condition message =
        if not condition then
            raise (InvalidDataException message)

    let private name (property: JsonProperty) =
        try
            property.Name
        with :? InvalidOperationException as error when
            error.GetType() = typeof<InvalidOperationException> ->
            raise (InvalidDataException("Malformed Unicode in JSON property name.", error))

    let properties (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.Object) "Expected a JSON object."

        let properties =
            element.EnumerateObject()
            |> Seq.map (fun property -> name property, property.Value)
            |> Seq.toList

        let names = properties |> List.map fst
        require (names.Length = (List.distinct names).Length) "Duplicate JSON properties."
        properties

    let fields expected element =
        let names = properties element |> List.map fst
        require (List.sort names = List.sort expected) "Missing or unsupported JSON properties."

    let property name (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.Object) "Expected a JSON object."

        match element.TryGetProperty(name: string) with
        | true, value -> value
        | false, _ -> raise (InvalidDataException $"Missing property: {name}.")

    let text (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.String) "Expected a JSON string."

        try
            element.GetString()
        with :? InvalidOperationException as error when
            error.GetType() = typeof<InvalidOperationException> ->
            raise (InvalidDataException("Malformed Unicode in JSON string value.", error))

    let integer (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.Number) "Expected a JSON integer."

        match element.TryGetInt64() with
        | true, value -> value
        | false, _ -> raise (InvalidDataException "Expected a 64-bit JSON integer.")

    let number (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.Number) "Expected a numeric temperature."

        match element.TryGetDouble() with
        | true, value when Double.IsFinite value -> value
        | _ -> raise (InvalidDataException "Temperatures must be finite numbers.")

    let boolean (element: JsonElement) =
        require
            (element.ValueKind = JsonValueKind.True
             || element.ValueKind = JsonValueKind.False)
            "Expected a JSON boolean."

        element.GetBoolean()

    let items (element: JsonElement) =
        require (element.ValueKind = JsonValueKind.Array) "Expected a JSON array."
        element.EnumerateArray() |> Seq.toList

    let exactText expected element =
        require (text element = expected) $"Expected '{expected}'."

    let exactInteger expected element =
        require (integer element = expected) $"Expected {expected}."

    let parse (stream: Stream) =
        try
            JsonDocument.Parse(stream, JsonDocumentOptions(MaxDepth = 32))
        with :? JsonException as error ->
            raise (InvalidDataException("Malformed bundle JSON.", error))

[<RequireQualifiedAccess>]
module private BundlePath =
    let validate (relativePath: string) =
        let segments = relativePath.Split('/')

        let safeCharacter character =
            Char.IsAsciiLetterOrDigit character || "._-".Contains(character: char)

        let safeSegment (segment: string) =
            segment.Length > 0
            && segment <> "."
            && segment <> ".."
            && not (segment.EndsWith('.'))
            && Seq.forall safeCharacter segment

        BundleJson.require
            (not (Path.IsPathRooted relativePath) && Array.forall safeSegment segments)
            "Artifact paths must be portable, relative paths without traversal."

    let rec private ancestors (directory: DirectoryInfo) =
        if not (isNull directory) then
            BundleJson.require
                (isNull directory.LinkTarget
                 && not (directory.Attributes.HasFlag FileAttributes.ReparsePoint))
                "Symlink/reparse-point directories are unsupported."

            ancestors directory.Parent

    let regular (path: string) =
        let file = FileInfo path
        BundleJson.require (isNull file.LinkTarget) "Artifact symlinks are unsupported."

        if not file.Exists then
            raise (FileNotFoundException("Required local artifact is missing.", path))

        BundleJson.require
            (not (file.Attributes.HasFlag FileAttributes.Directory)
             && not (file.Attributes.HasFlag FileAttributes.ReparsePoint))
            "Expected a regular local file."

        ancestors file.Directory
        file

    let resolve (modelDirectory: string) relativePath =
        validate relativePath
        let root = Path.GetFullPath modelDirectory

        if not (Directory.Exists root) then
            raise (DirectoryNotFoundException $"Model directory does not exist: {root}")

        ancestors (DirectoryInfo root)
        Path.GetFullPath(Path.Combine(root, relativePath))

[<RequireQualifiedAccess>]
module private BundleDocument =
    let read maximumBytes (path: string) =
        let file = BundlePath.regular (Path.GetFullPath path)

        BundleJson.require
            (file.Length > 0L && file.Length <= maximumBytes)
            "JSON file is empty or exceeds its validation bound."

        use stream =
            new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)

        BundleJson.require
            (stream.Length > 0L && stream.Length <= maximumBytes)
            "JSON file changed before reading."

        BundleJson.parse stream

/// A pinned local artifact; only validated manifests construct descriptions.
type BundleArtifact =
    private
        {
            Path: string
            Sha256: string
            SizeBytes: int64
        }

/// A parsed manifest for the selected laya-decision-v1 graph and sidecars.
type BundleManifest =
    private
        {
            BundleId: string
            Artifacts: BundleArtifact list
        }

/// Validated raw agent configuration; calibration is a separate downstream operation.
type AgentConfiguration =
    private
        {
            Temperatures: Map<string, float>
        }

/// Local agent-configuration IO, without implicit defaults for missing calibration.
[<RequireQualifiedAccess>]
module AgentConfiguration =
    let private defaults element =
        let values = element |> BundleJson.items |> List.map BundleJson.number
        BundleJson.require (values.Length = 3) "Expected choice, score, and noul temperatures."
        values |> List.mapi (fun index value -> $"temperature[{index}]", value)

    let private buckets element =
        let allowed =
            [
                for questionType in [ "choice"; "score"; "noul" ] do
                    for size in [ "2"; "3-5"; "6-10"; "11+" ] do
                        $"{questionType}:{size}"
            ]
            |> Set.ofList

        element
        |> BundleJson.properties
        |> List.map (fun (name, value) ->
            BundleJson.require (Set.contains name allowed) "Unsupported temperature bucket."

            name, BundleJson.number value)

    /// Read supported lengths and finite temperatures, without verifying a manifest hash.
    let read (configurationPath: string) : AgentConfiguration =
        use document = BundleDocument.read 67108864L configurationPath
        let root = document.RootElement
        BundleJson.properties root |> ignore
        root |> BundleJson.property "max_len" |> BundleJson.exactInteger 512L
        root |> BundleJson.property "head_max_len" |> BundleJson.exactInteger 192L
        let defaults = root |> BundleJson.property "temperature" |> defaults
        let buckets = root |> BundleJson.property "temperature_by_options" |> buckets

        {
            Temperatures = Map.ofList (defaults @ buckets)
        }

    /// Return the raw temperatures using source field and option-bucket keys.
    let temperatures (configuration: AgentConfiguration) : Map<string, float> =
        configuration.Temperatures

    /// Identify keys that require clamping to [0.5, 5.0] during downstream calibration.
    let clampingRequired (configuration: AgentConfiguration) : string list =
        configuration.Temperatures
        |> Map.toList
        |> List.choose (fun (key, value) -> if value < 0.5 || value > 5.0 then Some key else None)

/// Local integrity verification does not certify native loading or numerical parity.
type BundleVerification =
    {
        /// The manifest's supported bundle identifier.
        BundleId: string
        /// The verified local graph path.
        GraphPath: string
        /// Always false until separate native certification is available.
        IsNativeCertified: bool
    }

/// Local file integrity operations, without network acquisition.
[<RequireQualifiedAccess>]
module BundleArtifact =
    /// Return the manifest-relative local path.
    let path (artifact: BundleArtifact) : string = artifact.Path

    /// Verify the local regular file, size, and SHA-256.
    /// Provision an immutable directory; these checks are not a concurrent filesystem sandbox.
    let verify (modelDirectory: string) (artifact: BundleArtifact) : unit =
        let fullPath = BundlePath.resolve modelDirectory artifact.Path
        let file = BundlePath.regular fullPath
        BundleJson.require (file.Length = artifact.SizeBytes) "Artifact byte count mismatch."

        use stream =
            new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)

        BundleJson.require (stream.Length = artifact.SizeBytes) "Artifact changed before hashing."
        let actual = SHA256.HashData stream |> Convert.ToHexStringLower
        BundleJson.require (actual = artifact.Sha256) "Artifact SHA-256 mismatch."
        BundlePath.regular fullPath |> ignore

[<RequireQualifiedAccess>]
module private BundleContract =
    let private artifact expectedFilename element =
        BundleJson.fields [ "path"; "sha256"; "sizeBytes" ] element
        let path = element |> BundleJson.property "path" |> BundleJson.text
        BundlePath.validate path

        BundleJson.require
            (Path.GetFileName path = expectedFilename)
            "Unsupported artifact filename."

        let hash = element |> BundleJson.property "sha256" |> BundleJson.text

        BundleJson.require
            (hash.Length = 64 && Seq.forall Char.IsAsciiHexDigitLower hash)
            "Expected a lowercase SHA-256 digest."

        let size = element |> BundleJson.property "sizeBytes" |> BundleJson.integer
        BundleJson.require (size > 0L) "Artifact byte counts must be positive."

        {
            Path = path
            Sha256 = hash
            SizeBytes = size
        }

    let private graph element =
        let description = artifact "laya.onnx" element

        BundleJson.require
            (description.Sha256 = "ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e"
             && description.SizeBytes = 1688711336L)
            "Only the pinned inline graph is supported; custom/external graphs are unsupported."

        description

    let private sidecars element =
        BundleJson.fields [ "tokenizer"; "tokenizerConfig"; "agentConfig" ] element

        [
            "tokenizer", "tokenizer.json"
            "tokenizerConfig", "tokenizer_config.json"
            "agentConfig", "rl_agent_config.json"
        ]
        |> List.map (fun (name, filename) ->
            let description = element |> BundleJson.property name |> artifact filename

            BundleJson.require
                (description.SizeBytes <= 67108864L)
                "Sidecar exceeds the 64 MiB validation bound."

            description)

    let private tensor element =
        BundleJson.fields [ "name"; "elementType"; "dimensions" ] element
        let name = element |> BundleJson.property "name" |> BundleJson.text
        let elementType = element |> BundleJson.property "elementType" |> BundleJson.text

        let dimensions =
            element
            |> BundleJson.property "dimensions"
            |> BundleJson.items
            |> List.map BundleJson.text

        name, elementType, dimensions

    let private tensors expected element =
        let actual = element |> BundleJson.items |> List.map tensor

        BundleJson.require
            (List.sort actual = List.sort expected)
            "Tensor names, types, ranks, or dimensions do not match laya-decision-v1."

    let private inputs element =
        element
        |> tensors
            [
                "input_ids", "int64", [ "questions"; "sequence_length" ]
                "attention_mask", "int64", [ "questions"; "sequence_length" ]
                "marker_pos", "int64", [ "questions"; "option_count" ]
                "marker_mask", "bool", [ "questions"; "option_count" ]
                "qtype", "int64", [ "questions" ]
            ]

    let private outputs element =
        element
        |> tensors
            [
                "logits", "float32", [ "questions"; "option_count" ]
                "act_logits", "float32", [ "questions"; "2" ]
            ]

    let private limits element =
        BundleJson.fields [ "maxSequenceLength"; "maxHeadLength"; "maxOptionTokens" ] element

        for name, expected in
            [ "maxSequenceLength", 512L; "maxHeadLength", 192L; "maxOptionTokens", 48L ] do
            element |> BundleJson.property name |> BundleJson.exactInteger expected

    let private provenance element =
        BundleJson.fields [ "repository"; "revision"; "sourceRepository"; "sourceRevision" ] element

        element
        |> BundleJson.property "repository"
        |> BundleJson.exactText "mariojcr/laya-onnx"

        element
        |> BundleJson.property "revision"
        |> BundleJson.exactText "aeebb05497cc473360caf4de1653107cf08d6b10"

        element
        |> BundleJson.property "sourceRepository"
        |> BundleJson.exactText "convaiinnovations/laya"
        // Source weight equivalence has not been established for this exported graph.
        BundleJson.require
            ((BundleJson.property "sourceRevision" element).ValueKind = JsonValueKind.Null)
            "Source revision must remain null until independently established."

    let private shape element =
        BundleJson.fields [ "questions"; "optionCount"; "sequenceLength" ] element
        let questions = element |> BundleJson.property "questions" |> BundleJson.integer
        let options = element |> BundleJson.property "optionCount" |> BundleJson.integer
        let sequence = element |> BundleJson.property "sequenceLength" |> BundleJson.integer

        BundleJson.require
            (questions > 0L && options >= 2L && options <= sequence && sequence <= 512L)
            "Tested shape is outside the supported bounds."

    let private metadata element =
        element
        |> BundleJson.property "schemaVersion"
        |> BundleJson.exactText "laya-decision-v1"

        element |> BundleJson.property "bundleId" |> BundleJson.exactText "laya-english"
        element |> BundleJson.property "precision" |> BundleJson.exactText "float32"
        element |> BundleJson.property "limits" |> limits
        element |> BundleJson.property "inputs" |> inputs
        element |> BundleJson.property "outputs" |> outputs
        element |> BundleJson.property "provenance" |> provenance

        element
        |> BundleJson.property "testedShapes"
        |> BundleJson.items
        |> List.iter shape

        for name in [ "customOperatorLibraries"; "externalData" ] do
            let entries = element |> BundleJson.property name |> BundleJson.items

            BundleJson.require
                entries.IsEmpty
                "Custom operators and external tensor data are unsupported."

    let read element =
        BundleJson.fields
            [
                "schemaVersion"
                "bundleId"
                "precision"
                "graph"
                "sidecars"
                "limits"
                "inputs"
                "outputs"
                "customOperatorLibraries"
                "externalData"
                "testedShapes"
                "provenance"
            ]
            element

        metadata element
        let graph = element |> BundleJson.property "graph" |> graph
        let sidecars = element |> BundleJson.property "sidecars" |> sidecars

        {
            BundleId = "laya-english"
            Artifacts = graph :: sidecars
        }

[<RequireQualifiedAccess>]
module private TokenizerContract =
    let private identifier element =
        let value = BundleJson.integer element
        BundleJson.require (value >= 0L && value <= int64 Int32.MaxValue) "Invalid token ID."
        value

    let private distinctTokens tokens =
        let names = tokens |> List.map fst
        let identifiers = tokens |> List.map snd

        BundleJson.require
            (List.distinct names = names && List.distinct identifiers = identifiers)
            "Token contents and IDs must be unique."

    let private addedToken element =
        BundleJson.fields
            [ "id"; "content"; "special"; "normalized"; "single_word"; "lstrip"; "rstrip" ]
            element

        for flag in [ "special"; "normalized"; "single_word"; "lstrip"; "rstrip" ] do
            element |> BundleJson.property flag |> BundleJson.boolean |> ignore

        let content = element |> BundleJson.property "content" |> BundleJson.text
        let identifier = element |> BundleJson.property "id" |> identifier
        BundleJson.require (content.Length > 0) "Added tokens must not be empty."
        content, identifier

    let private vocabulary element =
        BundleJson.properties element |> ignore
        element |> BundleJson.property "type" |> BundleJson.exactText "BPE"
        element |> BundleJson.property "merges" |> BundleJson.items |> ignore

        let tokens =
            element
            |> BundleJson.property "vocab"
            |> BundleJson.properties
            |> List.map (fun (name, value) -> name, identifier value)

        BundleJson.require (not tokens.IsEmpty) "Tokenizer vocabulary must not be empty."
        distinctTokens tokens
        Map.ofList tokens

    let private merge vocabulary added =
        distinctTokens added
        let occupied = vocabulary |> Map.values |> Set.ofSeq

        let insert (tokens, occupied) (content, identifier) =
            match Map.tryFind content tokens with
            | Some existing ->
                BundleJson.require (existing = identifier) "Added token and vocabulary ID disagree."
                tokens, occupied
            | None ->
                BundleJson.require
                    (not (Set.contains identifier occupied))
                    "Added token ID collides with vocabulary."

                Map.add content identifier tokens, Set.add identifier occupied

        let tokens, _ = List.fold insert (vocabulary, occupied) added
        tokens

    let private tokens element =
        BundleJson.properties element |> ignore
        element |> BundleJson.property "version" |> BundleJson.exactText "1.0"
        let normalizer = BundleJson.property "normalizer" element
        BundleJson.fields [ "type" ] normalizer
        normalizer |> BundleJson.property "type" |> BundleJson.exactText "NFC"
        let preTokenizer = BundleJson.property "pre_tokenizer" element
        BundleJson.properties preTokenizer |> ignore
        preTokenizer |> BundleJson.property "type" |> BundleJson.exactText "ByteLevel"

        let prefix =
            preTokenizer |> BundleJson.property "add_prefix_space" |> BundleJson.boolean

        let regex = preTokenizer |> BundleJson.property "use_regex" |> BundleJson.boolean
        BundleJson.require (not prefix && regex) "Unsupported ByteLevel tokenizer flags."
        let vocabulary = element |> BundleJson.property "model" |> vocabulary
        let added = element |> BundleJson.property "added_tokens" |> BundleJson.items
        added |> List.map addedToken |> merge vocabulary

    let private controlToken (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> BundleJson.text element
        | JsonValueKind.Object ->
            BundleJson.properties element |> ignore
            element |> BundleJson.property "content" |> BundleJson.text
        | _ -> raise (InvalidDataException "Expected a special token string or descriptor.")

    let private controls vocabulary element =
        BundleJson.properties element |> ignore

        let identifiers =
            [ "cls_token"; "sep_token"; "pad_token"; "mask_token" ]
            |> List.map (fun name ->
                let content = element |> BundleJson.property name |> controlToken

                match Map.tryFind content vocabulary with
                | Some identifier -> identifier
                | None -> raise (InvalidDataException $"Special token {name} has no tokenizer ID."))

        BundleJson.require
            ((List.distinct identifiers).Length = 4)
            "CLS/SEP/PAD/MASK must have distinct tokenizer-derived IDs."

    let verify configurationPath tokenizerPath =
        use tokenizerDocument = BundleDocument.read 67108864L tokenizerPath
        use configurationDocument = BundleDocument.read 67108864L configurationPath
        let vocabulary = tokens tokenizerDocument.RootElement
        controls vocabulary configurationDocument.RootElement

/// The pinned, local-only laya-decision-v1 artifact boundary.
[<RequireQualifiedAccess>]
module ModelBundle =
    /// Read and validate a manifest; unsupported graphs and schemas are rejected.
    let readManifest (manifestPath: string) : BundleManifest =
        use document = BundleDocument.read 1048576L manifestPath
        BundleContract.read document.RootElement

    /// Return the graph and three required sidecars in verification order.
    let artifacts (manifest: BundleManifest) : BundleArtifact list = manifest.Artifacts

    /// Verify sidecar integrity and supported configuration only, not the graph or native runtime.
    let verifySidecars (modelDirectory: string) (manifest: BundleManifest) : AgentConfiguration =
        let sidecars = List.tail manifest.Artifacts
        sidecars |> List.iter (BundleArtifact.verify modelDirectory)

        let paths =
            sidecars
            |> List.map (fun artifact -> BundlePath.resolve modelDirectory artifact.Path)

        TokenizerContract.verify paths[1] paths[0]
        AgentConfiguration.read paths[2]

    /// Verify all artifacts and their supported configuration, not native execution.
    let verify (modelDirectory: string) (manifest: BundleManifest) : BundleVerification =
        let graph = List.head manifest.Artifacts
        graph |> BundleArtifact.verify modelDirectory
        manifest |> verifySidecars modelDirectory |> ignore

        {
            BundleId = manifest.BundleId
            GraphPath = BundlePath.resolve modelDirectory graph.Path
            IsNativeCertified = false
        }
