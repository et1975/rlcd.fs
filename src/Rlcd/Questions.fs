namespace Rlcd

/// Question semantics, independent of any model encoding.
[<RequireQualifiedAccess>]
type QuestionKind =
    /// Select among caller-ordered alternatives.
    | Choice
    /// Ordered alternatives represent zero-based ordinal levels.
    | Score
    /// Exactly two alternatives in false, then true semantic order.
    | Boolean

/// An immutable validated question with stable identifiers and caller-ordered options.
type Question =
    private
        {
            Id: QuestionId
            Instructions: string
            Kind: QuestionKind
            Options: ChoiceOption list
        }

/// Pure choice, ordinal-score, and boolean question construction.
[<RequireQualifiedAccess>]
module Question =
    let private optionErrors options =
        let missing = Validation.collection "options" options

        if isNull (box options) then
            missing
        else
            let present = options |> List.filter (fun value -> not (isNull (box value)))

            missing
            @ [
                if options.Length < 2 then
                    ValidationError.TooFewOptions options.Length
              ]
            @ (present
               |> List.map (ChoiceOption.id >> OptionId.value)
               |> Validation.duplicates ValidationError.DuplicateOptionId)
            @ (present
               |> List.map ChoiceOption.label
               |> Validation.duplicates ValidationError.DuplicateOptionLabel)

    let private booleanErrors kind options =
        match kind with
        | QuestionKind.Choice
        | QuestionKind.Score -> []
        | QuestionKind.Boolean ->
            options
            |> List.filter (fun value -> not (isNull (box value)))
            |> List.choose ChoiceOption.criterion
            |> Validation.duplicates ValidationError.DuplicateBooleanCriterion

    let private create kind id instructions options =
        let errors =
            Validation.text "instructions" instructions
            @ optionErrors options
            @ (if isNull (box options) then [] else booleanErrors kind options)

        QuestionId.create id
        |> Validation.addErrors errors
        |> Result.map (fun identifier ->
            {
                Id = identifier
                Instructions = instructions
                Kind = kind
                Options = options
            })

    /// Validate a choice with at least two options and distinct exact IDs and labels.
    let choice
        (id: string)
        (instructions: string)
        (options: ChoiceOption list)
        : Result<Question, ValidationError list> =
        create QuestionKind.Choice id instructions options

    /// Validate ordinal levels in caller order; the first option is level zero.
    let score
        (id: string)
        (instructions: string)
        (options: ChoiceOption list)
        : Result<Question, ValidationError list> =
        create QuestionKind.Score id instructions options

    /// None uses false/true IDs and labels, with no supplied criteria.
    /// Some (falseOption, trueOption) preserves that semantic order and all supplied text.
    let boolean
        (id: string)
        (instructions: string)
        (options: (ChoiceOption * ChoiceOption) option)
        : Result<Question, ValidationError list> =
        let values =
            match options with
            | None ->
                [
                    ChoiceOption.create "false" "false" None
                    ChoiceOption.create "true" "true" None
                ]
                |> Validation.collect
            | Some pair when isNull (box pair) ->
                Error [ ValidationError.MissingValue "booleanOptions" ]
            | Some (falseOption, trueOption) -> Ok [ falseOption; trueOption ]

        values |> Result.bind (create QuestionKind.Boolean id instructions)

    /// Return the stable question identifier.
    let id (question: Question) : QuestionId = question.Id

    /// Return the exact instructions.
    let instructions (question: Question) : string = question.Instructions

    /// Return choice, score, or boolean semantics.
    let kind (question: Question) : QuestionKind = question.Kind

    /// Return the immutable options in semantic order.
    let options (question: Question) : ChoiceOption list = question.Options

/// Unvalidated input for single or all-or-nothing batch request construction.
type DecisionRequestInput =
    {
        /// Domain-independent text; callers explicitly serialize other representations.
        State: string
        /// Ordered, individually validated questions.
        Questions: Question list
        /// Generic budgets and the requested truncation policy.
        Options: RequestOptions
    }

/// A complete immutable request; validity does not imply model or token-fit compatibility.
type DecisionRequest =
    private
        {
            State: string
            Questions: Question list
            Options: RequestOptions
        }

/// Pure request construction; no tokenization, model loading, or inference occurs.
[<RequireQualifiedAccess>]
module DecisionRequest =
    let private questionErrors questions =
        let missing = Validation.collection "questions" questions

        if isNull (box questions) then
            missing
        else
            missing
            @ [
                if List.isEmpty questions then
                    ValidationError.EmptyQuestions
              ]
            @ (questions
               |> List.filter (fun question -> not (isNull (box question)))
               |> List.map (Question.id >> QuestionId.value)
               |> Validation.duplicates ValidationError.DuplicateQuestionId)

    /// Validate state, request options, and nonempty ordered questions with unique IDs.
    let create (input: DecisionRequestInput) : Result<DecisionRequest, ValidationError list> =
        if isNull (box input) then
            Error [ ValidationError.MissingValue "request" ]
        else
            let errors =
                Validation.text "state" input.State
                @ questionErrors input.Questions
                @ Validation.missing "requestOptions" input.Options

            Ok
                {
                    State = input.State
                    Questions = input.Questions
                    Options = input.Options
                }
            |> Validation.addErrors errors

    /// Validate every item before returning the entire batch, or all indexed item errors.
    let createBatch
        (inputs: DecisionRequestInput list)
        : Result<DecisionRequest list, ValidationError list> =
        if isNull (box inputs) then
            Error [ ValidationError.MissingValue "requests" ]
        elif List.isEmpty inputs then
            Error [ ValidationError.EmptyBatch ]
        else
            inputs
            |> List.mapi (fun index input ->
                create input
                |> Result.mapError (fun errors ->
                    [ ValidationError.InvalidBatchItem(index, errors) ]))
            |> Validation.collect

    /// Return the exact caller-supplied state.
    let state (request: DecisionRequest) : string = request.State

    /// Return the immutable questions in caller order.
    let questions (request: DecisionRequest) : Question list = request.Questions

    /// Return the validated generic request options.
    let options (request: DecisionRequest) : RequestOptions = request.Options
