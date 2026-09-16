# Shadow prices for gpt-5.6-* (Copilot)

Copilot bills by seat, not per token, so the host's public catalog deliberately has no entry for
these ids and every conversation cost resolves `unavailable`. The eval needs a **relative** cost
signal, so every eval host runs with the shadow catalog below, passed as `--Pricing:…` arguments
(`PricingCatalog` is id-agnostic; no code change).

**Provenance: ASSUMED, not cited.** Ratios come from the `billing.token_prices.default` shape the
Copilot `/models` endpoint returned for `gpt-5.5` (research-copilot §3: input 500 · cache_read 100 ·
output 3000 · cache_write 0, per 1M, unit undocumented). Absolute dollars are a placeholder scale
(input = $5/M); the judge reports **tokens first** (uncached input, cached input, output) and dollars
second. Terra/sol/luna multipliers are a guess about tier and are the same across cells, so
cross-cell comparisons are unaffected.

| id | PromptPerMillion | CacheReadPerMillion | CompletionPerMillion | CacheAccounting | MaxContextTokens | MaxOutputTokens |
|---|---|---|---|---|---|---|
| gpt-5.6-terra | 5 | 1 | 30 | SubsetOfInput | 272000 | 128000 |
| gpt-5.6-sol | 10 | 2 | 60 | SubsetOfInput | 272000 | 128000 |
| gpt-5.6-luna | 2 | 0.4 | 12 | SubsetOfInput | 272000 | 128000 |

Windows: `max_prompt_tokens` of the default tier (272k) — the long-context tier (922k at ~2×)
is never entered by a compacting cell; an Off cell that crosses 272k hits the real
`model_max_prompt_tokens_exceeded`, which is the "Off did not fit" signal. Clamped cells override
`MaxContextTokens=64000` so compaction triggers inside the budget.

`Pricing:Version=shadow-2026-09-16` marks every archived cost as shadow-priced.

Host arguments (one variant's `extraArgs` prefix; see `runner.example.jsonc`):

```
--Pricing:Version=shadow-2026-09-16
--Pricing:Models:gpt-5.6-terra:PromptPerMillion=5 --Pricing:Models:gpt-5.6-terra:CacheReadPerMillion=1 --Pricing:Models:gpt-5.6-terra:CompletionPerMillion=30 --Pricing:Models:gpt-5.6-terra:CacheAccounting=SubsetOfInput --Pricing:Models:gpt-5.6-terra:EffectiveDate=2026-09-16 --Pricing:Models:gpt-5.6-terra:MaxContextTokens=272000 --Pricing:Models:gpt-5.6-terra:MaxOutputTokens=128000
--Pricing:Models:gpt-5.6-sol:PromptPerMillion=10 --Pricing:Models:gpt-5.6-sol:CacheReadPerMillion=2 --Pricing:Models:gpt-5.6-sol:CompletionPerMillion=60 --Pricing:Models:gpt-5.6-sol:CacheAccounting=SubsetOfInput --Pricing:Models:gpt-5.6-sol:EffectiveDate=2026-09-16 --Pricing:Models:gpt-5.6-sol:MaxContextTokens=272000 --Pricing:Models:gpt-5.6-sol:MaxOutputTokens=128000
--Pricing:Models:gpt-5.6-luna:PromptPerMillion=2 --Pricing:Models:gpt-5.6-luna:CacheReadPerMillion=0.4 --Pricing:Models:gpt-5.6-luna:CompletionPerMillion=12 --Pricing:Models:gpt-5.6-luna:CacheAccounting=SubsetOfInput --Pricing:Models:gpt-5.6-luna:EffectiveDate=2026-09-16 --Pricing:Models:gpt-5.6-luna:MaxContextTokens=272000 --Pricing:Models:gpt-5.6-luna:MaxOutputTokens=128000
```
