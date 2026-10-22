namespace Rlcd

open System

/// Malformed supplied output or calibration configuration; no inference is performed.
[<RequireQualifiedAccess>]
type CalibrationError =
    /// A required reference value was supplied as null.
    | MissingValue of field: string
    /// The option mask and supplied option logits have different lengths.
    | OptionMaskLengthMismatch of logits: int * mask: int
    /// The mask-selected logits do not match the question's real options.
    | OptionCountMismatch of expected: int * actual: int
    /// The action head must contain exactly two logits.
    | ActionCountMismatch of actual: int
    /// A supplied logit, including a padded position, is not finite.
    | NonFiniteLogit of field: string * index: int
    /// Neither the selected bucket nor its base temperature was supplied.
    | MissingTemperature of key: string

/// Material adjustments made while decoding a question's supplied logits.
[<RequireQualifiedAccess>]
type CalibrationDiagnostic =
    /// A selected finite temperature was confined to the inclusive [0.5, 5.0] range.
    | TemperatureClamped of key: string * configured: float * applied: float

/// One supplied float32 output row, independent of model loading or tensor ownership.
type QuestionLogits =
    {
        /// A validated question whose options define the real semantic order.
        Question: Question
        /// Finite option logits, including any padding.
        OptionLogits: float32 list
        /// True positions correspond one-to-one, in order, to the question's options.
        OptionMask: bool list
        /// Two finite action logits; class zero is the reference action.
        ActionLogits: float32 list
    }

/// Typed answer semantics; numeric values retain full precision without display rounding.
[<RequireQualifiedAccess>]
type AnswerValue =
    /// The highest-probability option, choosing the first in semantic order on a tie.
    | Choice of OptionId
    /// Probability-weighted expected zero-based ordinal level.
    | Score of expectedLevel: float
    /// Probability of the semantic true option, regardless of its label or identifier.
    | Boolean of trueProbability: float

/// An immutable decoded answer, not evidence that any model has been run or certified.
type Answer =
    private
        {
            QuestionId: QuestionId
            Value: AnswerValue
            Probabilities: (OptionId * float) list
            AnswerConfidence: float
            EntropyConfidence: float
            ActionProbability: float
            Diagnostics: CalibrationDiagnostic list
        }

