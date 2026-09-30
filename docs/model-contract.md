# Laya English model contract

## Status and evidence boundary

**Unverified metadata — not a certified or loadable bundle.**

The 2026-09-30 bounded local investigation found no selected model graph,
tokenizer sidecars, or official checkpoint in the inspected locations. It also
found no usable independent oracle in the inspected Python environment. G0
(certified local artifact and independent oracle) has **not passed**.

`models/laya-english.manifest.json` is intentionally not created by this
investigation. Its runtime schema must be agreed with the bundle-loader owner.
Missing hashes, fixture outputs, or certification results must not be replaced
with invented values, empty-string hashes, or all-zero placeholders.

There are three distinct evidence levels:

1. **Plan-recorded primary-source observations:** pinned Hub tree/card/config/LFS
   information recorded in the approved plan. No network revalidation occurred
   during this investigation.
2. **Locally observed source and environment:** source checkout commits, source
   file hashes, explicit path checks, and installed-package metadata.
3. **Not yet observed:** local model bytes, graph contents/native execution,
   independent tokenizer IDs/sequences/logits, exact export weight lineage,
   dynamic-shape behavior, and performance.

Neither a model card nor a matching configuration proves numerical parity.
Reference source code alone is not a runnable, provisioned reference oracle.

## Selected published candidate

The following values are **published metadata recorded in the approved plan**,
not locally measured artifact properties:

| Property | Value |
| --- | --- |
| Repository | `mariojcr/laya-onnx` |
| Revision | `aeebb05497cc473360caf4de1653107cf08d6b10` |
| Graph | `english/laya.onnx` |
| Published size | `1688711336` bytes |
| Published LFS SHA-256 | `ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e` |
| Sidecars | `english/tokenizer.json`, `english/tokenizer_config.json`, `english/rl_agent_config.json` |
| Sidecar SHA-256 | Unknown; no local sidecar bytes inspected |
| Publisher claims | FP32, opset 18, inline weights, dynamic dimensions, parity on two shapes using ORT 1.29 |
| Card license | Apache-2.0; plan-recorded observation, not a new local license audit |

Pinned source locator:
<https://huggingface.co/mariojcr/laya-onnx/tree/aeebb05497cc473360caf4de1653107cf08d6b10/english>.
This link is provenance, not an instruction or authorization to download.

The authoritative English checkpoint candidate is
`convaiinnovations/laya@55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851`.
The plan records public, ungated Apache-2.0 metadata, ModernBERT-large weights,
and no ONNX file in the inspected official tree. The exact relationship between
those weights and the selected community export remains **unproven**.

## Expected tokenizer and sequence behavior

The plan records an NFC-normalized ByteLevel BPE pipeline with
`add_prefix_space=false`, `use_regex=true`, and added tokens including whitespace
runs with normalization/strip flags. Support must be derived from the pinned
sidecars and unknown tokenizer schemas rejected explicitly.

The plan-observed CLS/SEP/PAD/MASK IDs are `50281/50282/50283/50284`. These are
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

## Expected graph contract, pending local certification

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

The eventual loader/certification path must validate file identity, names,
element types, ranks, dimensions, marker counts, finite outputs, token limits,
operators, and external tensor references. Reject unknown/custom operators and
unapproved external paths; never execute artifact-supplied code.

The publisher's ORT version is not proof of a compatible .NET package or
working target-platform native assets. Actual CPU loading and dynamic shapes
must be exercised, including question counts, option counts, and sequence
lengths different from the export sample.

## Local availability and independent oracle

The explicitly inspected conventional Hugging Face cache root and both selected
model cache directories were absent. Laya-local `.cache`, `models`,
`checkpoints`, `.venv`, `venv`, and exact root-level artifact candidates were
also absent. `RLCD_MODELS_DIR`, `HF_HOME`, `HF_HUB_CACHE`,
`HUGGINGFACE_HUB_CACHE`, `TRANSFORMERS_CACHE`, `XDG_CACHE_HOME`, and `VIRTUAL_ENV`
were unset. This is not a claim that no artifacts exist anywhere on the host.

An executable session-only F# probe invoked the existing `/usr/bin/python3`
package-metadata leaf with index access disabled. `pip show` reported no
installed distributions for `tokenizers`, `transformers`, `torch`, `onnx`,
`onnxruntime`, `numpy`, `safetensors`, or `huggingface-hub` in that interpreter's
environment. Other Python environments were not broadly searched.

No independent synthetic tokenization or inference check could be run with
these inputs. No golden IDs, sequences, logits, probabilities, or measured
tolerances were produced.

The inspected upstream `tests/test_onnx.py` is not a ready local oracle: it uses
an unpinned Hub model ID and exports a graph. The upstream agent constructors
may download missing models; tokenizer loading may fall back to an encoder
identifier. Those entry points were **not executed**.

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

## Manifest recommendation and remaining gate

The planned runtime contract is `laya-decision-v1`; its concrete JSON shape is
owned by the bundle-loader implementation, not established by this document.
Keep discovery metadata separate from loadable certification:

- An unverified metadata record may state the published revision/path/size/hash
  and unknown local hashes, but must be unmistakably non-loadable.
- A certified runtime manifest must bind each required graph/sidecar to an
  actual verified hash, with tokenizer/config identity, tensor contract,
  precision, tested shapes/limits, and independent evidence provenance.
- A loader must fail closed for unverified metadata and missing required
  artifacts. Explicit real-artifact tests must not silently skip to success.
- Source-file SHA-256 hashes and Git commit/blob IDs are not artifact hashes.

**Blocker:** no selected local artifact bundle or independently provisioned
oracle was found in the approved search scope; export weight lineage is also
unresolved. No downloads, installations, conversion, or acquisition workflows
are authorized by this document. User-approved provisioning/baseline decisions
and schema agreement are required before advancing G0.
