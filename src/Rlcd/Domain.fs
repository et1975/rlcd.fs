namespace Rlcd

open System

/// Caller input failures; text and identifiers are compared without normalization.
[<RequireQualifiedAccess>]
type ValidationError =
    /// A required string is null, empty, or whitespace-only.
    | InvalidText of field: string
    /// A required reference value was supplied as null.
    | MissingValue of field: string
    /// Choice and score questions require at least two options.
    | TooFewOptions of count: int
    /// An option identifier occurs more than once in a question.
    | DuplicateOptionId of id: string
    /// An option label occurs more than once in a question.
    | DuplicateOptionLabel of label: string
    /// The two supplied boolean criteria are identical.
    | DuplicateBooleanCriterion of criterion: string
    /// A request must contain at least one question.
    | EmptyQuestions
    /// A question identifier occurs more than once in a request.
    | DuplicateQuestionId of id: string
    /// Token budgets must be positive.
    | NonPositiveBudget of field: string * value: int
    /// The head budget must be strictly below the sequence budget.
    | HeadBudgetNotBelowSequence
    /// The per-option budget must not exceed the head budget.
    | OptionBudgetExceedsHead
    /// A batch must contain at least one request.
    | EmptyBatch
    /// All errors for a batch item, identified by its zero-based input position.
    | InvalidBatchItem of index: int * errors: ValidationError list

[<RequireQualifiedAccess>]
module internal Validation =
    let text field value =
        [
            if String.IsNullOrWhiteSpace value then
                ValidationError.InvalidText field
        ]

    let missing field value =
        [
            if isNull (box value) then
                ValidationError.MissingValue field
        ]

    let addErrors errors result =
        match result, errors with
        | Ok value, [] -> Ok value
        | Ok _, errors -> Error errors
        | Error previous, errors -> Error(previous @ errors)

    let duplicates error values =
        values
        |> List.countBy id
        |> List.choose (fun (value, count) -> if count > 1 then Some(error value) else None)

    let collection field values =
        if isNull (box values) then
            [ ValidationError.MissingValue field ]
        else
            values
            |> List.mapi (fun index value -> missing $"{field}[{index}]" value)
            |> List.concat

    let collect results =
        let prepend result rest =
            match result, rest with
            | Ok value, Ok values -> Ok(value :: values)
            | Error errors, Error remaining -> Error(errors @ remaining)
            | Error errors, Ok _
            | Ok _, Error errors -> Error errors

        List.foldBack prepend results (Ok [])

/// A nonempty, exact caller-supplied question identifier.
type QuestionId = private QuestionId of string

/// Construction and read-out of question identifiers.
[<RequireQualifiedAccess>]
module QuestionId =
    /// Reject null, empty, and whitespace-only identifiers; preserve accepted text.
    let create (text: string) : Result<QuestionId, ValidationError list> =
        Ok text
        |> Validation.addErrors (Validation.text "questionId" text)
        |> Result.map QuestionId

    /// Return the exact identifier text.
    let value (id: QuestionId) : string =
        let (QuestionId text) = id
        text

/// A nonempty identifier scoped to one question, not a positional tensor index.
type OptionId = private OptionId of string

/// Construction and read-out of option identifiers.
[<RequireQualifiedAccess>]
module OptionId =
    /// Reject null, empty, and whitespace-only identifiers; preserve accepted text.
    let create (text: string) : Result<OptionId, ValidationError list> =
        Ok text
        |> Validation.addErrors (Validation.text "optionId" text)
        |> Result.map OptionId

    /// Return the exact identifier text.
    let value (id: OptionId) : string =
        let (OptionId text) = id
        text

/// A named option with an optional textual criterion, shared by all question kinds.
type ChoiceOption =
    private
        {
            Id: OptionId
            Label: string
            Criterion: string option
        }

