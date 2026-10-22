# Generic sequence preparation and answer decoding

These `Rlcd` APIs implement the transformations on either side of a future
ONNX session. They do **not** load or execute a model:

- `Sequence.build` / `Sequence.buildBatch` require a caller-supplied encoder.
- `Answer.decode` requires caller-supplied logits and validated calibration
  configuration.

Use the [question/request API](generic-api.md) to construct inputs first.
There is no router dependency, implemented tokenizer, `DecisionModel` facade,
native certification, or independent model/tokenizer parity claim.
The original [artifact and oracle requirements](model-contract.md) still apply.

## Sequence API

The following signature-form declarations describe the public operations.
All types are in namespace `Rlcd`; companion modules require qualified access.

```fsharp
namespace Rlcd

type ITextEncoder =
    abstract Encode: text: string -> Result<int list, TextEncodingError>

[<RequireQualifiedAccess>]
module TokenId =
    val create: value: int -> Result<TokenId, SequenceError list>
    val value: token: TokenId -> int

[<RequireQualifiedAccess>]
module TokenPosition =
    val create: value: int -> Result<TokenPosition, SequenceError list>
    val value: position: TokenPosition -> int

[<RequireQualifiedAccess>]
module SpecialTokens =
    val create:
        classification: int -> separator: int -> mask: int -> padding: int ->
            maskText: string -> Result<SpecialTokens, SequenceError list>
    val classification: tokens: SpecialTokens -> TokenId
    val separator: tokens: SpecialTokens -> TokenId
    val mask: tokens: SpecialTokens -> TokenId
    val padding: tokens: SpecialTokens -> TokenId
    val maskText: tokens: SpecialTokens -> string

[<RequireQualifiedAccess>]
module QuestionSequence =
    val questionId: sequence: QuestionSequence -> QuestionId
    val optionIds: sequence: QuestionSequence -> OptionId list
    val kind: sequence: QuestionSequence -> QuestionKind
    val inputIds: sequence: QuestionSequence -> TokenId list
    val markerPositions: sequence: QuestionSequence -> TokenPosition list
    val diagnostics: sequence: QuestionSequence -> SequenceDiagnostic list

[<RequireQualifiedAccess>]
module SequenceBatch =
    val sequences: batch: SequenceBatch -> QuestionSequence list list
    val inputIds: batch: SequenceBatch -> TokenId list list
    val attentionMask: batch: SequenceBatch -> int list list
    val markerPositions: batch: SequenceBatch -> TokenPosition list list
    val markerMask: batch: SequenceBatch -> bool list list
    val questionTypes: batch: SequenceBatch -> int list

[<RequireQualifiedAccess>]
module Sequence =
    val build:
        tokens: SpecialTokens -> encoder: ITextEncoder -> request: DecisionRequest ->
            Result<QuestionSequence list, SequenceError list>
    val buildBatch:
        tokens: SpecialTokens -> encoder: ITextEncoder -> requests: DecisionRequest list ->
            Result<SequenceBatch, SequenceError list>
```

`TokenId` and `TokenPosition` are private single-case reference DUs.
`SpecialTokens`, `QuestionSequence` and `SequenceBatch` are private immutable
records. Use their companion modules. Collections are immutable lists;
batch results cannot be altered by
mutating a caller-owned array.

### Encoder and special-token contract

Supply a pure, deterministic `ITextEncoder` that returns the complete encoded
text **without automatic special tokens or truncation**. Report encoding failures
with `Error (TextEncodingError.Failed message)`, not exceptions. Throwing violates
this boundary contract; arbitrary encoder exceptions are not caught or converted
into successful results. Full token output is held in memory so losses can be
detected; the sequence budget is not an encoder-work or memory limit.

Special IDs must be nonnegative and mutually distinct, and mask text must be
nonblank. Read the actual identities from compatible tokenizer sidecars when
implementing an encoder; example or test IDs are not model defaults. Sequence
construction validates nonnegative encoded IDs and rejects unexpected mask IDs,
but cannot prove vocabulary membership or tokenizer compatibility.

The literal mask text is replaced by a space in caller instructions, rendered
option content and state before encoding. Replacements are diagnosed under both
policies. This does not mutate the original request. State is encoded once per
request and reused across that request's questions.

### Rendering, budgets and diagnostics

The sequence format is:

```text
[CLS] <type> question: <instructions> [SEP]
[MASK] option0 [MASK] option1 ... [SEP] state [SEP]
```

The textual type names are `choice`, `score` and Laya's `noul` for boolean.
Choice options render as their label, or `label: criterion`. Score options render
as `level N: criterion`, using zero-based caller order and the label when no
criterion was supplied. Boolean options retain false-then-true semantic order;
each renders its label and criterion, with defaults
`no, the statement does not hold` and `yes, the statement holds`.
Each option text receives a leading space before encoding.

The existing budget defaults remain sequence/head/option **512/192/48**.
The initial option cap excludes its mask marker. If the remaining head allowance
is below 16, option spans are capped again using
`max(4, floor((headLength - 16) / optionCount))`, including each marker.
Instruction tokens then keep at most `max(8, remainingHeadAllowance)` tokens.
State keeps its left prefix in the remaining space, followed by a separator;
the final sequence is clipped to the requested sequence length if necessary.
Small valid construction budgets can therefore still cause losses or errors.

