# Router composition, catalog and selection policy

`Rlcd.Router` combines inference-backed task classification with a pure catalog
and selection policy. Composition borrows a caller-loaded `Rlcd.DecisionModel`;
the generic core remains independent of routing. The existing `Policy.select`
API still accepts **precomputed** three-tier probabilities and performs no IO.
Neither layer calls a generation provider, discovers model availability, or
dispatches a child.

Passing these synthetic policy tests does not certify native inference,
calibration, model-selection quality, or MCP hosting. A real-model composition
smoke test establishes callability, not whether a task was assigned the right tier.
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
The policy does not interpret task text. Composition submits task text as state
for a fixed tier question; it does not infer language from character ranges.

## Selection contract

`SelectionRequest` contains a validated `TierDistribution`, `Requirements`,
explicit `AvailableModelIds: Set<ModelId>`, `ModelPreference`, and four evidence
flags. All four flags must be established true by composition:

1. `SupportedEnglish`: input is within the supported English scope.
2. `WithinBudget`: classifier input fits its configured model budget.
3. `NotTruncated`: no relevant classifier input was truncated.
4. `NotCollapsed`: classification options remained distinguishable.

Unknown evidence is **false**, never assumed safe. The policy does not establish
these facts itself. Budget-fit evidence is not an independent model certificate.
Generation requirements may be thousands of tokens even though
the classifier has a 512-token input budget; those are separate limits.
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

## Composition API

All router types are in `Rlcd.Router`. Construct a `TaskText` before building
the request. These are signature-form declarations:

```fsharp
[<RequireQualifiedAccess>]
module TaskText =
    val maximumLength: int
    val create: raw: string -> Result<TaskText, ValidationError>
    val value: text: TaskText -> string

[<RequireQualifiedAccess>]
type TaskSupport =
    | SupportedEnglishInScope
    | UnsupportedEnglish
    | UnsupportedScope
    | Unknown

type RoutingRequest =
    {
        Task: TaskText
        Support: TaskSupport
        Requirements: Requirements
        AvailableModelIds: Set<ModelId>
        Preference: ModelPreference
    }

type Router =
    new: model: Rlcd.DecisionModel * thresholds: Thresholds * catalog: Catalog -> Router
    member Recommend:
        request: RoutingRequest * cancellationToken: System.Threading.CancellationToken ->
            System.Threading.Tasks.Task<Result<RoutingResult, RoutingError>>

[<RequireQualifiedAccess>]
module Rubric =
    val version: string
    val question: unit -> Result<Rlcd.Question, Rlcd.ValidationError list>
    val request: text: TaskText -> Result<Rlcd.DecisionRequest, Rlcd.ValidationError list>
```

`TaskText` is a private single-case reference DU. `create` rejects null/blank
text with `InvalidTaskText`, and more than **8192 UTF-16 code units** with
`TaskTextTooLong(actual, maximum)`. It preserves accepted text exactly, without
trimming, normalization or clipping. This finite text bound is not a token-fit
guarantee; shorter tasks can still exceed the classifier's token budget.

`Rubric.version` is `router-task-tiers-v1`. The single choice question uses that
question ID and exactly the option IDs/labels `Economy`, `Balanced`, `Deep`, in
that order. `Rubric.request` uses the generic default budgets **512/192/48** and
**Reject**. Task text is state, never a source of replacement criteria.

The `Router` constructor borrows a non-null, already-loaded model with a fixed
validated catalog and thresholds. It neither loads nor owns that model and has
no disposal operation. The caller remains responsible for `DecisionModel`
shutdown and asynchronous disposal.

For example, given a caller-owned model and validated catalog, requirements and
availability, this boundary function reports both construction and prediction
failures. The example's `SupportedEnglishInScope` is an explicit caller assertion,
not a language-detection result:

```fsharp
open Rlcd.Router

let recommend
    (model: Rlcd.DecisionModel)
    catalog
    requirements
    available
    rawTask
    cancellationToken
    = task {
        match TaskText.create rawTask with
        | Error error -> printfn "Invalid task: %A" error
        | Ok text ->
            let router = Router(model, Thresholds.defaults, catalog)
            let request =
                {
                    Task = text
                    Support = TaskSupport.SupportedEnglishInScope
                    Requirements = requirements
                    AvailableModelIds = available
                    Preference = ModelPreference.Automatic
                }
            let! result = router.Recommend(request, cancellationToken)
            match result with
            | Error error -> printfn "Routing failed: %A" error
            | Ok result -> printfn "%A" (RoutingResult.decision result)
    }
```

This does not dispatch the selected model or transfer ownership of the
classifier. Reuse a `Router` instance when its catalog and thresholds are
unchanged. Concurrent supported requests inherit the borrowed model's immediate
capacity rejection; there is no extra queue, session clone or retry.

### Results, errors and provenance

`RoutingResult` is an immutable opaque aggregate with qualified accessors:

