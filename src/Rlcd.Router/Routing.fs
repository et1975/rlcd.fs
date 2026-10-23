namespace Rlcd.Router

open Rlcd

/// Caller-established language and scope; the router performs no language detection.
[<RequireQualifiedAccess>]
type TaskSupport =
    /// The caller established English input within this task-complexity rubric's scope.
    | SupportedEnglishInScope
    /// The caller established unsupported language.
    | UnsupportedEnglish
    /// The caller established unsupported task scope.
    | UnsupportedScope
    /// Language or scope remains unknown.
    | Unknown

/// Explicit task data and generation constraints; prose cannot change these constraints.
type RoutingRequest =
    {
        /// Exact bounded task prose.
        Task: TaskText
        /// Caller-established language/scope status.
        Support: TaskSupport
        /// Validated generation requirements, not inferred from the classifier input.
        Requirements: Requirements
        /// Caller-established available generation model IDs.
        AvailableModelIds: Set<ModelId>
        /// An explicit fixed choice bypasses inference.
        Preference: ModelPreference
    }

/// Reproducible policy/catalog identity and available actual inference provenance.
type RoutingProvenance =
    {
        /// Fixed question identity and revision.
        RubricVersion: string
        /// Selection algorithm revision.
        PolicyVersion: string
        /// Catalog schema, not its content revision.
        CatalogSchemaVersion: string
        /// Deterministic ordered catalog content digest.
        CatalogRevision: string
        /// Exact configured confidence and margin requirements.
        Thresholds: Thresholds
        /// Actual core provenance when returned; absent on bypass and preparation failure.
        Model: ModelProvenance option
    }

/// Explicit failures, never an implicit recommendation or fallback distribution.
[<RequireQualifiedAccess>]
type RoutingError =
    /// Generic construction failed before inference.
    | RequestConstruction of Rlcd.ValidationError list
    /// Returned evidence violates the fixed output contract; includes actual available provenance.
    | InvalidOutput of details: string list * provenance: ModelProvenance
    /// Non-fit inference failure, retaining its original typed reason.
    | PredictionFailed of PredictionError

type internal RoutingAnswer =
    {
        QuestionId: QuestionId
        Value: AnswerValue
        Probabilities: (OptionId * float) list
        CalibrationDiagnostics: CalibrationDiagnostic list
    }

type internal RoutingPrediction =
    {
        Answers: RoutingAnswer list
        Diagnostics: SequenceDiagnostic list list
        Usage: PredictionUsage
        Provenance: ModelProvenance
    }

/// Immutable recommendation or abstention, with no prompt text or dispatch instructions.
type RoutingResult =
    private
        {
            Decision: SelectionDecision
            Distribution: TierDistribution option
            Provenance: RoutingProvenance
            Diagnostics: SequenceDiagnostic list
            CalibrationDiagnostics: CalibrationDiagnostic list
            Usage: PredictionUsage option
        }

