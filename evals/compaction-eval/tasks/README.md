# Compaction eval — task suite

One directory per task id (spec §8). Contract:

| File | Purpose |
|---|---|
| `task.md` | The user message, verbatim after the `---` marker. `{SEED}` is replaced by the seed word so repeats are not byte-identical. An optional `## steer` heading starts a second message the runner sends while the run is active. |
| `meta.json` | `family`, `split` (`dev` / `test`), `seeds`, `minCompactions` (expected under the 64k clamp), `timeoutMinutes`, optional `steerAfterSeconds` (delay before the `## steer` message). |
| `fixtures/` | Copied into a fresh workspace before every run. `SOURCE.md` pins any vendored data. |
| `hidden/` | Checker-only material (tests, answer keys). Never copied into the workspace. |
| `check.ps1` | `pwsh check.ps1 -Workspace <dir> -Out <score.json>`; exit 0 when it ran (pass or fail), 2 when it could not judge. No LLM. |

`score.json` shape (`compaction-eval/score@1`, J1 layer):

```json
{ "task": "d1", "outcome": "pass|partial|fail", "score": 0.0, "checks": [ { "name": "…", "pass": true, "detail": "…" } ] }
```

`score` = passed checks / total checks; `pass` needs every check; `fail` = none.
