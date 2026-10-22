namespace Rlcd.Tests

open System

open Xunit

open Rlcd

[<Trait("Category", "Unit")>]
module ApiTests =
    let private ok = function
        | Ok value -> value
        | Error errors -> failwithf "Expected valid construction, got %A" errors

    let private question id = Question.boolean id "Does this need follow-up?" None |> ok

    let private input state questions : DecisionRequestInput =
        {
            State = state
            Questions = questions
            Options = RequestOptions.defaults
        }

    [<Fact>]
    let ``request defaults retain explicit rejecting truncation and generic budgets`` () =
        let request =
            input " Ticket text.\n" [ question "z"; question "a" ]
            |> DecisionRequest.create
            |> ok
        Assert.Equal(" Ticket text.\n", DecisionRequest.state request)

        Assert.Equal<string list>(
            [ "z"; "a" ],
            request |> DecisionRequest.questions |> List.map (Question.id >> QuestionId.value)
        )

        let options = DecisionRequest.options request
        let budget = RequestOptions.budget options
        Assert.Equal(TruncationPolicy.Reject, RequestOptions.truncation options)
        Assert.Equal(512, TokenBudget.sequenceLength budget)
        Assert.Equal(192, TokenBudget.headLength budget)
        Assert.Equal(48, TokenBudget.optionTokens budget)

    [<Fact>]
    let ``request retains explicit custom budgets and truncation policy`` () =
        let budget = TokenBudget.create 256 100 25 |> ok
        let options = RequestOptions.create budget TruncationPolicy.TruncateWithDiagnostics |> ok

        let request =
            { input "State" [ question "q" ] with Options = options }
            |> DecisionRequest.create
            |> ok

        Assert.Equal(options, DecisionRequest.options request)
        Assert.Equal(
            TruncationPolicy.TruncateWithDiagnostics,
            request |> DecisionRequest.options |> RequestOptions.truncation
        )

        let actual = request |> DecisionRequest.options |> RequestOptions.budget
        Assert.Equal(256, TokenBudget.sequenceLength actual)
        Assert.Equal(100, TokenBudget.headLength actual)
        Assert.Equal(25, TokenBudget.optionTokens actual)

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(-1)>]
    [<InlineData(Int32.MinValue)>]
    let ``each budget must be positive`` value =
        let has expected (result: Result<TokenBudget, ValidationError list>) =
            match result with
            | Error errors -> Assert.Contains(expected, errors)
            | Ok _ -> failwith "Expected invalid budget"

        TokenBudget.create value 192 48
        |> has (ValidationError.NonPositiveBudget("sequenceLength", value))

        TokenBudget.create 512 value 48
        |> has (ValidationError.NonPositiveBudget("headLength", value))

        TokenBudget.create 512 192 value
        |> has (ValidationError.NonPositiveBudget("optionTokens", value))

    [<Theory>]
    [<InlineData(192)>]
    [<InlineData(193)>]
    let ``head must leave room within sequence budget`` head =
        Assert.Equal(
            Error [ ValidationError.HeadBudgetNotBelowSequence ],
            TokenBudget.create 192 head 48
        )

    [<Fact>]
    let ``option budget cannot exceed head budget`` () =
        Assert.Equal(
            Error [ ValidationError.OptionBudgetExceedsHead ],
            TokenBudget.create 512 192 193
        )

        TokenBudget.create 512 192 192 |> ok |> ignore
        TokenBudget.create Int32.MaxValue (Int32.MaxValue - 1) 1 |> ok |> ignore

    [<Fact>]
    let ``budget validation is not a token fit check`` () =
        let options =
            RequestOptions.create (TokenBudget.create 3 2 1 |> ok) TruncationPolicy.Reject
            |> ok

        let request =
            { input (String.replicate 2000 "long state ") [ question "q" ] with Options = options }
            |> DecisionRequest.create
            |> ok

        Assert.Equal(22000, (DecisionRequest.state request).Length)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" \n\t")>]
    let ``request state must be meaningful text`` state =
        Assert.Equal(
            Error [ ValidationError.InvalidText "state" ],
            input state [ question "q" ] |> DecisionRequest.create
        )

    [<Fact>]
    let ``request requires questions and rejects repeated question IDs`` () =
        Assert.Equal(
            Error [ ValidationError.EmptyQuestions ],
            input "State" [] |> DecisionRequest.create
        )

        Assert.Equal(
            Error [ ValidationError.DuplicateQuestionId "q" ],
            input "State" [ question "q"; question "q" ] |> DecisionRequest.create
        )

    [<Fact>]
    let ``request and options reject fabricated null public values`` () =
        Assert.Equal(
            Error [ ValidationError.MissingValue "budget" ],
            RequestOptions.create Unchecked.defaultof<_> TruncationPolicy.Reject
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "truncation" ],
            RequestOptions.create TokenBudget.defaults Unchecked.defaultof<_>
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "request" ],
            DecisionRequest.create Unchecked.defaultof<_>
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "questions" ],
            input "State" Unchecked.defaultof<_> |> DecisionRequest.create
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "questions[0]" ],
            input "State" [ Unchecked.defaultof<_> ] |> DecisionRequest.create
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "requestOptions" ],
            { input "State" [ question "q" ] with Options = Unchecked.defaultof<_> }
            |> DecisionRequest.create
        )

    [<Fact>]
    let ``batch retains requests and their question order`` () =
        let requests =
            [
                input "Second" [ question "z"; question "a" ]
                input "First" [ question "b" ]
            ]
            |> DecisionRequest.createBatch
            |> ok

        Assert.Equal<string list>([ "Second"; "First" ], List.map DecisionRequest.state requests)

        Assert.Equal<string list list>(
            [ [ "z"; "a" ]; [ "b" ] ],
            requests
            |> List.map (DecisionRequest.questions >> List.map (Question.id >> QuestionId.value))
        )

    [<Fact>]
    let ``batch reports every invalid item without a successful subset`` () =
        Assert.Equal(
            Error
                [
                    ValidationError.InvalidBatchItem(
                        1,
                        [ ValidationError.InvalidText "state"; ValidationError.EmptyQuestions ]
                    )
                    ValidationError.InvalidBatchItem(
                        3,
                        [ ValidationError.DuplicateQuestionId "duplicate" ]
                    )
                    ValidationError.InvalidBatchItem(
                        4,
                        [ ValidationError.MissingValue "request" ]
                    )
                ],
            [
                input "Valid" [ question "q" ]
                input "" []
                input "Also valid" [ question "r" ]
                input "Invalid" [ question "duplicate"; question "duplicate" ]
                Unchecked.defaultof<_>
            ]
            |> DecisionRequest.createBatch
        )

    [<Fact>]
    let ``empty and null batches are rejected`` () =
        Assert.Equal(Error [ ValidationError.EmptyBatch ], DecisionRequest.createBatch [])

        Assert.Equal(
            Error [ ValidationError.MissingValue "requests" ],
            DecisionRequest.createBatch Unchecked.defaultof<_>
        )

    [<Fact>]
    let ``requests and batches are isolated from caller array mutation`` () =
        let questions = [| question "original" |]
        let source = [| input "Original" (List.ofArray questions) |]
        let requests = List.ofArray source |> DecisionRequest.createBatch |> ok
        questions[0] <- question "changed"
        source[0] <- input "Changed" []
        let request = List.head requests
        Assert.Equal("Original", DecisionRequest.state request)

        Assert.Equal<string list>(
            [ "original" ],
            request |> DecisionRequest.questions |> List.map (Question.id >> QuestionId.value)
        )
