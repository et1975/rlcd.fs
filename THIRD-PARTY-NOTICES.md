# Third-party provenance and notices

This inventory records the Laya/tokenizer source references, Unicode data
adaptation, and model investigation.
It is not a certification of model lineage or blanket redistribution compliance.
No third-party model weights or tokenizer files are included. The F# adaptations
below implement source-derived algorithms; they do not contain a Python runtime
or copied Python implementation text.

## Laya source reference

- Project: Laya, developed by Convai Innovations.
- Inspected source checkout revision:
  `9d955671415fc19f069b9cc998928075c1f255ec`.
- Locally inspected license evidence: `LICENSE` contains Apache License 2.0;
  `pyproject.toml` declares Apache-2.0 and names Convai Innovations.
- Reference areas: question/option rendering, sequence construction,
  calibration and confidence semantics, ONNX agent boundaries, and exporter
  tensor names/dynamic axes.
- License: <https://www.apache.org/licenses/LICENSE-2.0>.
- Unmodified locally inspected license text: [Laya-LICENSE.txt](docs/Laya-LICENSE.txt).
  This copy and this notice are included in the core NuGet package.

### F# adaptations

`src/Rlcd/Sequence.fs` adapts question/option rendering, sequence budgeting,
marker assembly, option-collapse checks and batch collation from
`laya/common.py` (`render_options`, `build_sequence`, `collate_items`).
`src/Rlcd/Calibration.fs` adapts temperature buckets/clamping and confidence
formulas from `laya/common.py`, and answer/action decoding from
`laya/onnx_agent.py`.
`src/Rlcd/Tokenizer.fs` also references the byte alphabet, GPT-2 splitting and
ranked-merge mechanics in `laya-ts/src/tokenizer.ts` at the same revision.

These are modified F# implementations of the pinned revision above. They use
immutable typed inputs/results, supplied encoder/logit boundaries, explicit
errors and diagnostics, and full-precision answers. In particular, missing
option markers fail rather than silently disappear; finite temperature clamps
are diagnosed, invalid temperatures are rejected by configuration validation,
and output probabilities are not rounded to four decimals. Sequence processing
checks for option collapse both before and after final clipping.

The inspected source checkout has no tracked upstream `NOTICE` file.
The license copy is not a license or provenance determination for the model
artifacts below.

The source checkout revision is distinct from both model-repository revisions
below. Inspecting source does not establish that a community graph was exported
from that source or from the official checkpoint.

## Hugging Face Tokenizers source reference

The original F# tokenizer implementation uses HF Tokenizers **v0.22.1** as a
behavioral reference for raw/normalized added-token phases, matching and strip
semantics, byte mapping, and pre-tokenization:

- <https://github.com/huggingface/tokenizers/blob/v0.22.1/tokenizers/src/tokenizer/added_vocabulary.rs>
- <https://github.com/huggingface/tokenizers/blob/v0.22.1/tokenizers/src/pre_tokenizers/byte_level.rs>
- Upstream license: Apache License 2.0, inspected at
  <https://github.com/huggingface/tokenizers/blob/v0.22.1/LICENSE>.

No Rust tokenizer runtime is included. This source reference is not independent
execution evidence for the selected model's tokenizer.

## Unicode word-property data

`src/Rlcd/Tokenizer.fs` contains derived Unicode **16.0.0** ranges for
`Alphabetic | Mark | Decimal_Number | Connector_Punctuation | Join_Control`.
They are generated from these Unicode Character Database files:

| Source under `https://www.unicode.org/Public/16.0.0/ucd/` | SHA-256 |
| --- | --- |
| `DerivedCoreProperties.txt` | `39d35161f2954497f69e08bdb9e701493f476a3d30222de20028feda36c1dabd` |
| `UnicodeData.txt` | `ff58e5823bd095166564a006e47d111130813dcf8bf234ef79fa51a870edb48f` |
| `PropList.txt` | `53d614508e2a0b2305a8aa21cd60d993de9326cdf65993660dfcce4503548583` |

The resulting 796 ranges were cross-checked across all Unicode code points
against the Unicode 16.0 table in Rust regex **1.11.2**:
<https://github.com/rust-lang/regex/blob/1.11.2/regex-syntax/src/unicode_tables/perl_word.rs>.
This verifies the property set, not whole-encoder or model parity.

The following notice is retained in source and in this packaged document.
License source: <https://www.unicode.org/license.txt>, inspected 2026-10-22.

```text
UNICODE LICENSE V3
COPYRIGHT AND PERMISSION NOTICE
Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```

## FastCoref.fs source reference

- Project: FastCoref.fs.
- Inspected source checkout revision:
  `046b061fd1178e9517c1755a0a8464dfad1907fc`.
- Copyright notice in the locally inspected `LICENSE.md`:
  **Copyright 2026 Eugene Tolmachev**.
- `LICENSE.md` and `README.md` identify Apache License 2.0.
- Reference areas: typed F# model boundaries, resource ownership, explicit
  artifact provisioning, and RoBERTa/Longformer byte-level BPE mechanics.
- License: <https://www.apache.org/licenses/LICENSE-2.0>.

The tokenizer is prior art, not a certified tokenizer for the selected
ModernBERT artifact.

## Model candidates — plan-recorded license metadata only

| Model repository and revision | Role | License evidence |
| --- | --- | --- |
| `mariojcr/laya-onnx@aeebb05497cc473360caf4de1653107cf08d6b10` | Selected community English ONNX export candidate | Apache-2.0 card reported by approved plan |
| `convaiinnovations/laya@55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851` | Official English reference checkpoint | Apache-2.0 metadata reported by approved plan |

Pinned repository locators:

- <https://huggingface.co/mariojcr/laya-onnx/tree/aeebb05497cc473360caf4de1653107cf08d6b10>
- <https://huggingface.co/convaiinnovations/laya/tree/55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851>

Those remote observations were not refreshed during the local investigation.
The selected graph and three JSON sidecars have since been acquired and their
identities verified; see [the measured bundle hashes](docs/model-contract.md#verified-bundle-bytes).
No separate model license or NOTICE payload was acquired in that four-file bundle. The exact
community-export weight lineage and any additional applicable upstream
attribution remain unverified.

## Before distribution or adaptation

Retain applicable upstream copyright, license, attribution and NOTICE material
when redistributing or adapting third-party work; include the required license
copy and identify modified files where required. Reconcile this inventory with
the actual provisioned bundle and any code adaptations before release.

Do not infer that similarly named source/model repositories, matching configs,
or an Apache-2.0 card alone settle every artifact's provenance or notice
requirements. See [the model contract](docs/model-contract.md) for the current
certification boundary.
