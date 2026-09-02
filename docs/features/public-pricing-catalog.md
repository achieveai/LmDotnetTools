# Public pricing catalog

Category-complete public-price cost estimates for usage records (#682, spec #679 §4.5).

## What an estimate covers

`ModelPricing.Estimate(UsageRecord)` prices every billed category of a record:

| Category | Tokens | Rate |
|---|---|---|
| Uncached input | `InputTokens` (Additive) or `InputTokens − CacheReadTokens` (SubsetOfInput) | `PromptPerMillion` |
| Cache read | `CacheReadTokens` | `CacheReadPerMillion` |
| Cache write, 5m TTL | `CacheWriteTokens − CacheWrite1hTokens` | `CacheWrite5mPerMillion` |
| Cache write, 1h TTL | `CacheWrite1hTokens` | `CacheWrite1hPerMillion` |
| Output | `OutputTokens − ReasoningTokens` | `CompletionPerMillion` |
| Reasoning | `ReasoningTokens` | `ReasoningPerMillion`, else `CompletionPerMillion` |

Rules:

- Money is integer micro-units. The total is rounded once, half-to-even.
- A category with tokens and **no rate is never priced at the base rate and never at zero**. It is left out and named in `CostEstimate.MissingCategories`; the estimate is `CostCompleteness.Partial` and its figure is a lower bound.
- Cache writes with an unknown TTL split (`CacheWrite1hTokens == null`) are priced at the 5m rate and flagged `cache_write_ttl_unknown` (Partial). The Anthropic provider reports only the combined `cache_creation_input_tokens`, so every Anthropic estimate with cache writes is Partial today.
- An unknown model is `CostCompleteness.Unavailable` with no figure.
- When nothing with tokens could be priced the figure is `null`, not `0`.
- Preferred display amount (`UsageRecord.PreferredCostMicros`) is the provider-reported figure when present, else the estimate, else null. Both remain queryable in their own fields.

### Cache accounting modes

| Mode | Provider | Meaning |
|---|---|---|
| `SubsetOfInput` (default) | OpenAI | `cached_tokens ⊆ prompt_tokens`. Uncached input = input − cache read. |
| `Additive` | Anthropic | `input_tokens` excludes cache read and cache creation. Every category is billed on top. |

The mode changes the arithmetic. The same record priced under the wrong mode is double-counted or under-counted, so a misspelt `CacheAccounting` rejects the entry instead of defaulting.

### Completeness enum

`CostCompleteness { Unavailable = 0, Partial, Complete }`. Zero is `Unavailable` so a usage row persisted before the field existed never deserializes as `Complete`; the ledger seed path re-derives `Partial` for a legacy row that carries an estimate.

## Configuration schema

Sample host: `samples/LmStreaming.Sample/appsettings.json`, section `Pricing`.

```json
"Pricing": {
  "Version": "2026-09-02",
  "Models": {
    "<model id>": {
      "PromptPerMillion": 3,
      "CompletionPerMillion": 15,
      "CacheReadPerMillion": 0.3,
      "CacheWrite5mPerMillion": 3.75,
      "CacheWrite1hPerMillion": 6,
      "ReasoningPerMillion": null,
      "CacheAccounting": "Additive",
      "EffectiveDate": "2026-09-02",
      "_source": "https://vendor.example/pricing",
      "Aliases": ["<another id the model is stamped with>"]
    }
  }
}
```

- `PromptPerMillion`, `CompletionPerMillion`: required.
- Category rates: optional. Absent = unpriced category (Partial when it has tokens). Present-but-negative/NaN/infinite rejects the whole entry.
- `CacheAccounting`: `SubsetOfInput` (default) or `Additive`.
- `EffectiveDate`: `yyyy-MM-dd`.
- `_source`: vendor URL, ignored by the binder.

LmConfig JSON catalogs (`PricingConfig`) carry the same fields as `cache_read_per_million`, `cache_write_5m_per_million`, `cache_write_1h_per_million`, `reasoning_per_million`, `cache_accounting`, `effective_date`. Two routes sharing a model name must agree on every field or the name is dropped as conflicting.

## Shipped rates and citations

All USD per million tokens. Verified 2026-09-02 against the vendor page. Re-verify before trusting a figure older than the page's own change date.

| Model id | Aliases | Input | Cache read | Cache write 5m | Cache write 1h | Output | Accounting | Source |
|---|---|---|---|---|---|---|---|---|
| `gpt-4o` | — | 2.50 | 1.25 | — | — | 10.00 | SubsetOfInput | https://developers.openai.com/api/docs/pricing |
| `claude-sonnet-4-20250514` | `claude-sonnet-4` | 3.00 | 0.30 | 3.75 | 6.00 | 15.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |
| `claude-sonnet-4-5-20250929` | `claude-sonnet-4-5` | 3.00 | 0.30 | 3.75 | 6.00 | 15.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |

Notes:

- OpenAI publishes no cache-write price for `gpt-4o`; cache writes are not billed separately, and the record carries none, so the category stays absent rather than zero.
- Reasoning is billed as output by both vendors; `ReasoningPerMillion` is left null.

## Deliberately unpriced

These ids appear in the sample's configuration but are served over a subscription transport with no per-token list price to cite. Their cost resolves null ("unavailable"). Do not add a guessed rate (#378).

- GitHub Copilot catalog ids (`SubAgentIntelligence.Tiers`, and the `copilot` provider default `claude-sonnet-4.5`).
- Claude CLI default `claude-sonnet-4-6` (Anthropic lists the API rate for Sonnet 4.6, but the CLI transport here is subscription-billed; an operator on the API can add it from the same Anthropic page).
- Codex default `gpt-5.3-codex`.

## Known limits

- OpenAI's long-context tiers and service-tier (priority/flex/batch) variants are not derivable from a usage record; the shipped `gpt-4o` rate is the standard tier.
- The Anthropic provider does not report the 5m/1h cache-write split, so Anthropic estimates with cache writes are always Partial (lower bound at the 5m rate).
