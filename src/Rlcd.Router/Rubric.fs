namespace Rlcd.Router

open System

/// Validated precomputed probabilities for exactly the three router tiers.
type TierDistribution =
    private
        {
            Economy: float
            Balanced: float
            Deep: float
        }

/// Validation and read-out of externally computed tier evidence, not a classifier.
[<RequireQualifiedAccess>]
module TierDistribution =
    let private tier name =
        match name with
        | "Economy" -> Ok Tier.Economy
        | "Balanced" -> Ok Tier.Balanced
        | "Deep" -> Ok Tier.Deep
        | invalid -> Error(ValidationError.InvalidTierName invalid)

    let private named (name, probability) =
        tier name |> Result.map (fun tier -> tier, probability)

    let private probability (tier, value) =
        if Double.IsFinite value && value >= 0.0 && value <= 1.0 then
            Ok(tier, value)
        else
            Error(ValidationError.InvalidProbability tier)

    let private assemble values =
        match List.sortBy fst values with
        | [ (Tier.Economy, economy); (Tier.Balanced, balanced); (Tier.Deep, deep) ] ->
            let sum = economy + balanced + deep

            if abs (sum - 1.0) > 1e-9 then
                Error(ValidationError.InvalidProbabilitySum sum)
            else
                Ok { Economy = economy; Balanced = balanced; Deep = deep }
        | _ -> Error ValidationError.InvalidDistributionTiers

    /// Require exact tier names, finite [0,1] values, and sum within 1e-9 of one.
    /// Does not normalize or repair supplied probabilities.
    let create probabilities : Result<TierDistribution, ValidationError> =
        if List.length probabilities <> 3 then
            Error ValidationError.InvalidDistributionTiers
        else
            probabilities
            |> List.fold
                (fun accumulated item ->
                    accumulated
                    |> Result.bind (fun values ->
                        item
                        |> named
                        |> Result.bind probability
                        |> Result.map (fun value -> value :: values)))
                (Ok [])
            |> Result.bind assemble

    /// Return named probabilities in Economy, Balanced, Deep order.
    let probabilities (distribution: TierDistribution) : (Tier * float) list =
        [
            Tier.Economy, distribution.Economy
            Tier.Balanced, distribution.Balanced
            Tier.Deep, distribution.Deep
        ]

    /// Return the two largest labeled probabilities; ties remain visible in their values.
    let topTwo (distribution: TierDistribution) : (Tier * float) * (Tier * float) =
        let economy = Tier.Economy, distribution.Economy
        let balanced = Tier.Balanced, distribution.Balanced
        let deep = Tier.Deep, distribution.Deep
        let first, second =
            if snd economy >= snd balanced then economy, balanced else balanced, economy

        if snd deep > snd first then deep, first
        elif snd deep > snd second then first, deep
        else first, second

/// Validated inclusive thresholds for confidence and top-two margin.
type Thresholds =
    private
        {
            Confidence: float
            Margin: float
        }

/// Conservative policy thresholds, independent of probability-sum tolerance.
[<RequireQualifiedAccess>]
module Thresholds =
    /// Require each threshold to be finite and in [0,1].
    let create confidence margin : Result<Thresholds, ValidationError> =
        let valid value = Double.IsFinite value && value >= 0.0 && value <= 1.0

        if valid confidence && valid margin then
            Ok { Confidence = confidence; Margin = margin }
        else
            Error ValidationError.InvalidThresholds

    /// Default minimum confidence 0.80 and minimum top-two margin 0.15.
    let defaults: Thresholds = { Confidence = 0.80; Margin = 0.15 }

    /// Read the minimum confidence.
    let confidence (thresholds: Thresholds) : float = thresholds.Confidence

    /// Read the minimum top-two margin.
    let margin (thresholds: Thresholds) : float = thresholds.Margin
