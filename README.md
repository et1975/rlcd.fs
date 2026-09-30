# rlcd.fs

F#/.NET foundations for **Reinforcement Learning for Calibrated Decisions
(RLCD)**. The intended products are a reusable, domain-independent inference
library and a separate model router.

**Current status:** the repository implements local model-bundle and
configuration validation and an offline build runner, not inference. A
tokenizer/inference engine, native ONNX Runtime integration, model router, and
MCP host are **planned, not shipped**. Passing the unit tests does not establish
model parity or native certification.

## Repository layout

| Path | Present responsibility |
| --- | --- |
| [src/Rlcd/](src/Rlcd/) | Packable F# library; `ModelBundle.fs` validates manifests, artifact identity, paths, tokenizer sidecar contracts, and agent configuration. |
| [tests/Rlcd.Tests/](tests/Rlcd.Tests/) | xUnit bundle-validation and build-runner tests using synthetic fixtures; no provisioned model is needed. |
| [build.fsx](build.fsx) | Dependency-free F#/BCL orchestration for offline restore, build, tests, packaging, and model checks. |
| [scripts/VerifyArtifacts.fsx](scripts/VerifyArtifacts.fsx) | Local artifact-integrity verifier with structured results; does not load ONNX. |
| [docs/model-contract.md](docs/model-contract.md) | Model investigation, provenance, evidence limits, and unresolved certification gates. |
| [Rlcd.slnx](Rlcd.slnx) | Library and test solution. |
| [global.json](global.json) | Exact SDK selection. |
| [Directory.Build.props](Directory.Build.props) / [Directory.Packages.props](Directory.Packages.props) | Stable F# 10, `net10.0`, warnings-as-errors, deterministic builds, and centrally pinned packages. |
| [NuGet.Config](NuGet.Config) | Repository-local, offline-only package source policy. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Existing source/model attribution and provenance limits. |
| [AGENTS.md](AGENTS.md) | Contributor orientation and validation boundaries. |

No `models/` directory or runtime manifest is supplied.

## Prerequisites and offline boundary

- **.NET SDK 10.0.302** on `PATH`. `global.json` disables roll-forward and
  prerelease selection; another installed SDK version is not a substitute.
- The packages in both project `packages.lock.json` files must already be
  provisioned locally. Direct versions are FSharp.Core 10.1.302,
  Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, and
  xunit.runner.visualstudio 3.1.3; their locked transitive dependencies are
  required too.
- `HOME` must identify the existing local cache. `NuGet.Config` clears
  inherited sources and uses only `%HOME%/.nuget/packages`.
- The executable script examples assume a Unix-like shell with `dotnet` and
  `/usr/bin/env` available.

Restore is deliberately offline and locked. Missing cached packages are a
provisioning blocker, not permission to add a feed, install a tool, change
global configuration, or download a model. NuGet auditing is disabled in the
repository build properties; an offline build is not a vulnerability audit.

## Quickstart workflow

Run from the repository root:

```bash
dotnet --version
./build.fsx --list
./build.fsx --dry-run
./build.fsx -t All
```

The first command must report `10.0.302`. `--list` enumerates the targets;
`--dry-run` prints the planned commands without executing them. `All` restores,
builds, tests, and packs the foundation. Running `./build.fsx` with no options
also selects `All`.

Stop on any nonzero exit rather than continuing with stale outputs.
Build/test outputs are under project `bin/` and `obj/`; packing produces a local
`Rlcd.0.1.0.nupkg` under `src/Rlcd/bin/Release/`. That package contains the
current validation foundation, not the planned inference or routing API.

The equivalent direct SDK workflow is:

```bash
dotnet restore Rlcd.slnx --configfile NuGet.Config --locked-mode
dotnet build Rlcd.slnx -c Release --no-restore
dotnet test tests/Rlcd.Tests/Rlcd.Tests.fsproj -c Release --no-build --no-restore
dotnet pack src/Rlcd/Rlcd.fsproj -c Release --no-build --no-restore
```

### Build targets

