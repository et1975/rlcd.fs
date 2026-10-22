# rlcd.fs

F#/.NET foundations for **Reinforcement Learning for Calibrated Decisions
(RLCD)**. The intended products are a reusable, domain-independent inference
library and a separate model router.

**Current status:** the repository implements typed decision-request
construction, sequence assembly using a supplied encoder, calibrated decoding
of supplied logits, local model-bundle validation, and a separate pure
model-selection policy. These APIs do not run a model. The tokenizer, native ONNX inference,
task-to-tier classification, and the MCP host are **planned, not shipped**.
Passing the unit tests does not establish model parity or native certification.

## Repository layout

| Path | Present responsibility |
| --- | --- |
| [src/Rlcd/](src/Rlcd/) | Generic typed requests, supplied-encoder sequence assembly, supplied-logit decoding, and local artifact/configuration validation. |
| [src/Rlcd.Router/](src/Rlcd.Router/) | Pure catalog, capability, tier-distribution, and model-selection policy; consumes supplied evidence, not task text. |
| [tests/Rlcd.Tests/](tests/Rlcd.Tests/) | xUnit request, sequence, calibration, bundle-validation, and build tests; no provisioned model is needed. |
| [tests/Rlcd.Router.Tests/](tests/Rlcd.Router.Tests/) | xUnit catalog, probability, threshold, eligibility, and abstention tests. |
| [docs/generic-api.md](docs/generic-api.md) | Callable generic request-construction API and example. |
| [docs/preprocessing.md](docs/preprocessing.md) | Sequence preparation and typed answer decoding, including supplied-input contracts and evidence limits. |
| [docs/router-policy.md](docs/router-policy.md) | Callable model-selection policy and its caller-supplied evidence boundary. |
| [examples/GenericDecisions.fsx](examples/GenericDecisions.fsx) | Executable non-routing triage request example; outputs constructed questions, not predictions. |
| [build.fsx](build.fsx) | FAKE targets for offline restore, build, tests, packaging, and model checks. |
| [scripts/VerifyArtifacts.fsx](scripts/VerifyArtifacts.fsx) | Local artifact-integrity verifier with structured results; does not load ONNX. |
| [docs/model-contract.md](docs/model-contract.md) | Model investigation, provenance, evidence limits, and unresolved certification gates. |
| [Rlcd.slnx](Rlcd.slnx) | Library and test solution. |
| [global.json](global.json) | Exact SDK selection. |
| [Directory.Build.props](Directory.Build.props) / [Directory.Packages.props](Directory.Packages.props) | Stable F# 10, `net10.0`, warnings-as-errors, deterministic builds, and centrally pinned packages. |
| [NuGet.Config](NuGet.Config) | Repository-local, offline-only package source policy. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Existing source/model attribution and provenance limits. |
| [AGENTS.md](AGENTS.md) | Contributor orientation and validation boundaries. |

No `models/` directory or runtime manifest is supplied.

## APIs available now

`Rlcd` provides `ChoiceOption.create`, `Question.choice`, `Question.score`, and
`Question.boolean`, validated token budgets and truncation options, and
`DecisionRequest.create` / `DecisionRequest.createBatch`. Constructors return
typed validation errors and preserve option order. A batch returns all valid
requests or indexed errors, never a partial successful batch.

`Sequence.build` and `Sequence.buildBatch` assemble Laya-compatible inputs using
a caller-supplied `ITextEncoder` and validated special token IDs. They preserve
question/option order, pad batches, and report or reject truncation and collapsed
options. No tokenizer implementation is supplied.

`Answer.decode` converts supplied, explicitly masked logits into typed choice,
ordinal-score, or boolean answers. It applies configured temperatures, exposes
clamp diagnostics, and keeps answer confidence separate from entropy confidence.
This is numerical postprocessing, not a call to a model.

`Rlcd.Router.Policy.select` takes an ordered validated catalog, caller-known
available models and generation capabilities, and an already-computed
Economy/Balanced/Deep probability distribution. It returns a model selection or
an explicit abstention. Fixed user choices are preserved; uncertain, truncated,
unsupported, or ineligible evidence does not cause a substitute model selection.
It does not yet infer those probabilities from a task or dispatch an agent.

