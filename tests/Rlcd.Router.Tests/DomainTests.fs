namespace Rlcd.Router.Tests

open System
open System.Reflection

open Microsoft.FSharp.Reflection
open Xunit

open Rlcd.Router

module internal Fixtures =
    let valid = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected validation error: %A" error

    let id value = ModelId.create value |> valid
    let tokens value = GenerationTokens.create value |> valid

    let requirements tools prompt output modalities =
        Requirements.create tools (tokens prompt) (tokens output) (Set.ofList modalities)
        |> valid

    let capabilities tools prompt output context modalities =
        Capabilities.create
            tools
            (tokens prompt)
            (tokens output)
            (tokens context)
            (Set.ofList modalities)
        |> valid

[<Trait("Category", "Unit")>]
module DomainTests =
    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" \t\r\n")>]
    let ``task text rejects absent content`` text =
        Assert.Equal(Error ValidationError.InvalidTaskText, TaskText.create text)

    [<Fact>]
    let ``task text preserves exact bounded unicode and rejects rather than truncates`` () =
        let raw = "  Explain café and 日本語 as English examples.\n"
        Assert.Equal(raw, raw |> TaskText.create |> Fixtures.valid |> TaskText.value)
        let maximum = String('x', TaskText.maximumLength)
        Assert.Equal(maximum, maximum |> TaskText.create |> Fixtures.valid |> TaskText.value)
        Assert.Equal(
            Error(ValidationError.TaskTextTooLong(maximum.Length + 1, TaskText.maximumLength)),
            TaskText.create (maximum + "x"))

    [<Theory>]
    [<InlineData(typeof<ModelId>, typeof<string>)>]
    [<InlineData(typeof<GenerationTokens>, typeof<int>)>]
    let ``scalar wrappers are private single-case reference unions``
        (wrapper: Type, payload: Type)
        =
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic
        Assert.True(FSharpType.IsUnion(wrapper, flags))
        Assert.True(wrapper.IsClass)
        let case = FSharpType.GetUnionCases(wrapper, flags) |> Assert.Single
        let field = case.GetFields() |> Assert.Single
        Assert.Equal(payload, field.PropertyType)
        Assert.False(FSharpValue.PreComputeUnionConstructorInfo(case, flags).IsPublic)
        Assert.Empty(wrapper.GetConstructors())

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" ")>]
    [<InlineData(" example-model")>]
    [<InlineData("example model")>]
    [<InlineData("example-model\n")>]
    [<InlineData("example\u0000model")>]
    let ``model IDs reject empty whitespace and control characters`` raw =
        Assert.Equal(Error ValidationError.InvalidModelId, ModelId.create raw)

    [<Fact>]
    let ``model IDs preserve exact caller identity`` () =
        let actual = Fixtures.id "illustrative/Model-v1" |> ModelId.value
        Assert.Equal("illustrative/Model-v1", actual)
        Assert.NotEqual(Fixtures.id "illustrative/A", Fixtures.id "illustrative/a")

    [<Fact>]
    let ``model ID sets deduplicate and order exact caller identities`` () =
        let actual =
            [ "model/b"; "model/A"; "model/a"; "model/A" ]
            |> List.map Fixtures.id
            |> Set.ofList
            |> Set.toList
            |> List.map ModelId.value

        Assert.Equal<string list>([ "model/A"; "model/a"; "model/b" ], actual)

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(-1)>]
    [<InlineData(Int32.MinValue)>]
    let ``generation token quantities must be positive`` count =
        Assert.Equal(
            Error(ValidationError.InvalidGenerationTokens count),
            GenerationTokens.create count
        )

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(Int32.MaxValue)>]
    let ``generation token quantities retain positive boundaries`` count =
        Assert.Equal(count, Fixtures.tokens count |> GenerationTokens.value)

    [<Fact>]
    let ``requirements must state input modalities`` () =
        let actual =
            Requirements.create false (Fixtures.tokens 1) (Fixtures.tokens 1) Set.empty

        Assert.Equal(Error ValidationError.EmptyModalities, actual)
