namespace Rlcd.Router

open System

/// Task requirement tiers, not claims about a named model's performance.
[<RequireQualifiedAccess>]
type Tier =
    /// Bounded mechanical work or straightforward explanation.
    | Economy
    /// Routine implementation or debugging with connected decisions.
    | Balanced
    /// Ambiguous diagnosis, architecture, or difficult interacting reasoning.
    | Deep

/// Input modalities declared by the caller and catalog maintainer.
[<RequireQualifiedAccess>]
type Modality =
    /// Text input.
    | Text
    /// Image input.
    | Image
    /// Audio input.
    | Audio
    /// Video input.
    | Video

/// An exact model identifier, without whitespace or control characters.
type ModelId = private ModelId of string

/// Positive generation-model tokens; unrelated to the classifier's input budget.
type GenerationTokens = private GenerationTokens of int

/// Invalid construction input, distinct from an ordinary policy abstention.
[<RequireQualifiedAccess>]
type ValidationError =
    /// Identifier is empty or contains whitespace/control characters.
    | InvalidModelId
    /// Generation token quantity is not positive.
    | InvalidGenerationTokens of int
    /// No input modalities were declared.
    | EmptyModalities
    /// Individual capacities exceed context, or prompt and output cannot coexist.
    | InvalidCapabilities
    /// Catalog schema version is not supported.
    | UnsupportedCatalogVersion of string
    /// Catalog has no configured entries.
    | EmptyCatalog
    /// A model ID appears more than once, even if the entries have different tiers.
    | DuplicateModelId of ModelId
    /// A probability label is not an exact supported tier name.
    | InvalidTierName of string
    /// Distribution does not contain each tier exactly once.
    | InvalidDistributionTiers
    /// A probability is non-finite or outside [0,1].
    | InvalidProbability of Tier
    /// Probability sum differs from one by more than 1e-9.
    | InvalidProbabilitySum of float
    /// Confidence or margin threshold is non-finite or outside [0,1].
    | InvalidThresholds

/// Validated model identifiers; identity is case-sensitive and never normalized.
[<RequireQualifiedAccess>]
module ModelId =
    /// Reject empty identifiers and embedded whitespace/control characters.
    let create (raw: string) : Result<ModelId, ValidationError> =
        if
            String.IsNullOrWhiteSpace raw
            || raw |> Seq.exists (fun character ->
                Char.IsWhiteSpace character || Char.IsControl character)
        then
            Error ValidationError.InvalidModelId
        else
            Ok (ModelId raw)

    /// Return the exact configured identifier.
    let value (identifier: ModelId) : string =
        let (ModelId raw) = identifier
        raw

/// Positive token quantities for generation requirements and capabilities.
[<RequireQualifiedAccess>]
module GenerationTokens =
    /// Validate a positive token quantity.
    let create (count: int) : Result<GenerationTokens, ValidationError> =
        if count <= 0 then
            Error(ValidationError.InvalidGenerationTokens count)
        else
            Ok (GenerationTokens count)

    /// Return the generation token quantity.
    let value (tokens: GenerationTokens) : int =
        let (GenerationTokens count) = tokens
        count

/// Explicit generation requirements, never inferred from task prose.
type Requirements =
    private
        {
            Tools: bool
            Prompt: GenerationTokens
            Output: GenerationTokens
            Modalities: Set<Modality>
        }

/// Validated generation requirements.
[<RequireQualifiedAccess>]
module Requirements =
    /// Require nonempty input modalities and explicit positive token quantities.
    let create tools prompt output modalities : Result<Requirements, ValidationError> =
        if Set.isEmpty modalities then
            Error ValidationError.EmptyModalities
        else
            Ok
                {
                    Tools = tools
                    Prompt = prompt
                    Output = output
                    Modalities = modalities
                }

    /// Whether tool support is required.
    let tools (requirements: Requirements) : bool = requirements.Tools

    /// Minimum supported prompt tokens.
    let prompt (requirements: Requirements) : GenerationTokens = requirements.Prompt

    /// Minimum supported output tokens.
    let output (requirements: Requirements) : GenerationTokens = requirements.Output

    /// All required input modalities.
    let modalities (requirements: Requirements) : Set<Modality> = requirements.Modalities