/// Pure calibrated decoding and read-out of caller-supplied logits.
/// Formula reference: Laya common.py and onnx_agent.py at
/// 9d955671415fc19f069b9cc998928075c1f255ec; see THIRD-PARTY-NOTICES.md.
[<RequireQualifiedAccess>]
module Answer =
    let private missing field value =
        [
            if isNull (box value) then
                CalibrationError.MissingValue field
        ]

    let private missingValues configuration logits =
        missing "configuration" configuration
        @ (if isNull (box logits) then
               missing "logits" logits
           else
               missing "question" logits.Question
               @ missing "optionLogits" logits.OptionLogits
               @ missing "optionMask" logits.OptionMask
               @ missing "actionLogits" logits.ActionLogits)

    let private nonfinite field values =
        values
        |> List.indexed
        |> List.choose (fun (index, value) ->
            if Single.IsFinite value then
                None
            else
                Some(CalibrationError.NonFiniteLogit(field, index)))

    let private shapeErrors logits =
        [
            if logits.OptionLogits.Length <> logits.OptionMask.Length then
                CalibrationError.OptionMaskLengthMismatch(
                    logits.OptionLogits.Length,
                    logits.OptionMask.Length
                )
            else
                let expected = logits.Question |> Question.options |> List.length
                let actual = logits.OptionMask |> List.filter id |> List.length

                if actual <> expected then
                    CalibrationError.OptionCountMismatch(expected, actual)

            if logits.ActionLogits.Length <> 2 then
                CalibrationError.ActionCountMismatch logits.ActionLogits.Length
        ]

    let private validate configuration logits =
        match missingValues configuration logits with
        | [] ->
            let errors =
                shapeErrors logits
                @ nonfinite "optionLogits" logits.OptionLogits
                @ nonfinite "actionLogits" logits.ActionLogits

            if List.isEmpty errors then Ok logits else Error errors
        | errors -> Error errors

    let private temperatureKeys question =
        let name, index =
            match Question.kind question with
            | QuestionKind.Choice -> "choice", 0
            | QuestionKind.Score -> "score", 1
            | QuestionKind.Boolean -> "noul", 2

        let count = question |> Question.options |> List.length

        let bucket =
            if count <= 2 then "2"
            elif count <= 5 then "3-5"
            elif count <= 10 then "6-10"
            else "11+"

        $"{name}:{bucket}", $"temperature[{index}]"

    let private clampTemperature (key, configured) =
        let applied = configured |> max 0.5 |> min 5.0

        let diagnostics =
            [
                if applied <> configured then
                    CalibrationDiagnostic.TemperatureClamped(key, configured, applied)
            ]

        applied, diagnostics

    let private temperature configuration question =
        let bucket, fallback = temperatureKeys question
        let temperatures = AgentConfiguration.temperatures configuration

        let selected =
            match Map.tryFind bucket temperatures with
            | Some value -> Some(bucket, value)
            | None -> Map.tryFind fallback temperatures |> Option.map (fun value -> fallback, value)

        match selected with
        | Some entry -> Ok(clampTemperature entry)
        | None -> Error [ CalibrationError.MissingTemperature fallback ]

    let private softmax temperature values =
        match values |> List.map float with
        | [] -> []
        | first :: rest ->
            let maximum = rest |> List.fold max first
            // Widen before subtraction; opposite finite float32 extremes then remain finite.
            let weights =
                first :: rest |> List.map (fun value -> exp ((value - maximum) / temperature))

            let total = List.sum weights
            weights |> List.map (fun weight -> weight / total)

    let private normalizedEntropyConfidence probabilities =
        let entropy =
            probabilities
            |> List.sumBy (fun (_, probability) -> -probability * log (max 1e-12 probability))

        1.0 - entropy / log (float probabilities.Length) |> max 0.0 |> min 1.0

    let private bestOption first rest =
        rest
        |> List.fold
            (fun best candidate -> if snd candidate > snd best then candidate else best)
            first

    let private answerValue kind best second probabilities =
        match kind with
        | QuestionKind.Choice -> AnswerValue.Choice(fst best)
        | QuestionKind.Score ->
            probabilities
            |> List.mapi (fun index (_, probability) -> float index * probability)
            |> List.sum
            |> AnswerValue.Score
        | QuestionKind.Boolean -> AnswerValue.Boolean(snd second)

    let private answer diagnostics probabilities actionProbabilities question =
        match probabilities, actionProbabilities with
        | first :: second :: rest, actionProbability :: _ ->
            let best = bestOption first (second :: rest)

            Ok
                {
                    QuestionId = Question.id question
                    Value = answerValue (Question.kind question) best second probabilities
                    Probabilities = probabilities
                    AnswerConfidence = snd best
                    EntropyConfidence = normalizedEntropyConfidence probabilities
                    ActionProbability = actionProbability
                    Diagnostics = diagnostics
                }
        | _, [] -> Error [ CalibrationError.ActionCountMismatch 0 ]
        | _, _ ->
            let expected = question |> Question.options |> List.length
            Error [ CalibrationError.OptionCountMismatch(expected, probabilities.Length) ]

    let private calibrated configuration logits =
        temperature configuration logits.Question
        |> Result.bind (fun (scale, diagnostics) ->
            // Validation establishes both equal-length zip invariants before this operation.
            let probabilities =
                List.zip logits.OptionMask logits.OptionLogits
                |> List.choose (fun (real, value) -> if real then Some value else None)
                |> softmax scale
                |> List.zip (logits.Question |> Question.options |> List.map ChoiceOption.id)

            let actions = logits.ActionLogits |> softmax 1.0
            answer diagnostics probabilities actions logits.Question)

    /// Validate a row, select type/count temperature with base fallback, and decode real options.
    /// Finite padding is excluded; all supplied logits must be finite. Results are never rounded.
    /// Configuration IO and its invalid-configuration exceptions belong to AgentConfiguration.read.
    let decode
        (configuration: AgentConfiguration)
        (logits: QuestionLogits)
        : Result<Answer, CalibrationError list> =
        logits |> validate configuration |> Result.bind (calibrated configuration)

    /// Return the stable identifier of the question that was decoded.
    let questionId (answer: Answer) : QuestionId = answer.QuestionId

    /// Return choice identity, expected zero-based score, or semantic P(true).
    let value (answer: Answer) : AnswerValue = answer.Value

    /// Return only real-option probabilities, retaining original option IDs and semantic order.
    let probabilities (answer: Answer) : (OptionId * float) list = answer.Probabilities

    /// Return max(p), not entropy confidence or a guarantee for the caller's domain.
    let answerConfidence (answer: Answer) : float = answer.AnswerConfidence

    /// Return normalized Shannon entropy confidence, 1 - H(p)/log(k), distinct from max(p).
    let entropyConfidence (answer: Answer) : float = answer.EntropyConfidence

    /// Return softmax(action logits)[0], without temperature scaling; this is not authorization.
    let actionProbability (answer: Answer) : float = answer.ActionProbability

    /// Return adjustments to the selected temperature; unused configuration entries are excluded.
    let diagnostics (answer: Answer) : CalibrationDiagnostic list = answer.Diagnostics