| Accessor | Returned value |
| --- | --- |
| `decision` | Existing `SelectionDecision`: eligible model ID or explicit abstention |
| `distribution` | `TierDistribution option`; no invented distribution on bypass or preparation failure |
| `tier` | Unique highest-probability `Tier option`; `None` for ties or absent evidence |
| `confidence` / `margin` | `float option`, derived from the validated probabilities |
| `diagnostics` | Original `Rlcd.SequenceDiagnostic list` |
| `calibrationDiagnostics` | Original `Rlcd.CalibrationDiagnostic list`; temperatures are not reapplied |
| `usage` | Actual `Rlcd.PredictionUsage option` |
| `provenance` | `RoutingProvenance` |

An available tier/confidence is evidence, **not permission to dispatch**:
`decision` may still be an abstention because the input was altered or no
candidate was eligible.

`RoutingProvenance` contains `RubricVersion`, `PolicyVersion`,
`CatalogSchemaVersion`, `CatalogRevision`, the exact `Thresholds`, and
`Model: Rlcd.ModelProvenance option`. `Policy.version` is `router-selection-v1`.
`Catalog.version` remains the schema version; the new `Catalog.revision`
returns a `sha256:` content digest covering ordered entries and all capability
fields, not just the schema. Its deterministic encoding length-prefixes fields
and hashes exact little-endian UTF-16 code units without normalization.
Actual model provenance is present only when supplied by successful inference.
It is absent on bypass/preparation failure, not fabricated from a model label.

Failure and abstention are distinct:

| Outcome | Meaning |
| --- | --- |
| `Ok` + `PreserveExplicitChoice id` | Exact fixed choice preserved without inference, even if support is unknown or the token is already cancelled |
| `Ok` + `UnsupportedEnglish`, `UnsupportedScope` or `UnknownSupport` | Caller-established unsupported/unknown input; no inference |
| `Ok` + `InputRejected predictionError` | Model-limit or wholly fit/loss-related sequence failure; complete original error and nested indexes retained |
| `Ok` + `InputAltered diagnostics` | Successful inference reported a preparation alteration; original diagnostics and available model evidence retained |
| `Error (RequestConstruction errors)` | Generic question/request construction failed |
| `Error (InvalidOutput(details, provenance))` | Returned evidence violates the fixed single-question output contract |
| `Error (PredictionFailed error)` | Other core failure, including busy, cancelled, disposed, encoder, native or calibration failure |

The last three cases are qualified `RoutingError` cases. A mixed sequence failure
containing encoder/contract errors is not treated as ordinary lack of fit.
Supported automatic calls forward the caller's cancellation token and preserve
the core's admission/error precedence: busy or disposed can precede cancellation.
Unexpected task faults propagate rather than becoming ordinary abstentions;
`Task<Result<...>>` is not a promise that every CLR failure is caught.
Original backend error messages are retained, so callers must apply their own
logging/privacy policy rather than assume arbitrary error text is sanitized.

### Composition boundary

The classifier's three options and rubric are fixed independently of task text.
Task prose cannot add catalog entries, claim availability, replace generation
requirements, or supply a dispatch command. The model provides probabilities;
the existing policy remains the authority for selecting an eligible ID.
Structural separation does not prove that model predictions resist adversarial
wording or accurately measure task difficulty.

The caller establishes supported-English scope and owns the loaded model's
lifetime. Unknown language/scope must not be promoted to supported merely
because the text is ASCII. A fixed model preference bypasses inference rather
than asking the classifier to reinterpret the user's choice.

Successful core inference alone is not proof of intact input: preparation can
report truncation, clipping, empty encodings, collapsed options or mask-text
replacement. This composition conservatively returns `InputAltered` for
**any** sequence diagnostic, including mask replacement that generic core
inference permits under Reject. It accounts for actual preparation evidence,
not just token usage or a winning option. It uses already-calibrated option
probabilities, not action probability or entropy confidence, and does not
apply a second softmax. In particular, core choice tie-breaking is not a
routing recommendation: equal top tier probabilities still cause abstention.

Model loading, acquisition, registration and dispatch remain caller concerns.
The router does not own or dispose the borrowed `DecisionModel`, introduce an
unbounded work queue, or silently turn native/cancellation failures into
confident fallback selections. See the [generic inference contract](generic-api.md#inference-api)
for the model's admission, cancellation and disposal behavior.

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

## Repository validation

From the repository root, using the pinned SDK and configured NuGet sources:

```bash
./build.fsx -t All
```

This restores, builds, tests, and packages both libraries. Ordinary tests use a
controlled router-local inference boundary and require no model files. They
reuse the core decoder for valid synthetic answers rather than copying softmax
or accessing private core representations.

For explicit actual core and router execution against the selected local cache:

```bash
./build.fsx -t CheckInference
```

`CheckInference` runs the solution's `RealInference` category. Missing inputs
fail, never skip or download. The router smoke exercises the actual fixed
rubric, provenance, long-input rejection and reuse of the caller-owned model.
It accepts a justified abstention rather than asserting that a sample task
must select a particular tier. The observed default-threshold result for the
sample was low-confidence abstention; this is not a measured routing-quality
or coverage result.

`build.fsx` is the only approved build entry point; use `-t Tests` when
packaging is not needed.
