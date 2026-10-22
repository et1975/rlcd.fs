namespace Rlcd.Tests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading

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

    let private dotnetStartInfo workingDirectory arguments =
        let start = ProcessStartInfo("dotnet")
        start.WorkingDirectory <- workingDirectory
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        for argument in arguments do
            start.ArgumentList.Add argument

        start

    let private response start =
        task {
            use child = Process.Start(start: ProcessStartInfo)
            let output = child.StandardOutput.ReadToEndAsync()
            let error = child.StandardError.ReadToEndAsync()
            use deadline = new CancellationTokenSource(TimeSpan.FromMinutes 2.0)

            try
                do! child.WaitForExitAsync deadline.Token
            with :? OperationCanceledException ->
                child.Kill true
                do! child.WaitForExitAsync()

            let! text = output
            let! diagnostic = error
            Assert.False(
                deadline.IsCancellationRequested,
                "Child exceeded the two-minute deadline."
            )
            return child.ExitCode, text + diagnostic
        }

    let private invoke workingDirectory scriptPath environment arguments =
        let start =
            dotnetStartInfo workingDirectory ([ "fsi"; "--exec"; "--"; scriptPath ] @ arguments)

        start.Environment.Remove "RLCD_MODELS_DIR" |> ignore
        start.Environment.Remove "RLCD_MODEL_MANIFEST" |> ignore

        for key, value in environment do
            start.Environment[key] <- value

        response start

    let private copyRunner directory =
        for name in [ "build.fsx"; "global.json"; "NuGet.Config" ] do
            File.Copy(Path.Combine(repository, name), Path.Combine(directory, name))

        Path.Combine(directory, "build.fsx")

    let private frameworkReferences targetingPackRoot properties projectPath =
        task {
            let arguments =
                [
                    "msbuild"
                    projectPath
                    "-nologo"
                    "-target:ProcessFrameworkReferences"
                    "-getItem:FrameworkReference,PackageDownload"
                    $"-p:NetCoreTargetingPackRoot={targetingPackRoot}"
                ]
                @ properties

            let! exitCode, output = response (dotnetStartInfo repository arguments)
            Assert.True((exitCode = 0), $"MSBuild exited {exitCode}.\n{output}")
            use document = JsonDocument.Parse output
            return document.RootElement.Clone()
        }

    let private itemIdentities (itemName: string) (output: JsonElement) =
        output.GetProperty("Items").GetProperty(itemName).EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("Identity").GetString())
        |> Seq.toList

    [<Theory>]
    [<InlineData("src/Rlcd/Rlcd.fsproj")>]
    [<InlineData("tests/Rlcd.Tests/Rlcd.Tests.fsproj")>]
    let ``missing framework packs request only directly referenced frameworks`` projectPath =
        task {
            // An empty pack root makes this independent of globally installed targeting packs.
            use fixture = new Fixture()

            let! control =
                frameworkReferences
                    fixture.Directory
                    [ "-p:DisableTransitiveFrameworkReferenceDownloads=false" ]
                    projectPath

            Assert.Equal<string list>(
                [ "Microsoft.NETCore.App" ],
                itemIdentities "FrameworkReference" control
            )

            Assert.Contains(
                "Microsoft.AspNetCore.App.Ref",
                itemIdentities "PackageDownload" control
            )
            Assert.Contains("Microsoft.NETCore.App.Ref", itemIdentities "PackageDownload" control)

            let! configured = frameworkReferences fixture.Directory [] projectPath

            Assert.Equal<string list>(
                [ "Microsoft.NETCore.App" ],
                itemIdentities "FrameworkReference" configured
            )

            Assert.Equal<string list>(
                [ "Microsoft.NETCore.App.Ref" ],
                itemIdentities "PackageDownload" configured
            )
        }

    [<Fact>]
    let ``help reaches FAKE instead of FSI from an external working directory`` () =
        task {
            use fixture = new Fixture()
            let! exitCode, output = invoke fixture.Directory script [] [ "--help" ]
            Assert.True((exitCode = 0), output)
            Assert.Contains("Target Module options", output)
            Assert.Contains("--single-target", output)
        }

    [<Fact>]
    let ``list exposes the registered build targets without executing them`` () =
        task {
            use fixture = new Fixture()
            let! exitCode, output = invoke fixture.Directory script [] [ "--list" ]
            Assert.True((exitCode = 0), output)
            Assert.Contains("targets are available", output)

            let targets = [ "Restore"; "Build"; "Tests"; "Package"; "CheckModels"; "Setup"; "All" ]

            for target in targets do
                Assert.Contains(target, output)
        }

    [<Fact>]
    let ``FAKE graph keeps Package independent of Tests and orders both under All`` () =
        task {
            use fixture = new Fixture()
            let info = Path.Combine(fixture.Directory, "targets.json")
            let! exitCode, output = invoke repository script [] [ "--write-info"; info ]
            Assert.True((exitCode = 0), output)
            use document = JsonDocument.Parse(File.ReadAllText info)

            let dependencies (property: string) =
                document.RootElement.GetProperty("targets").EnumerateArray()
                |> Seq.map (fun target ->
                    target.GetProperty("name").GetString(),
                    (target.GetProperty(property).EnumerateArray()
                     |> Seq.map (fun dependency -> dependency.GetProperty("name").GetString())
                     |> Seq.toList))
                |> Map.ofSeq

            let hardDependencies = dependencies "hardDependencies"
            let softDependencies = dependencies "softDependencies"

            Assert.Equal<string list>([], hardDependencies["Restore"])
            Assert.Equal<string list>([ "Restore" ], hardDependencies["Build"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["Tests"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["Package"])
            Assert.Equal<string list>([ "Package"; "Tests" ], List.sort hardDependencies["All"])
            Assert.Equal<string list>([ "Tests" ], softDependencies["Package"])
            Assert.Equal<string list>(
                [ "CheckModels"; "Restore" ],
                List.sort hardDependencies["Setup"]
            )
        }

    [<Theory>]
    [<InlineData("--unknown")>]
    [<InlineData("-t")>]
    [<InlineData("-t|NoSuchTarget")>]
    let ``invalid CLI fails without running a target`` (arguments: string) =
        task {
            let! exitCode, output =
                invoke repository script [] (arguments.Split('|') |> Array.toList)

            Assert.NotEqual(0, exitCode)
            Assert.DoesNotContain("Starting target", output)
        }

    [<Fact>]
    let ``CheckModels forwards environment paths with spaces and fails on missing inputs`` () =
        task {
            use fixture = new Fixture()
            let modelDirectory = Path.Combine(fixture.Directory, "missing models")
            let manifest = Path.Combine(fixture.Directory, "missing manifest.json")

            let! exitCode, output =
                invoke
                    fixture.Directory
                    script
                    [ "RLCD_MODELS_DIR", modelDirectory; "RLCD_MODEL_MANIFEST", manifest ]
                    [ "-t"; "CheckModels" ]

            Assert.NotEqual(0, exitCode)
            Assert.Contains(modelDirectory, output)
            Assert.Contains(manifest, output)
            Assert.Contains("\"status\":\"blocked\"", output)
            Assert.Contains("Required local artifact is missing.", output)
        }

    [<Fact>]
    let ``failed real restore stops Build in an isolated repository`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            // No solution exists here. This exercises DotNet.restore's real failure handling.
            let! exitCode, output = invoke repository copiedScript [] [ "-t"; "Build" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("MSB1009", output)
            Assert.Contains("Rlcd.slnx", output)
            Assert.DoesNotContain("Starting target 'Build'", output)
        }

    [<Theory>]
    [<InlineData("Tests")>]
    [<InlineData("Package")>]
    let ``test and pack targets address the solution`` target =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            // Missing inputs reveal the real SDK argument without running a nested suite.
            let! exitCode, output =
                invoke repository copiedScript [] [ "--single-target"; "-t"; target ]

            Assert.NotEqual(0, exitCode)
            Assert.Contains("MSB1009", output)
            Assert.Contains("Switch: Rlcd.slnx", output)
        }

    [<Fact>]
    let ``Setup fails model verification after successful offline Restore`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            File.WriteAllText(Path.Combine(fixture.Directory, "Rlcd.slnx"), "<Solution />")

            for relative in [ "scripts/VerifyArtifacts.fsx"; "src/Rlcd/ModelBundle.fs" ] do
                let destination = Path.Combine(fixture.Directory, relative)
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                File.Copy(Path.Combine(repository, relative), destination)

            let models = Path.Combine(Path.GetTempPath(), $"missing {Guid.NewGuid():N}")
            let manifest = Path.Combine(fixture.Directory, "missing manifest.json")

            let! exitCode, output =
                invoke
                    repository
                    copiedScript
                    [ "RLCD_MODELS_DIR", models; "RLCD_MODEL_MANIFEST", manifest ]
                    [ "-t"; "Setup" ]

            Assert.NotEqual(0, exitCode)
            Assert.Contains("Finished (Success) 'Restore'", output)
            Assert.Contains("\"status\":\"blocked\"", output)
            Assert.Contains("CheckModels", output)
        }
