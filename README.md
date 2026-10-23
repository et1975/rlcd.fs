# rlcd.fs

F#/.NET inference for **Reinforcement Learning for Calibrated Decisions
(RLCD)**: a reusable, domain-independent Laya-compatible ONNX library and a
separate model router. This project does not implement training.

**Current status:** the repository implements typed decision-request
construction, an English ByteLevel/BPE tokenizer for supported configurations,
sequence assembly, calibrated decoding, verified local bundle loading, and
real CPU inference through `DecisionModel.Load`, `Predict`, and `PredictBatch`.
The pinned English bundle has been downloaded outside the repository and
exercised on Linux ARM64 with ONNX Runtime 1.29.0.
The router currently provides pure selection from supplied probabilities;
task-to-tier classification and the MCP host remain **planned, not shipped**.
Real execution establishes callability, not independent tokenizer/numerical
parity, export weight lineage, or routing quality.

## Repository layout

| Path | Present responsibility |
| --- | --- |
| [src/Rlcd/](src/Rlcd/) | Generic typed requests, tokenization, sequence assembly, calibrated answers, verified bundle loading, and disposable CPU inference. |
| [src/Rlcd.Router/](src/Rlcd.Router/) | Pure catalog, capability, tier-distribution, and model-selection policy; consumes supplied evidence, not task text. |
| [tests/Rlcd.Tests/](tests/Rlcd.Tests/) | Model-free contracts and controlled-runtime tests, native runtime smoke, and an explicitly selected real-model category. |
| [tests/Rlcd.Router.Tests/](tests/Rlcd.Router.Tests/) | xUnit catalog, probability, threshold, eligibility, and abstention tests. |
| [docs/generic-api.md](docs/generic-api.md) | Request construction, inference signatures, results, errors, capacity, cancellation, and disposal. |
| [docs/preprocessing.md](docs/preprocessing.md) | Tokenization, sequence preparation, and typed answer decoding, including supported configurations and evidence limits. |
| [docs/router-policy.md](docs/router-policy.md) | Callable model-selection policy and its caller-supplied evidence boundary. |
| [build.fsx](build.fsx) | FAKE targets for builds, tests, packaging, explicit model acquisition/checks, real inference, and the construction-only example. |
| [docs/model-contract.md](docs/model-contract.md) | Expected model contract, provenance, evidence limits, and native certification requirements. |
| [Rlcd.slnx](Rlcd.slnx) | Library and test solution. |
| [global.json](global.json) | Requested SDK version and feature-band roll-forward policy. |
| [Directory.Build.props](Directory.Build.props) / [Directory.Packages.props](Directory.Packages.props) | Stable F# 10, `net10.0`, warnings-as-errors, deterministic builds, and centrally pinned packages. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Existing source/model attribution and provenance limits. |
| [AGENTS.md](AGENTS.md) | Contributor orientation and validation boundaries. |

No weights or runtime manifest are committed. `DownloadModels` provisions the
pinned bundle and an integrity manifest in an external cache.

## APIs available now

`Rlcd` provides `ChoiceOption.create`, `Question.choice`, `Question.score`, and
`Question.boolean`, validated token budgets and truncation options, and
`DecisionRequest.create` / `DecisionRequest.createBatch`. Constructors return
typed validation errors and preserve option order. A batch returns all valid
requests or indexed errors, never a partial successful batch.

`Tokenizer.read` loads supported local `tokenizer_config.json` and
`tokenizer.json` files. `Tokenizer.encode` performs NFC normalization,
added-token matching, scalar-aware ByteLevel splitting, and ranked BPE.
It does not add automatic special tokens or truncate. Unsupported configurations
fail explicitly; see the supported subset and parity limits in the preprocessing
documentation.

`Sequence.build` and `Sequence.buildBatch` assemble Laya-compatible inputs using
a caller-supplied `ITextEncoder` and validated special token IDs. They preserve
question/option order, pad batches, and report or reject truncation and collapsed
options. Use `Tokenizer.encoder` and `Tokenizer.specialTokens` to connect the
included tokenizer, or supply another compatible encoder.

