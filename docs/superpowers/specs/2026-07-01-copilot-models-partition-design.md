# Expose GitHub Copilot models in the sample model list, partitioned by vendor

**Date:** 2026-07-01
**Status:** Approved via goal directive + HITL scope answers
**Target repo:** `B:/sources/LmDotnetTools` (main checkout; feature branch off `main`)

## Goal
Expose all models available through the GitHub Copilot-based provider in the
LmStreaming.Sample model list, clearly partitioned into **Anthropic** and **OpenAI** groups.

## Locked decisions (from HITL)
1. **Which list:** LmStreaming.Sample provider dropdown (`ProviderRegistry` → `/api/providers` → `ProviderSelector.vue`). NOT LmConfig `models.json`.
2. **Source:** Dynamic discovery — call Copilot `GET /models` at startup and build the list from the live response.
3. **Scope:** Only models whose `vendor ∈ {Anthropic, OpenAI}` (normalizing `Azure OpenAI` → `OpenAI`) **and** that are routable via an existing factory (`/v1/messages` → Anthropic, `/responses` → Responses). Google/Gemini and `/chat/completions`-only models excluded.
4. **UI:** Grouped dropdown with non-selectable headers (`Copilot · Anthropic`, `Copilot · OpenAI`).
5. **Curated entries:** Remove the 4 hardcoded Copilot entries (`sonnet`, `haiku`, `gpt-5.5`, `gpt-5.5-mini`); the discovered list is the single source. Generalize the dispatch to route by discovered transport instead of those literal ids.

## Current state (grounding)
- `samples/LmStreaming.Sample/Services/ProviderRegistry.cs:20-41` — hardcoded catalog incl. the 4 Copilot entries; availability gated on `hasCopilotToken` (:73, :86).
- `ProviderDescriptor(Id, DisplayName, Available, KnownLimitation?)` — flat, no group field.
- Dispatch: `Func<string,IStreamingAgent>` switch `Program.cs:314-328` (`sonnet`/`haiku` → `CopilotAnthropicAgentFactory`; `gpt-5.5*` → `CopilotResponsesAgentFactory`).
- Model id: `GetModelIdForProvider` switch `Program.cs:1496-1512` → `GenerateReplyOptions.ModelId` (`:757`, `:839`).
- Copilot `/models` real response: `tests/CopilotAnthropicProxy.Tests/Fixtures/copilot-models-real-response.json` — each entry has `id`, `name`, `vendor`, `supported_endpoints`, `capabilities.family`.
- Auth/HTTP to reuse: `CopilotHttpClientFactory.Create(host, tokenProvider, session, options)`; token via `CliCredentialCopilotTokenProvider`; host `CopilotOptions.DefaultBaseUrl`.
- Existing endpoint-filter/parse precedent (not reused directly): `ProxyModelResolver.ParseMessagesCapableModelIds` in `samples/CopilotAnthropicProxy.Sample/Program.cs`.

## Design

### Component 1 — Discovery (`src/GithubCopilotProvider/Models/`)
- `CopilotModelInfo` record: `Id`, `DisplayName`, `Vendor` (normalized), `SupportedEndpoints`, derived `Transport` enum { `Anthropic`, `Responses`, `Unsupported` }.
- `CopilotVendor`/`CopilotModelTransport` — small enums or normalized strings.
- `CopilotModelCatalogParser.Parse(json)` — **pure**. Parses `{"data":[...]}`, normalizes vendor, derives transport from `supported_endpoints`, keeps only `Vendor ∈ {Anthropic, OpenAI}` and `Transport ∈ {Anthropic, Responses}`. Unit-tested against the real fixture.
- `CopilotModelsClient.GetModelsAsync(ct)` — `CopilotHttpClientFactory.Create(...)` → `GET /models` → `Parse`. Returns empty list on failure.

### Component 2 — Sample wiring
- `ProviderDescriptor` gains `string? Group` (null → flat render). Add `Vendor`/`RawModelId` only if needed for dispatch; prefer a separate lookup map `id → CopilotModelInfo`.
- `ProviderRegistry` accepts injected `IReadOnlyList<CopilotModelInfo>`; builds one descriptor per model, **id = raw model id**, `Group = "Copilot · Anthropic" | "Copilot · OpenAI"`, `Available = hasCopilotToken`. Removes the 4 curated entries. Non-Copilot entries unchanged (`Group = null`).
- Startup resolve in `Program.cs` (sync-over-async at registration, same pattern as `:796`). Token absent → empty list → no Copilot models. Token present + discovery fails → small built-in Anthropic+OpenAI fallback set so the app still runs.
- Dispatch generalization: factory switch + model-id resolution look up the discovered `CopilotModelInfo` by id → `Transport` picks the factory; raw id → `GenerateReplyOptions.ModelId`. Direct (`openai`/`anthropic`) and CLI (`claude`/`codex`/`copilot`) branches unchanged.

### Component 3 — UI
- `types/providers.ts` `ProviderDescriptor` gains `group?: string | null`.
- `ProviderSelector.vue`: group entries by `group`; `group == null` render flat first (unchanged look), grouped entries under non-selectable headers. Preserve availability/limitation/active rendering.

### Testing (TDD, RED→GREEN)
- Parser unit tests vs real fixture: Anthropic→Anthropic partition/Transport; GPT→OpenAI/Responses; Gemini/Google + `/chat/completions`-only excluded; `Azure OpenAI`→`OpenAI`.
- `ProviderRegistry` tests: stub list → descriptors+groups; empty/no-token → no Copilot entries; non-Copilot entries unchanged.
- Dispatch tests: `claude-*` id → Anthropic factory; `gpt-*` id → Responses factory; raw id flows to `ModelId`.

## Out of scope (YAGNI)
- `CopilotAnthropicProxy.Sample`, `src/LmConfig/models.json`, real Anthropic/OpenAI providers.
- Caching/refresh beyond the single startup resolve.
- Live network in tests (fixture-driven).

## Risks
- Overlap of discovered `gpt-5.5` with a former curated id — resolved by "replace" decision (single source).
- `Azure OpenAI` vendor drift — handled by normalization.
- Startup network dependency — handled by graceful fallback.
