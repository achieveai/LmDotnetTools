# r2 — research in two waves: the second request makes the first wave's pages a summarisable head

Family `research`, split `dev`. Workspace = `fixtures/` (`kb/*.md`, 10 pages, ~36k words). Same knowledge
base and answer key as r1, but the reads arrive in two waves: the first message asks for five values that
live on pages 01-05, the second (`## steer`, sent after the first answer) asks for the remaining three from
pages 06-10 and a supersession check against everything already answered. Round 2 showed that r1's single
ten-page burst is the tail of the conversation and no cut can free anything; here the first wave is a
~22k-token head by the time the second wave lands, so the summariser actually runs. Checker: `answers.json`
against the hidden key, normalised string compare, plus a `sources.md` line per key.

---

Project code name: {SEED}.

The `kb/` directory is the internal wiki for the Orion platform. Read pages `01-overview.md` through `05-gateway.md` fully — do not grep for the keys, and do not read pages 06 to 10 yet; they are for a later request. Find the current, authoritative value for each item below and write them to `answers.json` as an object with string values. Some pages describe old behaviour; when a page says a value changed, use the newest value.

- `region_with_no_replicas` — the region that hosts leaders only
- `max_shards_per_tenant` — the maximum number of shards one tenant may own
- `snapshot_interval_minutes` — how often leader replicas snapshot
- `approvals_required_for_schema_change` — number of approvals a schema migration needs
- `default_request_timeout_ms` — the gateway's current default per-request timeout, in milliseconds

For each answer also write one line to `sources.md` in the form `<key>: <page file> — <the sentence you relied on>`. Reply with the five answers when done.

## steer

Thanks. Now read pages `06-security.md` through `10-glossary.md` fully and add these three keys to `answers.json`:

- `audit_retention_days` — how many days audit records stay in the cold tier once they leave the hot tier (the cold-tier figure alone, not hot plus cold)
- `canary_traffic_percent` — the percentage of production traffic that canary stage two receives
- `incident_commander_role` — who acts as incident commander (short role name)

Pages 06 to 10 also mention some of the five values you already answered, sometimes with older figures. Check each of your five earlier answers against what these pages say and keep the newest value in every case. The final `answers.json` must contain exactly the eight keys, and `sources.md` one line per key. Reply with all eight answers when done.
