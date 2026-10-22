namespace Rlcd.Router.Tests

open Xunit

open Rlcd.Router

[<Trait("Category", "Unit")>]
module PolicyTests =
    let private safe =
        {
            SupportedEnglish = true
            WithinBudget = true
            NotTruncated = true
            NotCollapsed = true
        }

    let private requirements =
        Fixtures.requirements true 8000 2000 [ Modality.Text ]

    let private models =
        [
            for name, tier in
                [
                    "illustrative-economy", Tier.Economy
                    "illustrative-balanced-z", Tier.Balanced
                    "illustrative-balanced-a", Tier.Balanced
                    "illustrative-deep", Tier.Deep
                ] do
                {
                    Id = Fixtures.id name
                    Tier = tier
                    Capabilities = Fixtures.capabilities true 8000 2000 10000 [ Modality.Text ]
                }
        ]

    let private catalog = models |> Catalog.create "router-catalog-v1" |> Fixtures.valid

    let private request economy balanced deep =
        {
            Distribution =
                [ "Economy", economy; "Balanced", balanced; "Deep", deep ]
                |> TierDistribution.create
                |> Fixtures.valid
            Evidence = safe
            Requirements = requirements
            AvailableModelIds = models |> List.map _.Id |> Set.ofList
            Preference = ModelPreference.Automatic
        }

    [<Theory>]
    [<InlineData(0.8, 0.1, 0.1, "illustrative-economy")>]
    [<InlineData(0.1, 0.8, 0.1, "illustrative-balanced-z")>]
    [<InlineData(0.1, 0.1, 0.8, "illustrative-deep")>]
    let ``confident tier selects first eligible catalog model`` economy balanced deep want =
        let actual = catalog |> Policy.select Thresholds.defaults (request economy balanced deep)
        Assert.Equal(SelectionDecision.Select(Fixtures.id want), actual)

    [<Fact>]
    let ``default confidence rejects values immediately below point eight`` () =
        let actual =
            catalog |> Policy.select Thresholds.defaults (request 0.799999999 0.1 0.100000001)

        Assert.Equal(
            SelectionDecision.Abstain(AbstentionReason.BelowConfidence(0.799999999, 0.8)),
            actual
        )

    [<Theory>]
    [<InlineData(0.8, 0.15, true)>]
    [<InlineData(0.800000001, 0.15, false)>]
    [<InlineData(0.8, 0.6, true)>]
    [<InlineData(0.8, 0.600000001, false)>]
    let ``confidence and margin edges are inclusive without sum tolerance`` confidence margin want =
        let thresholds = Thresholds.create confidence margin |> Fixtures.valid
        let actual = catalog |> Policy.select thresholds (request 0.8 0.2 0.0)
        let selected = actual = SelectionDecision.Select(Fixtures.id "illustrative-economy")
        Assert.Equal(want, selected)

    [<Theory>]
    [<InlineData(0.45, 0.3, 0.25, true)>]
    [<InlineData(0.449999999, 0.3, 0.250000001, false)>]
    let ``default margin edge is point fifteen`` economy balanced deep want =
        let thresholds =
            Thresholds.create 0.0 (Thresholds.margin Thresholds.defaults) |> Fixtures.valid
        let actual = catalog |> Policy.select thresholds (request economy balanced deep)
        let selected = actual = SelectionDecision.Select(Fixtures.id "illustrative-economy")
        Assert.Equal(want, selected)

    [<Theory>]
    [<InlineData(0.5, 0.5, 0.0)>]
    [<InlineData(0.0, 0.5, 0.5)>]
    [<InlineData(0.5, 0.0, 0.5)>]
    [<InlineData(0.3333333333333333, 0.3333333333333333, 0.3333333333333333)>]
    let ``ties abstain even with zero thresholds`` economy balanced deep =
        let thresholds = Thresholds.create 0.0 0.0 |> Fixtures.valid
        let actual = catalog |> Policy.select thresholds (request economy balanced deep)
        Assert.Equal(SelectionDecision.Abstain AbstentionReason.TiedTiers, actual)

    [<Theory>]
    [<InlineData(0.625, 0.375, 0.0, "illustrative-economy")>]
    [<InlineData(0.625, 0.0, 0.375, "illustrative-economy")>]
    [<InlineData(0.375, 0.625, 0.0, "illustrative-balanced-z")>]
    [<InlineData(0.0, 0.625, 0.375, "illustrative-balanced-z")>]
    [<InlineData(0.375, 0.0, 0.625, "illustrative-deep")>]
    [<InlineData(0.0, 0.375, 0.625, "illustrative-deep")>]
    let ``top two ordering handles every permutation at an exact binary margin`` e b d name =
        let input = request e b d
        let thresholds = Thresholds.create 0.625 0.25 |> Fixtures.valid

        Assert.Equal(
            SelectionDecision.Select(Fixtures.id name),
            catalog |> Policy.select thresholds input
        )

        let higherMargin = Thresholds.create 0.625 0.2500000001 |> Fixtures.valid

        Assert.Equal(
            SelectionDecision.Abstain(AbstentionReason.BelowMargin(0.25, 0.2500000001)),
            catalog |> Policy.select higherMargin input
        )

    [<Fact>]
    let ``each unsafe or uncertain evidence flag abstains`` () =
        for evidence, reason in
            [
                { safe with SupportedEnglish = false }, AbstentionReason.UnsupportedEnglish
                { safe with WithinBudget = false }, AbstentionReason.OverBudgetOrUnknown
                { safe with NotTruncated = false }, AbstentionReason.TruncatedOrUnknown
                { safe with NotCollapsed = false }, AbstentionReason.CollapsedOrUnknown
            ] do
            let input = { request 0.1 0.8 0.1 with Evidence = evidence }
            let actual = catalog |> Policy.select Thresholds.defaults input
            Assert.Equal(SelectionDecision.Abstain reason, actual)

    [<Theory>]
    [<InlineData("illustrative-economy")>]
    [<InlineData("illustrative-unavailable")>]
    let ``fixed user choice is preserved even when unavailable or evidence is unsafe`` name =
        let fixedId = Fixtures.id name

        let input =
            {
                request 0.1 0.8 0.1 with
                    Preference = ModelPreference.Fixed fixedId
                    AvailableModelIds = Set.empty
                    Evidence = { safe with NotTruncated = false }
            }

        Assert.Equal(
            SelectionDecision.Abstain(AbstentionReason.PreserveExplicitChoice fixedId),
            catalog |> Policy.select Thresholds.defaults input
        )

    [<Fact>]
    let ``unavailable tier abstains rather than escalating or selecting outsiders`` () =
        let input =
            {
                request 0.1 0.8 0.1 with
                    AvailableModelIds =
                        [ Fixtures.id "illustrative-deep"; Fixtures.id "illustrative-outsider" ]
                        |> Set.ofList
            }

        Assert.Equal(
            SelectionDecision.Abstain(AbstentionReason.NoEligibleModel Tier.Balanced),
            catalog |> Policy.select Thresholds.defaults input
        )

    [<Fact>]
    let ``unmet capabilities abstain`` () =
        let input =
            {
                request 0.1 0.8 0.1 with
                    Requirements = Fixtures.requirements true 8000 2000 [ Modality.Image ]
            }

        Assert.Equal(
            SelectionDecision.Abstain(AbstentionReason.NoEligibleModel Tier.Balanced),
            catalog |> Policy.select Thresholds.defaults input
        )

    [<Fact>]
    let ``all availability subsets select only permitted IDs and remain stable`` () =
        let original = Catalog.models catalog

        for mask in 0..15 do
            let available =
                models
                |> List.indexed
                |> List.choose (fun (index, model) ->
                    if mask &&& (1 <<< index) <> 0 then Some model.Id else None)
                |> Set.ofList
                |> Set.add (Fixtures.id "illustrative-outsider")

            let input = { request 0.1 0.8 0.1 with AvailableModelIds = available }
            let actual = catalog |> Policy.select Thresholds.defaults input

            match actual with
            | SelectionDecision.Select selected ->
                Assert.Contains(selected, available)
                Assert.Contains(selected, [ Fixtures.id "illustrative-balanced-z"
                                            Fixtures.id "illustrative-balanced-a" ])
            | SelectionDecision.Abstain reason ->
                Assert.DoesNotContain(Fixtures.id "illustrative-balanced-z", available)
                Assert.DoesNotContain(Fixtures.id "illustrative-balanced-a", available)
                Assert.Equal(AbstentionReason.NoEligibleModel Tier.Balanced, reason)

            Assert.Equal(actual, catalog |> Policy.select Thresholds.defaults input)
            Assert.Equal<Set<ModelId>>(available, input.AvailableModelIds)
            Assert.Equal<CatalogModel list>(original, Catalog.models catalog)