/// Pure construction and read-out of question options.
[<RequireQualifiedAccess>]
module ChoiceOption =
    /// Validate raw ID, label, and any supplied criterion without altering their text.
    let create
        (id: string)
        (label: string)
        (criterion: string option)
        : Result<ChoiceOption, ValidationError list> =
        let errors =
            Validation.text "label" label
            @ (criterion |> Option.map (Validation.text "criterion") |> Option.defaultValue [])

        OptionId.create id
        |> Validation.addErrors errors
        |> Result.map (fun identifier ->
            {
                Id = identifier
                Label = label
                Criterion = criterion
            })

    /// Return the stable option identifier.
    let id (option: ChoiceOption) : OptionId = option.Id

    /// Return the exact option label.
    let label (option: ChoiceOption) : string = option.Label

    /// Return the supplied criterion; None means no criterion was supplied.
    let criterion (option: ChoiceOption) : string option = option.Criterion

/// A requested future token-overflow policy; construction itself never truncates.
[<RequireQualifiedAccess>]
type TruncationPolicy =
    /// Reject input that does not fit during later tokenization.
    | Reject
    /// Permit later truncation only with diagnostics.
    | TruncateWithDiagnostics

/// Generic positive token-budget relationships, not model-specific token-fit evidence.
type TokenBudget =
    private
        {
            SequenceLength: int
            HeadLength: int
            OptionTokens: int
        }

/// Generic token-budget construction and read-out.
[<RequireQualifiedAccess>]
module TokenBudget =
    let private errors sequenceLength headLength optionTokens =
        [
            for field, value in
                [
                    "sequenceLength", sequenceLength
                    "headLength", headLength
                    "optionTokens", optionTokens
                ] do
                if value <= 0 then
                    ValidationError.NonPositiveBudget(field, value)
            if headLength >= sequenceLength then
                ValidationError.HeadBudgetNotBelowSequence
            if optionTokens > headLength then
                ValidationError.OptionBudgetExceedsHead
        ]

    /// Require 0 < optionTokens <= headLength < sequenceLength.
    let create sequenceLength headLength optionTokens : Result<TokenBudget, ValidationError list> =
        Ok
            {
                SequenceLength = sequenceLength
                HeadLength = headLength
                OptionTokens = optionTokens
            }
        |> Validation.addErrors (errors sequenceLength headLength optionTokens)

    /// Default sequence/head/per-option budgets: 512/192/48.
    let defaults : TokenBudget =
        {
            SequenceLength = 512
            HeadLength = 192
            OptionTokens = 48
        }

    /// Return the requested sequence-length budget.
    let sequenceLength (budget: TokenBudget) : int = budget.SequenceLength

    /// Return the requested question-head budget.
    let headLength (budget: TokenBudget) : int = budget.HeadLength

    /// Return the requested per-option token budget.
    let optionTokens (budget: TokenBudget) : int = budget.OptionTokens

/// Validated request configuration, independent of any loaded model.
type RequestOptions =
    private
        {
            Budget: TokenBudget
            Truncation: TruncationPolicy
        }

/// Request configuration construction and read-out.
[<RequireQualifiedAccess>]
module RequestOptions =
    /// Combine a validated budget with an explicit policy, rejecting fabricated null values.
    let create
        (budget: TokenBudget)
        (truncation: TruncationPolicy)
        : Result<RequestOptions, ValidationError list> =
        Ok
            {
                Budget = budget
                Truncation = truncation
            }
        |> Validation.addErrors (
            Validation.missing "budget" budget @ Validation.missing "truncation" truncation
        )

    /// Default budgets with Reject; no caller text is truncated during construction.
    let defaults : RequestOptions =
        {
            Budget = TokenBudget.defaults
            Truncation = TruncationPolicy.Reject
        }

    /// Return the validated generic budget.
    let budget (options: RequestOptions) : TokenBudget = options.Budget

    /// Return the explicitly requested truncation policy.
    let truncation (options: RequestOptions) : TruncationPolicy = options.Truncation
