# Laya English model contract

## Status and evidence boundary

**Verified local integrity and real CPU execution; independent parity is not certified.**

The selected community English graph and three sidecars have been acquired
into `~/.cache/rlcd/laya-english`, verified, and exercised through
`DecisionModel.Load`, `PredictBatch`, and `Predict` on Linux ARM64 with ONNX
Runtime 1.29.0 / CPUExecutionProvider. No model files are committed or packaged.
The official checkpoint and independent tokenizer/model oracle are not provisioned.

`DownloadModels` creates `laya-english.manifest.json` inside the selected cache.
`RLCD_MODELS_DIR` overrides the cache and `RLCD_MODEL_MANIFEST` overrides the
manifest read by checks. The schema is defined by the bundle validator.
Missing hashes, fixture outputs, or certification results must not be replaced
with invented values, empty-string hashes, or all-zero placeholders.

Keep these evidence levels separate:

1. **Reported published metadata:** pinned Hub tree/card/config/LFS information.
   Exporter parity and license metadata remain publisher reports.
2. **Verified local integrity:** actual sizes and hashes below, including
   sidecar Git-blob identities checked against the pinned repository tree.
3. **Observed native callability:** the explicit `CheckInference` test loads the
   graph, predicts all question kinds and batched states, checks finite normalized
   answers/order/provenance, reuses the session, and disposes it. This is not a
   comparison against an independent implementation.
4. **Unverified certification:** independent token IDs/sequences/logits,
   official-checkpoint weight lineage, comprehensive dynamic-shape coverage,
   real mid-kernel cancellation timing, performance and routing quality.

Neither a model card nor a matching configuration proves numerical parity.
Reference source code alone is not a runnable, provisioned reference oracle.

## Selected published candidate

Published source identity and the locally verified graph identity:

| Property | Value |
| --- | --- |
| Repository | `mariojcr/laya-onnx` |
| Revision | `aeebb05497cc473360caf4de1653107cf08d6b10` |
| Graph | `english/laya.onnx` |
| Verified size | `1688711336` bytes |
| Verified SHA-256, matching published LFS identity | `ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e` |
| Sidecars | `english/tokenizer.json`, `english/tokenizer_config.json`, `english/rl_agent_config.json` |
| Sidecar SHA-256 | Locally measured; see below |
| Publisher claims | FP32, opset 18, inline weights, dynamic dimensions, parity on two shapes using ORT 1.29 |
| Card license | Apache-2.0; reported metadata, not a local artifact license audit |

Pinned source locator:
<https://huggingface.co/mariojcr/laya-onnx/tree/aeebb05497cc473360caf4de1653107cf08d6b10/english>.
This link records provenance. The explicit `DownloadModels` target implements
the separately approved acquisition; inference never downloads missing files.

### Verified bundle bytes

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `laya.onnx` | 1688711336 | `ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e` |
| `tokenizer.json` | 3583228 | `6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30` |
| `tokenizer_config.json` | 124 | `2dc9a079ca6fca477b0f69d291eb9464be51d0d1e1f7753095b5c85719cf9341` |
| `rl_agent_config.json` | 745 | `ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd` |

The acquisition checks sidecar Git-blob SHA-1 values
`2f4d8583e507b7466d2490e2d6c045647a822698`,
`8e705ec8ffe005917120c14d5b530edce3221217`, and
`3e4fcbf12cf36164ce18a1398aa9f35f58375ae0`, respectively.
These hash `"blob <length>\0" + bytes`; they are not file SHA-256 values.
The manifest records the actual SHA-256 values above. Repeated acquisition
reuses verified files without fetching them again.

The authoritative English checkpoint candidate is
`convaiinnovations/laya@55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851`.
Reported metadata describes a public, ungated Apache-2.0 checkpoint with
ModernBERT-large weights and no ONNX file in the pinned official tree. The exact
relationship between those weights and the selected community export remains
**unproven**.

## Expected tokenizer and sequence behavior

The acquired tokenizer describes an NFC-normalized ByteLevel BPE pipeline
with `add_prefix_space=false`, `use_regex=true`, and added tokens including
whitespace runs with normalization/strip flags. Support must be derived from the
pinned sidecars and unknown tokenizer schemas rejected explicitly.

The repository implements a supported NFC/ByteLevel/BPE subset; see the
[tokenizer API and limits](preprocessing.md). Synthetic mechanism tests and
Unicode word-property table checks are not certification of the selected
checkpoint's exact output IDs, even though the real sidecars load successfully.

The reported CLS/SEP/PAD/MASK IDs are `50281/50282/50283/50284`. These are
cross-check expectations only: derive and validate the actual IDs from the
sidecars, never silently substitute these constants. They are not golden
tokenizations of any input.

The locally inspected Laya source at
`9d955671415fc19f069b9cc998928075c1f255ec` confirms:

