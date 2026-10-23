# Generic decisions: construction and inference

`Rlcd` constructs and validates domain-independent choice, ordinal-score, and
boolean questions over textual state. This API is pure: no IO, tokenizer, model
loading, or inference is performed. A successful construction result is **not**
a model answer, token-fit guarantee, or native certification.

Request construction does not establish artifact integrity or independent
tokenizer/model parity. The [inference API](#inference-api) below loads a verified
local bundle and executes the real CPU backend.
See [the model contract](model-contract.md) for the remaining certification requirements.
The separate [tokenization, sequence and decoding API](preprocessing.md) supports
the included tokenizer, caller-supplied encoders and supplied logits;
construction itself remains unchanged.

## Construction API

The following are signature-form declarations of the implemented constructors.
All referenced types are in namespace `Rlcd`; there is no router dependency.

```fsharp
namespace Rlcd

[<RequireQualifiedAccess>]
module QuestionId =
    val create: text: string -> Result<QuestionId, ValidationError list>
    val value: id: QuestionId -> string

[<RequireQualifiedAccess>]
module OptionId =
    val create: text: string -> Result<OptionId, ValidationError list>
    val value: id: OptionId -> string

[<RequireQualifiedAccess>]
module ChoiceOption =
    val create:
        id: string -> label: string -> criterion: string option ->
            Result<ChoiceOption, ValidationError list>
    val id: option: ChoiceOption -> OptionId
    val label: option: ChoiceOption -> string
    val criterion: option: ChoiceOption -> string option

[<RequireQualifiedAccess>]
module Question =
    val choice:
        id: string -> instructions: string -> options: ChoiceOption list ->
            Result<Question, ValidationError list>
    val score:
        id: string -> instructions: string -> options: ChoiceOption list ->
            Result<Question, ValidationError list>
    val boolean:
        id: string -> instructions: string -> options: (ChoiceOption * ChoiceOption) option ->
            Result<Question, ValidationError list>
    val id: question: Question -> QuestionId
    val instructions: question: Question -> string
    val kind: question: Question -> QuestionKind
    val options: question: Question -> ChoiceOption list

[<RequireQualifiedAccess>]
module TokenBudget =
    val create:
        sequenceLength: int -> headLength: int -> optionTokens: int ->
            Result<TokenBudget, ValidationError list>
    val defaults: TokenBudget
    val sequenceLength: budget: TokenBudget -> int
    val headLength: budget: TokenBudget -> int
    val optionTokens: budget: TokenBudget -> int

[<RequireQualifiedAccess>]
module RequestOptions =
    val create:
        budget: TokenBudget -> truncation: TruncationPolicy ->
            Result<RequestOptions, ValidationError list>
    val defaults: RequestOptions
    val budget: options: RequestOptions -> TokenBudget
    val truncation: options: RequestOptions -> TruncationPolicy

type DecisionRequestInput =
    { State: string; Questions: Question list; Options: RequestOptions }

[<RequireQualifiedAccess>]
module DecisionRequest =
    val create: input: DecisionRequestInput -> Result<DecisionRequest, ValidationError list>
    val createBatch:
        inputs: DecisionRequestInput list -> Result<DecisionRequest list, ValidationError list>
    val state: request: DecisionRequest -> string
    val questions: request: DecisionRequest -> Question list
    val options: request: DecisionRequest -> RequestOptions
```

`QuestionId` and `OptionId` are private single-case reference DUs.
`ChoiceOption`, `Question`, `TokenBudget`, `RequestOptions`, and `DecisionRequest`
are private immutable records. Use their companion constructors and accessors.
`DecisionRequestInput` is an
unvalidated immutable record, intentionally public for record-expression
construction and batch validation. Constructors that accept strings are the
raw-input validation boundary; returned IDs are distinct types.

`QuestionKind` has qualified cases `Choice`, `Score`, and `Boolean`.
`TruncationPolicy` has qualified cases `Reject` and `TruncateWithDiagnostics`.

## Semantics and validation

- Required text—state, IDs, instructions, labels, and supplied criteria—must not
  be null, empty, or whitespace-only. `None` means no criterion was supplied;
  `Some ""`, `Some null`, and whitespace-only criteria are errors.
- Accepted strings are preserved exactly. There is no trimming, case folding,
  normalization, mask replacement, or implicit serialization. Callers serialize
  non-text state themselves; arbitrary Python object serialization is unsupported.
- Every choice/score has at least two options. Option IDs and labels must be
  unique within that question, using exact case-sensitive comparison. IDs may be
  reused in other questions. Question IDs must be unique within a request and may
  be reused in another batch item.
- All three kinds use `ChoiceOption`. For score questions, list position defines
  the ordinal level: first is zero, second is one, and so on. Supply the ordered
  levels' names and optional criteria, not numerical predictions or weights.
  Construction does not render model prompt text or produce a score.
- For boolean, `None` creates two options with IDs/labels `"false"` and `"true"`
  and no supplied criteria. `Some (falseOption, trueOption)` accepts exactly two
  custom options in **false-then-true semantic order**, regardless of their
  labels or IDs. Both still require unique IDs and labels. If both criteria are
  supplied, they must be distinct too.
- Collections are F# immutable lists. Options/questions/batches retain input
  order; no map sorting changes positions. When starting with caller-owned
  arrays, use `List.ofArray` at the boundary to snapshot their elements. Every
  stored element is immutable, so later array replacement cannot alter a request.
- Fabricated null reference values, null collections/elements, and null policies are
  rejected at construction boundaries. Accessors expect successfully constructed
  values, not values fabricated with `Unchecked.defaultof`.

### Budgets and truncation

`TokenBudget.defaults` is sequence/head/per-option **512/192/48**.
`RequestOptions.defaults` uses that budget and **Reject**.
`TokenBudget.create` checks `0 < optionTokens <= headLength < sequenceLength`
without arithmetic that can overflow.

These are generic relationships only. They do not prove that instructions,
markers, multiple options, or state fit after tokenization. No model-specific
maximum is enforced here. Later tokenization and model validation must check
actual token counts, usable option spans, special tokens, and model limits.

`TruncateWithDiagnostics` records a caller's explicit policy; construction
neither truncates nor emits fabricated token diagnostics. Text remains exact
under both policies.

### Typed errors and complete batches

`ValidationError` cases are:

- `InvalidText of field: string`
- `MissingValue of field: string`
- `TooFewOptions of count: int`
- `DuplicateOptionId of id: string`
- `DuplicateOptionLabel of label: string`
- `DuplicateBooleanCriterion of criterion: string`
- `EmptyQuestions`
- `DuplicateQuestionId of id: string`
- `NonPositiveBudget of field: string * value: int`
- `HeadBudgetNotBelowSequence`
- `OptionBudgetExceedsHead`
- `EmptyBatch`
- `InvalidBatchItem of index: int * errors: ValidationError list`

`DecisionRequest.createBatch` checks every supplied input record, not just a
prefix up to the first invalid request. Success contains the entire batch in
input order. Failure contains every invalid item's zero-based index and errors,
with no successful subset. Empty and null batches are errors. Questions/options
are individually smart-constructed before they enter a request; callers must
handle those constructor errors too, rather than silently dropping failed values.

## Build-target example

From the repository root, using the already provisioned SDK and local
packages:

```bash
./build.fsx -t Example
RLCD_EXAMPLE_STATE='The customer cannot access paid features.' ./build.fsx -t Example
```

[build.fsx](../build.fsx) loads the core construction sources for `Example`;
no prebuilt library or model is needed. It constructs customer
triage questions for category (choice), urgency (score), and follow-up (boolean).
It emits a compact JSON line amid FAKE logs with `status="constructed"`,
`inferenceAvailable=false`, the exact
state, questions/options, budgets, and policy. These are **request descriptions,
not model answers**. Invalid state emits an error description and fails the target
with exit `1`; valid construction exits `0`. Unset `RLCD_EXAMPLE_STATE` selects
the sample; an explicitly empty or whitespace-only value is invalid.

Here is a minimal construction example; the build target above expands
this same pattern to all three question kinds:

```fsharp
open Rlcd

let request =
    Question.boolean "follow-up" "Does this ticket need a reply?" None
    |> Result.bind (fun question ->
        DecisionRequest.create
            {
                State = "The customer asks when a refund will arrive."
                Questions = [ question ]
                Options = RequestOptions.defaults
            })

match request with
| Ok value ->
    printfn "Constructed %d question(s); no inference was run."
        (value |> DecisionRequest.questions |> List.length)
| Error errors -> printfn "Invalid request: %A" errors
```

The unit tests use synthetic text only and run with the repository test target:

```bash
./build.fsx -t Tests
```

Rendering, sequence assembly and supplied-logit decoding are documented in
[preprocessing.md](preprocessing.md), including the supported tokenizer.
These standalone transformations remain usable without native inference.

## Inference API

`DecisionModel` owns one real CPU ONNX Runtime session. There is no router
dependency, public fake backend, implicit download, or arbitrary-graph loader.
The following are signature-form declarations, not a standalone F# script:

```fsharp
type DecisionModel =
    interface System.IDisposable
    interface System.IAsyncDisposable
    static member Load:
        modelDirectory: string * manifestPath: string ->
            Result<DecisionModel, ModelLoadError>
    member Predict:
        request: DecisionRequest * cancellationToken: System.Threading.CancellationToken ->
            System.Threading.Tasks.Task<Result<DecisionResult, PredictionError>>
    member PredictBatch:
        requests: DecisionRequest list * cancellationToken: System.Threading.CancellationToken ->
            System.Threading.Tasks.Task<Result<DecisionResult list, PredictionError>>

[<RequireQualifiedAccess>]
module DecisionResult =
    val answers: result: DecisionResult -> Answer list
    val diagnostics: result: DecisionResult -> SequenceDiagnostic list list
    val usage: result: DecisionResult -> PredictionUsage
    val provenance: result: DecisionResult -> ModelProvenance

type PredictionUsage =
    { QuestionCount: int
      InputTokens: int
      PaddedTokens: int
      NativeRuns: int }

type ModelProvenance =
    { BundleId: string
      GraphSha256: string
      ArtifactHashes: (string * string) list
      RuntimeVersion: string
      ExecutionProvider: string }
```

`Load` is synchronous, noncancellable local IO/native construction. It validates
the manifest, every artifact's size/hash, supported tokenizer/calibration
configuration, and exact native input/output names, types, ranks and dimensions.
Keep the input files immutable during loading; this is not a filesystem sandbox.
Successful loading establishes that those inputs can be used by this backend,
not independent parity or official-checkpoint weight equivalence.

Pass an explicit cancellation token to prediction (`CancellationToken.None`
when none is needed). Both methods return a `Task<Result<_, PredictionError>>`.
They preserve request, question and option order, including reused question IDs
across different requests. The whole batch is prepared before the first native
run. A bad later request makes zero native runs; a failed later chunk discards
all accumulated answers, never returning partial success.

### Results and bounds

Each `DecisionResult` is an immutable aggregate. Answers use the existing
choice, ordinal-score and boolean semantics described in
[preprocessing.md](preprocessing.md). Sequence diagnostics are grouped in
question order; calibration diagnostics remain on each `Answer`.
Provenance reports verified hashes, bundle identity, actual runtime version and
the selected CPU provider, not a certification flag.

Usage is per request: `InputTokens` sums assembled, unpadded question lengths
after any diagnosed truncation; `PaddedTokens` sums full native chunk row widths,
**not just added padding**. `NativeRuns` counts chunks touching the request.
A shared chunk counts for every participating request, so those counts are
not globally additive.

The current limits are one active call per model, **64 requests**, **128 total
questions**, and native chunks of at most **8 rows**. Another concurrent call
immediately returns `CapacityExceeded("activeCalls", 1)`; there is no queue.
Model budget ceilings are sequence/head/per-option **512/192/48**.
These bound admitted calls and native tensors, not the work/memory of encoding
arbitrarily long input text. The synchronous tokenizer is not preemptible.
CPU execution is sequential, with one inter-op thread and at most four intra-op
threads. No performance or latency guarantee is implied.

### Cancellation and resource lifetime

Caller cancellation returns `Error PredictionError.Cancelled`, not a cancelled
task. Native work receives termination through `RunOptions.Terminate`.
Cancellation during synchronous preparation is observed when preparation
returns; an individual tokenizer call cannot be interrupted.
The implementation awaits its admitted worker and disposes per-call tensors,
outputs, run options, registrations and linked token sources.

`IDisposable.Dispose` immediately closes admission and requests cancellation.
If idle, it releases the session before returning. If a call is active, that
call owns cleanup and returns `Disposed` on normal shutdown.
**`IAsyncDisposable.DisposeAsync` is the joining operation**: await it when the
session must be released before proceeding. Subsequent predictions return
`Disposed`; repeated disposal does not release the session twice.
Cleanup exceptions are failures, not proof of successful release.
A recoverable native run failure does not itself dispose the model.

### Typed failures

Both error unions require qualified case access:

```fsharp
type ModelLoadError =
    | InvalidArgument of field: string
    | MissingArtifact of path: string
    | InvalidBundle of message: string
    | IncompatibleModel of details: string list
    | NativeFailure of message: string
    | IoFailure of message: string

type PredictionError =
    | InvalidRequests of ValidationError list
    | ModelLimitExceeded of requestIndex: int * field: string * requested: int * maximum: int
    | CapacityExceeded of resource: string * maximum: int
    | SequenceFailure of SequenceError list
    | InvalidOutput of details: string list
    | CalibrationFailure of questionIndex: int * errors: CalibrationError list
    | NativeFailure of message: string
    | Cancelled
    | Disposed
```

Indexes are zero-based; calibration indexes flatten request order then question
order. Model-limit errors identify the first exceeded bound. Expected artifact,
runtime and output-contract failures become typed errors. Unexpected/fatal CLR
failures are not swallowed; disposal failures may fault the asynchronous join.
Callers must handle load and prediction errors independently.

### Real execution versus certification

Use `./build.fsx -t CheckInference` to exercise the public loader, both prediction
methods, all question kinds and asynchronous disposal against the selected local
cache. Missing inputs fail rather than skip. This command has passed on Linux
ARM64 with CPU ONNX Runtime 1.29.0.

Ordinary `Tests` excludes `RealInference` and needs no model bytes. Controlled
runtime tests cover failure paths, ordering, capacity and lifecycle; actual
mid-kernel cancellation timing and independent tokenizer/logit/probability parity
are not established by the real smoke test. `VerifyModels` remains blocked on
independent certification, not on the existence of this implementation.
