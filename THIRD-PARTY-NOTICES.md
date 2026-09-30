# Third-party provenance and notices

This is an attribution/provenance inventory for the initial Laya backend
investigation, not a certification of model lineage or redistribution
compliance. No third-party model weights or tokenizer files were added by this
investigation. No upstream implementation source was copied into production
code by this task.

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

The source checkout revision is distinct from both model-repository revisions
below. Inspecting source does not establish that a community graph was exported
from that source or from the official checkpoint.

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
No graph/sidecar license or NOTICE payload was locally inspected. The exact
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
