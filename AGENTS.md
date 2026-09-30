# Contributor guide

## Start here

- Read [README.md](README.md) for the current layout, commands, and blockers.
- `src/Rlcd/ModelBundle.fs` is a local bundle/configuration validator.
  `tests/Rlcd.Tests/ModelBundleTests.fs` contains its xUnit tests.
  `tests/Rlcd.Tests/BuildTests.fs` exercises the build runner.
  `scripts/VerifyArtifacts.fsx` is the local integrity-check entrypoint.
- Generic inference, native ONNX execution, the model router, and MCP hosting
  are planned, not implemented. Keep generic library code independent of
  router, provider, Copilot, and task-service concepts.
- Consult [docs/model-contract.md](docs/model-contract.md) and
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before making model or
  provenance claims. Published metadata and synthetic fixtures are not native
  certification.

## Build and tests

Use SDK **10.0.302**, stable F# 10, and `net10.0`. Warnings are errors.
Run from the repository root:

```bash
./build.fsx --list
./build.fsx -t Tests
./build.fsx -t All
```

`Tests` runs Restore/Build/Tests; `All` adds Package. Dependencies run once per
invocation. These tests do not need real model files. Re-run the suite after
implementation changes and report the observed result, not an old test count.
Direct SDK equivalents and model options are in the README.

Use `--dry-run` to inspect commands without executing them. The runner writes
its JSON result to stdout and subprocess logs to stderr; check the exit status.
`Setup` is Restore/CheckModels, not a downloader. It fails with the absent
default model inputs; a successful `All` does not certify native inference.

## Change boundaries

- Preserve the existing `src/`, `tests/`, `scripts/`, and `docs/` structure.
  Keep dependency versions in `Directory.Packages.props` and preserve locked
  restore; do not regenerate lock files incidentally.
- Preserve `NuGet.Config` and its offline-only cache source. Missing packages,
  model bytes, a runtime manifest, native ONNX Runtime, or an independent oracle
  are blockers to report, not permission for network installation, downloads,
  feed changes, or global configuration changes.
- Keep model artifacts outside the repository. Never replace unknown hashes
  or certification results with placeholders. Integrity success is not native
  certification; real-input checks must not silently skip into success.
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
