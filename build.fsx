#!/usr/bin/env -S dotnet fsi --exec --

open System
open System.IO
open System.Diagnostics
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks

type Target =
    | Restore
    | Build
    | Tests
    | Package
    | CheckModels
    | Setup
    | All

type Options =
    {
        Target: Target
        IsList: bool
        IsDryRun: bool
        ModelDirectory: string
        ManifestPath: string
    }

type Command =
    {
        Target: string
        FileName: string
        Arguments: string array
        WorkingDirectory: string
    }

type Outcome =
    {
        ExitCode: int
        Completed: string list
        FailedTarget: string option
    }

[<RequireQualifiedAccess>]
module Target =
    let names =
        [ "Restore"; "Build"; "Tests"; "Package"; "CheckModels"; "Setup"; "All" ]

    let parse name =
        let choices = String.Join(", ", names)

        match name with
        | "Restore" -> Restore
        | "Build" -> Build
        | "Tests" -> Tests
        | "Package" -> Package
        | "CheckModels" -> CheckModels
        | "Setup" -> Setup
        | "All" -> All
        | _ -> invalidArg "target" $"Unknown target '{name}'. Choose: {choices}."

    let plan target =
        match target with
        | Restore -> [ Restore ]
        | Build -> [ Restore; Build ]
        | Tests -> [ Restore; Build; Tests ]
        | Package -> [ Restore; Build; Package ]
        | CheckModels -> [ CheckModels ]
        | Setup -> [ Restore; CheckModels ]
        | All -> [ Restore; Build; Tests; Package ]

[<RequireQualifiedAccess>]
module Options =
    let defaults repository =
        let modelDirectory =
            match Environment.GetEnvironmentVariable "RLCD_MODELS_DIR" with
            | value when not (String.IsNullOrWhiteSpace value) -> value
            | _ ->
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache/rlcd/laya-english"
                )

        {
            Target = All
            IsList = false
            IsDryRun = false
            ModelDirectory = modelDirectory
            ManifestPath = Path.Combine(repository, "models/laya-english.manifest.json")
        }

    let private assign key value (options: Options) =
        match key with
        | "-t" ->
            { options with
                Target = Target.parse value
            }
        | "--model-dir" -> { options with ModelDirectory = value }
        | "--manifest" -> { options with ManifestPath = value }
        | "--list" -> { options with IsList = true }
        | "--dry-run" -> { options with IsDryRun = true }
        | _ -> invalidArg "arguments" $"Unknown option '{key}'."

    let parse defaults arguments =
        let rec loop options seen remaining =
            match remaining with
            | [] -> options
            | key :: _ when Set.contains key seen ->
                invalidArg "arguments" $"Duplicate option '{key}'."
            | ("--list" | "--dry-run" as key) :: rest ->
                loop (assign key "" options) (Set.add key seen) rest
            | ("-t" | "--model-dir" | "--manifest" as key) :: value :: rest when
                not (String.IsNullOrWhiteSpace value) && not (value.StartsWith "-")
                ->
                loop (assign key value options) (Set.add key seen) rest
            | key :: _ -> invalidArg "arguments" $"Unknown option or missing value: '{key}'."

        let options = loop defaults Set.empty arguments

        if options.IsList && arguments <> [ "--list" ] then
            invalidArg "arguments" "--list cannot be combined with other options."

        options

    let resolvePaths options =
        { options with
            ModelDirectory = Path.GetFullPath options.ModelDirectory
            ManifestPath = Path.GetFullPath options.ManifestPath
        }

