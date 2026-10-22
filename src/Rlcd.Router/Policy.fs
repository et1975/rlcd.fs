namespace Rlcd.Router

/// Explicit evidence supplied by composition. Unknown status must be represented as false.
type EvidenceStatus =
    {
        /// Composition has established that the evidence is supported English.
        SupportedEnglish: bool
        /// Classifier input fits its certified budget, not the generation-model context.
        WithinBudget: bool
        /// No relevant classifier input was truncated.
        NotTruncated: bool
        /// No classification options collapsed.
        NotCollapsed: bool
    }

/// A fixed user choice always outranks an automatic recommendation.
[<RequireQualifiedAccess>]
type ModelPreference =
    /// Automatic selection is permitted within the explicit catalog and availability set.
    | Automatic
    /// Preserve this exact user choice, whether currently available or not.
    | Fixed of ModelId

/// Validated values and explicit caller evidence needed for pure selection.
type SelectionRequest =
    {
        /// Precomputed tier distribution; no task prose is accepted here.
        Distribution: TierDistribution
        /// Caller-supplied safety evidence; no language detection is performed.
        Evidence: EvidenceStatus
        /// Generation-model requirements.
        Requirements: Requirements
        /// Caller-known available IDs, intersected with the catalog.
        AvailableModelIds: Set<ModelId>
        /// Explicit user preference, if any.
        Preference: ModelPreference
    }

/// Ordinary reasons to decline a recommendation, not construction errors.
[<RequireQualifiedAccess>]
type AbstentionReason =
    /// No substitute is recommended for an explicit choice.
    | PreserveExplicitChoice of ModelId
    /// Supported English input was not established.
    | UnsupportedEnglish
    /// Certified classifier-budget compliance was not established.
    | OverBudgetOrUnknown
    /// Absence of input truncation was not established.
    | TruncatedOrUnknown
    /// Distinguishable classification options were not established.
    | CollapsedOrUnknown
    /// The two largest probabilities are equal.
    | TiedTiers
    /// Maximum probability is below the configured inclusive confidence threshold.
    | BelowConfidence of actual: float * required: float
    /// Top-two probability difference is below the configured inclusive margin threshold.
    | BelowMargin of actual: float * required: float
    /// No available, capable catalog model exists in the selected tier.
    | NoEligibleModel of Tier

/// A concrete eligible model ID or an explicit abstention; never a dispatch command.
[<RequireQualifiedAccess>]
type SelectionDecision =
    /// Recommend this concrete eligible catalog model.
    | Select of ModelId
    /// Decline to recommend a replacement model.
    | Abstain of AbstentionReason

/// Conservative pure selection over caller-supplied evidence and metadata.
[<RequireQualifiedAccess>]
module Policy =
    let private evidence (status: EvidenceStatus) =
        if not status.SupportedEnglish then Error AbstentionReason.UnsupportedEnglish
        elif not status.WithinBudget then Error AbstentionReason.OverBudgetOrUnknown
        elif not status.NotTruncated then Error AbstentionReason.TruncatedOrUnknown
        elif not status.NotCollapsed then Error AbstentionReason.CollapsedOrUnknown
        else Ok ()

    let private tier thresholds distribution =
        let (tier, first), (_, second) = TierDistribution.topTwo distribution
        let confidence = Thresholds.confidence thresholds
        let margin = Thresholds.margin thresholds

        if first = second then
            Error AbstentionReason.TiedTiers
        elif first < confidence then
            Error(AbstentionReason.BelowConfidence(first, confidence))
        elif first - second < margin then
            Error(AbstentionReason.BelowMargin(first - second, margin))
        else
            Ok tier

    let private candidate tier request catalog =
        catalog
        |> Catalog.eligible tier request.AvailableModelIds request.Requirements
        |> List.tryHead
        |> Option.map (fun model -> SelectionDecision.Select model.Id)
        |> Option.defaultValue (SelectionDecision.Abstain(AbstentionReason.NoEligibleModel tier))

    /// Preserve fixed choices; otherwise require safe evidence and a confident eligible tier.
    let select thresholds (request: SelectionRequest) (catalog: Catalog) : SelectionDecision =
        match request.Preference with
        | ModelPreference.Fixed identifier ->
            SelectionDecision.Abstain(AbstentionReason.PreserveExplicitChoice identifier)
        | ModelPreference.Automatic ->
            request.Evidence
            |> evidence
            |> Result.bind (fun () -> tier thresholds request.Distribution)
            |> Result.map (fun tier -> catalog |> candidate tier request)
            |> function
                | Ok decision -> decision
                | Error reason -> SelectionDecision.Abstain reason
