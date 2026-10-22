# Generic question and request construction

`Rlcd` constructs and validates domain-independent choice, ordinal-score, and
boolean questions over textual state. This API is pure: no IO, tokenizer, model
loading, or inference is performed. A successful construction result is **not**
a model answer, token-fit guarantee, or native certification.

This is independent preparation only. The original G0 artifact/oracle gate and
the complete T1 acceptance gate remain open; native inference is unavailable.
See [the model contract](model-contract.md) for the remaining native requirements.
The separate [sequence and decoding API](preprocessing.md) now supports
caller-supplied encoders and logits; construction itself remains unchanged.

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

## Executable example

From the repository root, using the already provisioned SDK and restored local
packages:

```bash
dotnet build src/Rlcd/Rlcd.fsproj -c Release --no-restore
./examples/GenericDecisions.fsx
./examples/GenericDecisions.fsx --state 'The customer cannot access paid features.'
```

[GenericDecisions.fsx](../examples/GenericDecisions.fsx) references only the built
core assembly, with no NuGet directive or network access. It constructs customer
triage questions for category (choice), urgency (score), and follow-up (boolean).
It emits JSON with `status="constructed"`, `inferenceAvailable=false`, the exact
state, questions/options, budgets, and policy. These are **request descriptions,
not model answers**. Invalid state or arguments emit an error description and
exit 2; valid construction exits 0. Build first so the referenced assembly is
current.

Here is a minimal construction example; the executable example above expands
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

The unit tests use synthetic text only:

```bash
dotnet test tests/Rlcd.Tests/Rlcd.Tests.fsproj -c Release --no-restore \
  --filter 'FullyQualifiedName~QuestionTests|FullyQualifiedName~ApiTests'
```

Rendering, sequence assembly and supplied-logit decoding are documented in
[preprocessing.md](preprocessing.md). Native inference, a tokenizer implementation,
model-specific compatibility and independent oracle parity remain unavailable.
