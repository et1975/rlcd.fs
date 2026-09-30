namespace Rlcd.Tests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks

open Xunit

[<Trait("Category", "Unit")>]
module BuildTests =
    let private repository =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let private script = Path.Combine(repository, "build.fsx")

    type private Fixture() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"rlcd build tests {Guid.NewGuid():N}")

        do Directory.CreateDirectory directory |> ignore
        member _.Directory = directory

        interface IDisposable with
            member _.Dispose() = Directory.Delete(directory, true)

    type private Response =
        {
            ExitCode: int
            Output: JsonElement
            Error: string
        }

    let private startInfo workingDirectory scriptPath modelDirectory arguments =
        let start = ProcessStartInfo("dotnet")
        start.WorkingDirectory <- workingDirectory
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.Environment.Remove "RLCD_MODELS_DIR" |> ignore

        modelDirectory
        |> Option.iter (fun value -> start.Environment["RLCD_MODELS_DIR"] <- value)

        for argument in [ "fsi"; "--exec"; "--"; scriptPath ] @ arguments do
            start.ArgumentList.Add argument

        start

    let private response (child: Process) =
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit 120000) then
            child.Kill true
            child.WaitForExit()
            failwith "Build script exceeded the two-minute test deadline."

        Task.WhenAll(output, error).GetAwaiter().GetResult() |> ignore
        let text = output.GetAwaiter().GetResult()
        let diagnostic = error.GetAwaiter().GetResult()
        Assert.True(not (String.IsNullOrWhiteSpace text), diagnostic)
        use document = JsonDocument.Parse text
        Assert.Equal("rlcd-build-v1", document.RootElement.GetProperty("schemaVersion").GetString())

        {
            ExitCode = child.ExitCode
            Output = document.RootElement.Clone()
            Error = diagnostic
        }

    let private invoke workingDirectory scriptPath modelDirectory arguments =
        use child =
            Process.Start(startInfo workingDirectory scriptPath modelDirectory arguments)

        response child

    let private copyRunner directory =
        for name in [ "build.fsx"; "global.json"; "NuGet.Config" ] do
            File.Copy(Path.Combine(repository, name), Path.Combine(directory, name))

        Path.Combine(directory, "build.fsx")

    let private strings (element: JsonElement) =
        element.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList

    let private targets (response: Response) =
        response.Output.GetProperty("commands").EnumerateArray()
        |> Seq.map (fun command -> command.GetProperty("Target").GetString())
        |> Seq.toList

    let private commandArguments index (response: Response) =
        let command = response.Output.GetProperty("commands")[index]
        command.GetProperty("Arguments") |> strings

    [<Fact>]
    let ``list is a single JSON document with only supported targets`` () =
        use fixture = new Fixture()
        let response = invoke fixture.Directory script None [ "--list" ]
        Assert.Equal(0, response.ExitCode)
        Assert.Equal("list", response.Output.GetProperty("status").GetString())

        Assert.Equal<string list>(
            [ "Restore"; "Build"; "Tests"; "Package"; "CheckModels"; "Setup"; "All" ],
            response.Output.GetProperty("targets") |> strings
        )

    [<Theory>]
    [<InlineData("Restore", "Restore")>]
    [<InlineData("Build", "Restore,Build")>]
    [<InlineData("Tests", "Restore,Build,Tests")>]
    [<InlineData("Package", "Restore,Build,Package")>]
    [<InlineData("CheckModels", "CheckModels")>]
    [<InlineData("Setup", "Restore,CheckModels")>]
    [<InlineData("All", "Restore,Build,Tests,Package")>]
    let ``dry run exposes dependency order without executing`` (target: string, expected: string) =
        let response = invoke repository script None [ "--dry-run"; "-t"; target ]
        Assert.Equal(0, response.ExitCode)
        Assert.Equal("dry-run", response.Output.GetProperty("status").GetString())
        Assert.Equal<string list>(expected.Split(',') |> Array.toList, targets response)

    [<Fact>]
    let ``default All uses pinned offline release commands from external cwd`` () =
        use fixture = new Fixture()
        let response = invoke fixture.Directory script None [ "--dry-run" ]
        Assert.Equal(0, response.ExitCode)
        Assert.Equal<string list>([ "Restore"; "Build"; "Tests"; "Package" ], targets response)

        let expected =
            [
                [
                    "restore"
                    "Rlcd.slnx"
                    "--locked-mode"
                    "--configfile"
                    Path.Combine(repository, "NuGet.Config")
                ]
                [ "build"; "Rlcd.slnx"; "-c"; "Release"; "--no-restore" ]
                [
                    "test"
                    "tests/Rlcd.Tests/Rlcd.Tests.fsproj"
                    "-c"
                    "Release"
                    "--no-build"
                    "--no-restore"
                ]
                [
                    "pack"
                    "src/Rlcd/Rlcd.fsproj"
                    "-c"
                    "Release"
                    "--no-build"
                    "--no-restore"
                ]
            ]

        for index, arguments in List.indexed expected do
            Assert.Equal<string list>(arguments, commandArguments index response)

        for command in response.Output.GetProperty("commands").EnumerateArray() do
            Assert.Equal("dotnet", command.GetProperty("FileName").GetString())
            Assert.Equal(repository, command.GetProperty("WorkingDirectory").GetString())

    [<Fact>]
    let ``model paths preserve spaces and resolve against caller cwd`` () =
        use fixture = new Fixture()

        let response =
            invoke
                fixture.Directory
                script
                (Some "ignored default")
                [
                    "-t"
                    "CheckModels"
                    "--dry-run"
                    "--model-dir"
                    "model space"
                    "--manifest"
                    "manifest space.json"
                ]

        Assert.Equal(0, response.ExitCode)

        Assert.Equal<string list>(
            [
                "fsi"
                "--exec"
                Path.Combine(repository, "scripts/VerifyArtifacts.fsx")
                "--"
                "--integrity-only"
                "--model-dir"
                Path.Combine(fixture.Directory, "model space")
                "--manifest"
                Path.Combine(fixture.Directory, "manifest space.json")
            ],
            commandArguments 0 response
        )

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("environment model space")>]
    let ``model defaults use environment or user cache and repository manifest`` environment =
        use fixture = new Fixture()

        let response =
            invoke
                fixture.Directory
                script
                (Option.ofObj environment)
                [ "-t"; "CheckModels"; "--dry-run" ]

        let expectedDirectory =
            if isNull environment then
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache/rlcd/laya-english"
                )
            else
                Path.Combine(fixture.Directory, environment)

        let arguments = commandArguments 0 response
        Assert.Equal(expectedDirectory, arguments[6])
        Assert.Equal(Path.Combine(repository, "models/laya-english.manifest.json"), arguments[8])

    [<Theory>]
    [<InlineData("--unknown")>]
    [<InlineData("-t")>]
    [<InlineData("-t|Clean")>]
    [<InlineData("-t|all")>]
    [<InlineData("-t|Build|-t|Tests")>]
    [<InlineData("--list|--list")>]
    [<InlineData("--dry-run|--dry-run")>]
    [<InlineData("--model-dir")>]
    [<InlineData("--model-dir|--dry-run")>]
    [<InlineData("--model-dir||--dry-run")>]
    [<InlineData("--model-dir|one|--model-dir|two")>]
    [<InlineData("--manifest")>]
    [<InlineData("--manifest|one|--manifest|two")>]
    [<InlineData("--list|-t|Build")>]
    [<InlineData("--list|--dry-run")>]
    [<InlineData("Restore")>]
    let ``invalid arguments fail as JSON before executing`` (arguments: string) =
        let response = invoke repository script None (arguments.Split('|') |> Array.toList)
        Assert.Equal(2, response.ExitCode)
        Assert.Equal("error", response.Output.GetProperty("status").GetString())
        Assert.Equal<string list>([], response.Output.GetProperty("completed") |> strings)
        Assert.False(String.IsNullOrWhiteSpace(response.Output.GetProperty("detail").GetString()))

    [<Fact>]
    let ``missing model provision preserves verifier failure and isolates child stdout`` () =
        use fixture = new Fixture()

        let response =
            invoke
                fixture.Directory
                script
                None
                [
                    "-t"
                    "CheckModels"
                    "--model-dir"
                    "missing model directory"
                    "--manifest"
                    "missing manifest.json"
                ]

        Assert.Equal(2, response.ExitCode)
        Assert.Equal("failed", response.Output.GetProperty("status").GetString())
        Assert.Equal("CheckModels", response.Output.GetProperty("failedTarget").GetString())
        Assert.Equal<string list>([], response.Output.GetProperty("completed") |> strings)
        Assert.Contains("\"status\":\"blocked\"", response.Error)
        Assert.Contains("Required local artifact is missing.", response.Error)

    [<Theory>]
    [<InlineData("All")>]
    [<InlineData("Setup")>]
    let ``restore failure short circuits dependent actions without network`` target =
        use fixture = new Fixture()
        let copiedScript = copyRunner fixture.Directory

        // No solution exists in this fixture: Restore must fail before any dependency runs.
        let response = invoke repository copiedScript None [ "-t"; target ]
        Assert.Equal(1, response.ExitCode)
        Assert.Equal("Restore", response.Output.GetProperty("failedTarget").GetString())
        Assert.Equal<string list>([], response.Output.GetProperty("completed") |> strings)
        Assert.Contains("Rlcd.slnx", response.Error)

    [<Fact>]
    let ``dry run needs neither a solution nor models and creates no build outputs`` () =
        use fixture = new Fixture()
        let copiedScript = copyRunner fixture.Directory
        let response = invoke repository copiedScript None [ "--dry-run" ]
        Assert.Equal(0, response.ExitCode)
        Assert.Equal<string list>([ "Restore"; "Build"; "Tests"; "Package" ], targets response)
        Assert.Empty(Directory.GetDirectories fixture.Directory)
        Assert.Equal(3, Directory.GetFiles(fixture.Directory).Length)

    [<Theory>]
    [<InlineData("Restore", 0)>]
    [<InlineData("Setup", 2)>]
    let ``Setup requires models even after successful offline Restore`` target expectedExit =
        use fixture = new Fixture()
        let copiedScript = copyRunner fixture.Directory
        File.WriteAllText(Path.Combine(fixture.Directory, "Rlcd.slnx"), "<Solution />")

        for relative in [ "scripts/VerifyArtifacts.fsx"; "src/Rlcd/ModelBundle.fs" ] do
            let destination = Path.Combine(fixture.Directory, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(Path.Combine(repository, relative), destination)

        let response =
            invoke
                repository
                copiedScript
                None
                [
                    "-t"
                    target
                    "--model-dir"
                    Path.Combine(Path.GetTempPath(), $"missing models {Guid.NewGuid():N}")
                    "--manifest"
                    Path.Combine(fixture.Directory, "missing manifest.json")
                ]

        Assert.Equal(expectedExit, response.ExitCode)

        Assert.Equal<string list>(
            [ "Restore" ],
            response.Output.GetProperty("completed") |> strings
        )

        if expectedExit = 0 then
            Assert.Equal("succeeded", response.Output.GetProperty("status").GetString())
            Assert.Equal(JsonValueKind.Null, response.Output.GetProperty("failedTarget").ValueKind)
        else
            Assert.Equal("CheckModels", response.Output.GetProperty("failedTarget").GetString())
            Assert.Contains("\"status\":\"blocked\"", response.Error)
