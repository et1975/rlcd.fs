namespace Rlcd.Router.Tests

open System

open Xunit

open Rlcd.Router

[<Trait("Category", "Unit")>]
module CatalogTests =
    [<Fact>]
    let ``revision preserves exact code units rather than replacing invalid unicode`` () =
        let revision name =
            [ { Id = Fixtures.id name
                Tier = Tier.Economy
                Capabilities = Fixtures.capabilities false 1 1 2 [ Modality.Text ] } ]
            |> Catalog.create "router-catalog-v1"
            |> Fixtures.valid
            |> Catalog.revision
        // Construct at runtime: compiler metadata encoding can replace surrogate literals.
        let first = String([| 'a'; char 0xD800 |])
        let second = String([| 'a'; char 0xD801 |])
        Assert.NotEqual<string>(first, second)
        Assert.NotEqual<string>(revision first, revision second)

    [<Fact>]
    let ``catalog revision covers order identity tier and every capability`` () =
        let entry name tier tools prompt output context modalities =
            { Id = Fixtures.id name
              Tier = tier
              Capabilities = Fixtures.capabilities tools prompt output context modalities }
        let first = entry "a" Tier.Economy false 10 5 20 [ Modality.Text ]
        let second = entry "b" Tier.Deep true 10 5 20 [ Modality.Text ]
        let revision models =
            models |> Catalog.create "router-catalog-v1" |> Fixtures.valid |> Catalog.revision
        let original = revision [ first; second ]
        Assert.Equal(original, revision [ first; second ])
        Assert.NotEqual<string>("router-catalog-v1", original)
        for changed in
            [ [ second; first ]
              [ { first with Id = Fixtures.id "other" }; second ]
              [ { first with Tier = Tier.Balanced }; second ]
              [ entry "a" Tier.Economy true 10 5 20 [ Modality.Text ]; second ]
              [ entry "a" Tier.Economy false 11 5 20 [ Modality.Text ]; second ]
              [ entry "a" Tier.Economy false 10 6 20 [ Modality.Text ]; second ]
              [ entry "a" Tier.Economy false 10 5 21 [ Modality.Text ]; second ]
              [ entry "a" Tier.Economy false 10 5 20 [ Modality.Text; Modality.Image ]; second ] ] do
            Assert.NotEqual<string>(original, revision changed)

    let private model name tier capabilities =
        {
            Id = Fixtures.id name
            Tier = tier
            Capabilities = capabilities
        }

    let private text = [ Modality.Text ]
    let private standard = Fixtures.capabilities true 8000 2000 10000 text

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData("router-catalog-v2")>]
    let ``catalog rejects unsupported schema version`` version =
        let actual = [ model "illustrative-a" Tier.Economy standard ] |> Catalog.create version
        Assert.Equal(Error(ValidationError.UnsupportedCatalogVersion version), actual)

    [<Fact>]
    let ``catalog rejects empty models`` () =
        Assert.Equal(Error ValidationError.EmptyCatalog, Catalog.create "router-catalog-v1" [])

    [<Fact>]
    let ``catalog rejects duplicate IDs even across tiers`` () =
        let actual =
            [
                model "illustrative-a" Tier.Economy standard
                model "illustrative-a" Tier.Deep standard
            ]
            |> Catalog.create "router-catalog-v1"

        Assert.Equal(Error(ValidationError.DuplicateModelId(Fixtures.id "illustrative-a")), actual)

    [<Theory>]
    [<InlineData(101, 20, 100)>]
    [<InlineData(80, 101, 100)>]
    let ``capabilities cannot exceed shared context`` prompt output context =
        let actual =
            Capabilities.create
                true
                (Fixtures.tokens prompt)
                (Fixtures.tokens output)
                (Fixtures.tokens context)
                (Set.ofList text)

        Assert.Equal(Error ValidationError.InvalidCapabilities, actual)

    [<Fact>]
    let ``capabilities must state usable input modalities`` () =
        let actual =
            Capabilities.create
                true
                (Fixtures.tokens 80)
                (Fixtures.tokens 20)
                (Fixtures.tokens 100)
                Set.empty

        Assert.Equal(Error ValidationError.EmptyModalities, actual)

    [<Fact>]
    let ``capabilities must fit at least one prompt and one output token`` () =
        let actual =
            Capabilities.create
                true
                (Fixtures.tokens 1)
                (Fixtures.tokens 1)
                (Fixtures.tokens 1)
                (Set.ofList text)

        Assert.Equal(Error ValidationError.InvalidCapabilities, actual)

    [<Theory>]
    [<InlineData(6000, 2000, true)>]
    [<InlineData(6001, 2000, false)>]
    let ``joint context limit applies even when individual maxima fit`` prompt output want =
        let capabilities = Fixtures.capabilities true 8000 2000 8000 text
        let requirements = Fixtures.requirements true prompt output text
        Assert.Equal(want, capabilities |> Capabilities.supports requirements)

    [<Theory>]
    [<InlineData(false, 8000, 2000, true)>]
    [<InlineData(true, 8000, 2000, true)>]
    [<InlineData(true, 8001, 2000, false)>]
    [<InlineData(true, 8000, 2001, false)>]
    let ``capability comparisons include exact prompt and output edges`` tools prompt output want =
        let requirements = Fixtures.requirements tools prompt output text
        Assert.Equal(want, standard |> Capabilities.supports requirements)

    [<Fact>]
    let ``shared context accounts for prompt plus output without integer overflow`` () =
        let capabilities =
            Fixtures.capabilities true Int32.MaxValue Int32.MaxValue Int32.MaxValue text

        let requirements = Fixtures.requirements true Int32.MaxValue 1 text
        Assert.False(capabilities |> Capabilities.supports requirements)

    [<Fact>]
    let ``tools and all modalities must be supported`` () =
        let plain = Fixtures.capabilities false 8000 2000 10000 text
        let tools = Fixtures.requirements true 100 20 text
        let image = Fixtures.requirements false 100 20 [ Modality.Text; Modality.Image ]
        Assert.False(plain |> Capabilities.supports tools)
        Assert.False(standard |> Capabilities.supports image)

    [<Fact>]
    let ``eligible pool is ordered tier availability and capability intersection`` () =
        let noTools = Fixtures.capabilities false 8000 2000 10000 text

        let models =
            [
                model "illustrative-unavailable" Tier.Balanced standard
                model "illustrative-deep" Tier.Deep standard
                model "illustrative-no-tools" Tier.Balanced noTools
                model "illustrative-z" Tier.Balanced standard
                model "illustrative-a" Tier.Balanced standard
            ]

        let catalog = models |> Catalog.create "router-catalog-v1" |> Fixtures.valid

        let available =
            [ "illustrative-deep"; "illustrative-no-tools"; "illustrative-z"; "illustrative-a"
              "illustrative-outsider" ]
            |> List.map Fixtures.id
            |> Set.ofList

        let actual =
            catalog
            |> Catalog.eligible Tier.Balanced available (Fixtures.requirements true 8000 2000 text)
            |> List.map (_.Id >> ModelId.value)

        Assert.Equal<string list>([ "illustrative-z"; "illustrative-a" ], actual)
        Assert.Equal<CatalogModel list>(models, Catalog.models catalog)
