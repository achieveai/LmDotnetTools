# Live compatibility snapshot

Checked: 2026-09-14

Tool-name/schema reachability check, not all tool parameter contracts or business operations. Microsoft Learn public search succeeded. No live mutations.

```json
{
  "drift": {
    "missing": [],
    "unbundled": []
  },
  "ok": true,
  "tools": 6
}
```

New live tools are not silently enabled. Missing tools may fail; use a verified supported operation rather than inventing a replacement. Refresh original clients and review policies explicitly before expanding coverage.
