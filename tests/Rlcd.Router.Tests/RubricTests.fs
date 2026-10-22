namespace Rlcd.Router.Tests

open System

open Xunit

open Rlcd.Router

[<Trait("Category", "Unit")>]
module RubricTests =
    [<Theory>]
    [<InlineData(Double.NaN)>]
    [<InlineData(Double.PositiveInfinity)>]
    [<InlineData(Double.NegativeInfinity)>]
    [<InlineData(-0.01)>]
    [<InlineData(1.01)>]
    let ``probabilities must be finite within the unit interval`` probability =
        let actual =
            [ "Economy", probability; "Balanced", 0.1; "Deep", 0.1 ]
            |> TierDistribution.create

        Assert.Equal(Error(ValidationError.InvalidProbability Tier.Economy), actual)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData("economy")>]
    [<InlineData("Other")>]
    let ``probability tier names are explicit and exact`` name =
        let actual =
            [ name, 0.8; "Balanced", 0.1; "Deep", 0.1 ] |> TierDistribution.create

        Assert.Equal(Error(ValidationError.InvalidTierName name), actual)

    [<Fact>]
    let ``probability lists require each tier exactly once`` () =
        for values in
            [
                []
                [ "Economy", 1.0 ]
                [ "Economy", 0.8; "Balanced", 0.2 ]
                [ "Economy", 0.8; "Economy", 0.1; "Deep", 0.1 ]
                [ "Economy", 0.7; "Balanced", 0.1; "Deep", 0.1; "Deep", 0.1 ]
            ] do
            Assert.Equal(
                Error ValidationError.InvalidDistributionTiers,
                TierDistribution.create values
            )

    [<Theory>]
    [<InlineData(0.0, false)>]
    [<InlineData(0.2, false)>]
    [<InlineData(0.799999998, false)>]
    [<InlineData(0.7999999995, true)>]
    [<InlineData(0.8, true)>]
    [<InlineData(0.8000000005, true)>]
    [<InlineData(0.800000002, false)>]
    let ``probability sum uses absolute tolerance of one billionth`` economy want =
        let actual =
            [ "Economy", economy; "Balanced", 0.1; "Deep", 0.1 ]
            |> TierDistribution.create

        Assert.Equal(want, Result.isOk actual)

    [<Fact>]
    let ``distribution preserves labels independent of caller order`` () =
        let actual =
            [ "Deep", 0.1; "Economy", 0.8; "Balanced", 0.1 ]
            |> TierDistribution.create
            |> Fixtures.valid
            |> TierDistribution.probabilities
            |> Map.ofList

        Assert.Equal(0.8, actual[Tier.Economy])
        Assert.Equal(0.1, actual[Tier.Balanced])
        Assert.Equal(0.1, actual[Tier.Deep])

    [<Theory>]
    [<InlineData(Double.NaN, 0.15)>]
    [<InlineData(Double.PositiveInfinity, 0.15)>]
    [<InlineData(Double.NegativeInfinity, 0.15)>]
    [<InlineData(-0.01, 0.15)>]
    [<InlineData(1.01, 0.15)>]
    [<InlineData(0.8, Double.NaN)>]
    [<InlineData(0.8, Double.PositiveInfinity)>]
    [<InlineData(0.8, Double.NegativeInfinity)>]
    [<InlineData(0.8, -0.01)>]
    [<InlineData(0.8, 1.01)>]
    let ``thresholds must be finite within the unit interval`` confidence margin =
        Assert.Equal(Error ValidationError.InvalidThresholds, Thresholds.create confidence margin)

    [<Theory>]
    [<InlineData(0.0, 0.0)>]
    [<InlineData(1.0, 1.0)>]
    let ``threshold endpoints are valid`` confidence margin =
        Assert.True(Thresholds.create confidence margin |> Result.isOk)
