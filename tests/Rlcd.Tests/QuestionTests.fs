namespace Rlcd.Tests

open System
open System.Reflection

open Microsoft.FSharp.Reflection
open Xunit

open Rlcd

[<Trait("Category", "Unit")>]
module QuestionTests =
    let private ok = function
        | Ok value -> value
        | Error errors -> failwithf "Expected valid construction, got %A" errors

    let private option id label criterion =
        ChoiceOption.create id label criterion |> ok

    let private options =
        [
            option "z" "Review" (Some "Needs a person")
            option "a" "Resolve" None
        ]

    [<Theory>]
    [<InlineData(typeof<QuestionId>)>]
    [<InlineData(typeof<OptionId>)>]
    let ``identifiers are private single-case reference unions`` (wrapper: Type) =
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic
        Assert.True(FSharpType.IsUnion(wrapper, flags))
        Assert.True(wrapper.IsClass)
        let case = FSharpType.GetUnionCases(wrapper, flags) |> Assert.Single
        let field = case.GetFields() |> Assert.Single
        Assert.Equal(typeof<string>, field.PropertyType)
        Assert.False(FSharpValue.PreComputeUnionConstructorInfo(case, flags).IsPublic)
        Assert.Empty(wrapper.GetConstructors())

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" \t\n")>]
    let ``identifiers reject absent or meaningless text`` text =
        Assert.Equal(
            Error [ ValidationError.InvalidText "questionId" ],
            QuestionId.create text
        )

        Assert.Equal(Error [ ValidationError.InvalidText "optionId" ], OptionId.create text)

    [<Theory>]
    [<InlineData(" Id \t\n")>]
    [<InlineData("id\u0000value")>]
    [<InlineData("e\u0301")>]
    let ``identifiers preserve accepted text exactly`` text =
        Assert.Equal(text, QuestionId.create text |> ok |> QuestionId.value)
        Assert.Equal(text, OptionId.create text |> ok |> OptionId.value)

    [<Fact>]
    let ``choice preserves exact text and caller option order`` () =
        let question = Question.choice " triage " " Choose a category.\n" options |> ok
        Assert.Equal(" triage ", question |> Question.id |> QuestionId.value)
        Assert.Equal(" Choose a category.\n", Question.instructions question)
        Assert.Equal(QuestionKind.Choice, Question.kind question)

        let actual =
            question
            |> Question.options
            |> List.map (fun value ->
                ChoiceOption.id value |> OptionId.value,
                ChoiceOption.label value,
                ChoiceOption.criterion value)

        Assert.Equal<(string * string * string option) list>(
            [ "z", "Review", Some "Needs a person"; "a", "Resolve", None ],
            actual
        )

    [<Fact>]
    let ``score retains ordinal levels rather than sorting labels or identifiers`` () =
        let question = Question.score "urgency" "Rate urgency." options |> ok
        Assert.Equal(QuestionKind.Score, Question.kind question)

        let levels =
            question
            |> Question.options
            |> List.mapi (fun level value -> level, ChoiceOption.label value)

        Assert.Equal<(int * string) list>([ 0, "Review"; 1, "Resolve" ], levels)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" \t")>]
    let ``options reject invalid IDs labels and supplied criteria`` text =
        Assert.Equal(
            Error [ ValidationError.InvalidText "optionId" ],
            ChoiceOption.create text "Label" None
        )

        Assert.Equal(
            Error [ ValidationError.InvalidText "label" ],
            ChoiceOption.create "id" text None
        )

        Assert.Equal(
            Error [ ValidationError.InvalidText "criterion" ],
            ChoiceOption.create "id" "Label" (Some text)
        )

    [<Fact>]
    let ``option text is not trimmed or normalized`` () =
        let value = option " id " " Label " (Some "\n Criterion ")
        Assert.Equal(" id ", value |> ChoiceOption.id |> OptionId.value)
        Assert.Equal(" Label ", ChoiceOption.label value)
        Assert.Equal(Some "\n Criterion ", ChoiceOption.criterion value)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData("\n ")>]
    let ``each question kind requires meaningful ID and instructions`` text =
        for create in
            [
                fun id instructions -> Question.choice id instructions options
                fun id instructions -> Question.score id instructions options
                fun id instructions -> Question.boolean id instructions None
            ] do
            Assert.Equal(
                Error [ ValidationError.InvalidText "questionId" ],
                create text "Instructions"
            )

            Assert.Equal(
                Error [ ValidationError.InvalidText "instructions" ],
                create "q" text
            )

    [<Fact>]
    let ``choice and score require at least two options`` () =
        for create in [ Question.choice; Question.score ] do
            Assert.Equal(
                Error [ ValidationError.TooFewOptions 0 ],
                create "q" "Instructions" []
            )

            Assert.Equal(
                Error [ ValidationError.TooFewOptions 1 ],
                create "q" "Instructions" [ option "a" "A" None ]
            )

    [<Fact>]
    let ``choice and score reject duplicate option IDs and labels`` () =
        for create in [ Question.choice; Question.score ] do
            Assert.Equal(
                Error [ ValidationError.DuplicateOptionId "same" ],
                create "q" "Instructions" [ option "same" "A" None; option "same" "B" None ]
            )

            Assert.Equal(
                Error [ ValidationError.DuplicateOptionLabel "Same" ],
                create "q" "Instructions" [ option "a" "Same" None; option "b" "Same" None ]
            )

    [<Fact>]
    let ``uniqueness uses exact case sensitive text`` () =
        let values = [ option "a" "A" None; option "A" "a" None; option " a" " A" None ]
        let question = Question.choice "q" "Instructions" values |> ok
        Assert.Equal<ChoiceOption list>(values, Question.options question)

    [<Fact>]
    let ``boolean defaults have false then true semantic order`` () =
        let question = Question.boolean "follow-up" "Is follow-up needed?" None |> ok
        Assert.Equal(QuestionKind.Boolean, Question.kind question)

        let actual =
            question
            |> Question.options
            |> List.map (fun value ->
                (ChoiceOption.id value |> OptionId.value), ChoiceOption.label value)

        Assert.Equal<(string * string) list>([ "false", "false"; "true", "true" ], actual)

    [<Fact>]
    let ``boolean custom options retain false then true order and criteria`` () =
        let falseOption = option "z-no" "No" (Some "Nothing remains")
        let trueOption = option "a-yes" "Yes" (Some "A reply is needed")

        let question =
            Question.boolean "follow-up" "Is follow-up needed?" (Some(falseOption, trueOption))
            |> ok

        Assert.Equal<ChoiceOption list>([ falseOption; trueOption ], Question.options question)

    [<Fact>]
    let ``boolean rejects duplicate IDs labels or supplied criteria`` () =
        let first = option "a" "No" (Some "Criterion")

        for second, error in
            [
                option "a" "Yes" None, ValidationError.DuplicateOptionId "a"
                option "b" "No" None, ValidationError.DuplicateOptionLabel "No"
                option "b" "Yes" (Some "Criterion"),
                ValidationError.DuplicateBooleanCriterion "Criterion"
            ] do
            Assert.Equal(
                Error [ error ],
                Question.boolean "q" "Instructions" (Some(first, second))
            )

    [<Fact>]
    let ``question validation reports all independent errors`` () =
        let repeated = option "same" "Same" None

        Assert.Equal(
            Error
                [
                    ValidationError.InvalidText "questionId"
                    ValidationError.InvalidText "instructions"
                    ValidationError.DuplicateOptionId "same"
                    ValidationError.DuplicateOptionLabel "Same"
                ],
            Question.choice "" "" [ repeated; repeated ]
        )

    [<Fact>]
    let ``question constructors reject fabricated null collections and options`` () =
        for create in [ Question.choice; Question.score ] do
            Assert.Equal(
                Error [ ValidationError.MissingValue "options" ],
                create "q" "Instructions" Unchecked.defaultof<_>
            )

            Assert.Equal(
                Error [ ValidationError.MissingValue "options[1]" ],
                create "q" "Instructions" [ option "a" "A" None; Unchecked.defaultof<_> ]
            )

        Assert.Equal(
            Error [ ValidationError.MissingValue "booleanOptions" ],
            Question.boolean "q" "Instructions" (Some Unchecked.defaultof<_>)
        )

        Assert.Equal(
            Error [ ValidationError.MissingValue "options[0]" ],
            Question.boolean
                "q"
                "Instructions"
                (Some(Unchecked.defaultof<_>, option "b" "B" None))
        )

    [<Fact>]
    let ``construction is isolated from later changes to caller option storage`` () =
        let source = [| option "z" "Last" None; option "a" "First" None |]
        let question = Question.choice "q" "Instructions" (List.ofArray source) |> ok
        source[0] <- option "replacement" "Changed" None

        Assert.Equal<string list>(
            [ "z"; "a" ],
            question |> Question.options |> List.map (ChoiceOption.id >> OptionId.value)
        )
