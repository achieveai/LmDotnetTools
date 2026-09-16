# Fixture provenance

`logs/*.log` (12 files, 2026-03-02 .. 2026-03-13) are synthetic, generated once by
`../hidden/gen_logs.py` (seeded, deterministic; the same run writes `../hidden/expected.json`, the
CLI answer key, and `../hidden/incidents.json`, the per-day incident key). No external data.
Regenerate only together with the hidden keys.
