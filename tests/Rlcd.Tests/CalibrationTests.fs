namespace Rlcd.Tests

open System
open System.IO

open Xunit

open Rlcd

[<Trait("Category", "Unit")>]
module CalibrationTests =
    let private ok = function
        | Ok value -> value
        | Error errors -> failwithf "Expected successful decoding, got %A" errors

    let private configuration temperatures buckets =
        let directory = Path.Combine(Path.GetTempPath(), $"rlcd-calibration-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore

        try
            let path = Path.Combine(directory, "rl_agent_config.json")

            File.WriteAllText(
                path,
                $"""{{"max_len":512,"head_max_len":192,"temperature":{temperatures},
                     "temperature_by_options":{{{buckets}}}}}"""
            )

            AgentConfiguration.read path
        finally
            Directory.Delete(directory, true)

    let private neutral () = configuration "[1,1,1]" ""

    let private option id =
        ChoiceOption.create id $"Label {id}" None |> ok

    let private question kind count =
        let options = [ for index in 0 .. count - 1 -> option $"id-{count - index}" ]

        match kind with
        | "choice" -> Question.choice "question" "Choose." options |> ok
        | "score" -> Question.score "question" "Rate." options |> ok
        | "boolean" ->
            Question.boolean "question" "Decide." (Some(option "yes", option "no")) |> ok
        | _ -> failwithf "Unknown test question kind %s" kind

    let private row question logits =
        {
            Question = question
            OptionLogits = logits
            OptionMask = List.replicate (List.length logits) true
            ActionLogits = [ 0.0f; 0.0f ]
        }

    let private close expected actual =
        Assert.True(
            Double.IsFinite actual && abs (expected - actual) <= 1e-6,
            $"Expected {expected:R}, got {actual:R}"
        )

    let private probabilities answer =
        answer |> Answer.probabilities |> List.map snd

    [<Fact>]
    let ``choice retains option identity and order and selects first on a tie`` () =
        let answer =
            row (question "choice" 3) [ 0.0f; 0.0f; 0.0f ]
            |> Answer.decode (neutral ())
            |> ok

        Assert.Equal("question", answer |> Answer.questionId |> QuestionId.value)

        match Answer.value answer with
        | AnswerValue.Choice id -> Assert.Equal("id-3", OptionId.value id)
        | other -> failwithf "Expected choice, got %A" other

        Assert.Equal<string list>(
            [ "id-3"; "id-2"; "id-1" ],
            answer |> Answer.probabilities |> List.map (fst >> OptionId.value)
        )

        probabilities answer |> List.iter (close (1.0 / 3.0))
        close 1.0 (probabilities answer |> List.sum)
        Assert.Empty(Answer.diagnostics answer)

    [<Fact>]
    let ``score is the expected zero based level at full precision`` () =
        let answer =
            row (question "score" 3) [ 0.0f; float32 (log 2.0); float32 (log 3.0) ]
            |> Answer.decode (neutral ())
            |> ok

        match Answer.value answer with
        | AnswerValue.Score level ->
            close (4.0 / 3.0) level
            Assert.True(abs (level - Math.Round(level, 4)) > 1e-6)
        | other -> failwithf "Expected score, got %A" other

        List.iter2 close [ 1.0 / 6.0; 1.0 / 3.0; 0.5 ] (probabilities answer)

    [<Fact>]
    let ``boolean means probability of second semantic option not label spelling`` () =
        let answer =
            row (question "boolean" 2) [ float32 (log 3.0); 0.0f ]
            |> Answer.decode (neutral ())
            |> ok

        match Answer.value answer with
        | AnswerValue.Boolean probability -> close 0.25 probability
        | other -> failwithf "Expected boolean, got %A" other

        Assert.Equal<string list>(
            [ "yes"; "no" ],
            answer |> Answer.probabilities |> List.map (fst >> OptionId.value)
        )

        close 0.75 (Answer.answerConfidence answer)
        close 0.18872187554086717 (Answer.entropyConfidence answer)

    [<Theory>]
    [<InlineData("choice", 2, "choice:2")>]
    [<InlineData("choice", 3, "choice:3-5")>]
    [<InlineData("choice", 5, "choice:3-5")>]
    [<InlineData("choice", 6, "choice:6-10")>]
    [<InlineData("choice", 10, "choice:6-10")>]
    [<InlineData("choice", 11, "choice:11+")>]
    [<InlineData("choice", 20, "choice:11+")>]
    [<InlineData("score", 2, "score:2")>]
    [<InlineData("score", 3, "score:3-5")>]
    [<InlineData("score", 5, "score:3-5")>]
    [<InlineData("score", 6, "score:6-10")>]
    [<InlineData("score", 10, "score:6-10")>]
    [<InlineData("score", 11, "score:11+")>]
    [<InlineData("score", 20, "score:11+")>]
    [<InlineData("boolean", 2, "noul:2")>]
    let ``question type and every count boundary select the bucket`` kind count key =
        let config = configuration "[1,1,1]" $"\"{key}\":0.25"
        let logits = float32 (log 3.0 / 2.0) :: List.replicate (count - 1) 0.0f
        let answer = row (question kind count) logits |> Answer.decode config |> ok

        // At temperature 0.5 the real weights are 3,1,...,1.
        close (3.0 / float (count + 2)) (Answer.answerConfidence answer)

        Assert.Equal<CalibrationDiagnostic list>(
            [ CalibrationDiagnostic.TemperatureClamped(key, 0.25, 0.5) ],
            Answer.diagnostics answer
        )

    [<Theory>]
    [<InlineData("choice", 2)>]
    [<InlineData("choice", 3)>]
    [<InlineData("choice", 5)>]
    [<InlineData("choice", 6)>]
    [<InlineData("choice", 10)>]
    [<InlineData("choice", 11)>]
    [<InlineData("score", 2)>]
    [<InlineData("score", 3)>]
    [<InlineData("score", 5)>]
    [<InlineData("score", 6)>]
    [<InlineData("score", 10)>]
    [<InlineData("score", 11)>]
    [<InlineData("boolean", 2)>]
    let ``absent buckets use the base temperature for that question type`` kind count =
        let config = configuration "[1,2,4]" ""

        let temperature =
            match kind with
            | "choice" -> 1.0
            | "score" -> 2.0
            | _ -> 4.0

        let logits = float32 (temperature * log 3.0) :: List.replicate (count - 1) 0.0f
        let answer = row (question kind count) logits |> Answer.decode config |> ok
        close (3.0 / float (count + 2)) (Answer.answerConfidence answer)
        Assert.Empty(Answer.diagnostics answer)

    [<Theory>]
    [<InlineData("-2", 0.5, true)>]
    [<InlineData("0", 0.5, true)>]
    [<InlineData("0.1", 0.5, true)>]
    [<InlineData("0.5", 0.5, false)>]
    [<InlineData("1", 1.0, false)>]
    [<InlineData("5", 5.0, false)>]
    [<InlineData("7", 5.0, true)>]
    [<InlineData("1e300", 5.0, true)>]
    let ``finite base temperatures clamp with selected value diagnostics``
        (raw: string)
        applied
        clamped
        =
        let config = configuration $"[{raw},1,1]" ""

        let answer =
            row (question "choice" 2) [ float32 (applied * log 3.0); 0.0f ]
            |> Answer.decode config
            |> ok

        close 0.75 (Answer.answerConfidence answer)

        let expected =
            if clamped then
                [
                    CalibrationDiagnostic.TemperatureClamped(
                        "temperature[0]",
                        Double.Parse(raw, Globalization.CultureInfo.InvariantCulture),
                        applied
                    )
                ]
            else
                []

        Assert.Equal<CalibrationDiagnostic list>(expected, Answer.diagnostics answer)

    [<Fact>]
    let ``unused base clamp does not appear when an in range bucket overrides it`` () =
        let config = configuration "[7,1,1]" "\"choice:2\":2"

        let answer =
            row (question "choice" 2) [ float32 (2.0 * log 3.0); 0.0f ]
            |> Answer.decode config
            |> ok

        close 0.75 (Answer.answerConfidence answer)
        Assert.Empty(Answer.diagnostics answer)

    [<Fact>]
    let ``bucket above maximum is clamped and diagnosed rather than using the base`` () =
        let config = configuration "[1,1,1]" "\"noul:2\":8"

        let answer =
            row (question "boolean" 2) [ 0.0f; float32 (5.0 * log 3.0) ]
            |> Answer.decode config
            |> ok

        match Answer.value answer with
        | AnswerValue.Boolean probability -> close 0.75 probability
        | other -> failwithf "Expected boolean, got %A" other

        Assert.Equal<CalibrationDiagnostic list>(
            [ CalibrationDiagnostic.TemperatureClamped("noul:2", 8.0, 5.0) ],
            Answer.diagnostics answer
        )

    [<Theory>]
    [<InlineData("\"NaN\"")>]
    [<InlineData("\"Infinity\"")>]
    [<InlineData("\"-Infinity\"")>]
    [<InlineData("NaN")>]
    [<InlineData("Infinity")>]
    [<InlineData("-Infinity")>]
    [<InlineData("1e999")>]
    [<InlineData("-1e999")>]
    [<InlineData("null")>]
    [<InlineData("\"invalid\"")>]
    let ``existing config boundary rejects invalid base and bucket temperatures`` raw =
        Assert.Throws<InvalidDataException>(fun () ->
            configuration $"[{raw},1,1]" "" |> ignore)
        |> ignore

        Assert.Throws<InvalidDataException>(fun () ->
            configuration "[1,1,1]" $"\"choice:2\":{raw}" |> ignore)
        |> ignore

    [<Fact>]
    let ``padding is excluded from softmax and option count bucket`` () =
        let config = configuration "[1,1,1]" "\"choice:2\":2,\"choice:3-5\":5"

        let logits =
            {
                row (question "choice" 2) [ 0.0f; Single.MaxValue; float32 (2.0 * log 3.0) ] with
                    OptionMask = [ true; false; true ]
            }

        let answer = logits |> Answer.decode config |> ok
        List.iter2 close [ 0.25; 0.75 ] (probabilities answer)

        match Answer.value answer with
        | AnswerValue.Choice id -> Assert.Equal("id-1", OptionId.value id)
        | other -> failwithf "Expected choice, got %A" other

    [<Fact>]
    let ``trailing padding never changes probabilities or answer diagnostics`` () =
        let config = neutral ()
        let input = row (question "score" 2) [ 0.0f; float32 (log 3.0) ]
        let expected = Answer.decode config input

        let padded =
            {
                input with
                    OptionLogits = input.OptionLogits @ [ Single.MaxValue; -Single.MaxValue ]
                    OptionMask = [ true; true; false; false ]
            }

        Assert.Equal(expected, Answer.decode config padded)

    [<Fact>]
    let ``huge finite float32 logits widen before subtracting and scaling`` () =
        let config = configuration "[0.5,1,1]" ""
        let input =
            row (question "choice" 3) [ -Single.MaxValue; Single.MaxValue; Single.MaxValue ]

        let answer =
            {
                input with
                    ActionLogits = [ Single.MaxValue; -Single.MaxValue ]
            }
            |> Answer.decode config
            |> ok

        Assert.Equal<float list>([ 0.0; 0.5; 0.5 ], probabilities answer)
        close 1.0 (Answer.actionProbability answer)
        close 0.3690702464285426 (Answer.entropyConfidence answer)

        match Answer.value answer with
        | AnswerValue.Choice id -> Assert.Equal("id-2", OptionId.value id)
        | other -> failwithf "Expected choice, got %A" other

    [<Fact>]
    let ``equal huge negative logits produce a normalized uniform distribution`` () =
        let answer =
            row (question "choice" 2) [ -Single.MaxValue; -Single.MaxValue ]
            |> Answer.decode (neutral ())
            |> ok

        Assert.Equal<float list>([ 0.5; 0.5 ], probabilities answer)
        close 0.5 (Answer.answerConfidence answer)
        close 0.0 (Answer.entropyConfidence answer)

    [<Theory>]
    [<InlineData("choice")>]
    [<InlineData("score")>]
    [<InlineData("boolean")>]
    let ``max probability and normalized entropy remain separate for every type`` kind =
        let answer =
            row (question kind 2) [ float32 (log 3.0); 0.0f ]
            |> Answer.decode (neutral ())
            |> ok

        close 0.75 (Answer.answerConfidence answer)
        close 0.18872187554086717 (Answer.entropyConfidence answer)

    [<Theory>]
    [<InlineData(1.0)>]
    [<InlineData(5.0)>]
    let ``action probability is uncalibrated action softmax class zero`` (temperature: float) =
        let raw = temperature.ToString(Globalization.CultureInfo.InvariantCulture)
        let config = configuration $"[{raw},{raw},{raw}]" ""

        let answer =
            {
                row (question "choice" 2) [ 0.0f; 0.0f ] with
                    ActionLogits = [ float32 (log 3.0); 0.0f ]
            }
            |> Answer.decode config
            |> ok

        close 0.75 (Answer.actionProbability answer)
        close 0.5 (Answer.answerConfidence answer)

    [<Theory>]
    [<InlineData(0.0f, 1.0f, 0.2689414213699951)>]
    [<InlineData(1.0f, 0.0f, 0.7310585786300049)>]
    let ``action probability retains precision without display rounding`` first second expected =
        let answer =
            {
                row (question "choice" 2) [ 0.0f; 0.0f ] with
                    ActionLogits = [ first; second ]
            }
            |> Answer.decode (neutral ())
            |> ok

        close expected (Answer.actionProbability answer)

    [<Fact>]
    let ``nonfinite real padded and action logits are rejected with original positions`` () =
        let config = neutral ()

        for nonfinite in [ Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity ] do
            let input =
                {
                    row (question "choice" 2) [ 0.0f; nonfinite; nonfinite ] with
                        OptionMask = [ true; true; false ]
                        ActionLogits = [ nonfinite; 0.0f ]
                }

            Assert.Equal(
                Error
                    [
                        CalibrationError.NonFiniteLogit("optionLogits", 1)
                        CalibrationError.NonFiniteLogit("optionLogits", 2)
                        CalibrationError.NonFiniteLogit("actionLogits", 0)
                    ],
                Answer.decode config input
            )

    [<Fact>]
    let ``mask length real option count and action width must match`` () =
        let config = neutral ()
        let input = row (question "choice" 2) [ 0.0f; 1.0f ]

        Assert.Equal(
            Error [ CalibrationError.OptionMaskLengthMismatch(2, 1) ],
            Answer.decode config { input with OptionMask = [ true ] }
        )

        for mask, count in [ [ false; false ], 0; [ true; false ], 1 ] do
            Assert.Equal(
                Error [ CalibrationError.OptionCountMismatch(2, count) ],
                Answer.decode config { input with OptionMask = mask }
            )

        Assert.Equal(
            Error [ CalibrationError.OptionCountMismatch(2, 3) ],
            Answer.decode config (row input.Question [ 0.0f; 1.0f; 2.0f ])
        )

        for actions in [ []; [ 0.0f ]; [ 0.0f; 0.0f; 0.0f ] ] do
            Assert.Equal(
                Error [ CalibrationError.ActionCountMismatch actions.Length ],
                Answer.decode config { input with ActionLogits = actions }
            )

    [<Fact>]
    let ``fabricated null values fail without domain exceptions`` () =
        let config = neutral ()
        let input = row (question "choice" 2) [ 0.0f; 0.0f ]

        let cases =
            [
                "logits", Unchecked.defaultof<QuestionLogits>
                "question", { input with Question = Unchecked.defaultof<Question> }
                "optionLogits", { input with OptionLogits = Unchecked.defaultof<float32 list> }
                "optionMask", { input with OptionMask = Unchecked.defaultof<bool list> }
                "actionLogits", { input with ActionLogits = Unchecked.defaultof<float32 list> }
            ]

        for field, value in cases do
            Assert.Equal(
                Error [ CalibrationError.MissingValue field ],
                Answer.decode config value
            )

        Assert.Equal(
            Error [ CalibrationError.MissingValue "configuration" ],
            Answer.decode Unchecked.defaultof<AgentConfiguration> input
        )
