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
      "MaxContextTokens": 200000,
      "MaxOutputTokens": 64000,
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
- `MaxContextTokens`, `MaxOutputTokens` (#681): optional positive integers — the model's context window and output ceiling, surfaced through `IModelCapacityResolver` so each generation's context observation carries a utilization. Present-but-not-positive rejects the whole entry.
- `ContextWindow:MaxTokens` (top-level, not per model; default `156000`): the sample host's ceiling. Every resolved window is clamped to it, and a model with no `MaxContextTokens` (Claude CLI, Codex, any Copilot id not listed below) resolves to exactly it, so the gauge and compaction work for those models. `0` turns the cap off, and an absent window is then unknown again (no gauge, no compaction pressure). A negative value fails startup.

LmConfig JSON catalogs (`PricingConfig`) carry the same fields as `cache_read_per_million`, `cache_write_5m_per_million`, `cache_write_1h_per_million`, `reasoning_per_million`, `cache_accounting`, `effective_date`. Two routes sharing a model name must agree on every field or the name is dropped as conflicting.

## Shipped rates and citations

All USD per million tokens. Verified 2026-09-02 against the vendor page. Re-verify before trusting a figure older than the page's own change date.

| Model id | Aliases | Input | Cache read | Cache write 5m | Cache write 1h | Output | Accounting | Source |
|---|---|---|---|---|---|---|---|---|
| `gpt-4o` | — | 2.50 | 1.25 | — | — | 10.00 | SubsetOfInput | https://developers.openai.com/api/docs/pricing |
| `claude-sonnet-4-20250514` | `claude-sonnet-4` | 3.00 | 0.30 | 3.75 | 6.00 | 15.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |
| `claude-sonnet-4-5-20250929` | `claude-sonnet-4-5` | 3.00 | 0.30 | 3.75 | 6.00 | 15.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |

Copilot-served ids, priced at the vendor's retail API list price as a public-equivalent estimate (verified 2026-09-18). This is what the same usage would cost on the vendor API, not what the Copilot subscription bills:

| Model id | Aliases | Input | Cache read | Cache write 5m | Cache write 1h | Output | Accounting | Source |
|---|---|---|---|---|---|---|---|---|
| `gpt-5.6-sol` | — | 4.00 | 0.40 | — | — | 20.00 | SubsetOfInput | https://developers.openai.com/api/docs/pricing |
| `gpt-5.6-terra` | — | 2.00 | 0.20 | — | — | 12.00 | SubsetOfInput | https://developers.openai.com/api/docs/pricing |
| `gpt-5.6-luna` | — | 0.20 | 0.02 | — | — | 1.20 | SubsetOfInput | https://developers.openai.com/api/docs/pricing |
| `claude-opus-5` | — | 5.00 | 0.50 | 6.25 | 10.00 | 25.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |
| `claude-sonnet-5` | — | 2.00 | 0.20 | 2.50 | 4.00 | 10.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |
| `claude-haiku-4.5` | `claude-haiku-4-5`, `claude-haiku-4-5-20251001` | 1.00 | 0.10 | 1.25 | 2.00 | 5.00 | Additive | https://platform.claude.com/docs/en/about-claude/pricing |
| `deepseek-v4-pro` | — | 1.32 | 0.044 | — | — | 3.96 | SubsetOfInput | https://api-docs.deepseek.com/quick_start/pricing/ |

- `gpt-5.6-sol`'s rate is promotional through at least 2026-11-21. Re-verify after that date.
- OpenAI bills prompts over 272K input at 2x input and 1.5x output. While compaction is on (the sample default), it targets the 156K window, so requests normally stay below that. With compaction off, a long conversation can cross it.
- `deepseek-v4-pro` is the peak-hour rate. Off-peak is half price, so off-peak runs read high.
- Copilot reports no cache reads for Claude ids (recorded `CacheReadTokens` is 0). Their estimates therefore price all input at the uncached rate, which is an upper bound.
- The vendor windows (200K to 1.05M) are recorded as cited. `ContextWindow:MaxTokens` clamps them to 156K.

Notes:

- OpenAI publishes no cache-write price for `gpt-4o`; cache writes are not billed separately, and the record carries none, so the category stays absent rather than zero.
- Reasoning is billed as output by both vendors; `ReasoningPerMillion` is left null.

Context windows (`MaxContextTokens` / `MaxOutputTokens`, #681), verified 2026-09-02 against the vendor model page in `_window_source`:

| Model id | Window | Max output | Source |
|---|---|---|---|
| `gpt-4o` | 128,000 | 16,384 | https://developers.openai.com/api/docs/models/gpt-4o |
| `claude-sonnet-4-5-20250929` | 200,000 | 64,000 | https://platform.claude.com/docs/en/models/sonnet-4-5/overview |

`claude-sonnet-4-20250514` carries no window: its model page no longer resolves, so there is nothing to cite. It still prices, and its window resolves to the `ContextWindow:MaxTokens` cap (no utilization only when the cap is 0).

## Deliberately unpriced

These ids appear in the sample's configuration but have no entry. Their cost resolves null ("unavailable"). Do not add a guessed rate (#378).

- Copilot catalog ids not in the table above (for example the `copilot` provider default `claude-sonnet-4.5`). Add one from its vendor page when it is used.
- Claude CLI default `claude-sonnet-4-6` (Anthropic lists the API rate for Sonnet 4.6, but the CLI transport here is subscription-billed; an operator on the API can add it from the same Anthropic page).
- Codex default `gpt-5.3-codex`.

## Known limits

- OpenAI's long-context tiers and service-tier (priority/flex/batch) variants are not derivable from a usage record; the shipped `gpt-4o` rate is the standard tier.
- The Anthropic provider does not report the 5m/1h cache-write split, so Anthropic estimates with cache writes are always Partial (lower bound at the 5m rate).
