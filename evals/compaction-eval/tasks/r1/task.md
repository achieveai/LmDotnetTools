# r1 — research: scattered and superseded facts across a large knowledge base

Family `research`, split `dev`. Workspace = `fixtures/` (`kb/*.md`, 10 pages, ~30k words). Each answer
sits in one sentence on one page; three older pages carry stale values that a summary written from a
skim will keep (R3 target: exact numbers and identifiers must survive the cut). Checker: `answers.json`
against the hidden key, normalised string compare.

---

Project code name: {SEED}.

The `kb/` directory is the internal wiki for the Orion platform. Find the current, authoritative value for each item below and write them to `answers.json` as an object with exactly these keys and string values. Some pages describe old behaviour; when a page says a value changed, use the newest value. Read every page fully — do not grep for the keys.

- `default_request_timeout_ms` — the gateway's current default per-request timeout, in milliseconds
- `max_shards_per_tenant` — the maximum number of shards one tenant may own
- `canary_traffic_percent` — the percentage of production traffic that canary stage two receives
- `incident_commander_role` — who acts as incident commander (short role name)
- `audit_retention_days` — how many days audit records are kept in total after the hot tier
- `snapshot_interval_minutes` — how often leader replicas snapshot
- `approvals_required_for_schema_change` — number of approvals a schema migration needs
- `region_with_no_replicas` — the region that hosts leaders only

For each answer also write one line to `sources.md` in the form `<key>: <page file> — <the sentence you relied on>`. Reply with the answers when done.
