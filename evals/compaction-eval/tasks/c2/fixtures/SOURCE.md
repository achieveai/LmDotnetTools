# Fixture provenance

`logs/*.log` (12 files, 2026-03-02 .. 2026-03-13) are synthetic, generated once by
`../hidden/gen_logs.py` (seeded, deterministic; the same run writes `../hidden/expected.json`, the
CLI answer key, and `../hidden/incidents.json`, the per-day incident key). No external data.
Regenerate only together with the hidden keys.

These twelve files are **committed**, and `.gitignore` carries an explicit re-include for
`evals/compaction-eval/tasks/*/fixtures/logs/` to keep them that way — the repository's `**/logs/`
and `*.log` rules would otherwise treat a task input as a build output. `meta.json` declares
`requiredFixtures` so that a checkout missing them fails the run instead of judging an empty
workspace. Regenerating without also regenerating the hidden keys silently changes the answer.
