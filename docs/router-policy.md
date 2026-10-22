# Pure router catalog and selection policy

`Rlcd.Router` is a standalone pure library. It validates a catalog and consumes
**precomputed** three-tier probabilities. It does not classify prose, load a
model, call a provider, discover availability, or dispatch a child. It currently
has no reference to the generic `Rlcd` library. A later composition layer can
adapt generic inference results without leaking core-specific types into this API.

Passing these synthetic policy tests does not certify native inference,
calibration, model-selection quality, or MCP hosting.
Both libraries and their tests participate in the root solution build.

## Construction and validation

All constructors return `Result<_, ValidationError>`. Validation errors are not
ordinary abstention reasons. `ModelId` and `GenerationTokens` are private
single-case reference DUs; validated composite values use private immutable
records. Construction/read-out goes through their qualified modules.

| API | Contract |
| --- | --- |
| `ModelId.create` / `value` | Exact case-sensitive ID, nonempty, no whitespace or control characters. No normalization or entitlement inference. |
| `GenerationTokens.create` / `value` | Positive generation-model token quantity, distinct from the classifier's input budget. |
| `Requirements.create tools prompt output modalities` | Explicit tool requirement, minimum prompt/output capacities, and nonempty required **input** modalities. |
| `Capabilities.create tools maxPrompt maxOutput contextWindow modalities` | Nonempty supported input modalities; positive capacities; individual maxima must not exceed shared context; context must fit at least one prompt and one output token. |
| `Capabilities.supports requirements capabilities` | All tools/modalities and individual capacities must match; prompt plus output must fit shared context. The sum uses 64-bit arithmetic. |
| `Catalog.create version models` | Version must be exactly `router-catalog-v1`; ordered nonempty entries; duplicate IDs rejected, including across tiers. |
| `Catalog.version` / `models` | Read the schema version and immutable ordered entries. |
| `Catalog.eligible tier available requirements catalog` | Intersection of exact tier, configured catalog, explicit available IDs, and required capabilities, retaining catalog order. |
| `TierDistribution.create` | Exactly one each of `"Economy"`, `"Balanced"`, `"Deep"`, in any input order. Every probability finite and in `[0,1]`; absolute sum error at most `1e-9`. No repair or normalization. |
| `TierDistribution.probabilities` / `topTwo` | Canonical labeled probability list / two largest labeled values, retaining visible ties. |
| `Thresholds.create confidence margin` | Both finite in `[0,1]`. Defaults: confidence `0.80`, top-two margin `0.15`. |
| `Policy.select thresholds request catalog` | Returns `SelectionDecision.Select ModelId` or `SelectionDecision.Abstain AbstentionReason`. |

`Requirements.tools`, `prompt`, `output`, and `modalities`, plus
`Thresholds.confidence` and `margin`, expose the corresponding validated values.
`CatalogModel` is an immutable record of validated ID/capabilities and a `Tier`.

No JSON parser or config file is supplied by this library. The catalog
version is validated by its real constructor, not by reflection serialization.
Future configuration IO must strictly validate its own schema before constructing
these values; private domain representations are not serialization DTOs.

## Tier meanings

- **Economy:** clear bounded mechanical work or straightforward explanation with
  complete context.
- **Balanced:** routine implementation/debugging with several connected decisions.
- **Deep:** ambiguous diagnosis, architecture, difficult reasoning, broad
  interacting changes, or explicitly high-consequence work.

These are task-requirement categories, not measured performance or pricing claims.
The policy neither interprets task text nor infers language from character ranges.
The named distribution is supplied by a separate composition layer.

## Selection contract

`SelectionRequest` contains a validated `TierDistribution`, `Requirements`,
explicit `AvailableModelIds: Set<ModelId>`, `ModelPreference`, and four evidence
flags. All four flags must be established true by composition:

1. `SupportedEnglish`: input is within the supported English scope.
2. `WithinBudget`: classifier input fits its certified budget.
3. `NotTruncated`: no relevant classifier input was truncated.
4. `NotCollapsed`: classification options remained distinguishable.

Unknown evidence is **false**, never assumed safe. The policy does not establish
these facts itself. Generation requirements may be thousands of tokens even if
the future classifier has a 512-token input budget; those are separate limits.
Caller-supplied generation counts must be appropriate to the candidate models'
tokenization and limits.

Decision order:

1. `ModelPreference.Fixed id` always yields `PreserveExplicitChoice id`, even
   when the fixed model is unavailable, absent from the catalog, or evidence is
   unsafe. It never recommends a substitute. This reason is not permission to
   dispatch an unavailable model.
2. Unsafe/unknown evidence yields the first applicable structured reason:
   `UnsupportedEnglish`, `OverBudgetOrUnknown`, `TruncatedOrUnknown`, or
   `CollapsedOrUnknown`.
3. Equal top probabilities yield `TiedTiers`, even with zero thresholds.
4. Confidence and margin must meet their inclusive thresholds. Failures carry
   actual and required values in `BelowConfidence` / `BelowMargin`.
5. Select the **first** eligible entry in the winning tier's catalog order.
   An empty eligible pool yields `NoEligibleModel tier`; there is no upgrade,
   downgrade, most-expensive fallback, or provider call.

Probability arithmetic is .NET binary64 (`float`). Threshold comparisons are
ordinary inclusive comparisons of those values; the probability-sum tolerance
is **not** an extra allowance for low confidence, low margin, or near ties.

The result contains no dispatch command, agent, effort/context setting, raw
prompt, or replacement instructions. On abstention the caller retains applicable
explicit choices or its existing default behavior; this library performs neither.

## Caller responsibilities and illustrative metadata

The caller owns accurate capability, availability, and evidence metadata.
String validation does not establish model availability or entitlement.
Availability can change after selection; dispatch-time validation belongs to the
caller. No credential stores or private provider APIs are consulted.

For example, a synthetic Balanced catalog ordered as
`illustrative-balanced-z`, then `illustrative-balanced-a` chooses `z` when both
are eligible, not alphabetically first `a`. If only `a` is available, it chooses
`a`. If neither is eligible, it abstains even if a Deep model is available.
All such IDs in the tests are illustrative, with no current model, pricing, or
performance assertions.

## Offline repository validation

From the repository root, using the existing SDK and local package cache:

```bash
./build.fsx -t All
```

This restores, builds, tests, and packages both libraries.