For exact signatures and examples, see the API documents above. After building:

```bash
./examples/GenericDecisions.fsx
```

The example explicitly reports `inferenceAvailable=false`.

## Prerequisites and offline boundary

- **.NET SDK 10.0.402** on `PATH`. `global.json` disables roll-forward and
  prerelease selection; another installed SDK version is not a substitute.
- The packages in each project's `packages.lock.json` must already be
  provisioned locally. Direct versions are FSharp.Core 10.1.302,
  Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, and
  xunit.runner.visualstudio 3.1.3; their locked transitive dependencies are
  required too.
- The build script references `Fake.Core.Target`, `Fake.IO.FileSystem`, and
  `Fake.DotNet.Cli`, pinned to **6.1.3**, with their transitive dependencies
  provisioned in the local NuGet cache. These are build-time dependencies,
  not dependencies of the shipped library.
- `HOME` must identify the existing local cache. `NuGet.Config` clears
  inherited sources and uses only `%HOME%/.nuget/packages`.
- The executable script examples assume a Unix-like shell with `dotnet` and
  `/usr/bin/env` available.

Restore is deliberately offline and locked. Missing cached packages are a
provisioning blocker, not permission to add a feed, install a tool, change
global configuration, or download a model. NuGet auditing is disabled in the
repository build properties; an offline build is not a vulnerability audit.
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

This still uses the configured local package source; it does not acquire
missing FAKE packages from the network.

## Quickstart workflow

Run from the repository root:

```bash
dotnet --version
./build.fsx --help
./build.fsx --list
./build.fsx -t All
```

The first command must report `10.0.402`. FAKE provides `--help` and `--list`.
`All` restores, builds, tests, and packs the foundation. Running `./build.fsx`
with no options also selects `All`. Invoke from this repository for initial
offline package resolution; target commands always use the repository root.

Stop on any nonzero exit rather than continuing with stale outputs.
Build/test outputs are under project `bin/` and `obj/`; packing produces local
`Rlcd.0.1.0.nupkg` and `Rlcd.Router.0.1.0.nupkg` packages in their respective
`src/<project>/bin/Release/` directories. They contain request construction,
pre/postprocessing, validation, and pure selection APIs, not an inference backend.

The equivalent direct SDK workflow is:

```bash
dotnet restore Rlcd.slnx --configfile NuGet.Config --locked-mode
dotnet build Rlcd.slnx -c Release --no-restore
dotnet test Rlcd.slnx -c Release --no-build --no-restore
dotnet pack Rlcd.slnx -c Release --no-build --no-restore
```

### Build targets

Select one case-sensitive target with `-t`. Dependencies run once per invocation:

```bash
./build.fsx -t Restore
./build.fsx -t Build
./build.fsx -t Tests
./build.fsx -t Package
./build.fsx -t Setup
```

| Target | Behavior |
| --- | --- |
| `Restore` | Offline locked solution restore using the repository `NuGet.Config`. |
| `Build` | `Restore`, then Release build with `--no-restore`. |
| `Tests` | `Build` and its dependencies, then both Release test projects with `--no-build --no-restore`. |
| `Package` | `Build` and its dependencies, then local Release packages for both libraries with `--no-build --no-restore`. |
| `CheckModels` | Run the existing verifier with `--integrity-only`; no downloads or native certification. |
| `Setup` | `Restore`, then `CheckModels`. Not a model acquisition step. |
| `All` | Run `Restore`, `Build`, `Tests`, and `Package` once each. Default when no target is supplied; excludes model checks. |

`Setup` and `CheckModels` fail with the currently absent model inputs.
`Setup` completes Restore, then CheckModels invokes the verifier, which exits
`2` with `status="blocked"`, `integrityVerified=false`, and
`nativeCertified=false`. FAKE reports the failed target and exits nonzero
(`1` for this failure). No model downloads occur.
`All` can validate the foundation without those inputs; its success must not
be read as model readiness.

