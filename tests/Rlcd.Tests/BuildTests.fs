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
        start.Environment.Remove "RLCD_EXAMPLE_STATE" |> ignore

        for key, value in environment do
            start.Environment[key] <- value

        response start

    let private copyRunner directory =
        for name in
            [
                "build.fsx"
                "global.json"
                "src/Rlcd/Domain.fs"
                "src/Rlcd/Questions.fs"
                "src/Rlcd/ModelBundle.fs"
            ] do
            let destination = Path.Combine(directory, name)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(Path.Combine(repository, name), destination)

        Path.Combine(directory, "build.fsx")

    let private acquisitionRunner directory fetchBody identity =
        let path = copyRunner directory
        let extension =
            $$"""
Target.create "FixtureAcquisition" (fun _ ->
    let directory = System.Environment.GetEnvironmentVariable "RLCD_MODELS_DIR"
    let pin: Models.ArtifactPin =
        { Filename = "tokenizer.json"; SizeBytes = 3L; Identity = {{identity}} }
    let source =
        { new Models.IArtifactSource with
            member _.Fetch(artifact, destination) =
                if artifact.Filename <> "tokenizer.json" then invalidOp "Unexpected fixture fetch."
                File.AppendAllText(directory + ".fetches", "fetch\n")
                {{fetchBody}} }
    Models.acquire source [ pin ]
        (fun receipts _ ->
            receipts |> JsonSerializer.Serialize |> printfn "receipts=%s"
            File.WriteAllText(Path.Combine(directory, "published.json"), "ready"))
        directory)
"""
        File.WriteAllText(
            path,
            (File.ReadAllText path).Replace(
                """Target.runOrDefault "All" """.TrimEnd(),
                extension + "\nTarget.runOrDefault \"All\""
            )
        )
        path

    let private fixtureSha256 =
        """Models.Sha256 "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" """
            .TrimEnd()

    let private fixtureGitBlob =
        """Models.GitBlob "f2ba8f84ab5c1bce84a7b441cb1959cfc7093b7f" """.TrimEnd()

    let private restoreProject directory version =
        let project = Path.Combine(directory, "RestoreFixture.fsproj")
        File.WriteAllText(
            project,
            $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <DisableTransitiveFrameworkReferenceDownloads>true</DisableTransitiveFrameworkReferenceDownloads>
    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="FSharp.Core" Version="{version}" /></ItemGroup>
</Project>"""
        )
        File.WriteAllText(
            Path.Combine(directory, "Rlcd.slnx"),
            """<Solution><Project Path="RestoreFixture.fsproj" /></Solution>"""
        )

    let private payload (output: string) =
        let json =
            output.Split('\n')
            |> Array.tryFind (fun line -> line.StartsWith("{", StringComparison.Ordinal))

        let text =
            json
            |> Option.defaultWith (fun () ->
                failwith $"Expected a JSON payload amid FAKE diagnostics.\n{output}")
        use document = JsonDocument.Parse text
        document.RootElement.Clone()

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

            let targets =
                [
                    "Restore"; "UpdateLocks"; "Build"; "Tests"; "CheckRuntime"; "CheckInference"; "Package"
                    "DownloadModels"; "CheckModels"; "VerifyModels"; "Example"; "Setup"; "All"
                ]

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
            Assert.Equal<string list>([], hardDependencies["UpdateLocks"])
            Assert.Equal<string list>([ "Restore" ], hardDependencies["Build"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["Tests"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["CheckRuntime"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["CheckInference"])
            Assert.Equal<string list>([ "Build" ], hardDependencies["Package"])
            Assert.Equal<string list>([ "Package"; "Tests" ], List.sort hardDependencies["All"])
            Assert.Equal<string list>([ "Tests" ], softDependencies["Package"])
            Assert.Equal<string list>([], hardDependencies["CheckModels"])
            Assert.Equal<string list>([], hardDependencies["DownloadModels"])
            Assert.Equal<string list>([], hardDependencies["VerifyModels"])
            Assert.Equal<string list>([], hardDependencies["Example"])
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

    [<Theory>]
    [<InlineData("CheckModels")>]
    [<InlineData("VerifyModels")>]
    let ``model targets own local verification with environment paths containing spaces`` target =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            let manifest = Path.Combine(fixture.Directory, "missing manifest.json")
            // Models must be outside even the isolated runner's repository.
            let models = Path.Combine(Path.GetTempPath(), $"missing models {Guid.NewGuid():N}")

            let! exitCode, output =
                invoke
                    repository
                    copiedScript
                    [ "RLCD_MODELS_DIR", models; "RLCD_MODEL_MANIFEST", manifest ]
                    [ "-t"; target ]

            Assert.Equal(1, exitCode)
            Assert.Contains(models, output)
            Assert.Contains(manifest, output)
            let result = payload output
            Assert.Equal("blocked", result.GetProperty("status").GetString())
            Assert.False(result.GetProperty("integrityVerified").GetBoolean())
            Assert.False(result.GetProperty("nativeCertified").GetBoolean())
            Assert.Contains("Required local artifact is missing.", output)
        }

    [<Theory>]
    [<InlineData(".")>]
    [<InlineData("models")>]
    let ``CheckModels rejects artifacts inside its repository`` relative =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            let models = Path.GetFullPath(Path.Combine(fixture.Directory, relative))
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ] [ "-t"; "CheckModels" ]

            Assert.Equal(1, exitCode)
            let result = payload output
            Assert.Equal("invalid-arguments", result.GetProperty("status").GetString())
            Assert.False(result.GetProperty("nativeCertified").GetBoolean())
            Assert.Contains("outside the repository", result.GetProperty("detail").GetString())
        }

    [<Theory>]
    [<InlineData("CheckModels")>]
    [<InlineData("VerifyModels")>]
    let ``model checks default to the selected external cache manifest`` target =
        task {
            use fixture = new Fixture()
            let models = Path.Combine(fixture.Directory, "absent cache")
            let! exitCode, output =
                invoke repository script [ "RLCD_MODELS_DIR", models ] [ "-t"; target ]

            Assert.Equal(1, exitCode)
            Assert.Contains(Path.Combine(models, "laya-english.manifest.json"), output)
            Assert.False((payload output).GetProperty("integrityVerified").GetBoolean())
        }

    [<Fact>]
    let ``DownloadModels rejects repository cache before fetching`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            let! exitCode, output =
                invoke repository copiedScript
                    [ "RLCD_MODELS_DIR", Path.Combine(fixture.Directory, "models") ]
                    [ "-t"; "DownloadModels" ]

            Assert.Equal(1, exitCode)
            Assert.Contains("outside the repository", output)
            Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "models")))
        }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``acquisition verifies identity and reuses valid cached bytes`` gitBlob =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let identity = if gitBlob then fixtureGitBlob else fixtureSha256
            let copiedScript =
                acquisitionRunner runner.Directory
                    """File.WriteAllText(destination, "abc")""" identity
            let models = Path.Combine(fixture.Directory, "cache")
            let invoke () =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            let! firstCode, firstOutput = invoke ()
            Assert.True((firstCode = 0), firstOutput)
            Assert.Equal("abc", File.ReadAllText(Path.Combine(models, "tokenizer.json")))
            Assert.Contains(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                firstOutput
            )
            let cachedTime = File.GetLastWriteTimeUtc(Path.Combine(models, "tokenizer.json"))
            let! secondCode, secondOutput = invoke ()
            Assert.True((secondCode = 0), secondOutput)
            Assert.Contains("Reusing verified", secondOutput)
            Assert.Equal("fetch\n", File.ReadAllText(models + ".fetches"))
            Assert.Equal(
                cachedTime,
                File.GetLastWriteTimeUtc(Path.Combine(models, "tokenizer.json"))
            )
            Assert.Empty(Directory.GetDirectories models)
        }

    [<Theory>]
    [<InlineData("ab", "byte count")>]
    [<InlineData("abd", "SHA-256")>]
    let ``acquisition diagnoses and repairs corrupt cache without touching unrelated files``
        (corrupt: string) diagnostic =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let copiedScript =
                acquisitionRunner runner.Directory
                    """File.WriteAllText(destination, "abc")""" fixtureSha256
            let models = Path.Combine(fixture.Directory, "cache")
            Directory.CreateDirectory models |> ignore
            File.WriteAllText(Path.Combine(models, "tokenizer.json"), corrupt)
            File.WriteAllText(Path.Combine(models, "unrelated.txt"), "keep me")
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            Assert.True((exitCode = 0), output)
            Assert.Contains(diagnostic, output)
            Assert.Equal("abc", File.ReadAllText(Path.Combine(models, "tokenizer.json")))
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(models, "unrelated.txt")))
        }

    [<Theory>]
    [<InlineData("""File.WriteAllText(destination, "ab")""", "byte count", false)>]
    [<InlineData("""File.WriteAllText(destination, "abd")""", "SHA-256", false)>]
    [<InlineData("""File.WriteAllText(destination, "abd")""", "Git blob SHA-1", true)>]
    [<InlineData(
        """File.WriteAllText(destination, "a"); raise (IOException "Interrupted fixture fetch.")""",
        "Interrupted fixture fetch",
        false
    )>]
    let ``failed acquisition leaves old cache intact and cleans unpublished staging``
        fetchBody diagnostic gitBlob =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let identity = if gitBlob then fixtureGitBlob else fixtureSha256
            let copiedScript = acquisitionRunner runner.Directory fetchBody identity
            let models = Path.Combine(fixture.Directory, "cache")
            Directory.CreateDirectory models |> ignore
            let artifact = Path.Combine(models, "tokenizer.json")
            File.WriteAllText(artifact, "old")
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            Assert.Equal(1, exitCode)
            Assert.Contains(diagnostic, output)
            Assert.Equal("old", File.ReadAllText artifact)
            Assert.False(File.Exists(Path.Combine(models, "published.json")))
            Assert.Empty(Directory.GetDirectories models)
            Assert.Equal<string array>([| artifact |], Directory.GetFiles models)
        }

    [<Fact>]
    let ``acquisition rejects a live lock without fetching or deleting it`` () =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let copiedScript =
                acquisitionRunner runner.Directory
                    """File.WriteAllText(destination, "abc")""" fixtureSha256
            let models = Path.Combine(fixture.Directory, "cache")
            Directory.CreateDirectory models |> ignore
            let lockPath = Path.Combine(models, ".laya-english.download.lock")
            use held =
                new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            Assert.Equal(1, exitCode)
            Assert.Contains("lock", output)
            Assert.True(File.Exists lockPath)
            Assert.False(File.Exists(models + ".fetches"))
        }

    [<Fact>]
    let ``acquisition refuses an unrelated cache manifest before fetching`` () =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let copiedScript =
                acquisitionRunner runner.Directory
                    """File.WriteAllText(destination, "abc")""" fixtureSha256
            let models = Path.Combine(fixture.Directory, "cache")
            Directory.CreateDirectory models |> ignore
            let manifest = Path.Combine(models, "laya-english.manifest.json")
            File.WriteAllText(manifest, """{"unrelated":true}""")
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            Assert.True((exitCode = 1), output)
            Assert.Equal("""{"unrelated":true}""", File.ReadAllText manifest)
            Assert.False(File.Exists(models + ".fetches"))
            Assert.False(File.Exists(Path.Combine(models, "published.json")))
        }

    [<Theory>]
    [<InlineData("root")>]
    [<InlineData("ancestor")>]
    [<InlineData("artifact")>]
    [<InlineData("dangling")>]
    [<InlineData("file-root")>]
    [<InlineData("manifest")>]
    [<InlineData("lock")>]
    [<InlineData("directory-artifact")>]
    let ``acquisition rejects unsafe cache paths before fetching`` location =
        task {
            use runner = new Fixture()
            use fixture = new Fixture()
            let copiedScript =
                acquisitionRunner runner.Directory
                    """File.WriteAllText(destination, "abc")""" fixtureSha256
            let root = Path.Combine(fixture.Directory, "cache")
            let other = Path.Combine(fixture.Directory, "other")
            Directory.CreateDirectory other |> ignore
            let models =
                match location with
                | "root" ->
                    Directory.CreateSymbolicLink(root, other) |> ignore
                    root
                | "ancestor" ->
                    Directory.CreateSymbolicLink(root, other) |> ignore
                    Path.Combine(root, "child")
                | "file-root" ->
                    File.WriteAllText(root, "keep me")
                    root
                | _ ->
                    Directory.CreateDirectory root |> ignore
                    let target = Path.Combine(other, "target")
                    if location = "artifact" then File.WriteAllText(target, "abc")
                    let filename =
                        match location with
                        | "manifest" -> "laya-english.manifest.json"
                        | "lock" -> ".laya-english.download.lock"
                        | _ -> "tokenizer.json"
                    let path = Path.Combine(root, filename)
                    if location = "directory-artifact" then
                        Directory.CreateDirectory path |> ignore
                    else
                        File.CreateSymbolicLink(path, target) |> ignore
                    root
            let! exitCode, output =
                invoke repository copiedScript [ "RLCD_MODELS_DIR", models ]
                    [ "-t"; "FixtureAcquisition" ]
            Assert.Equal(1, exitCode)
            Assert.Contains("cache", output.ToLowerInvariant())
            Assert.False(File.Exists(models + ".fetches"))
            Assert.False(File.Exists(Path.Combine(models, "published.json")))
        }

    [<Fact>]
    let ``Example constructs all question kinds without prior build outputs`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            let! exitCode, output = invoke repository copiedScript [] [ "-t"; "Example" ]
            Assert.True((exitCode = 0), output)
            let result = payload output
            Assert.Equal("constructed", result.GetProperty("status").GetString())
            Assert.False(result.GetProperty("inferenceAvailable").GetBoolean())
            Assert.Equal(
                "The customer was charged twice and asks when the refund will arrive.",
                result.GetProperty("state").GetString()
            )
            let questions = result.GetProperty("questions").EnumerateArray() |> Seq.toList
            Assert.Equal<string list>(
                [ "Choice"; "Score"; "Boolean" ],
                questions |> List.map (fun question -> question.GetProperty("kind").GetString())
            )
            Assert.Equal<int list>(
                [ 2; 3; 2 ],
                questions
                |> List.map (fun question -> question.GetProperty("options").GetArrayLength())
            )
            Assert.Equal("Reject", result.GetProperty("truncationPolicy").GetString())
            Assert.Equal(512, result.GetProperty("budget").GetProperty("sequenceLength").GetInt32())
        }

    [<Fact>]
    let ``Example preserves caller state supplied by environment`` () =
        task {
            use fixture = new Fixture()
            let state = "  Customer said \"help\".\nAccès bloqué.  "
            let! exitCode, output =
                invoke fixture.Directory script [ "RLCD_EXAMPLE_STATE", state ] [ "-t"; "example" ]

            Assert.True((exitCode = 0), output)
            Assert.Equal(state, (payload output).GetProperty("state").GetString())
        }

    [<Theory>]
    [<InlineData("")>]
    [<InlineData(" \n\t")>]
    let ``Example rejects invalid caller state rather than substituting its default`` state =
        task {
            let! exitCode, output =
                invoke repository script [ "RLCD_EXAMPLE_STATE", state ] [ "-t"; "Example" ]

            Assert.Equal(1, exitCode)
            let result = payload output
            Assert.Equal("invalid-request", result.GetProperty("status").GetString())
            Assert.False(result.GetProperty("inferenceAvailable").GetBoolean())
            let errors = result.GetProperty("errors")
            Assert.Contains("InvalidText", errors[0].GetString())
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
    [<InlineData("CheckInference")>]
    let ``test inference and pack targets address the solution`` target =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            // Missing inputs reveal the real SDK argument without running a nested suite.
            let! exitCode, output =
                invoke repository copiedScript [] [ "--single-target"; "-t"; target ]

            Assert.NotEqual(0, exitCode)
            Assert.Contains("MSB1009", output)
            Assert.Contains("Switch: Rlcd.slnx", output)
            if target = "Tests" then
                Assert.Contains("Category!=RealInference", output)
            if target = "CheckInference" then
                Assert.Contains("Category=RealInference", output)
        }

    [<Fact>]
    let ``Restore uses normal sources without a repository config`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            restoreProject fixture.Directory "[10.1.302]"
            let! exitCode, output =
                invoke repository copiedScript [] [ "-t"; "Restore" ]

            Assert.True((exitCode = 0), output)
            Assert.True(File.Exists(Path.Combine(fixture.Directory, "packages.lock.json")))
        }

    [<Fact>]
    let ``only UpdateLocks accepts an intentional package requirement change`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            restoreProject fixture.Directory "[10.1.302]"
            // Keep this graph-mismatch test local; this is not a repository source policy.
            let cache =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget/packages"
                )
                |> System.Security.SecurityElement.Escape
            File.WriteAllText(
                Path.Combine(fixture.Directory, "NuGet.Config"),
                $"""<configuration>
  <packageSources>
    <clear/>
    <add key="cache" value="{cache}"/>
  </packageSources>
</configuration>"""
            )
            let! initialCode, initialOutput =
                invoke repository copiedScript [] [ "-t"; "UpdateLocks" ]
            Assert.True((initialCode = 0), initialOutput)
            let lockPath = Path.Combine(fixture.Directory, "packages.lock.json")
            let originalLock = File.ReadAllText lockPath
            restoreProject fixture.Directory "10.1.302"

            let! lockedCode, lockedOutput =
                invoke repository copiedScript [] [ "-t"; "Restore" ]
            Assert.NotEqual(0, lockedCode)
            Assert.Contains("NU1004", lockedOutput)
            Assert.Equal(originalLock, File.ReadAllText lockPath)

            let! refreshCode, refreshOutput =
                invoke repository copiedScript [] [ "-t"; "UpdateLocks" ]
            Assert.True((refreshCode = 0), refreshOutput)
            Assert.NotEqual<string>(originalLock, File.ReadAllText lockPath)

            let! restoredCode, restoredOutput =
                invoke repository copiedScript [] [ "-t"; "Restore" ]
            Assert.True((restoredCode = 0), restoredOutput)
        }

    [<Fact>]
    let ``Setup fails model verification after successful Restore`` () =
        task {
            use fixture = new Fixture()
            let copiedScript = copyRunner fixture.Directory
            File.WriteAllText(Path.Combine(fixture.Directory, "Rlcd.slnx"), "<Solution />")

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
