---
name: repo-explorer
description: Map an unfamiliar code repository into a structured manifest — languages, entry points, build/test commands, and a one-line purpose for each top-level directory. Use when asked to "explore", "map", "summarize the structure of", or "get oriented in" a repo/workspace.
---

# Repo Explorer

Turn an unfamiliar repository into a deterministic, structured manifest. Follow these steps in order, then emit the JSON exactly as specified.

## Procedure

1. **List the tree.** Run `Bash: ls` at the workspace root, then `Glob **/*` (cap depth ~3). Ignore `node_modules`, `target`, `.git`, `bin`, `obj`, `dist`, and `.venv`. Note the top-level directories.

2. **Detect languages & manifests.** Look for `package.json` (JS/TS), `*.csproj`/`*.sln` (C#), `Cargo.toml` (Rust), `pyproject.toml`/`requirements.txt` (Python), `go.mod` (Go), `pom.xml`/`build.gradle` (JVM). Each manifest implies a language and a toolchain.

3. **Find entry points & commands.** Locate `Program.cs`, `main.rs`, `index.ts`/`index.js`, `__main__.py`/`main.py`, `cmd/*/main.go`, etc. Derive the build + test commands from the manifests (e.g. `dotnet build`/`dotnet test`, `npm run build`/`npm test`, `cargo build`/`cargo test`, `pytest`, `go build`/`go test ./...`).

4. **Confirm purpose.** `Read` the top-level `README*` (if present) and the primary manifest(s) to confirm what the project does and to label each top-level directory.

5. **Emit the manifest.** Output one fenced ```json block matching this exact schema (no extra keys):

```json
{
  "languages": ["..."],
  "entryPoints": ["..."],
  "build": ["..."],
  "test": ["..."],
  "topLevelDirs": [{ "path": "...", "purpose": "..." }],
  "summary": "one-paragraph overview"
}
```

Notes: Always `Read` a file before editing it, and keep every command read-only (list/read/inspect only) during exploration — do not build, install, or modify anything.
