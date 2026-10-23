namespace Rlcd.Router

open System
open System.Security.Cryptography

/// Caller-maintained generation capabilities; metadata is not an entitlement claim.
type Capabilities =
    private
        {
            Tools: bool
            MaxPrompt: GenerationTokens
            MaxOutput: GenerationTokens
            ContextWindow: GenerationTokens
            Modalities: Set<Modality>
        }

/// Capability validation and requirement comparisons.
[<RequireQualifiedAccess>]
module Capabilities =
    let internal revisionFields (capabilities: Capabilities) =
        [
            string capabilities.Tools
            string (GenerationTokens.value capabilities.MaxPrompt)
            string (GenerationTokens.value capabilities.MaxOutput)
            string (GenerationTokens.value capabilities.ContextWindow)
            capabilities.Modalities |> Set.toList |> List.map string |> String.concat ","
        ]

    /// Individual maxima must fit the shared context, which must allow prompt and output.
    let create tools maxPrompt maxOutput contextWindow modalities =
        if Set.isEmpty modalities then
            Error ValidationError.EmptyModalities
        elif
            GenerationTokens.value contextWindow < 2
            || GenerationTokens.value maxPrompt > GenerationTokens.value contextWindow
            || GenerationTokens.value maxOutput > GenerationTokens.value contextWindow
        then
            Error ValidationError.InvalidCapabilities
        else
            Ok
                {
                    Tools = tools
                    MaxPrompt = maxPrompt
                    MaxOutput = maxOutput
                    ContextWindow = contextWindow
                    Modalities = modalities
                }

    /// Match tools, all input modalities, individual limits, and shared context.
    let supports (requirements: Requirements) (capabilities: Capabilities) : bool =
        let prompt = requirements |> Requirements.prompt |> GenerationTokens.value
        let output = requirements |> Requirements.output |> GenerationTokens.value

        (not (Requirements.tools requirements) || capabilities.Tools)
        && Set.isSubset (Requirements.modalities requirements) capabilities.Modalities
        && prompt <= GenerationTokens.value capabilities.MaxPrompt
        && output <= GenerationTokens.value capabilities.MaxOutput
        && int64 prompt + int64 output <= int64 (GenerationTokens.value capabilities.ContextWindow)

/// A catalog entry composed only of validated values.
type CatalogModel =
    {
        /// Exact model ID, not a provider endpoint.
        Id: ModelId
        /// The tier this entry can satisfy; no cross-tier fallback is implied.
        Tier: Tier
        /// Locally maintained generation capabilities.
        Capabilities: Capabilities
    }

/// A nonempty ordered allowlist with a supported schema version and unique IDs.
type Catalog =
    private
        {
            Version: string
            Models: CatalogModel list
        }

/// Pure catalog construction and intersection; no file or provider access.
[<RequireQualifiedAccess>]
module Catalog =
    /// Validate the v1 catalog while retaining the supplied model preference order.
    let create version models : Result<Catalog, ValidationError> =
        let duplicate =
            models |> List.countBy _.Id |> List.tryFind (fun (_, count) -> count > 1)

        if version <> "router-catalog-v1" then
            Error(ValidationError.UnsupportedCatalogVersion version)
        elif List.isEmpty models then
            Error ValidationError.EmptyCatalog
        else
            match duplicate with
            | Some (identifier, _) -> Error(ValidationError.DuplicateModelId identifier)
            | None -> Ok { Version = version; Models = models }

    /// Return the supported catalog schema version.
    let version (catalog: Catalog) : string = catalog.Version

    /// Return the immutable configured preference order.
    let models (catalog: Catalog) : CatalogModel list = catalog.Models

    /// SHA-256 content revision over schema and ordered entries, including all capabilities.
    /// Fields are length-prefixed strings hashed as exact little-endian UTF-16 code units.
    /// No Unicode normalization or replacement is performed.
    let revision (catalog: Catalog) : string =
        let fields =
            catalog.Version
            :: (catalog.Models
                |> List.collect (fun model ->
                    [ ModelId.value model.Id; string model.Tier ]
                    @ Capabilities.revisionFields model.Capabilities))

        fields
        |> List.map (fun field -> $"{field.Length}:{field}")
        |> String.concat ""
        |> Seq.collect (fun character ->
            let code = uint16 character
            [ byte (code &&& 0xffus); byte (code >>> 8) ])
        |> Seq.toArray
        |> SHA256.HashData
        |> Convert.ToHexStringLower
        |> fun digest -> $"sha256:{digest}"

    /// Keep only the specified tier's available, capability-compatible entries, in order.
    let eligible tier available requirements (catalog: Catalog) : CatalogModel list =
        catalog.Models
        |> List.filter (fun model ->
            model.Tier = tier
            && Set.contains model.Id available
            && Capabilities.supports requirements model.Capabilities)
