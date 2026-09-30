# cb1 — effective-config tracing in a fictional service-mesh repo (synthetic)

Family `config-tracing`, split `dev`. Workspace = `fixtures/` (251 files, 0.42 MB,
~105k tokens; mostly handler/model boilerplate and padded docs). 8 questions "effective
value of X for service Y in env Z". Each answer rests on 3+ files: defaults < shared fragments (listed order,
nested includes first) < service.yaml < env global overlay < env service overlay < Startup.ConfigureMesh
(conditional on env / IsProduction (staging+prod) / feature flags (catalog default < env .jsonc) / another
setting). Every question has a commented-out override that would change it and a code step that changes it.
Checker: exact integer or normalised word; score = correct / 8. Trap flips and predicted trap-only
scores: `hidden/build-report.json`. Generator + knobs: `hidden/gen_repo.py`, `hidden/build.json`.

---

Project code name: {SEED}.

This workspace is a checkout of the Quillfeather mesh, a (fictional) service-mesh monorepo: 251 files
of YAML/JSON configuration, C# start-up code and docs under `config/`, `services/`, `env/`, `flags/`,
`platform/` and `docs/`. Nothing in it is from a real project, so the answers are only in these files;
general knowledge will not help. Much of it is routine boilerplate.

A service's effective settings are assembled from several files and then adjusted by its start-up code.
How the layers combine is documented in the repo itself (start with `docs/CONFIG.md`). Read the actual
files; do not assume.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read what you need.

Write `answers.json` in the workspace root:

```json
{
  "q1": { "answer": "<value>", "evidence": ["services/.../service.yaml", "..."] },
  "q2": { "answer": "...", "evidence": ["..."] }
}
```

- `answer` is the value exactly as the setting would hold it: a whole number for numeric settings, the
  bare word for text settings (for example `gzip`).
- `evidence` lists the files the answer rests on; it is optional and not scored.
- Include every question from q1 to q8. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** What is the effective mesh setting `pool_size` of service `ledgerbridge` in environment `prod`, once every configuration layer and the service's start-up code have been applied?
- **q2.** What is the effective mesh setting `request_timeout_ms` of service `manifestbridge` in environment `staging`, once every configuration layer and the service's start-up code have been applied?
- **q3.** What is the effective mesh setting `lb_policy` of service `berthbridge` in environment `prod`, once every configuration layer and the service's start-up code have been applied?
- **q4.** What is the effective mesh setting `queue_depth` of service `skiffworks` in environment `qa`, once every configuration layer and the service's start-up code have been applied?
- **q5.** What is the effective mesh setting `max_connections` of service `bollardbridge` in environment `prod`, once every configuration layer and the service's start-up code have been applied?
- **q6.** What is the effective mesh setting `cache_ttl_seconds` of service `berthyard` in environment `staging`, once every configuration layer and the service's start-up code have been applied?
- **q7.** What is the effective mesh setting `compression` of service `parcelscope` in environment `staging`, once every configuration layer and the service's start-up code have been applied?
- **q8.** What is the effective mesh setting `circuit_threshold` of service `ferryrelay` in environment `prod`, once every configuration layer and the service's start-up code have been applied?

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
