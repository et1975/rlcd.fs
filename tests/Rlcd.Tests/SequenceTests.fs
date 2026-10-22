namespace Rlcd.Tests

open System
open System.Reflection

open Microsoft.FSharp.Reflection
open Xunit

open Rlcd

/// Synthetic encoders exercise assembly only, not a real tokenizer or model.
[<Trait("Category", "Unit")>]
module SequenceTests =
    let private missing<'T> : 'T = Unchecked.defaultof<'T>

    let private ok = function
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private errors = function
        | Error errors -> errors
        | Ok value -> failwithf "Expected failure, got %A" value

    let private tokens () = SpecialTokens.create 1 2 3 0 "[MASK]" |> ok

    let private encoder encode =
        { new ITextEncoder with
            member _.Encode text = encode text |> Result.mapError TextEncodingError.Failed }

    let private table entries =
        encoder (fun text ->
            entries
            |> List.tryFind (fun (key, _) -> key = text)
            |> Option.map (snd >> Ok)
            |> Option.defaultValue (Error $"Unexpected text: {text}"))

    let private option id label criterion =
        ChoiceOption.create id label criterion |> ok

    let private choices () =
        [
            option "a" "A" (Some "first")
            option "b" "B" None
        ]

    let private choice () = Question.choice "q" "Pick" (choices ()) |> ok

    let private request state options questions =
        DecisionRequest.create
            {
                State = state
                Questions = questions
                Options = options
            }
        |> ok

    let private settings sequence head perOption policy =
        let budget = TokenBudget.create sequence head perOption |> ok
        RequestOptions.create budget policy |> ok

    let private ids sequence =
        sequence |> QuestionSequence.inputIds |> List.map TokenId.value

    let private markers sequence =
        sequence |> QuestionSequence.markerPositions |> List.map TokenPosition.value

    let private single encoder request =
        request |> Sequence.build (tokens ()) encoder |> ok |> List.exactlyOne

    let private baseEncoder () =
        table
            [
                "S", [ 30; 31 ]
                "choice question: Pick", [ 10; 11 ]
                " A: first", [ 20; 21 ]
                " B", [ 22 ]
            ]

    [<Theory>]
    [<InlineData(typeof<TokenId>)>]
    [<InlineData(typeof<TokenPosition>)>]
    let ``token wrappers are private single-case reference unions`` (wrapper: Type) =
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic
        Assert.True(FSharpType.IsUnion(wrapper, flags))
        Assert.True(wrapper.IsClass)
        let case = FSharpType.GetUnionCases(wrapper, flags) |> Assert.Single
        let field = case.GetFields() |> Assert.Single
        Assert.Equal(typeof<int>, field.PropertyType)
        Assert.False(FSharpValue.PreComputeUnionConstructorInfo(case, flags).IsPublic)
        Assert.Empty(wrapper.GetConstructors())

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(Int32.MaxValue)>]
    let ``token IDs and positions retain nonnegative boundaries`` value =
        Assert.Equal(value, TokenId.create value |> ok |> TokenId.value)
        Assert.Equal(value, TokenPosition.create value |> ok |> TokenPosition.value)

    [<Fact>]
    let ``choice assembles exact IDs and positional option alignment`` () =
        let actual = request "S" RequestOptions.defaults [ choice () ] |> single (baseEncoder ())
        Assert.Equal<int list>([ 1; 10; 11; 2; 3; 20; 21; 3; 22; 2; 30; 31; 2 ], ids actual)
        Assert.Equal<int list>([ 4; 7 ], markers actual)
        Assert.Equal<string list>([ "a"; "b" ], actual |> QuestionSequence.optionIds |> List.map OptionId.value)
        Assert.Equal("q", actual |> QuestionSequence.questionId |> QuestionId.value)
        Assert.Equal(QuestionKind.Choice, QuestionSequence.kind actual)
        Assert.Empty(QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``score renders zero-based levels using criterion then label fallback`` () =
        let question = Question.score "score" "Rate" (choices ()) |> ok
        let encode =
            table
                [
                    "S", [ 30 ]
                    "score question: Rate", [ 10 ]
                    " level 0: first", [ 20 ]
                    " level 1: B", [ 21 ]
                ]
        let actual = request "S" RequestOptions.defaults [ question ] |> single encode
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)
        Assert.Equal(QuestionKind.Score, QuestionSequence.kind actual)

    [<Fact>]
    let ``boolean renders source defaults in false then true order`` () =
        let question = Question.boolean "bool" "Holds" None |> ok
        let encode =
            table
                [
                    "S", [ 30 ]
                    "noul question: Holds", [ 10 ]
                    " false: no, the statement does not hold", [ 20 ]
                    " true: yes, the statement holds", [ 21 ]
                ]
        let actual = request "S" RequestOptions.defaults [ question ] |> single encode
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)
        Assert.Equal<string list>([ "false"; "true" ], actual |> QuestionSequence.optionIds |> List.map OptionId.value)

    [<Fact>]
    let ``boolean preserves custom labels and criteria without sorting`` () =
        let question =
            Question.boolean "bool" "Holds"
                (Some(option "z" "Nope" (Some "absent"), option "a" "Yep" (Some "present")))
            |> ok
        let encode =
            table
                [
                    "S", [ 30 ]
                    "noul question: Holds", [ 10 ]
                    " Nope: absent", [ 20 ]
                    " Yep: present", [ 21 ]
                ]
        let actual = request "S" RequestOptions.defaults [ question ] |> single encode
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)
        Assert.Equal<string list>([ "z"; "a" ], actual |> QuestionSequence.optionIds |> List.map OptionId.value)

    [<Fact>]
    let ``literal masks become spaces everywhere and replacements are diagnosed`` () =
        let question =
            Question.choice "q" "Pick[MASK][MASK]"
                [ option "a" "A[MASK]" (Some "[MASK]"); option "b" "B" None ]
            |> ok
        let encode =
            table
                [
                    "S S", [ 30 ]
                    "choice question: Pick  ", [ 10 ]
                    " A :  ", [ 20 ]
                    " B", [ 21 ]
                ]
        let actual = request "S[MASK]S" RequestOptions.defaults [ question ] |> single encode
        let qid = Question.id question
        let aid = Question.options question |> List.head |> ChoiceOption.id
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)
        Assert.Equal<SequenceDiagnostic list>(
            [
                SequenceDiagnostic.MaskTextReplaced(SequencePart.State, 1)
                SequenceDiagnostic.MaskTextReplaced(SequencePart.Instructions qid, 2)
                SequenceDiagnostic.MaskTextReplaced(SequencePart.Option(qid, aid), 2)
            ],
            QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``sanitization precedes generated instruction prefix and option leading space`` () =
        let custom = SpecialTokens.create 1 2 3 0 "question" |> ok
        let question =
            Question.choice "q" "question"
                [ option "a" "question" None; option "b" "B" None ]
            |> ok
        let encode =
            table
                [
                    " ", [ 30 ]
                    "choice question:  ", [ 10 ]
                    "  ", [ 20 ]
                    " B", [ 21 ]
                ]
        let actual =
            request "question" RequestOptions.defaults [ question ]
            |> Sequence.build custom encode
            |> ok
            |> List.exactlyOne
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)

    [<Fact>]
    let ``mask spelling containing a leading space does not consume generated option whitespace`` () =
        let custom = SpecialTokens.create 1 2 3 0 " A" |> ok
        let encode =
            table
                [
                    "S", [ 30 ]
                    "choice question: Pick", [ 10 ]
                    " A: first", [ 20 ]
                    " B", [ 21 ]
                ]
        let actual =
            request "S" RequestOptions.defaults [ choice () ]
            |> Sequence.build custom encode
            |> ok
            |> List.exactlyOne
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids actual)
        Assert.Empty(QuestionSequence.diagnostics actual)

    let private longEncoder () =
        table
            [
                "S", [ 200 .. 229 ]
                "choice question: Pick", [ 100 .. 129 ]
                " A: first", [ 10 .. 69 ]
                " B", [ 70 .. 129 ]
            ]

    [<Fact>]
    let ``default reject exposes both per-option losses rather than returning partial success`` () =
        let question = choice ()
        let result = request "S" RequestOptions.defaults [ question ] |> Sequence.build (tokens ()) (longEncoder ())
        let qid = Question.id question
        let aids = Question.options question |> List.map ChoiceOption.id
        Assert.Equal<SequenceError list>(
            [
                SequenceError.LossRejected
                    [
                        SequenceDiagnostic.TokensTruncated(SequencePart.Option(qid, aids[0]), 60, 48)
                        SequenceDiagnostic.TokensTruncated(SequencePart.Option(qid, aids[1]), 60, 48)
                    ]
            ],
            errors result)

    [<Fact>]
    let ``head reserves sixteen instruction tokens and caps option spans including masks`` () =
        let options = settings 60 24 20 TruncationPolicy.TruncateWithDiagnostics
        let question = choice ()
        let actual = request "S" options [ question ] |> single (longEncoder ())
        Assert.Equal<int list>(
            [ 1 ] @ [ 100 .. 115 ] @ [ 2; 3; 10; 11; 12; 3; 70; 71; 72; 2 ] @ [ 200 .. 229 ] @ [ 2 ],
            ids actual)
        Assert.Equal<int list>([ 18; 22 ], markers actual)
        Assert.Equal(5, QuestionSequence.diagnostics actual |> List.length)

    [<Fact>]
    let ``tiny head keeps eight instruction tokens and minimum four-token option spans`` () =
        let options = settings 40 8 8 TruncationPolicy.TruncateWithDiagnostics
        let actual = request "S" options [ choice () ] |> single (longEncoder ())
        Assert.Equal<int list>([ 10; 14 ], markers actual)
        Assert.Equal<int list>(
            [ 1 ] @ [ 100 .. 107 ] @ [ 2; 3; 10; 11; 12; 3; 70; 71; 72; 2 ] @ [ 200 .. 219 ] @ [ 2 ],
            ids actual)
        Assert.Contains(SequenceDiagnostic.TokensTruncated(SequencePart.State, 30, 20), QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``state truncation keeps the left prefix and rejects it by default`` () =
        let truncate = settings 12 10 4 TruncationPolicy.TruncateWithDiagnostics
        let strict = settings 12 10 4 TruncationPolicy.Reject
        let input options = request "S" options [ choice () ]
        let actual = input truncate |> single (baseEncoder ())
        Assert.Equal<int list>([ 1; 10; 11; 2; 3; 20; 21; 3; 22; 2; 30; 2 ], ids actual)
        let loss = SequenceDiagnostic.TokensTruncated(SequencePart.State, 2, 1)
        Assert.Equal<SequenceDiagnostic list>([ loss ], QuestionSequence.diagnostics actual)
        Assert.Equal<SequenceError list>(
            [ SequenceError.LossRejected [ loss ] ],
            input strict |> Sequence.build (tokens ()) (baseEncoder ()) |> errors)

    [<Fact>]
    let ``instruction-only truncation is rejected even when every option and state fits`` () =
        let encode =
            table
                [
                    "S", [ 30 ]
                    "choice question: Pick", [ 100 .. 119 ]
                    " A: first", [ 20 ]
                    " B", [ 21 ]
                ]
        let question = choice ()
        let strict = settings 40 20 4 TruncationPolicy.Reject
        let result = request "S" strict [ question ] |> Sequence.build (tokens ()) encode
        Assert.Equal<SequenceError list>(
            [
                SequenceError.LossRejected
                    [ SequenceDiagnostic.TokensTruncated(SequencePart.Instructions(Question.id question), 20, 16) ]
            ],
            errors result)

    [<Fact>]
    let ``zero state room emits no state and retains closing separator when it fits`` () =
        let options = settings 11 10 4 TruncationPolicy.TruncateWithDiagnostics
        let actual = request "S" options [ choice () ] |> single (baseEncoder ())
        Assert.Equal<int list>([ 1; 10; 11; 2; 3; 20; 21; 3; 22; 2; 2 ], ids actual)
        Assert.Contains(SequenceDiagnostic.TokensTruncated(SequencePart.State, 2, 0), QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``sequence clipping preserves every marker or returns a typed alignment failure`` () =
        let options = settings 7 6 4 TruncationPolicy.TruncateWithDiagnostics
        let question = choice ()
        let result = request "S" options [ question ] |> Sequence.build (tokens ()) (baseEncoder ())
        Assert.Contains(
            SequenceError.MissingOptionMarkers(Question.id question, [ Question.options question |> List.last |> ChoiceOption.id ]),
            errors result)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``smallest valid budgets fail all lost markers under either policy`` truncate =
        let policy =
            if truncate then TruncationPolicy.TruncateWithDiagnostics
            else TruncationPolicy.Reject
        let question = choice ()
        let input = request "S" (settings 2 1 1 policy) [ question ]
        Assert.Equal<SequenceError list>(
            [
                SequenceError.MissingOptionMarkers(
                    Question.id question,
                    Question.options question |> List.map ChoiceOption.id)
            ],
            input |> Sequence.build (tokens ()) (baseEncoder ()) |> errors)

    [<Fact>]
    let ``final clipping with retained markers diagnoses sequence and final option token loss`` () =
        let options = settings 8 7 4 TruncationPolicy.TruncateWithDiagnostics
        let actual = request "S" options [ choice () ] |> single (baseEncoder ())
        Assert.Equal<int list>([ 1; 10; 11; 2; 3; 20; 21; 3 ], ids actual)
        Assert.Equal<int list>([ 4; 7 ], markers actual)
        Assert.Contains(SequenceDiagnostic.SequenceClipped(Question.id (choice ()), 11L, 8), QuestionSequence.diagnostics actual)
        let question = choice ()
        let last = Question.options question |> List.last |> ChoiceOption.id
        Assert.Contains(
            SequenceDiagnostic.TokensTruncated(SequencePart.Option(Question.id question, last), 2, 1),
            QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``equal capped option prefixes report stable IDs and reject under strict policy`` () =
        let encode =
            table
                [
                    "S", [ 30 ]
                    "choice question: Pick", [ 10 ]
                    " A: first", [ 20; 21; 22; 23; 24 ]
                    " B", [ 20; 21; 22; 23; 25 ]
                ]
        let question = choice ()
        let input policy = request "S" (settings 40 24 4 policy) [ question ]
        let collapse = SequenceDiagnostic.OptionsCollapsed(Question.id question, Question.options question |> List.map ChoiceOption.id)
        let actual = input TruncationPolicy.TruncateWithDiagnostics |> single encode
        Assert.Contains(collapse, QuestionSequence.diagnostics actual)
        match input TruncationPolicy.Reject |> Sequence.build (tokens ()) encode |> errors with
        | [ SequenceError.LossRejected diagnostics ] -> Assert.Contains(collapse, diagnostics)
        | actual -> failwithf "Expected rejected collapse, got %A" actual

    [<Fact>]
    let ``encoder-created collisions without truncation are not silent`` () =
        let encode = encoder (fun _ -> Ok [ 10 ])
        let result = request "S" RequestOptions.defaults [ choice () ] |> Sequence.build (tokens ()) encode
        match errors result with
        | [ SequenceError.LossRejected [ SequenceDiagnostic.OptionsCollapsed(_, options) ] ] ->
            Assert.Equal<string list>([ "a"; "b" ], options |> List.map OptionId.value)
        | actual -> failwithf "Expected collapse rejection, got %A" actual

    [<Fact>]
    let ``final clipping cannot disguise a collapse already present in budgeted option spans`` () =
        let encode =
            table
                [
                    "S", [ 30 ]
                    "choice question: Pick", [ 10 ]
                    " A: first", [ 20; 21 ]
                    " B", [ 20; 21 ]
                ]
        let question = choice ()
        let input =
            request "S" (settings 7 6 4 TruncationPolicy.TruncateWithDiagnostics) [ question ]
        let actual = input |> single encode
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 21; 3 ], ids actual)
        Assert.Contains(
            SequenceDiagnostic.OptionsCollapsed(
                Question.id question,
                Question.options question |> List.map ChoiceOption.id),
            QuestionSequence.diagnostics actual)

    [<Fact>]
    let ``final clipping creates a collapse from previously distinct option spans`` () =
        let encode =
            table
                [
                    "S", [ 30 ]
                    "choice question: Pick", [ 10 ]
                    " A: first", [ 20; 21 ]
                    " B", [ 20; 21; 22 ]
                ]
        let question = choice ()
        let input length =
            request "S" (settings length 8 4 TruncationPolicy.TruncateWithDiagnostics) [ question ]
        let intact = input 13 |> single encode
        Assert.Equal<int list>(
            [ 1; 10; 2; 3; 20; 21; 3; 20; 21; 22; 2; 30; 2 ], ids intact)
        Assert.Empty(QuestionSequence.diagnostics intact)

        let clipped = input 9 |> single encode
        Assert.Equal<int list>([ 1; 10; 2; 3; 20; 21; 3; 20; 21 ], ids clipped)
        Assert.Equal<int list>([ 3; 6 ], markers clipped)
        Assert.Equal<string list>(
            [ "a"; "b" ], clipped |> QuestionSequence.optionIds |> List.map OptionId.value)
        Assert.Contains(
            SequenceDiagnostic.OptionsCollapsed(
                Question.id question,
                Question.options question |> List.map ChoiceOption.id),
            QuestionSequence.diagnostics clipped)

    [<Fact>]
    let ``empty encoder output is explicit and empty option spans still have aligned markers`` () =
        let encode = encoder (fun _ -> Ok [])
        let options = settings 40 24 4 TruncationPolicy.TruncateWithDiagnostics
        let actual = request "S" options [ choice () ] |> single encode
        Assert.Equal<int list>([ 1; 2; 3; 3; 2; 2 ], ids actual)
        Assert.Equal<int list>([ 2; 3 ], markers actual)
        Assert.Equal(5, QuestionSequence.diagnostics actual |> List.length)
        Assert.True(
            request "S" RequestOptions.defaults [ choice () ]
            |> Sequence.build (tokens ()) encode
            |> Result.isError)

    [<Fact>]
    let ``extreme valid budgets do not overflow or cause budget-sized allocations`` () =
        let options = settings Int32.MaxValue (Int32.MaxValue - 1) (Int32.MaxValue - 1) TruncationPolicy.Reject
        let actual = request "S" options [ choice () ] |> single (baseEncoder ())
        Assert.Equal<int list>([ 1; 10; 11; 2; 3; 20; 21; 3; 22; 2; 30; 31; 2 ], ids actual)

    [<Fact>]
    let ``shared state is encoded once per request across ordered heterogeneous questions`` () =
        let mutable stateCalls = 0
        let encode =
            encoder (fun text ->
                match text with
                | "S" ->
                    stateCalls <- stateCalls + 1
                    if stateCalls = 1 then Ok [ 30 ] else Error "state encoded twice"
                | "choice question: Pick"
                | "score question: Pick"
                | "noul question: Pick" -> Ok [ 10 ]
                | " A: first"
                | " level 0: first"
                | " false: no, the statement does not hold" -> Ok [ 20 ]
                | " B"
                | " level 1: B"
                | " true: yes, the statement holds" -> Ok [ 21 ]
                | _ -> Error $"Unexpected text: {text}")
        let questions =
            [
                choice ()
                Question.score "score" "Pick" (choices ()) |> ok
                Question.boolean "bool" "Pick" None |> ok
            ]
        let actual = request "S" RequestOptions.defaults questions |> Sequence.build (tokens ()) encode |> ok
        Assert.Equal<string list>([ "q"; "score"; "bool" ], actual |> List.map (QuestionSequence.questionId >> QuestionId.value))
        Assert.Equal(1, stateCalls)
        Assert.All(actual, fun item ->
            Assert.Equal<int list>([ 1; 10; 2; 3; 20; 3; 21; 2; 30; 2 ], ids item))

    [<Fact>]
    let ``batch pads variable token and option widths in request then question order`` () =
        let three = choices () @ [ option "c" "C" None ]
        let score = Question.score "score" "Rate" three |> ok
        let boolean = Question.boolean "bool" "Holds" None |> ok
        let encode =
            table
                [
                    "S", [ 30; 31 ]
                    "T", [ 32 ]
                    "choice question: Pick", [ 10; 11 ]
                    "score question: Rate", [ 12 ]
                    "noul question: Holds", [ 13 ]
                    " A: first", [ 20; 21 ]
                    " B", [ 22 ]
                    " level 0: first", [ 23 ]
                    " level 1: B", [ 24 ]
                    " level 2: C", [ 25 ]
                    " false: no, the statement does not hold", [ 26 ]
                    " true: yes, the statement holds", [ 27 ]
                ]
        let actual =
            [
                request "S" RequestOptions.defaults [ choice (); score ]
                request "T" RequestOptions.defaults [ boolean ]
            ]
            |> Sequence.buildBatch (tokens ()) encode
            |> ok
        Assert.Equal<int list list>(
            [
                [ 1; 10; 11; 2; 3; 20; 21; 3; 22; 2; 30; 31; 2 ]
                [ 1; 12; 2; 3; 23; 3; 24; 3; 25; 2; 30; 31; 2 ]
                [ 1; 13; 2; 3; 26; 3; 27; 2; 32; 2; 0; 0; 0 ]
            ],
            actual |> SequenceBatch.inputIds |> List.map (List.map TokenId.value))
        Assert.Equal<int list list>(
            [ List.replicate 13 1; List.replicate 13 1; List.replicate 10 1 @ [ 0; 0; 0 ] ],
            SequenceBatch.attentionMask actual)
        Assert.Equal<int list list>(
            [ [ 4; 7; 0 ]; [ 3; 5; 7 ]; [ 3; 5; 0 ] ],
            actual |> SequenceBatch.markerPositions |> List.map (List.map TokenPosition.value))
        Assert.Equal<bool list list>(
            [ [ true; true; false ]; [ true; true; true ]; [ true; true; false ] ],
            SequenceBatch.markerMask actual)
        Assert.Equal<int list>([ 0; 1; 2 ], SequenceBatch.questionTypes actual)
        Assert.Equal<int list>([ 2; 1 ], actual |> SequenceBatch.sequences |> List.map List.length)

    [<Fact>]
    let ``encoder failure prevents partial request and batch success`` () =
        let encode =
            encoder (fun text ->
                if text = "bad" then Error "unencodable"
                else
                    (baseEncoder ()).Encode text
                    |> Result.mapError (fun (TextEncodingError.Failed message) -> message))
        let good = request "S" RequestOptions.defaults [ choice () ]
        let bad = request "bad" RequestOptions.defaults [ choice () ]
        Assert.Equal<SequenceError list>(
            [ SequenceError.InvalidBatchItem(1, [ SequenceError.EncoderFailed(SequencePart.State, TextEncodingError.Failed "unencodable") ]) ],
            [ good; bad ] |> Sequence.buildBatch (tokens ()) encode |> errors)
        let badQuestion = Question.choice "other" "Other" (choices ()) |> ok
        let result = request "S" RequestOptions.defaults [ choice (); badQuestion ] |> Sequence.build (tokens ()) (baseEncoder ())
        Assert.Contains(
            SequenceError.EncoderFailed(SequencePart.Instructions(Question.id badQuestion), TextEncodingError.Failed "Unexpected text: choice question: Other"),
            errors result)

    [<Fact>]
    let ``batch reports every failing request at its original index`` () =
        let failure = TextEncodingError.Failed "unsupported"
        let encode = encoder (fun _ -> Error "unsupported")
        let input = request "S" RequestOptions.defaults [ choice () ]
        Assert.Equal<SequenceError list>(
            [
                SequenceError.InvalidBatchItem(0, [ SequenceError.EncoderFailed(SequencePart.State, failure) ])
                SequenceError.InvalidBatchItem(1, [ SequenceError.EncoderFailed(SequencePart.State, failure) ])
            ],
            [ input; input ] |> Sequence.buildBatch (tokens ()) encode |> errors)

    [<Theory>]
    [<InlineData(-1)>]
    [<InlineData(Int32.MinValue)>]
    let ``negative token IDs and positions fail validation`` value =
        Assert.Equal<SequenceError list>([ SequenceError.InvalidTokenId value ], TokenId.create value |> errors)
        Assert.Equal<SequenceError list>([ SequenceError.InvalidTokenPosition value ], TokenPosition.create value |> errors)
        Assert.Contains(SequenceError.InvalidTokenId value, SpecialTokens.create value 2 3 0 "[MASK]" |> errors)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" ")>]
    let ``absent mask spelling fails before replacement`` text =
        Assert.Contains(SequenceError.InvalidMaskText, SpecialTokens.create 1 2 3 0 text |> errors)

    [<Fact>]
    let ``conflicting special IDs fail validation`` () =
        Assert.Contains(SequenceError.ConflictingSpecialTokens, SpecialTokens.create 1 2 2 0 "[MASK]" |> errors)

    [<Fact>]
    let ``all special IDs are supplied rather than hardcoded`` () =
        let custom = SpecialTokens.create 101 102 103 104 "<m>" |> ok
        let actual = request "S" RequestOptions.defaults [ choice () ] |> Sequence.build custom (baseEncoder ()) |> ok |> List.exactlyOne
        Assert.Equal<int list>([ 101; 10; 11; 102; 103; 20; 21; 103; 22; 102; 30; 31; 102 ], ids actual)

    [<Fact>]
    let ``batch uses supplied padding ID without marking padding as attended`` () =
        let custom = SpecialTokens.create 101 102 103 104 "<m>" |> ok
        let shorter = settings 11 10 4 TruncationPolicy.TruncateWithDiagnostics
        let actual =
            [
                request "S" RequestOptions.defaults [ choice () ]
                request "S" shorter [ choice () ]
            ]
            |> Sequence.buildBatch custom (baseEncoder ())
            |> ok
        Assert.Equal<int list>(
            [ 101; 10; 11; 102; 103; 20; 21; 103; 22; 102; 102; 104; 104 ],
            actual |> SequenceBatch.inputIds |> List.last |> List.map TokenId.value)
        Assert.Equal<int list>(
            [ 1; 1; 1; 1; 1; 1; 1; 1; 1; 1; 1; 0; 0 ],
            actual |> SequenceBatch.attentionMask |> List.last)

    [<Fact>]
    let ``invalid encoder IDs and unexpected mask IDs cannot masquerade as markers`` () =
        let input = request "S" RequestOptions.defaults [ choice () ]
        Assert.Contains(
            SequenceError.InvalidEncodedToken(SequencePart.State, 1, -1),
            input |> Sequence.build (tokens ()) (encoder (fun _ -> Ok [ 10; -1 ])) |> errors)
        Assert.Contains(
            SequenceError.UnexpectedMaskToken(SequencePart.State, 1),
            input |> Sequence.build (tokens ()) (encoder (fun _ -> Ok [ 10; 3 ])) |> errors)

    [<Fact>]
    let ``invalid option IDs beyond the cap are validated before truncation`` () =
        let question = choice ()
        let encode =
            encoder (fun text ->
                match text with
                | "S" -> Ok [ 30 ]
                | "choice question: Pick" -> Ok [ 10 ]
                | " A: first" -> Ok [ 20; 21; 22; 23; -1 ]
                | " B" -> Ok [ 24 ]
                | _ -> Error $"Unexpected text: {text}")
        let input =
            request "S" (settings 40 24 4 TruncationPolicy.TruncateWithDiagnostics) [ question ]
        let first = Question.options question |> List.head |> ChoiceOption.id
        Assert.Contains(
            SequenceError.InvalidEncodedToken(SequencePart.Option(Question.id question, first), 4, -1),
            input |> Sequence.build (tokens ()) encode |> errors)

    [<Fact>]
    let ``null boundary values and empty batches return errors instead of exceptions`` () =
        let input = request "S" RequestOptions.defaults [ choice () ]
        Assert.Contains(SequenceError.MissingValue "tokens", Sequence.build missing (baseEncoder ()) input |> errors)
        Assert.Contains(SequenceError.MissingValue "encoder", Sequence.build (tokens ()) missing input |> errors)
        Assert.Contains(SequenceError.MissingValue "request", Sequence.build (tokens ()) (baseEncoder ()) missing |> errors)
        Assert.Contains(SequenceError.MissingValue "requests", Sequence.buildBatch (tokens ()) (baseEncoder ()) missing |> errors)
        Assert.Equal<SequenceError list>([ SequenceError.EmptyBatch ], Sequence.buildBatch (tokens ()) (baseEncoder ()) [] |> errors)
        Assert.Contains(
            SequenceError.MissingValue "encodedTokens",
            Sequence.build (tokens ()) (encoder (fun _ -> Ok missing)) input |> errors)
