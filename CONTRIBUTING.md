# Contributing to LmDotnetTools

Thanks for your interest in contributing. This guide covers the workflow rules
that the build and CI enforce automatically — please skim it before opening a
PR.

## Build setup

```bash
# Restore packages and the local tool manifest (Husky.Net, CSharpier).
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

**CSharpier is the formatting authority.** Its settings live in `.csharpierrc`
and its version is pinned in `.config/dotnet-tools.json`, so the hook, your
editor and CI all format identically. The pre-commit hook runs
`csharpier check` over the staged `.cs` files; CI runs it over the whole tree.

Run the formatter locally before pushing:

```bash
# Reformats every .cs file in place. This is the fix for any formatting failure.
dotnet csharpier format .

# What the gates run. Exits non-zero and names each offending file.
dotnet csharpier check .
```

If the pre-commit hook reports a diff, run `dotnet csharpier format .`, review
the changes, and stage them.

Two things worth knowing:

- **Do not reach for `dotnet format`.** It is no longer a gate here, and
  `dotnet format style` in particular applies Roslyn's own formatter, whose
  line wrapping disagrees with CSharpier's — running it will *introduce*
  formatting failures rather than fix them. That disagreement is why
  `IDE0055` is set to `none` in `.editorconfig`; every semantic and style
  analyzer stays enabled, and the zero-warning gate above is unaffected.
- **CI checks the whole tree, not just what you staged.** The hook only ever
  sees staged files, so a file changed by a merge, a revert, or a
  `--no-verify` commit reaches `main` unchecked. `dotnet csharpier check .` in
  CI is the step that catches it.

## Commit hook bypass

The pre-commit hook is intentionally fast — it checks only the staged `.cs`
files — so it should rarely be the bottleneck. If it is, you can run the build
gate manually via `./scripts/ci-test.ps1 -SkipRestore` and skip the hook with
`git commit --no-verify`. CI will still enforce both gates on the PR, and its
formatting step reads the whole tree rather than your staged set, so a
bypassed commit is caught there rather than slipping through.
