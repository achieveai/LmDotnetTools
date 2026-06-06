---
name: workspace-summary
description: Produce a structured inventory of the current workspace — file counts and total size by extension, the largest and most-recently-modified files, and the top-level layout. Use when asked to "summarize", "inventory", "take stock of", "what's in", or "give an overview of" the workspace (works for any folder, not just code repos).
---

# Workspace Summary

Turn the current workspace into a deterministic, structured inventory. Follow these steps in order, then emit the JSON exactly as specified. Keep every command read-only — never create, modify, or delete anything.

## Procedure

1. **Enumerate files.** Use `Glob` with pattern `**/*` from the workspace root. Ignore noise directories: `node_modules`, `.git`, `target`, `bin`, `obj`, `dist`, `.venv`, `__pycache__`. Collect the relative path of every regular file.

2. **Group by extension.** For each file, take its lowercase extension (or `"(none)"` when it has none). Count files per extension and, where cheap, sum their sizes. Use `Bash` (e.g. `ls -l`, `du`, `wc`) only for read-only size/line measurements when needed.

3. **Find the outliers.** Identify the 5 **largest** files (by bytes) and the 5 **most-recently-modified** files (by mtime). Record path + size for the former, path + an ISO-8601 timestamp for the latter.

4. **Map the top level.** List the immediate top-level entries (files and directories) under the workspace root, with a one-line note of what each appears to be (read a `README*` if present to inform the workspace-level summary).

5. **Emit the inventory.** Output exactly one fenced ```json block matching this schema (no extra keys):

```json
{
  "totalFiles": 0,
  "totalDirectories": 0,
  "byExtension": [{ "ext": ".md", "count": 0, "bytes": 0 }],
  "largestFiles": [{ "path": "...", "bytes": 0 }],
  "recentlyModified": [{ "path": "...", "modifiedUtc": "1970-01-01T00:00:00Z" }],
  "topLevel": [{ "name": "...", "kind": "file|dir", "note": "..." }],
  "summary": "one-paragraph overview of what this workspace contains"
}
```

Notes: counts and the `byExtension` array must reconcile with `totalFiles`. Prefer `Glob`/`Read` and read-only `Bash`; if a size is impractical to obtain, set `bytes` to `0` rather than guessing.
