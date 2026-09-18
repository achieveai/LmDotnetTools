# Compaction eval — task suite

One directory per task id (spec §8). Contract:

| File | Purpose |
|---|---|
| `task.md` | The user message, verbatim after the `---` marker. `{SEED}` is replaced by the seed word so repeats are not byte-identical. An optional `## steer` heading starts a second message the runner sends while the run is active. |
| `meta.json` | `family`, `split` (`dev` / `test`), `seeds`, `minCompactions` (expected under the 64k clamp), `timeoutMinutes`, optional `steerAfterSeconds` (delay before the `## steer` message), optional `requiredFixtures` (see below). |
| `fixtures/` | Copied into a fresh workspace before every run. `SOURCE.md` pins any vendored data. **Fixtures are inputs: commit them.** |
| `hidden/` | Checker-only material (tests, answer keys). Never copied into the workspace. |
| `check.ps1` | `pwsh check.ps1 -Workspace <dir> -Out <score.json>`; exit 0 when it ran (pass or fail), 2 when it could not judge. No LLM. |

`score.json` shape (`compaction-eval/score@1`, J1 layer):

```json
{ "task": "d1", "outcome": "pass|partial|fail", "score": 0.0, "checks": [ { "name": "…", "pass": true, "detail": "…" } ] }
```

`score` = passed checks / total checks; `pass` needs every check; `fail` = none.

## `requiredFixtures` — declaring what a task cannot run without

```jsonc
"requiredFixtures": [{ "glob": "logs/*.log", "count": 12 }]
```

Each entry is a glob relative to `fixtures/` and the exact number of files that must match it. The
runner checks the prepared workspace against the list and fails the run when it does not agree, so a
missing input stops the sweep rather than being scored as a bad answer.

Declare it whenever a task's prompt names a quantity of files, because the failure it prevents is
silent. c2 asks the agent to read twelve days of access logs; those logs lived under
`fixtures/logs/`, which the repository's `**/logs/` rule excluded, so they were never committed. On
the machine that wrote them everything worked. Everywhere else the workspace had an empty `logs/`
directory, the agent was asked to read files that did not exist, and the checker scored the answer
it produced as wrong — a corpus bug wearing a model bug's clothes. `.gitignore` now re-includes
`evals/compaction-eval/tasks/*/fixtures/logs/`, and
`TodoEval.Runner.Tests.EvalCorpusCheckoutTests` asks **git** — not the filesystem — whether every
declared fixture is in the checkout, because a test that reads the disk passes in both worlds.

A glob's `*` does not cross a directory separator, so `*.log` asserts a log at the workspace root
and `logs/*.log` asserts one a level down: the depth is part of what the entry claims.
