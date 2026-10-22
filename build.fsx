#!/usr/bin/env -S dotnet fsi --exec --
// Same FAKE libraries as FastCoref.fs; 6.1.3 is provisioned locally, unlike 5.23.1.
#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"

open System
open System.IO

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO.FileSystemOperators

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

let defaultModelsDir =
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) @@ ".cache/rlcd/laya-english"

let modelsDir =
    Environment.environVarOrDefault "RLCD_MODELS_DIR" defaultModelsDir
    |> Path.GetFullPath

let manifest =
    Environment.environVarOrDefault
        "RLCD_MODEL_MANIFEST"
        (repository @@ "models/laya-english.manifest.json")
    |> Path.GetFullPath

// FAKE 6.1.3's internal binlog reader predates SDK 10's format; keep console diagnostics.
Target.create "Restore" (fun _ ->
    DotNet.restore
        (fun options ->
            { options with
                Common = { dotnetOptions options.Common with CustomParams = Some "--locked-mode" }
                ConfigFile = Some (repository @@ "NuGet.Config")
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

Target.create "Tests" (fun _ ->
    DotNet.test
        (fun options ->
            { options with
                Common = dotnetOptions options.Common
                Configuration = DotNet.BuildConfiguration.Release
                NoBuild = true
                NoRestore = true
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

Target.create "CheckModels" (fun _ ->
    CreateProcess.fromRawCommand
        dotnetPath
        [
            "fsi"; "--exec"; "--"; repository @@ "scripts/VerifyArtifacts.fsx"
            "--integrity-only"; "--model-dir"; modelsDir; "--manifest"; manifest
        ]
    |> CreateProcess.withWorkingDirectory repository
    |> CreateProcess.ensureExitCode
    |> Proc.run
    |> ignore)

Target.create "Setup" ignore
Target.create "All" ignore

"Restore" ==> "Build" ==> "Tests" ==> "All" |> ignore
"Build" ==> "Package" ==> "All" |> ignore
"Tests" ?=> "Package" |> ignore
"Setup" <== [ "Restore"; "CheckModels" ] |> ignore
// Order these when both are selected, without making CheckModels restore on its own.
"Restore" ?=> "CheckModels" |> ignore

Target.runOrDefault "All"
