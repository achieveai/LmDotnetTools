# Fixture provenance

`specs/01-gateway.md` .. `08-notifications.md` and `workstreams/<name>/brief.md` are synthetic, generated
once by `../hidden/gen_specs.py` (seeded, deterministic; the same run writes `../hidden/keys.json`, the
active gate id per spec and the handoff code per workstream). No external data. Regenerate only together
with the hidden key.