FAKE owns target selection, dependency ordering, diagnostics, and failures.
The former custom `--dry-run`, `--model-dir`, `--manifest`, and
`rlcd-build-v1` JSON output contract have been removed. Use the environment
variables below for model inputs. No docs compiler, CI, model downloader,
publishing, or release workflow is supplied by this bootstrap.

## Models: integrity is not certification

The runner resolves model inputs as follows:

1. `RLCD_MODELS_DIR`, otherwise
   `~/.cache/rlcd/laya-english`.
2. `RLCD_MODEL_MANIFEST`, otherwise `models/laya-english.manifest.json` relative to
   the repository root.

Relative model/manifest environment paths resolve from the caller's working directory. Child commands
run from the repository root, located from the build script's own directory.

Both the default model bundle and the runtime manifest are **absent**. Native
ONNX Runtime and an independent reference oracle are also not provisioned.
Model bytes must live outside the repository. Do not invent manifest hashes,
use test fixtures as real certification evidence, or substitute a model card
for verified bytes. The [model contract](docs/model-contract.md) records the
candidate and remaining provenance/parity blockers.

After separately authorized provisioning of real local inputs, use:

```bash
RLCD_MODELS_DIR=/absolute/local/bundle \
RLCD_MODEL_MANIFEST=/absolute/local/manifest.json ./build.fsx -t CheckModels

RLCD_MODELS_DIR=/absolute/local/bundle \
RLCD_MODEL_MANIFEST=/absolute/local/manifest.json ./build.fsx -t Setup
```

Those paths are placeholders, not supplied assets or download instructions.
The verifier can also be inspected or invoked directly:

```bash
./scripts/VerifyArtifacts.fsx --help
./scripts/VerifyArtifacts.fsx --model-dir /absolute/local/bundle --manifest /absolute/local/manifest.json --integrity-only
```

Unlike the runner, the direct verifier requires `--model-dir`; it does not
read `RLCD_MODELS_DIR` itself. Its manifest default is the same currently
absent repository path.

### Output and exit rules

- The build uses native FAKE console output: target progress, child logs,
  dependency information, and a build summary. It is not a JSON protocol.
  Success exits `0`; a failed target stops dependent targets and exits nonzero.
- FAKE's internal MSBuild binary-log reader is disabled because the cached
  reader predates SDK 10's format. Console diagnostics and failure propagation
  remain enabled.
- Direct `dotnet` build/test/pack commands emit ordinary tool logs, not a
  JSON protocol. Check the process exit status.
- The verifier's `--help` prints usage to stdout and exits `0`.
- Normal verifier results are one JSON object on stdout with
  `schemaVersion`, `status`, `integrityVerified`, `nativeCertified`, and
  `detail`. Handled argument, artifact, and access errors are reported there,
  not only on stderr; they exit `2`.
- `--integrity-only` exits `0` only when local integrity checks pass.
  `nativeCertified` remains `false`. Without that flag, even a successful
  integrity check reports `blocked` and exits `2` because native certification
  is unavailable.
- For either script, SDK/FSI startup or unhandled failures may instead emit
  diagnostics on stderr. Do not assume every process failure produces JSON.

## Contributor checks and reference credit

See [AGENTS.md](AGENTS.md) for contributor guidance. Documentation changes are
checked for local links, formatting, and command/claim agreement with the final
files. There is no documentation compiler or generated documentation site.

The build uses the same FAKE libraries, `Target.create` definitions, dependency
operators, and `Fake.DotNet` helpers as the local FastCoref.fs `build.fsx`.
FastCoref pins 5.23.1; this repository pins the locally provisioned 6.1.3.
Only applicable targets are carried over: no source-model downloads,
repository metadata rewrites, documentation publishing, or release flows.

Laya is the behavioral/checkpoint reference, not proof of local execution or
export lineage. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the
existing reference revisions, attribution, and evidence limits.

## License

This repository is licensed under the [Apache License 2.0](LICENSE).
Third-party attribution and provenance limits are documented in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); upstream license notices remain
applicable to their respective materials.