Select one case-sensitive target with `-t`. Dependencies run once per invocation:

```bash
./build.fsx -t Restore
./build.fsx -t Build
./build.fsx -t Tests
./build.fsx -t Package
./build.fsx -t Setup --dry-run
```

| Target | Behavior |
| --- | --- |
| `Restore` | Offline locked solution restore using the repository `NuGet.Config`. |
| `Build` | `Restore`, then Release build with `--no-restore`. |
| `Tests` | `Build` and its dependencies, then Release tests with `--no-build --no-restore`. |
| `Package` | `Build` and its dependencies, then local Release pack with `--no-build --no-restore`. |
| `CheckModels` | Run the existing verifier with `--integrity-only`; no downloads or native certification. |
| `Setup` | `Restore`, then `CheckModels`. Not a model acquisition step. |
| `All` | Run `Restore`, `Build`, `Tests`, and `Package` once each. Default when no target is supplied; excludes model checks. |

`Setup` and `CheckModels` fail with the currently absent model inputs. The
verified Setup run completes Restore, then stops at CheckModels with exit `2`;
its stdout summary reports `completed=["Restore"]`, `failedTarget="CheckModels"`,
and `status="failed"`. The runner forwards the verifier's JSON to stderr with
`status="blocked"`, detail `"Required local artifact is missing."`,
`integrityVerified=false`, and `nativeCertified=false`. No downloads occur.
`All` can validate the foundation without those inputs; its success must not
be read as model readiness.

`--dry-run` can accompany a target and model options; it does not validate
package or model availability. `--list` must be used alone. No docs compiler,
CI, model downloader, publishing, or release workflow is supplied by this
bootstrap.

## Models: integrity is not certification

The runner resolves model inputs as follows:

1. `--model-dir PATH`, otherwise `RLCD_MODELS_DIR`, otherwise
   `~/.cache/rlcd/laya-english`.
2. `--manifest PATH`, otherwise `models/laya-english.manifest.json` relative to
   the repository root.

Explicit relative model/manifest paths, including a relative
`RLCD_MODELS_DIR`, resolve from the caller's working directory. Child commands
run from the repository root, located from the build script's own directory.

Both the default model bundle and the runtime manifest are **absent**. Native
ONNX Runtime and an independent reference oracle are also not provisioned.
Model bytes must live outside the repository. Do not invent manifest hashes,
use test fixtures as real certification evidence, or substitute a model card
for verified bytes. The [model contract](docs/model-contract.md) records the
candidate and remaining provenance/parity blockers.

After separately authorized provisioning of real local inputs, use:

```bash
./build.fsx -t CheckModels --model-dir /absolute/local/bundle --manifest /absolute/local/manifest.json
./build.fsx -t Setup --model-dir /absolute/local/bundle --manifest /absolute/local/manifest.json
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

- The build runner writes one JSON object to stdout using schema
  `rlcd-build-v1`. `--list` returns `status="list"` and `targets`;
  `--dry-run` returns `status="dry-run"` and `commands`. Both exit `0`.
- Executed targets report `status="succeeded"` or `"failed"`, `exitCode`,
  `completed` (the successfully completed target names), and `failedTarget`
  (`null` on success). The runner stops on the first child failure and returns
  its exit code; success exits `0`. Handled argument/path/process-start errors
  report `status="error"`, a `detail`, and exit `2`.
- Target progress and both stdout and stderr from subprocesses go to the
  runner's **stderr**. In particular, CheckModels sends the verifier's JSON
  there; the runner's stdout remains its own summary.
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

The build-target interface and explicit setup/model-check split are inspired
by the local FastCoref.fs `build.fsx` and bootstrap documentation. FastCoref
uses pinned FAKE 5.23.1 packages; those packages were not cached for this
bootstrap, and no source/download approval was obtained. This runner
therefore uses F#/BCL only rather than copying FastCoref's package acquisition,
model download, or release flows.

Laya is the behavioral/checkpoint reference, not proof of local execution or
export lineage. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the
existing reference revisions, attribution, and evidence limits.