`SequenceDiagnostic` distinguishes mask-text replacements, empty encodings,
per-part token truncation, collapsed options and final sequence clipping.
Parts identify state, question instructions, or a question/option ID pair.
Collapse checks compare token spans before and after final clipping: retaining
all markers does not prove that the options remain distinguishable.

**Reject**, the default, refuses empty encodings, truncation, clipping or
collapsed options through `SequenceError.LossRejected`. Mask replacement alone
is permitted and diagnosed. **TruncateWithDiagnostics** permits those losses
with diagnostics only while every option marker survives. Missing markers
always return `MissingOptionMarkers`; they cannot silently change option-to-logit
alignment. Under the permissive policy, clipping may remove separators or all
state, so inspect diagnostics rather than treating successful assembly as
evidence that the original request was preserved.

Other typed failures cover missing values, invalid token IDs/positions,
conflicting special tokens, encoder failures and malformed encoder output.
Batches reject null/empty inputs and report indexed failures without returning
a successful subset.

### Batch layout

Rows flatten **request order, then question order**. `SequenceBatch.sequences`
also preserves the original grouping. Columns are rectangular immutable lists:

| Column | Padding / meaning |
| --- | --- |
| `inputIds` | Supplied padding token ID |
| `attentionMask` | 1 for actual tokens, 0 for padding |
| `markerPositions` | Zero for padded option slots |
| `markerMask` | True for real options, false for padding |
| `questionTypes` | Choice 0, score 1, boolean 2 |

These are prepared values, not allocated ONNX tensors. A future native adapter
must validate certified dimensions and map them to the required tensor types.

## Supplied-logit decoding API

`AgentConfiguration` is the existing validated local calibration configuration
from `AgentConfiguration.read`; no second parser or inferred default
configuration is introduced. It is a private single-case reference DU wrapping
the temperature map; use its companion accessors rather than its representation.

```fsharp
namespace Rlcd

type QuestionLogits =
    {
        Question: Question
        OptionLogits: float32 list
        OptionMask: bool list
        ActionLogits: float32 list
    }

[<RequireQualifiedAccess>]
type AnswerValue =
    | Choice of OptionId
    | Score of expectedLevel: float
    | Boolean of trueProbability: float

[<RequireQualifiedAccess>]
module Answer =
    val decode:
        configuration: AgentConfiguration -> logits: QuestionLogits ->
            Result<Answer, CalibrationError list>
    val questionId: answer: Answer -> QuestionId
    val value: answer: Answer -> AnswerValue
    val probabilities: answer: Answer -> (OptionId * float) list
    val answerConfidence: answer: Answer -> float
    val entropyConfidence: answer: Answer -> float
    val actionProbability: answer: Answer -> float
    val diagnostics: answer: Answer -> CalibrationDiagnostic list
```

`QuestionLogits` is a raw input record; `Answer` is opaque and immutable.
The mask length must equal the option-logit length, with exactly one true entry
per question option. True entries map to real options in caller order; false
entries are excluded from the option distribution. All supplied logits,
**including padding**, must be finite. The action head requires exactly two
finite logits. Invalid shapes, counts and values produce typed
`CalibrationError` cases, not partial answers.

Temperature selection uses question type and real option-count buckets
**2 / 3-5 / 6-10 / 11+**, with type-base fallback when a bucket is absent.
Boolean maps to Laya's `noul` calibration keys. Finite selected temperatures
clamp to **[0.5, 5.0]**; changes are recorded by
`CalibrationDiagnostic.TemperatureClamped`. Invalid/non-finite configuration is
rejected by the existing reader, not silently replaced by 1.0. No temperature
entry means a typed decoding error.

Softmax widens float32 logits before arithmetic, subtracts the maximum and
normalizes only real options. Returned values keep float64 precision:

| Output | Meaning |
| --- | --- |
| Choice | Stable option ID at maximum probability; the first option wins exact ties |
| Score | Expected zero-based level: sum of `index * probability` |
| Boolean | Probability of the second, semantically true option |
| `probabilities` | Ordered `(OptionId, probability)` pairs |
| `answerConfidence` | Maximum option probability |
| `entropyConfidence` | Normalized concentration, `1 - H(p) / log(optionCount)`, using the reference clipped-log formula |
| `actionProbability` | Class-zero probability from the two action logits, without the answer temperature |

Entropy confidence is not calibrated answer confidence. Action probability is
not authorization or a guarantee of correctness. Configuration provenance does
not establish calibration on the caller's domain.

## Evidence and remaining gates

The algorithms adapt the pinned Laya source identified in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
Tests use synthetic lookup encoders and hand-computed logits, including overflow,
padding, clipping/collapse and invalid-input cases. They do not execute an
independent tokenizer or model oracle.

Run the focused tests after offline restore:

```bash
dotnet test tests/Rlcd.Tests/Rlcd.Tests.fsproj -c Release --no-restore \
  --filter 'FullyQualifiedName~SequenceTests|FullyQualifiedName~CalibrationTests'
```

Native inference still requires provisioned artifacts/runtime, exact tokenizer
fixtures, certified tensor contracts, real-output parity and lifetime checks.
No training, network acquisition, routing policy or provider call occurs here.