[<RequireQualifiedAccess>]
module Command =
    let create repository options target =
        let arguments =
            match target with
            | Restore ->
                [
                    "restore"
                    "Rlcd.slnx"
                    "--locked-mode"
                    "--configfile"
                    Path.Combine(repository, "NuGet.Config")
                ]
            | Build -> [ "build"; "Rlcd.slnx"; "-c"; "Release"; "--no-restore" ]
            | Tests ->
                [
                    "test"
                    "tests/Rlcd.Tests/Rlcd.Tests.fsproj"
                    "-c"
                    "Release"
                    "--no-build"
                    "--no-restore"
                ]
            | Package ->
                [
                    "pack"
                    "src/Rlcd/Rlcd.fsproj"
                    "-c"
                    "Release"
                    "--no-build"
                    "--no-restore"
                ]
            | CheckModels ->
                [
                    "fsi"
                    "--exec"
                    Path.Combine(repository, "scripts/VerifyArtifacts.fsx")
                    "--"
                    "--integrity-only"
                    "--model-dir"
                    options.ModelDirectory
                    "--manifest"
                    options.ManifestPath
                ]
            | Setup
            | All -> invalidArg "target" "Aggregate targets must be expanded first."

        {
            Target = string target
            FileName = "dotnet"
            Arguments = List.toArray arguments
            WorkingDirectory = repository
        }

    let run command =
        task {
            let start = ProcessStartInfo(command.FileName)
            start.WorkingDirectory <- command.WorkingDirectory
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            command.Arguments |> Array.iter start.ArgumentList.Add
            Console.Error.WriteLine($"[{command.Target}]")
            use child = Process.Start start
            use diagnostics = Console.OpenStandardError()
            // Stream both pipes concurrently with fixed-size buffers, never ReadToEnd.
            let output = child.StandardOutput.BaseStream.CopyToAsync diagnostics
            let error = child.StandardError.BaseStream.CopyToAsync diagnostics
            do! child.WaitForExitAsync()
            do! Task.WhenAll(output, error)
            return child.ExitCode
        }

[<RequireQualifiedAccess>]
module Execution =
    let run commands =
        let rec loop completed remaining =
            task {
                match remaining with
                | [] ->
                    return
                        {
                            ExitCode = 0
                            Completed = List.rev completed
                            FailedTarget = None
                        }
                | command :: rest ->
                    let! exitCode = Command.run command

                    if exitCode = 0 then
                        return! loop (command.Target :: completed) rest
                    else
                        return
                            {
                                ExitCode = exitCode
                                Completed = List.rev completed
                                FailedTarget = Some command.Target
                            }
            }

        loop [] commands

[<RequireQualifiedAccess>]
module Report =
    let private write payload =
        printfn "%s" (JsonSerializer.Serialize payload)

    let writeOutcome outcome =
        write
            {|
                schemaVersion = "rlcd-build-v1"
                status = if outcome.ExitCode = 0 then "succeeded" else "failed"
                exitCode = outcome.ExitCode
                completed = outcome.Completed
                failedTarget = Option.toObj outcome.FailedTarget
            |}

        outcome.ExitCode

    let writePlan (commands: Command list) =
        write
            {|
                schemaVersion = "rlcd-build-v1"
                status = "dry-run"
                commands = commands
            |}

        0

    let writeList () =
        write
            {|
                schemaVersion = "rlcd-build-v1"
                status = "list"
                targets = Target.names
            |}

        0

    let writeError (error: exn) =
        write
            {|
                schemaVersion = "rlcd-build-v1"
                status = "error"
                exitCode = 2
                completed = ([]: string list)
                detail = error.Message
            |}

        2

[<RequireQualifiedAccess>]
module Program =
    let run arguments =
        task {
            try
                let repository = __SOURCE_DIRECTORY__
                let options = arguments |> Options.parse (Options.defaults repository)

                if options.IsList then
                    return Report.writeList ()
                else
                    let resolved = Options.resolvePaths options

                    let commands =
                        options.Target
                        |> Target.plan
                        |> List.map (Command.create repository resolved)

                    if options.IsDryRun then
                        return Report.writePlan commands
                    else
                        let! outcome = Execution.run commands
                        return Report.writeOutcome outcome
            with
            | :? ArgumentException as error -> return Report.writeError error
            | :? IOException as error -> return Report.writeError error
            | :? UnauthorizedAccessException as error -> return Report.writeError error
            | :? Win32Exception as error -> return Report.writeError error
        }

let result = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList |> Program.run
exit (result.GetAwaiter().GetResult())