/// Pure composition of validated output evidence and the existing selection policy.
[<RequireQualifiedAccess>]
module RoutingResult =
    let private identity thresholds catalog model =
        {
            RubricVersion = Rubric.version
            PolicyVersion = Policy.version
            CatalogSchemaVersion = Catalog.version catalog
            CatalogRevision = Catalog.revision catalog
            Thresholds = thresholds
            Model = model
        }

    let private abstain provenance reason =
        {
            Decision = SelectionDecision.Abstain reason
            Distribution = None
            Provenance = provenance
            Diagnostics = []
            CalibrationDiagnostics = []
            Usage = None
        }

    let internal bypass thresholds (request: RoutingRequest) catalog =
        let reason =
            match request.Preference, request.Support with
            | ModelPreference.Fixed identifier, _ ->
                Some(AbstentionReason.PreserveExplicitChoice identifier)
            | ModelPreference.Automatic, TaskSupport.UnsupportedEnglish ->
                Some AbstentionReason.UnsupportedEnglish
            | ModelPreference.Automatic, TaskSupport.UnsupportedScope ->
                Some AbstentionReason.UnsupportedScope
            | ModelPreference.Automatic, TaskSupport.Unknown -> Some AbstentionReason.UnknownSupport
            | ModelPreference.Automatic, TaskSupport.SupportedEnglishInScope -> None

        reason |> Option.map (abstain (identity thresholds catalog None))

    let rec private fitLoss error =
        match error with
        | SequenceError.LossRejected diagnostics -> not (List.isEmpty diagnostics)
        | SequenceError.MissingOptionMarkers _ -> true
        | SequenceError.InvalidBatchItem (_, errors) ->
            not (List.isEmpty errors) && List.forall fitLoss errors
        | SequenceError.MissingValue _
        | SequenceError.InvalidTokenId _
        | SequenceError.InvalidTokenPosition _
        | SequenceError.InvalidMaskText
        | SequenceError.ConflictingSpecialTokens
        | SequenceError.EncoderFailed _
        | SequenceError.InvalidEncodedToken _
        | SequenceError.UnexpectedMaskToken _
        | SequenceError.EmptyBatch -> false

    let internal failure thresholds catalog error =
        let rejected =
            match error with
            | PredictionError.ModelLimitExceeded _ -> true
            | PredictionError.SequenceFailure errors ->
                not (List.isEmpty errors) && List.forall fitLoss errors
            | PredictionError.InvalidRequests _
            | PredictionError.CapacityExceeded _
            | PredictionError.InvalidOutput _
            | PredictionError.CalibrationFailure _
            | PredictionError.NativeFailure _
            | PredictionError.Cancelled
            | PredictionError.Disposed -> false

        if rejected then
            AbstentionReason.InputRejected error
            |> abstain (identity thresholds catalog None)
            |> Ok
        else
            Error(RoutingError.PredictionFailed error)

    let private answerErrors question (answer: RoutingAnswer) =
        let expected = question |> Question.options |> List.map ChoiceOption.id
        [
            if answer.QuestionId <> Question.id question then
                "Answer question identity does not match the fixed rubric."
            match answer.Value with
            | AnswerValue.Choice identifier when List.contains identifier expected -> ()
            | AnswerValue.Choice _ -> "Choice identity is outside the fixed rubric."
            | AnswerValue.Score _
            | AnswerValue.Boolean _ -> "Answer kind is not choice."
            if List.map fst answer.Probabilities <> expected then
                "Option identities must be exactly Economy, Balanced, Deep in that order."
        ]

    let private usageErrors (prediction: RoutingPrediction) =
        let maximum = TokenBudget.sequenceLength TokenBudget.defaults
        let usage = prediction.Usage
        [
            if List.length prediction.Diagnostics <> 1 then
                "Exactly one question diagnostic group is required."
            if
                usage.QuestionCount <> 1
                || usage.InputTokens <= 0
                || usage.InputTokens > maximum
                || usage.PaddedTokens <> usage.InputTokens
                || usage.PaddedTokens > maximum
                || usage.NativeRuns <> 1
            then
                "Usage is inconsistent with one bounded question and one native run."
        ]

    let private choiceErrors (answer: RoutingAnswer) distribution =
        let (_, highest), _ = TierDistribution.topTwo distribution
        let firstHighest =
            answer.Probabilities |> List.tryFind (fun (_, probability) -> probability = highest)
        match firstHighest with
        | Some (identifier, _) when answer.Value = AnswerValue.Choice identifier -> []
        | _ -> [ "Choice identity must match the first highest-probability option." ]

    let private decode question (prediction: RoutingPrediction) =
        let invalid errors = Error(RoutingError.InvalidOutput(errors, prediction.Provenance))
        match prediction.Answers with
        | [ answer ] ->
            match answerErrors question answer @ usageErrors prediction with
            | [] ->
                answer.Probabilities
                |> List.map (fun (identifier, probability) ->
                    OptionId.value identifier, probability)
                |> TierDistribution.create
                |> Result.mapError (fun error ->
                    RoutingError.InvalidOutput(
                        [ $"Invalid tier distribution: {error}." ], prediction.Provenance))
                |> Result.bind (fun distribution ->
                    match choiceErrors answer distribution with
                    | [] -> Ok distribution
                    | errors -> invalid errors)
            | errors -> invalid errors
        | _ -> invalid [ "Exactly one answer is required." ]

    let private select thresholds (request: RoutingRequest) catalog distribution =
        catalog
        |> Policy.select thresholds
            {
                Distribution = distribution
                // Established only after fixed Reject preparation, exact output validation,
                // coherent bounded usage, and an explicitly empty loss-diagnostic group.
                // This is observed core-contract evidence, not independent model certification.
                Evidence =
                    {
                        SupportedEnglish = request.Support = TaskSupport.SupportedEnglishInScope
                        WithinBudget = true
                        NotTruncated = true
                        NotCollapsed = true
                    }
                Requirements = request.Requirements
                AvailableModelIds = request.AvailableModelIds
                Preference = request.Preference
            }

    let internal fromPrediction thresholds request catalog (prediction: RoutingPrediction) =
        Rubric.question ()
        |> Result.mapError RoutingError.RequestConstruction
        |> Result.bind (fun question -> decode question prediction)
        |> Result.map (fun distribution ->
            let diagnostics = List.concat prediction.Diagnostics
            let decision =
                if List.isEmpty diagnostics then select thresholds request catalog distribution
                else SelectionDecision.Abstain(AbstentionReason.InputAltered diagnostics)

            {
                Decision = decision
                Distribution = Some distribution
                Provenance = identity thresholds catalog (Some prediction.Provenance)
                Diagnostics = diagnostics
                CalibrationDiagnostics =
                    prediction.Answers |> List.collect _.CalibrationDiagnostics
                Usage = Some prediction.Usage
            })

    /// Read the selection or explicit abstention.
    let decision (result: RoutingResult) : SelectionDecision = result.Decision

    /// Available validated probabilities; absent when inference evidence was not returned.
    let distribution (result: RoutingResult) : TierDistribution option = result.Distribution

    /// The unique highest-probability tier; absent on a tie or when no distribution exists.
    let tier (result: RoutingResult) : Tier option =
        result.Distribution
        |> Option.bind (fun distribution ->
            let (tier, first), (_, second) = TierDistribution.topTwo distribution
            if first = second then None else Some tier)

    /// Maximum probability derived from the validated distribution, not entropy/action output.
    let confidence (result: RoutingResult) : float option =
        result.Distribution
        |> Option.map (fun distribution ->
            let (_, first), _ = TierDistribution.topTwo distribution
            first)

    /// Top-two difference derived from the validated distribution.
    let margin (result: RoutingResult) : float option =
        result.Distribution
        |> Option.map (fun distribution ->
            let (_, first), (_, second) = TierDistribution.topTwo distribution
            first - second)

    /// Read configuration identities and actual available model provenance.
    let provenance (result: RoutingResult) : RoutingProvenance = result.Provenance

    /// Read original sequence observations without task prose.
    let diagnostics (result: RoutingResult) : SequenceDiagnostic list = result.Diagnostics

    /// Read original calibration observations without reapplying calibration.
    let calibrationDiagnostics (result: RoutingResult) : CalibrationDiagnostic list =
        result.CalibrationDiagnostics

    /// Read actual usage when returned by inference.
    let usage (result: RoutingResult) : PredictionUsage option = result.Usage