`Answer.decode` converts supplied, explicitly masked logits into typed choice,
ordinal-score, or boolean answers. It applies configured temperatures, exposes
clamp diagnostics, and keeps answer confidence separate from entropy confidence.
This is numerical postprocessing, not a call to a model.

`DecisionModel.Load(modelDirectory, manifestPath)` verifies local artifacts and
owns one **Microsoft.ML.OnnxRuntime 1.29.0** CPU session. `Predict` and
`PredictBatch` return asynchronous typed results with ordered answers,
diagnostics, usage, and artifact/runtime provenance. They admit one active call
per model, reject excess capacity, and support cancellation and disposal.
Use `DisposeAsync` when shutdown must await an active call's resource release.
See the [inference contract](docs/generic-api.md#inference-api) for exact bounds
and lifetime semantics.

`CheckRuntime` checks native version/provider without a model. `CheckInference`
executes the actual cached graph through the public API for all three question
kinds. Both have passed on Linux ARM64; other platforms and independent parity
remain unverified.

`Rlcd.Router.Policy.select` takes an ordered validated catalog, caller-known
available models and generation capabilities, and an already-computed
Economy/Balanced/Deep probability distribution. It returns a model selection or
an explicit abstention. Fixed user choices are preserved; uncertain, truncated,
unsupported, or ineligible evidence does not cause a substitute model selection.
It does not yet infer those probabilities from a task or dispatch an agent.

For exact signatures and examples, see the API documents above. Run the
construction-only example through the build entry point:

```bash
./build.fsx -t Example
```

The example explicitly reports `inferenceAvailable=false` for that
construction-only execution, not for the library. It loads the core
construction sources, so no prebuilt library or model is needed. To supply state:

```bash
RLCD_EXAMPLE_STATE='The customer cannot sign in.' ./build.fsx -t Example
```

## Prerequisites and package restore

- **.NET SDK 10.0.402** requested on `PATH`. `global.json` uses
  `rollForward="feature"` and disables prereleases: compatible patch/feature-band
  fallback within .NET 10.0 is allowed, not arbitrary major/minor SDK versions.
- Project package versions are pinned in `Directory.Packages.props`, with
  resolved dependencies recorded in each project's `packages.lock.json`.
  Restore uses normal NuGet configuration and the global package cache,
  downloading missing packages from configured sources as needed.
- CPU ONNX Runtime native assets are supplied by the core package's
  `Microsoft.ML.OnnxRuntime` dependency. The managed binding is version 1.29.0;
  its locked graph also includes `System.Numerics.Tensors` 9.0.0.
- The build script references `Fake.Core.Target`, `Fake.IO.FileSystem`, and
  `Fake.DotNet.Cli`, pinned to **6.1.3**. F# Interactive restores these build-time
  dependencies before target execution; they are not shipped library dependencies.
- The executable script examples assume a Unix-like shell with `dotnet` and
  `/usr/bin/env` available.

Ordinary restore is locked, but not restricted to offline sources. The former
repository-local cache-only `NuGet.Config` has been removed. Package restore
does not download model artifacts or install a reference toolchain.
NuGet auditing is disabled in the repository build properties; a successful
restore is not a vulnerability audit.
The SDK's transitive framework-pack prefetch is disabled so these non-web
projects do not request an unused ASP.NET Core targeting pack. Frameworks
actually required by project or package references still need their matching
targeting packs provisioned locally.

F# Interactive resolves the script's `#r "nuget: ..."` references before any
target runs. Its generated restore project does not inherit this repository's
build properties. If a cold FSI restore requests an unused framework pack, use
the same SDK setting for that invocation:

```bash
DisableTransitiveFrameworkReferenceDownloads=true ./build.fsx --help
```

This applies the same framework-prefetch setting to FSI bootstrap without
changing package versions or configured NuGet sources.

## Quickstart workflow

Run from the repository root:

```bash
dotnet --version
./build.fsx --help
./build.fsx --list
./build.fsx -t All
```

The first command reports the SDK selected by `global.json`; verification here
used `10.0.402`. FAKE provides `--help` and `--list`.
`All` restores, builds, tests, and packs both libraries. Running `./build.fsx`
with no options also selects `All`. Invoke from this repository for initial
package resolution; target commands always use the repository root.

Stop on any nonzero exit rather than continuing with stale outputs.
Build/test outputs are under project `bin/` and `obj/`; packing produces local
`Rlcd.0.1.0.nupkg` and `Rlcd.Router.0.1.0.nupkg` packages in their respective
`src/<project>/bin/Release/` directories. The core includes the inference backend
and its runtime dependency; the router includes the pure selection policy.
Neither package includes model weights.

`build.fsx` is the only approved entry point for repository restore, build,
test, packaging, and model checks. Use its targets rather than invoking the
SDK commands or internal verification scripts directly.

### Build targets

Select a target with `-t`. Dependencies run once per invocation:

```bash
./build.fsx -t Restore
./build.fsx -t Build
./build.fsx -t Tests
./build.fsx -t Package
./build.fsx -t Setup
```

| Target | Behavior |
| --- | --- |
| `Restore` | Locked solution restore using normal NuGet configuration and sources. |
| `UpdateLocks` | Explicit restore that reevaluates dependency requirements and updates lockfiles after intentional package changes; not part of `All`. |
| `Build` | `Restore`, then Release build with `--no-restore`. |
| `CheckRuntime` | `Build` and its dependencies, then the focused native version/CPU-provider smoke test; no model required. |
| `CheckInference` | `Build` and its dependencies, then the explicit `RealInference` tests against local model inputs; missing inputs fail, never skip. |
| `Tests` | `Build` and its dependencies, then both Release test projects with `--no-build --no-restore`, excluding `RealInference`. |
| `Package` | `Build` and its dependencies, then local Release packages for both libraries with `--no-build --no-restore`. |
| `DownloadModels` | Acquire/reuse the four revision-pinned English files outside the repository, verify identities, and publish a cache-local integrity manifest; not part of `All`. |
| `CheckModels` | Run local artifact-integrity validation directly; no downloads or native certification. |
| `VerifyModels` | Run integrity validation, then explicitly fail because independent certification remains unavailable; `CheckInference` is a separate callability check. |
| `Example` | Construct choice/score/boolean requests and emit JSON; no inference or model inputs. |
| `Setup` | `Restore`, then `CheckModels`. Not a model acquisition step. |
| `All` | Run `Restore`, `Build`, `Tests`, and `Package` once each. Default when no target is supplied; excludes model acquisition/checks, real inference, and `Example`. |

After intentionally changing a package requirement, run `./build.fsx -t UpdateLocks`,
review the lockfile changes, then run `./build.fsx -t All`. Ordinary restore fails
on a dependency/lock mismatch instead of silently rewriting the locks.

`Setup` and `CheckModels` require local inputs and perform no model download.
Missing or invalid inputs report `status="blocked"` (or `invalid-arguments`),
`integrityVerified=false`, and `nativeCertified=false`, then fail with exit `1`.
The acquired default cache passes integrity checks. `All` needs no model files;
its success must not be read as model certification.

FAKE owns target selection, dependency ordering, diagnostics, and failures.
Use the environment variables below for model inputs. No docs compiler, CI,
publishing, or release workflow is supplied.

## Models: integrity is not certification

The runner resolves model inputs as follows:

1. `RLCD_MODELS_DIR`, otherwise
   `~/.cache/rlcd/laya-english`.
2. `RLCD_MODEL_MANIFEST`, otherwise `laya-english.manifest.json` inside the
   resolved model directory.

Relative model/manifest environment paths resolve from the caller's working directory. Child commands
run from the repository root, located from the build script's own directory.

Acquire the approved pinned English bundle explicitly, then check it:

```bash
./build.fsx -t DownloadModels
./build.fsx -t CheckModels
./build.fsx -t CheckInference
```

`DownloadModels` requires preinstalled `curl` for missing/corrupt files. It
downloads the approximately 1.69 GB graph and three JSON sidecars over HTTPS
from an immutable Hugging Face revision, never executable model-provided code.
It checks sizes and the published graph SHA-256 / sidecar Git-blob identities,
records actual SHA-256 hashes, and reuses valid files without network requests.
Writes use a single-writer lock and verified staging. Symlink/reparse cache
paths are rejected; an unrelated or modified existing manifest is not overwritten.
After a crash, remove a stale download lock only after confirming no writer remains.
`RLCD_MODELS_DIR` selects the cache; `RLCD_MODEL_MANIFEST` is a read override
for checks, not the download manifest destination.

The bundle and cache-local manifest have been acquired and exercised here.
Fresh checkouts still need acquisition or equivalent local provisioning.
An independent reference oracle is not provisioned.
Model bytes must live outside the repository. Do not invent manifest hashes,
use test fixtures as real certification evidence, or substitute a model card
for verified bytes. The [model contract](docs/model-contract.md) records the
candidate and remaining provenance/parity blockers.

For another provisioned location, use:

```bash
RLCD_MODELS_DIR=/absolute/local/bundle \
RLCD_MODEL_MANIFEST=/absolute/local/manifest.json ./build.fsx -t CheckModels

RLCD_MODELS_DIR=/absolute/local/bundle \
RLCD_MODEL_MANIFEST=/absolute/local/manifest.json ./build.fsx -t Setup
```

Those paths are placeholders, not supplied assets.
`CheckModels` and `VerifyModels` call the library's existing bundle validator
directly with the resolved paths. The former standalone verifier and example
scripts have been folded into `build.fsx`; there are no alternative script entry
points.

### Output and exit rules

- The build uses native FAKE console output: target progress, child logs,
  dependency information, and a build summary. It is not a JSON protocol.
  Success exits `0`; a failed target stops dependent targets and exits nonzero.
- FAKE's internal MSBuild binary-log reader is disabled because the cached
  reader predates SDK 10's format. Console diagnostics and failure propagation
  remain enabled.
- The SDK subprocesses invoked by build targets emit ordinary tool logs, not a
  JSON protocol. Their failures propagate through FAKE.
- Model-check results include one compact JSON object on its own stdout line with
  `schemaVersion`, `status`, `integrityVerified`, `nativeCertified`, and
  `detail`, amid ordinary FAKE logs. Handled argument, artifact, and access errors
  are reported there before failing the target with exit `1`.
- `CheckModels` exits `0` only when local integrity checks pass.
  `nativeCertified` remains `false`. `VerifyModels` reports `blocked` and exits
  `1` even after successful integrity checks, because native certification is
  unavailable.
- `Example` likewise emits a compact JSON line amid FAKE logs. Construction
  succeeds with exit `0`; invalid state reports `invalid-request` and exits `1`.
  An unset `RLCD_EXAMPLE_STATE` uses the sample, but an explicitly empty or
  whitespace-only value is invalid.
- Do not parse the complete stdout stream as JSON. Failed targets, SDK/FSI startup,
  or unhandled failures may also emit exception diagnostics on stderr; not every
  process failure produces a JSON payload.

## Contributor checks and reference credit

See [AGENTS.md](AGENTS.md) for contributor guidance. Documentation changes are
checked for local links, formatting, and command/claim agreement with the final
files. There is no documentation compiler or generated documentation site.

The build uses the same FAKE libraries, `Target.create` definitions, dependency
operators, and `Fake.DotNet` helpers as the local FastCoref.fs `build.fsx`.
FastCoref pins 5.23.1; this repository pins 6.1.3.
The model acquisition target is separately scoped to this repository's pinned
English bundle. No source-checkpoint downloads, repository metadata rewrites,
documentation publishing, or release flows are included.

Laya is the behavioral/checkpoint reference, not proof of local execution or
export lineage. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the
existing reference revisions, attribution, and evidence limits.

## License

This repository is licensed under the [Apache License 2.0](LICENSE).
Third-party attribution and provenance limits are documented in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); upstream license notices remain
applicable to their respective materials.
