#!/usr/bin/env -S dotnet fsi --exec --
#load "../src/Rlcd/ModelBundle.fs"

open System
open System.IO
open System.Text.Json

open Rlcd

type Options =
    {
        ModelDirectory: string
        ManifestPath: string
        IsIntegrityOnly: bool
    }

let usage =
    "VerifyArtifacts.fsx --model-dir <directory> [--manifest <path>] [--integrity-only]"

let parse arguments =
    let defaultManifest =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../models/laya-english.manifest.json"))

    let rec loop options seen remaining =
        match remaining with
        | "--model-dir" :: value :: rest when not (Set.contains "--model-dir" seen) ->
            loop { options with ModelDirectory = value } (Set.add "--model-dir" seen) rest
        | "--manifest" :: value :: rest when not (Set.contains "--manifest" seen) ->
            loop { options with ManifestPath = value } (Set.add "--manifest" seen) rest
        | "--integrity-only" :: rest when not options.IsIntegrityOnly ->
            loop { options with IsIntegrityOnly = true } seen rest
        | [] when not (String.IsNullOrWhiteSpace options.ModelDirectory) -> options
        | _ -> invalidArg "arguments" usage

    loop
        {
            ModelDirectory = ""
            ManifestPath = defaultManifest
            IsIntegrityOnly = false
        }
        Set.empty
        arguments

let report status integrityVerified detail =
    let payload =
        {|
            schemaVersion = "rlcd-artifact-verification-v1"
            status = status
            integrityVerified = integrityVerified
            nativeCertified = false
            detail = detail
        |}

    printfn "%s" (JsonSerializer.Serialize payload)

let verify options =
    let repository = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
    let directory = Path.GetFullPath options.ModelDirectory

    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    if
        directory.Equals(repository, comparison)
        || directory.StartsWith(repository + string Path.DirectorySeparatorChar, comparison)
    then
        invalidArg "modelDirectory" "Provision model artifacts outside the repository."

    let verified =
        options.ManifestPath |> ModelBundle.readManifest |> ModelBundle.verify directory

    if options.IsIntegrityOnly then
        report
            "integrity-verified"
            true
            $"{verified.BundleId}: native certification was not requested."

        0
    else
        report
            "blocked"
            true
            ("Artifact integrity passed. Native certification is unavailable: no approved cached "
             + "Microsoft.ML.OnnxRuntime package/native harness. No runtime acquisition is performed.")

        2

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let exitCode =
    match arguments with
    | [ "--help" ] ->
        printfn "%s" usage
        0
    | _ ->
        try
            arguments |> parse |> verify
        with
        | :? ArgumentException as error ->
            report "invalid-arguments" false error.Message
            2
        | :? InvalidDataException as error ->
            report "blocked" false error.Message
            2
        | :? IOException as error ->
            report "blocked" false error.Message
            2
        | :? UnauthorizedAccessException as error ->
            report "blocked" false error.Message
            2

exit exitCode
