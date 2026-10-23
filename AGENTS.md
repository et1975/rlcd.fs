# Contributor guide

## Start here

- Read [README.md](README.md) for the current layout, commands, and blockers.
- `src/Rlcd/ModelBundle.fs` is a local bundle/configuration validator.
  `tests/Rlcd.Tests/ModelBundleTests.fs` contains its xUnit tests.
  `tests/Rlcd.Tests/BuildTests.fs` exercises the build runner.
  `build.fsx` owns the `CheckModels`, `VerifyModels`, and `Example` target logic;
  there are no standalone verifier or example scripts.
  `NativeRuntimeTests.fs` verifies the pinned runtime's actual native version and
  CPU provider; `CheckRuntime` runs it without requiring model files.
  `DownloadModels` explicitly provisions the pinned English cache and manifest.
- `src/Rlcd/Domain.fs` and `Questions.fs` construct validated generic questions,
  budgets and requests; see `docs/generic-api.md`.
  `Tokenizer.fs` implements the supported English NFC/ByteLevel/BPE pipeline;
  `Sequence.fs` uses its encoder or a supplied one. `Calibration.fs` decodes
  supplied logits. Synthetic tests do not certify selected-model tokenizer or
  native parity; see `docs/preprocessing.md`.
  `Inference.fs` owns the real CPU session, tensor validation and lifetime;
  `Api.fs` exposes `DecisionModel.Load`, `Predict`, and `PredictBatch`.
  `InferenceTests.fs` includes controlled-runtime tests and the separately
  selected `CheckInference` real-model test.
  `src/Rlcd.Router/` composes task-tier inference with the existing pure
  selection policy and a caller-maintained catalog; see `docs/router-policy.md`.
  `Routing.fs` owns pure composition/results and `Router.fs` the borrowed-model
  adapter. `RouterTests.fs` covers controlled outcomes and real cached execution.
  Keep `Policy.select` pure and caller-owned model lifetime explicit.
- MCP hosting and live Copilot dispatch integration remain planned.
  Keep generic library code independent of
  router, provider, Copilot, and task-service concepts.
- Consult [docs/model-contract.md](docs/model-contract.md) and
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before making model or
  provenance claims. Published metadata and synthetic fixtures are not native
  certification.

## Build and tests

The requested SDK is **10.0.402**, with `global.json` feature-band roll-forward,
stable F# 10, and `net10.0`. Warnings are errors.
Run from the repository root:

```bash
./build.fsx --list
./build.fsx -t Tests
./build.fsx -t All
```

`Tests` runs Restore/Build/Tests for both test projects; `All` adds packages for
both libraries. Dependencies run once per invocation. These tests do not need
real model files: `RealInference` is excluded. Run `./build.fsx -t CheckInference`
explicitly for actual graph execution; missing inputs must fail, not skip.
Re-run the suite after
implementation changes and report the observed result, not an old test count.
`build.fsx` is the only approved entry point for restore, build, tests,
packaging, and model checks. Do not invoke SDK commands or internal verifier
scripts directly. Model options are in the README.

Use FAKE's native `--help` and `--list` commands. The build uses FAKE 6.1.3
targets, dependency operators, and DotNet helpers, not a custom runner.
Console output is ordinary FAKE/build diagnostics, not JSON; check the exit status.
Model inputs use `RLCD_MODELS_DIR` and `RLCD_MODEL_MANIFEST`.
`Setup` is Restore/CheckModels, not a downloader. It fails when the selected
inputs are missing or invalid. `DownloadModels` is explicit and outside `All`;
its default manifest lives in the model cache. Neither successful `All` nor
`CheckInference` establishes independent tokenizer/model parity.
FSI restores the pinned FAKE packages from configured NuGet sources when they
are not cached. See the README for the process-local SDK setting needed if
cold FSI restore prefetches unused framework packs.

## Change boundaries

- Preserve the existing `src/`, `tests/`, and `docs/` structure; keep repository
  tooling in `build.fsx` targets.
  Keep dependency versions in `Directory.Packages.props` and preserve locked
  restore. For intentional dependency changes, use `./build.fsx -t UpdateLocks`
  and review the resulting lockfile changes; do not regenerate them incidentally.
- Use normal NuGet configuration and sources; do not recreate the removed
  repository-local offline-only `NuGet.Config` or change global feed settings.
  Dependency restore remains pinned and locked. Model acquisition is a separate
  explicit build operation, not an inference-time download or permission to
  install an unrelated reference toolchain.
- Keep model artifacts outside the repository. Never replace unknown hashes
  or certification results with placeholders. Integrity success is not native
  certification; real-input checks must not silently skip into success.
- Preserve Laya adaptation attribution in `THIRD-PARTY-NOTICES.md` and the
  upstream license in `docs/Laya-LICENSE.txt`, including their core-package copies.
  Retain the Unicode License V3 notice for the tokenizer's derived word-property data.
- Follow `.editorconfig`, explicit F# project compilation order, and the
  existing validation/error conventions. Add focused regression tests for
  changed behavior.
- When delegation is authorized, give F# implementation/review to an F#
  specialist with a bounded scope. Documentation-only work must not edit F#
  source or scripts; no repository-local agent/skill plugin is assumed.

## Validation and handoff

- For code changes, report the commands actually run, exit results, and test
  failures or skips. Do not infer inference readiness from unit-test success.
- For documentation, check every local link and reconcile commands, target
  dependencies, streams, and exit rules with the final scripts/configuration.
  Run `git diff --check` for tracked edits and inspect new files for whitespace
  and balanced code fences. There is no docs compiler or docs-build command.
- Report missing artifacts and unverified commands explicitly. Keep later
  product phases, CI, tooling installation, and release work out of a bounded
  documentation/bootstrap change unless separately approved.