- `laya/common.py:17`: choice/score/noul type indices are `0/1/2`.
- `laya/common.py:106-212`: ordered option rendering and sequence assembly,
  plain encoding without automatic special tokens, default `max_len=512` and
  `head_max_len=192`, a 48-content-token option cap before budget compression,
  mask-text sanitization, marker positions, and default right truncation.
- The sequence is `[CLS] <type> question: <instructions> [SEP]`, followed by
  `[MASK] option0 [MASK] option1 ... [SEP] state [SEP]`.
- Option encoding adds a leading space before rendered option text. Head
  compression and final slicing can lose or collapse options; marker count
  alone is insufficient evidence of distinguishable options.
- `laya/common.py:504-523`: finite temperatures are clamped to `[0.5,5.0]`.
  The F# contract additionally requires rejecting invalid/non-finite
  configuration rather than inheriting the source's neutral fallback.

These are source observations, not execution results against the selected
tokenizer. FastCoref's RoBERTa/Longformer byte-BPE implementation is architectural
prior art, not a ModernBERT tokenizer oracle or a drop-in replacement.

## Enforced graph interface, pending independent certification

The locally inspected `scripts/export_onnx.py:49-116` names the following
inputs/outputs, requests opset 18, and declares dynamic axes. It uses a dummy
shape of one row, 16 sequence tokens, and two markers. Inspecting that exporter
does not establish what is inside the separately published graph.

| Tensor | Required element type | Expected shape |
| --- | --- | --- |
| `input_ids` | int64 | `[questions, sequence_length]` |
| `attention_mask` | int64 | `[questions, sequence_length]` |
| `marker_pos` | int64 | `[questions, option_count]` |
| `marker_mask` | bool | `[questions, option_count]` |
| `qtype` | int64 | `[questions]` |
| `logits` | float32 | `[questions, option_count]` |
| `act_logits` | float32 | `[questions, 2]` |

The loader verifies file identity and the declared manifest contract, then checks
actual native metadata names, element types, ranks and dynamic dimensions
(`act_logits` has fixed width two). Prediction validates actual output shapes,
counts and finite values, including padded values. Sequence preparation tracks
real markers and token limits. The manifest disallows custom operator libraries
and external data; the loader does not register artifact-provided executable code.
These checks are not a comprehensive independent graph/operator audit.

The publisher's ORT version is not proof of a compatible .NET package or
working target-platform native assets. Actual Linux ARM64 CPU loading and
execution have now passed, including heterogeneous question/option counts and
sequence lengths beyond the export sample. Broader shape/limit coverage and
other platforms still require execution evidence.

## Independent oracle requirements

Certification requires explicitly provisioned local artifacts and an independent
tokenizer/model oracle. Synthetic lookup encoders and hand-computed logits do
not replace real-output parity fixtures.

The inspected upstream `tests/test_onnx.py` is not a ready local oracle: it uses
an unpinned Hub model ID and exports a graph. The upstream agent constructors
may download missing models; tokenizer loading may fall back to an encoder
identifier. Use only explicitly provisioned, pinned offline inputs.

After explicit offline provisioning, independent fixtures should record:

- Source code and checkpoint revisions, actual SHA-256 hashes for all input
  artifacts, exact oracle dependency versions, and the invoked leaf command.
- Synthetic NFC/decomposed Unicode, whitespace/added-token, control-token,
  ordering, head-budget, and truncation cases.
- Exact token IDs, marker positions, masks and padded tensors, plus raw logits
  and action logits for varied shapes and all three question types.
- Separately specified numeric tolerances and comparison results, not tolerances
  reverse-engineered to make the implementation pass.
- A lineage comparison to the official checkpoint, or an explicitly accepted
  alternative artifact baseline. Config equality cannot replace this decision.

## Manifest and certification requirements

The bundle validator supports `laya-decision-v1`; its concrete JSON shape is
defined in [ModelBundle.fs](../src/Rlcd/ModelBundle.fs).
Keep loadable integrity metadata separate from certification evidence:

- An unverified metadata record may state the published revision/path/size/hash
  and unknown local hashes, but must be unmistakably non-loadable.
- A loadable manifest binds every required graph/sidecar to an actual verified
  hash and declares tensor types, precision and limits. The acquired manifest
  has `sourceRevision=null` and `testedShapes=[]`: neither invents export lineage
  nor treats smoke execution as certification. Loading does not require an
  independent-oracle certificate.
- Certification additionally needs independently verified shapes/limits and
  evidence provenance. Missing artifacts or hash/schema/contract mismatches
  fail loading. Explicit real-artifact tests must not silently skip to success.
- Source-file SHA-256 hashes and Git commit/blob IDs are not artifact hashes.

**Certification remains unavailable:** integrity and real callability are
established, but independent tokenizer/model parity and export weight lineage
remain unresolved. `VerifyModels` therefore reports blocked after successful
integrity checks; `CheckInference` separately tests real execution.
Runtime/model acquisition is already approved and implemented. Installing
Python/Torch or another reference toolchain, conversion, and any alternative
artifact-baseline decision remain separate scope decisions.
