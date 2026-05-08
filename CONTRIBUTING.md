# Contributing to LmDotnetTools

Thanks for your interest in contributing. This guide covers the workflow rules
that the build and CI enforce automatically — please skim it before opening a
PR.

## Build setup

```bash
# Restore packages and the local tool manifest (Husky.Net, dotnet-format).
dotnet tool restore
dotnet restore LmDotnetTools.sln

# Build the solution. With no warnings the build prints `0 Warning(s)`.
dotnet build LmDotnetTools.sln
```

The first restore also runs `dotnet husky install`, which wires the repo's
pre-commit Git hook into your local clone. Set `HUSKY=0` if you need to bypass
that (e.g. on CI runners that already manage hooks).

## The zero-warning gate

`Directory.Build.props` sets `TreatWarningsAsErrors=true` for every project in
the solution, so any analyzer warning fails the build. Pull requests that
introduce a warning will be rejected by the `Build and Test` workflow.

To inspect warnings while you work, build with the analyzers visible:

```bash
dotnet build LmDotnetTools.sln /v:m -p:TreatWarningsAsErrors=false
```

If you genuinely need to suppress a rule (legacy code, generated files, design
intent), prefer either:

1. A scoped `dotnet_diagnostic.<RULE>.severity = none` entry in `.editorconfig`
   (with a comment explaining why), or
2. A targeted `#pragma warning disable <RULE> // reason` block in the source.

Avoid `<NoWarn>` in individual `.csproj` files unless the rule must be silenced
for an entire project (the central `NoWarn` list lives in
`Directory.Build.props`).

## Formatting

The repository uses `dotnet format` with the rules in `.editorconfig`. The
pre-commit hook runs `dotnet format whitespace --verify-no-changes`, and the
same step runs in CI before the build.

Run the autofixer locally before pushing:

```bash
# Whitespace + line endings only — safe and fast.
dotnet format whitespace LmDotnetTools.sln

# Style rules (collection expressions, redundant casts, etc.). The IDE0039
# fixer rewrites lambdas as local functions and can break code that uses
# multiple `_` discards, so we exclude it.
dotnet format style LmDotnetTools.sln --severity warn --exclude-diagnostics IDE0039
```

If the pre-commit hook reports a diff, run the commands above, review the
changes, and stage them.

## Commit hook bypass

The pre-commit hook is intentionally fast (whitespace verify only) so it
should rarely be the bottleneck. If it is, you can run the build gate
manually via `./scripts/ci-test.ps1 -SkipRestore` and skip the hook with
`git commit --no-verify`. CI will still enforce both gates on the PR.
