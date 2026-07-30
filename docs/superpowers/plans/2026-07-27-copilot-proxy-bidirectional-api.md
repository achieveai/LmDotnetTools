# Copilot Proxy Bidirectional API Implementation Plan

> Steps use checkbox (`- [ ]`) syntax so progress can be tracked task-by-task.

**Goal:** Let Claude Code, Codex CLI, and opencode each drive *any* GitHub Copilot model through `samples/CopilotAnthropicProxy.Sample`, regardless of which API dialect the client speaks.

**Architecture:** The proxy already forwards Anthropic Messages traffic to Copilot byte-for-byte. Copilot's `/models` catalog says which transport each model actually serves (`/v1/messages`, `/chat/completions`, `/responses`). We widen the catalog to carry that metadata, add two new inbound routes (`/v1/chat/completions`, `/v1/responses`) that are also byte-for-byte passthroughs, and write exactly one real translator for the single quadrant Copilot cannot serve directly: **Anthropic Messages in → OpenAI Responses out**, for Responses-only GPT models. Translation is direct wire-to-wire JSON — it does not go through LmCore's `IMessage` types.

**Tech Stack:** .NET 9, `Microsoft.NET.Sdk.Web` minimal APIs, `System.Text.Json` (`JsonNode` / `JsonDocument`), xUnit 2.9.3, FluentAssertions 7.1.0, `Microsoft.AspNetCore.Mvc.Testing` 9.0.0.

## Global Constraints

- **This is a sample, not production infrastructure.** Copied verbatim from the spec's purpose statement (the user's words): *"we just want to use this proxy to make claude, codex, open-code etc to work on top of all the models exposed through copilot backend APIs"*. When a choice is between "more correct in the abstract" and "fewer moving parts", pick fewer moving parts and write the limitation down.
- **No changes to anything under `src/`.** The spec puts provider changes out of scope. The only `src/` types this plan *reads* are `CopilotModelsResponse` and `CopilotHttpClientFactory`, both already referenced by the sample.
- **No hard-coded model ids anywhere in `samples/CopilotAnthropicProxy.Sample/`.** The live catalog gains and loses models (`claude-opus-5`, `gpt-5.6-*` appeared after the test fixture was captured). Everything is derived from `GET /models` at startup. Test fixtures may name ids; production code may not.
- **Types in `samples/CopilotAnthropicProxy.Sample/` live in the global namespace.** `Program.cs` uses top-level statements with no `namespace` declaration, and the test project references `ProxyModelResolver` / `ProxyGuard` with no `using`. New files must follow suit — no `namespace` line.
- **The sample does not expose internals to the test project.** There is no `InternalsVisibleTo` for `CopilotAnthropicProxy.Sample` anywhere in the repo (verified). `ProxyConfig` and `ProxyHttp` are `internal` and therefore *not* unit-testable; `ProxyModelResolver` and `ProxyGuard` are `public` and are. **Every new type this plan adds must be `public`** or it cannot be tested. Do not add `InternalsVisibleTo` — making the new types public is the smaller change.
- **`EnforceCodeStyleInBuild=true`** in the sample csproj: IDE analyzer diagnostics are build errors. In particular `IDE0370` fires on a null-forgiving `!` the compiler can prove is redundant. Prefer `is` patterns over `!`.
- **`dotnet format whitespace --verify-no-changes` gates every commit** via `.husky/pre-commit` → `.husky/task-runner.json`. If a commit is rejected for formatting, run `dotnet format whitespace LmDotnetTools.sln` and re-stage. Do **not** use `--no-verify`.
- **Never fabricate SSE frames.** `ProxyHttp.CopyBodyAsync` already encodes this rule for mid-stream failures. The translator inherits it: if the upstream stream ends without a terminal event, emit nothing further and let the client observe the truncation. An empty *result* is fine (zero content blocks); an invented *frame* is not.
- **Commit message style:** conventional commits (`feat:`, `fix:`, `test:`, `docs:`). **Never** add `Co-Authored-By` or any AI/Claude signature — this is a standing user instruction.
- **Every task ends green.** The gate for all tasks is:
  ```bash
  dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
  ```
  Tasks 1–9 must never leave that suite red between commits.

---

## Deviations from the spec

Two places where this plan intentionally does something other than what the spec's prose says. Both were reasoned out against the existing test suite; the reasoning is recorded below, and it — not the spec's prose — is what these deviations rest on.

### D1 — `COPILOT_ANTHROPIC_MODEL` keeps its discovery short-circuit

The spec says pinning should be "demoted to a fallback". This plan keeps the current behavior: when the env var is set, `ResolveAsync` returns immediately without calling `GET /models`.

Why: `tests/CopilotAnthropicProxy.Tests/PassthroughTests.cs`'s `Idle_timeout_before_first_byte_returns_504_api_error` uses an upstream handler that answers *every* request with `await Task.Delay(Timeout.Infinite, ct)`. If the override ran discovery, startup would block on the 30-second `modelCts` in `Program.cs:139-160` and then fail the boot (`return 1`). It would also directly break `ModelResolverTests.ResolveAsync_returns_override_without_calling_upstream` (`:81`), whose handler throws if called.

Consequence: in pinned mode the catalog holds one entry with an empty vendor and an empty endpoint list. Empty endpoints means "no metadata", which `ModelRouter` treats as Anthropic-Messages-capable — i.e. exactly today's behavior. This preserves spec success criterion 4 ("nothing that works today breaks"). The env var is opt-in and unset by default, so the multi-model routing the user actually wants is the default path.

### D2 — an unrecognized model id still falls back to the catalog default

The spec's routing table could be read as "unknown model → 404". This plan keeps `ModelDiscoveryPassthroughTests.Unrecognized_model_falls_back_to_the_discovered_default` (`:114`) green: `SelectOutboundModel` resolves an unknown id to the catalog default, and the *dialect* check then runs against the **resolved** model. A 404 happens only when the resolved model genuinely cannot serve the requested dialect.

Consequences, given a default of `claude-opus-4.8` (`/v1/messages` + `/chat/completions`):

| Route | Unknown id resolves to | Outcome |
|---|---|---|
| `/v1/messages` | default opus | passthrough (existing test stays green) |
| `/v1/chat/completions` | default opus | passthrough — opus advertises `/chat/completions` |
| `/v1/responses` | default opus | **404**, naming the models that do serve `/responses` |

---

## Final decisions, recorded after implementation

The tasks below are kept as they were written and executed; these two points record where the shipped code deliberately ends up somewhere else. They are appended rather than edited into the task text so the history stays readable.

### F1 — the default port stays `8787`

Task 5 moves the default listen port from `8787` to `8788`, on the reasoning that the sample should be able to run beside an existing proxy on the old port. That was reversed before merge: `8787` is the port every existing client config, shell alias and note already points at, and changing it silently breaks all of them to buy a convenience nobody asked for. Anyone who genuinely needs two proxies at once sets `COPILOT_ANTHROPIC_PORT`, which has always existed.

Shipped state: `ProxyConfig.FromEnvironment` falls back to `8787`, the sample README documents `8787` throughout, and `HostGuardTests`'s local `Port` constant — which was always `8787` — needs no exception. Every `8788` in Task 5's steps and in the client-configuration snippets below should be read as `8787`.

### F2 — `COPILOT_ANTHROPIC_MODEL_ENDPOINTS` supplements D1

D1's consequence — a pinned catalog entry carries no endpoint metadata, and "no metadata" routes as Anthropic-Messages-capable — turns out to have a sharper edge than D1 admits: pinning a Responses-ONLY model makes the translated route unreachable, so `COPILOT_ANTHROPIC_MODEL=gpt-5.3-codex` cannot be driven from Claude Code at all.

Running discovery in pinned mode is still not an option, for exactly the reason D1 gives. Instead the operator can now supply the metadata discovery would have found:

```bash
export COPILOT_ANTHROPIC_MODEL=gpt-5.3-codex
export COPILOT_ANTHROPIC_MODEL_ENDPOINTS=/responses
```

Unset, nothing changes: the pinned entry stays endpoint-free and behaves exactly as D1 describes. Absent metadata is deliberately not read as "serves everything" — inventing capabilities would make the proxy claim things nobody verified.

---


A background research pass mined the installed Claude Code 2.1.220 bundle, upstream Codex/opencode source, and the official specs. Items that change this plan are folded into the tasks below; the rest are recorded here as considered-and-set-aside rather than missed.

**Folded in as requirements:**

1. **Claude Code's first request against any new model is a validation probe with `max_tokens: 1`** (`querySource:"model_validation"`, single `"Hi"` text block, `maxRetries:0`). The OpenAI Responses API rejects `max_output_tokens` below 16. Without a floor, *every* GPT model would be rejected by Claude Code before the user ever gets a turn. → **Task 6 clamps the outbound floor to 16.**
2. That probe (and any truncated generation) can legitimately produce **zero content blocks**. The Anthropic envelope must still be well-formed. → **Task 7 and Task 8 both pin an empty-content case**, and neither fabricates a placeholder text block.
3. **Claude Code always sends `system` as an array of `{type:"text", text}` blocks**, never a bare string. → **Task 6 treats the array form as the primary case** and still accepts the string form.
4. **Haiku-tier side traffic shares the base URL.** `ANTHROPIC_SMALL_FAST_MODEL` / `ANTHROPIC_DEFAULT_HAIKU_MODEL` resolve against the same `ANTHROPIC_BASE_URL`, so conversation-title generation, auto-mode classification and summarization arrive as `claude-3-5-haiku-*`. Under a plain default-fallback those all bill against the default **opus** model. → **Task 3 adds family-aware fallback.**
5. **Codex always sends `store:false` and resends full history each turn**, and Responses' default for `store` is `true`. The translated path is likewise stateless (Claude Code resends full history). → **Task 6 sets `store:false`.**

**Safe by construction — pinned with a test, no code needed:**

6. Claude Code puts betas in the request **body** as `betas:[...]` (≈29 of them) as well as in the `anthropic-beta` header, and `cache_control` carries `ttl` / `scope:"global"` sub-fields. The translator builds a fresh Responses body from an explicit allowlist of fields, so both are dropped by construction. (On the passthrough path they already ride through untouched, which is today's live-proven behavior.) → asserted in Task 6.
7. Claude Code sends server tools such as `{type:"web_search_20250305", name:"web_search", max_uses:8}`, which Copilot rejects with `400 "The use of the web search tool is not supported."`. These blocks have no `input_schema`, and Task 6 maps only tools that have one — so they are dropped before they can 400 the request. → asserted in Task 6.
8. `metadata.user_id` is always present. The translator never reads it, and nothing deserializes into a strict DTO, so it is ignored. No action.

**Considered and deliberately rejected — see Known Limitations (Task 10):**

9. **Re-minting tool ids to `toolu_[A-Za-z0-9_]+`.** Claude Code matches that regex when explaining tool-pairing failures, and Responses emits `call_…` / `fc_…`. The *functional* round-trip already works (we hand back whatever id we were given, and map it back verbatim), so the only gain is nicer text in a rare error path — while a lossy id sanitize would risk breaking the round-trip outright. Not worth it in a sample.
10. **`stop_sequences` emulation.** Anthropic supports it; Responses has no equivalent parameter. Emulating it means truncating the output stream at a token boundary that can straddle SSE chunks. Claude Code only uses it for auto-mode classification, which targets the haiku tier — an Anthropic model, i.e. the passthrough path, where `stop_sequences` rides through untouched. Documented, not implemented.
11. **Encrypted-reasoning continuity across tool loops.** Preserving GPT reasoning across turns needs `include:["reasoning.encrypted_content"]` plus smuggling the blob through Anthropic's `thinking.signature` field and back. Real work, real risk, and a sample does not need it. Reasoning summaries are surfaced as `thinking` blocks for display only.
12. **A local `count_tokens` estimate for non-Anthropic models.** Claude Code degrades gracefully (it logs `countTokens API call failed` and falls back to a local estimate), so the existing Anthropic-only behavior stands unchanged.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `samples/CopilotAnthropicProxy.Sample/Translation/ModelRoute.cs` | `ProxyDialect`, `ProxyRouteKind`, `ModelRoute`, `ModelRouter` — pure routing decision. No I/O. |
| `samples/CopilotAnthropicProxy.Sample/Translation/AnthropicToResponsesRequest.cs` | Anthropic Messages request JSON → Responses request JSON. Pure function. |
| `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicJson.cs` | Non-streaming Responses response JSON → Anthropic Message JSON. Pure function. |
| `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicSse.cs` | Streaming Responses SSE → Anthropic SSE. Stateful per request; no I/O. |
| `tests/CopilotAnthropicProxy.Tests/ModelRouteTests.cs` | Unit tests for `ModelRouter`. |
| `tests/CopilotAnthropicProxy.Tests/OpenAiDialectTests.cs` | Integration tests for the two new passthrough routes. |
| `tests/CopilotAnthropicProxy.Tests/AnthropicToResponsesRequestTests.cs` | Unit tests for the request translator. |
| `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicJsonTests.cs` | Unit tests for the non-streaming response translator. |
| `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicSseTests.cs` | Unit tests for the SSE state machine. |
| `tests/CopilotAnthropicProxy.Tests/TranslatedMessagesTests.cs` | Integration tests for `/v1/messages` → `/responses`. |

**Modified**

| File | Change |
|---|---|
| `samples/CopilotAnthropicProxy.Sample/Program.cs` | Catalog reshape, family-aware fallback, new routes, dialect-shaped errors, models union, default port, translated branch. |
| `samples/CopilotAnthropicProxy.Sample/README.md` | New endpoints, per-client configuration, port, known limitations. |
| `tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs` | Retarget the catalog pins onto `ParseServableModels`. |
| `tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs` | Widened `/v1/models` expectations. |
| `tests/CopilotLive.Tests/CopilotAnthropicProxyLiveTests.cs` | Live smoke for the new routes. |

Everything in `Translation/` is a pure function or a pure state machine: no `HttpContext`, no `HttpClient`, no logging. That is what makes them unit-testable given the no-`InternalsVisibleTo` constraint, and it keeps `Program.cs` as the only file that knows about ASP.NET.

---

## Task 1: Catalog carries vendor and endpoints

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs:401` (`ProxyModelCatalog`), `:412-444` (`ResolveAsync`), `:502-532` (`ParseMessagesCapableModelIds`), `:539-555` (`SelectOutboundModel`)
- Test: `tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs`

**Interfaces:**
- Consumes: `CopilotModelsResponse.MessagesEndpoint` (`"/v1/messages"`), `.ResponsesEndpoint` (`"/responses"`), `.EnumerateModelEntries(JsonElement)`, `.HasSupportedEndpoints(JsonElement)`, `.SupportsEndpoint(JsonElement, string)`, `.GetString(JsonElement, string)` — all `public static` on `AchieveAi.LmDotnetTools.GithubCopilotProvider.Models.CopilotModelsResponse`.
- Produces:
  - `public sealed record ProxyModelInfo(string Id, string Vendor, IReadOnlyList<string> Endpoints)` with `bool Supports(string endpoint)` and `bool IsAnthropic { get; }`
  - `public sealed record ProxyModelCatalog(string Default, IReadOnlyList<ProxyModelInfo> Models)` with `IReadOnlyList<string> Available { get; }` and `ProxyModelInfo? Find(string? id)`
  - `public const string ProxyModelResolver.ChatCompletionsEndpoint = "/chat/completions"`
  - `public static IReadOnlyList<ProxyModelInfo> ProxyModelResolver.ParseServableModels(string json)`
  - `ProxyModelResolver.SelectOutboundModel(string?, ProxyModelCatalog)` keeps its signature.

**Background — why the record is reshaped.** `ProxyModelCatalog` currently carries `IReadOnlyList<string> Available`: ids only. Every routing decision in this plan needs to know *which endpoints a model serves*, and the token-field rewrite needs to know *the vendor*. Ids alone cannot answer either. `Available` survives as a computed property so logging and tests keep working.

**Background — the two filtering rules.**
1. *Endpoint rule:* keep a model if it advertises at least one endpoint this proxy can forward to. The live catalog has ~19 entries advertising **no** endpoints at all, including `text-embedding-*`. Those must stay out — an embedding model must never appear as a chat model.
2. *Vendor rule:* drop `Google` (Gemini is excluded by user decision, 2026-07-27) and `Microsoft` (`mai-code-1-flash-picker` is a Copilot-internal router, not a chat model). This is a **denylist, not an allowlist**, deliberately: several test fixtures inline `/models` JSON with no `vendor` field at all, and an allowlist would silently drop every one of them. The endpoint list is the real capability signal; vendor only vetoes.

**Background — the fallback is per response, not per entry.** If *no* entry in the whole response carries `supported_endpoints`, the response is an older/alternative `/models` shape and we keep every id rather than concluding that nothing is servable. This behavior is pinned by `ModelResolverTests.cs:68` and `:122`; preserve it exactly. Entries produced by that fallback get an empty endpoint list, which downstream means "no metadata" and is treated as Anthropic-Messages-capable.

- [ ] **Step 1: Write the failing tests**

Replace the two `ParseMessagesCapableModelIds` tests in `tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs` (around `:52` and `:68`) with these, and add the third:

```csharp
[Fact]
public void ParseServableModels_keeps_models_with_a_reachable_endpoint_and_a_servable_vendor()
{
    const string json = """
    {"data":[
      {"id":"claude-opus-4.8","vendor":"Anthropic","supported_endpoints":["/v1/messages","/chat/completions"]},
      {"id":"gpt-5.3-codex","vendor":"OpenAI","supported_endpoints":["/responses","ws:/responses"]},
      {"id":"gemini-3.5-flash","vendor":"Google","supported_endpoints":["/chat/completions"]},
      {"id":"mai-code-1-flash-picker","vendor":"Microsoft","supported_endpoints":["/responses"]},
      {"id":"text-embedding-3-small","vendor":"Azure OpenAI","supported_endpoints":[]}
    ]}
    """;

    var models = ProxyModelResolver.ParseServableModels(json);

    models.Select(m => m.Id).Should().Equal("claude-opus-4.8", "gpt-5.3-codex");
    models[0].Vendor.Should().Be("Anthropic");
    models[0].Supports(CopilotModelsResponse.MessagesEndpoint).Should().BeTrue();
    models[0].Supports(CopilotModelsResponse.ResponsesEndpoint).Should().BeFalse();
    models[0].IsAnthropic.Should().BeTrue();
    models[1].Supports(CopilotModelsResponse.ResponsesEndpoint).Should().BeTrue();
    models[1].IsAnthropic.Should().BeFalse();
}

[Fact]
public void ParseServableModels_falls_back_to_every_id_when_no_entry_has_endpoint_metadata()
{
    const string json = """
    {"data":[{"id":"claude-opus-4.8"},{"id":"claude-sonnet-4.5"}]}
    """;

    var models = ProxyModelResolver.ParseServableModels(json);

    models.Select(m => m.Id).Should().Equal("claude-opus-4.8", "claude-sonnet-4.5");
    models.Should().OnlyContain(m => m.Endpoints.Count == 0, "no-metadata entries carry no endpoints");
}

[Fact]
public void Catalog_find_is_case_insensitive_and_returns_null_for_unknown_ids()
{
    var catalog = new ProxyModelCatalog(
        "claude-opus-4.8",
        [new ProxyModelInfo("claude-opus-4.8", "Anthropic", [CopilotModelsResponse.MessagesEndpoint])]
    );

    catalog.Find("CLAUDE-OPUS-4.8")!.Id.Should().Be("claude-opus-4.8");
    catalog.Find("nope").Should().BeNull();
    catalog.Find(null).Should().BeNull();
    catalog.Available.Should().Equal("claude-opus-4.8");
}
```

Add `using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;` to the file's usings if it is not already there.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ModelResolverTests"
```
Expected: compile errors — `ParseServableModels` and `ProxyModelInfo` do not exist.

- [ ] **Step 3: Add the two records**

Replace `Program.cs:401` (`public sealed record ProxyModelCatalog(string Default, IReadOnlyList<string> Available);`) with:

```csharp
/// <summary>
///     One servable Copilot model: its id, its vendor, and the transports it advertises.
///     An EMPTY <paramref name="Endpoints"/> list means "no metadata" — either the model came from a
///     <c>/models</c> response that carried no <c>supported_endpoints</c> at all, or the catalog was
///     pinned via <c>COPILOT_ANTHROPIC_MODEL</c>. Callers treat that as Anthropic-Messages-capable,
///     which is exactly how the proxy behaved before endpoint metadata existed.
/// </summary>
public sealed record ProxyModelInfo(string Id, string Vendor, IReadOnlyList<string> Endpoints)
{
    /// <summary>True when this model advertises <paramref name="endpoint"/> (case-insensitive).</summary>
    public bool Supports(string endpoint) => Endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     True for Anthropic models. Falls back to the id when the vendor is unknown, so a pinned
    ///     Claude model is still recognised as Anthropic and keeps its <c>max_tokens</c> spelling.
    /// </summary>
    public bool IsAnthropic =>
        Vendor.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        || (Vendor.Length == 0 && Id.Contains("claude", StringComparison.OrdinalIgnoreCase));
}

/// <summary>The models this proxy will serve, plus the id used when a request names an unknown model.</summary>
public sealed record ProxyModelCatalog(string Default, IReadOnlyList<ProxyModelInfo> Models)
{
    /// <summary>
    ///     Every available model id, in upstream order. Computed rather than cached so a
    ///     <c>with</c>-expression cannot leave it stale; only startup logging and tests read it.
    /// </summary>
    public IReadOnlyList<string> Available => Models.Select(m => m.Id).ToArray();

    /// <summary>Case-insensitive lookup. Null when the id is absent, blank, or null.</summary>
    public ProxyModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Replace `ParseMessagesCapableModelIds` with `ParseServableModels`**

Delete `Program.cs:502-532` and put this in its place, inside `ProxyModelResolver`:

```csharp
/// <summary><c>POST /chat/completions</c> — the OpenAI Chat Completions transport.</summary>
public const string ChatCompletionsEndpoint = "/chat/completions";

/// <summary>The transports this proxy knows how to forward to.</summary>
private static readonly string[] ReachableEndpoints =
[
    CopilotModelsResponse.MessagesEndpoint,
    ChatCompletionsEndpoint,
    CopilotModelsResponse.ResponsesEndpoint,
];

/// <summary>
///     Vendors this proxy refuses to serve. A DENYLIST, not an allowlist: several <c>/models</c>
///     shapes omit <c>vendor</c> entirely, and an allowlist would silently drop all of them. The
///     advertised endpoint list is the real capability signal; vendor only vetoes.
///     Google is excluded by user decision (2026-07-27); Microsoft's <c>mai-code-*</c> is a
///     Copilot-internal router rather than a chat model.
/// </summary>
private static readonly string[] ExcludedVendors = ["Google", "Microsoft"];

/// <summary>
///     Parses a Copilot <c>/models</c> response into the models this proxy will serve, preserving
///     upstream order.
///
///     A model is kept when it advertises at least one endpoint in <see cref="ReachableEndpoints"/>
///     and its vendor is not in <see cref="ExcludedVendors"/>. Entries advertising NO endpoints are
///     dropped — that set includes <c>text-embedding-*</c>, which must never surface as a chat model.
///
///     The no-metadata fallback is deliberately per RESPONSE, not per entry: only when NOT ONE entry
///     carries <c>supported_endpoints</c> do we conclude the response uses an older shape and keep
///     every id. Otherwise a partially-annotated response would resurrect the embedding models.
/// </summary>
public static IReadOnlyList<ProxyModelInfo> ParseServableModels(string json)
{
    using var doc = JsonDocument.Parse(json);
    var entries = CopilotModelsResponse.EnumerateModelEntries(doc.RootElement).ToList();

    if (!entries.Any(CopilotModelsResponse.HasSupportedEndpoints))
    {
        return ParseModelIds(json).Select(id => new ProxyModelInfo(id, string.Empty, [])).ToArray();
    }

    var models = new List<ProxyModelInfo>();
    foreach (var item in entries)
    {
        var id = CopilotModelsResponse.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            continue;
        }

        var vendor = CopilotModelsResponse.GetString(item, "vendor") ?? string.Empty;
        if (ExcludedVendors.Contains(vendor, StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        var endpoints = ReachableEndpoints.Where(e => CopilotModelsResponse.SupportsEndpoint(item, e)).ToArray();
        if (endpoints.Length == 0)
        {
            continue;
        }

        models.Add(new ProxyModelInfo(id, vendor, endpoints));
    }

    return models;
}
```

- [ ] **Step 5: Update `ResolveAsync` and `SelectOutboundModel`**

In `ResolveAsync` (`Program.cs:412-444`), replace the override short-circuit and the discovery tail:

```csharp
if (!string.IsNullOrWhiteSpace(modelOverride))
{
    var pinned = modelOverride.Trim();

    // DEVIATION D1: pinning still short-circuits discovery. Vendor and endpoints are unknown, and an
    // empty endpoint list means "no metadata", which routes as Anthropic Messages — today's behavior.
    return new ProxyModelCatalog(pinned, [new ProxyModelInfo(pinned, string.Empty, [])]);
}

using var response = await client.GetAsync("/models", cancellationToken).ConfigureAwait(false);
_ = response.EnsureSuccessStatusCode();
var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

var models = ParseServableModels(json);

// The default is the fallback for the Anthropic surface, so it must be able to serve /v1/messages.
// A chat-completions-only model named "opus" is servable, but it cannot be the default.
var claudeIds = models
    .Where(m => m.Endpoints.Count == 0 || m.Supports(CopilotModelsResponse.MessagesEndpoint))
    .Select(m => m.Id)
    .Where(id => id.Contains("claude", StringComparison.OrdinalIgnoreCase))
    .ToList();

var opus = PickHighestVersionOpusId(claudeIds);
if (opus is null)
{
    throw new InvalidOperationException(
        "No Claude Opus model is available on this Copilot account. Messages-capable Claude models: "
            + (claudeIds.Count == 0 ? "(none)" : string.Join(", ", claudeIds))
    );
}

return new ProxyModelCatalog(opus, models);
```

Keep whatever exception type and message the existing code throws if it differs — `ModelResolverTests.cs:142` and `:172` assert on the message containing the discovered claude ids.

Then replace `SelectOutboundModel` (`Program.cs:539-555`):

```csharp
/// <summary>
///     Maps the model a client asked for onto a model this proxy serves. Unknown ids fall back to the
///     catalog default (DEVIATION D2) — the dialect check downstream runs against the RESOLVED model.
/// </summary>
public static string SelectOutboundModel(string? incomingModel, ProxyModelCatalog catalog)
{
    ArgumentNullException.ThrowIfNull(catalog);
    return catalog.Find(incomingModel)?.Id ?? catalog.Default;
}
```

- [ ] **Step 6: Fix the remaining compile breaks and stale expectations**

`Program.cs:139-160` logs `catalog.Available` — unchanged, still compiles. `BuildModelsStub(catalog.Available)` at `:220` will not: change the call site to `ProxyHttp.BuildModelsStub(catalog.Models)` and the parameter to `IReadOnlyList<ProxyModelInfo> models`, projecting `m.Id` where the body currently uses `model`. Task 5 reshapes the body properly; this step only keeps it compiling.

In `ModelResolverTests.cs`, update:

| Location | Change |
|---|---|
| `:100` `ResolveAsync_picks_the_opus_claude_id_from_models` | `gpt-4o` in that fixture advertises `/chat/completions` with no `vendor`, so it is now servable. Expect `Available` to equal `"gpt-4o", "claude-sonnet-4.5", "claude-opus-4.8"` in upstream order. `Default` is unchanged. |
| `:160` `ResolveAsync_excludes_messages_incapable_models_from_the_opus_search_even_if_named_opus` | Still passes unchanged — `claude-opus-chat-only` is servable but not messages-capable, so it stays out of the opus search. |
| `:215` real-fixture test | Rename to `ParseServableModels_matches_the_real_captured_copilot_response` and expect exactly these 13 ids in fixture order: `claude-opus-4.6`, `claude-opus-4.7`, `claude-opus-4.8`, `claude-sonnet-4.6`, `claude-sonnet-5`, `gpt-5.3-codex`, `gpt-5.4-mini`, `gpt-5.4-nano`, `gpt-5.4`, `gpt-5.5`, `gpt-5-mini`, `claude-sonnet-4.5`, `claude-haiku-4.5`. |
| `:268`, `:276` | `new ProxyModelCatalog("claude-opus-4.8", ["claude-sonnet-4.5", "claude-opus-4.8"])` → `new ProxyModelCatalog("claude-opus-4.8", [new ProxyModelInfo("claude-sonnet-4.5", "Anthropic", [CopilotModelsResponse.MessagesEndpoint]), new ProxyModelInfo("claude-opus-4.8", "Anthropic", [CopilotModelsResponse.MessagesEndpoint])])` |

In `ModelDiscoveryPassthroughTests.cs`:

| Location | Change |
|---|---|
| `:49` | Expect the same 13-id list as above. |
| `:88` | The inline discovery JSON's `gpt-5.4` advertises `/responses`, so `/v1/models` now lists all three. Expect `"claude-opus-4.8"`, `"claude-sonnet-4.5"`, `"gpt-5.4"`, and update the assertion's reason string — the old one said gpt-5.4 was excluded for lacking `/v1/messages`, which is no longer why we filter. |

- [ ] **Step 7: Run the full suite**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
```
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Program.cs tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs
git commit -m "feat(proxy): catalog carries vendor and advertised endpoints"
```

---

## Task 2: The routing decision

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Translation/ModelRoute.cs`
- Test: `tests/CopilotAnthropicProxy.Tests/ModelRouteTests.cs`

**Interfaces:**
- Consumes: `ProxyModelInfo`, `ProxyModelCatalog` (Task 1); `ProxyModelResolver.ChatCompletionsEndpoint` (Task 1); `CopilotModelsResponse.MessagesEndpoint` / `.ResponsesEndpoint`.
- Produces:
  - `public enum ProxyDialect { AnthropicMessages, ChatCompletions, Responses }`
  - `public enum ProxyRouteKind { Passthrough, TranslateAnthropicToResponses }`
  - `public sealed record ModelRoute(ProxyRouteKind Kind, string UpstreamPath, ProxyModelInfo Model)`
  - `public static class ModelRouter` with constants `MessagesPath`, `CountTokensPath`, `ChatCompletionsPath`, `ResponsesPath`; `static ModelRoute? Resolve(ProxyDialect, ProxyModelInfo)`; `static IReadOnlyList<string> Servable(ProxyDialect, ProxyModelCatalog)`.

This is the whole routing table in one pure function. `Resolve` returning `null` is the 404 signal; `Servable` exists so the 404 body can name what the client *could* have asked for.

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/ModelRouteTests.cs`:

```csharp
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ModelRouteTests
{
    private static ProxyModelInfo Dual =>
        new("claude-opus-4.8", "Anthropic", [CopilotModelsResponse.MessagesEndpoint, ProxyModelResolver.ChatCompletionsEndpoint]);

    private static ProxyModelInfo ResponsesOnly =>
        new("gpt-5.3-codex", "OpenAI", [CopilotModelsResponse.ResponsesEndpoint]);

    private static ProxyModelInfo ResponsesAndChat =>
        new("gpt-5.4", "OpenAI", [CopilotModelsResponse.ResponsesEndpoint, ProxyModelResolver.ChatCompletionsEndpoint]);

    private static ProxyModelInfo NoMetadata => new("pinned-model", "", []);

    [Fact]
    public void Anthropic_dialect_passes_through_for_a_messages_capable_model()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, Dual);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.Passthrough);
        route.UpstreamPath.Should().Be("/v1/messages");
    }

    [Fact]
    public void Anthropic_dialect_translates_for_a_responses_only_model()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, ResponsesOnly);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.TranslateAnthropicToResponses);
        route.UpstreamPath.Should().Be("/responses");
    }

    [Fact]
    public void Anthropic_dialect_prefers_passthrough_when_a_model_serves_both()
    {
        // gpt-5.4 advertises /responses AND /chat/completions but NOT /v1/messages, so it translates.
        ModelRouter.Resolve(ProxyDialect.AnthropicMessages, ResponsesAndChat)!
            .Kind.Should().Be(ProxyRouteKind.TranslateAnthropicToResponses);
    }

    [Fact]
    public void A_model_with_no_endpoint_metadata_is_treated_as_anthropic_capable()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, NoMetadata);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.Passthrough);
    }

    [Fact]
    public void Chat_completions_dialect_passes_through_only_for_models_that_advertise_it()
    {
        ModelRouter.Resolve(ProxyDialect.ChatCompletions, Dual)!.UpstreamPath.Should().Be("/chat/completions");
        ModelRouter.Resolve(ProxyDialect.ChatCompletions, ResponsesOnly).Should().BeNull();
    }

    [Fact]
    public void Responses_dialect_passes_through_only_for_models_that_advertise_it()
    {
        ModelRouter.Resolve(ProxyDialect.Responses, ResponsesOnly)!.UpstreamPath.Should().Be("/responses");
        ModelRouter.Resolve(ProxyDialect.Responses, Dual).Should().BeNull();
    }

    [Fact]
    public void A_pinned_model_cannot_serve_the_responses_dialect()
    {
        // Pinned mode has no endpoint metadata, so we cannot claim Responses support.
        ModelRouter.Resolve(ProxyDialect.Responses, NoMetadata).Should().BeNull();
    }

    [Fact]
    public void Servable_lists_the_ids_that_can_serve_a_dialect()
    {
        var catalog = new ProxyModelCatalog("claude-opus-4.8", [Dual, ResponsesOnly, ResponsesAndChat]);

        ModelRouter.Servable(ProxyDialect.Responses, catalog).Should().Equal("gpt-5.3-codex", "gpt-5.4");
        ModelRouter.Servable(ProxyDialect.ChatCompletions, catalog).Should().Equal("claude-opus-4.8", "gpt-5.4");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ModelRouteTests"
```
Expected: compile errors — `ModelRouter`, `ModelRoute`, `ProxyDialect` do not exist.

- [ ] **Step 3: Create the router**

Create `samples/CopilotAnthropicProxy.Sample/Translation/ModelRoute.cs` (no `namespace` line — global namespace, per Global Constraints):

```csharp
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>The API dialect a request arrived in.</summary>
public enum ProxyDialect
{
    /// <summary>Anthropic Messages — Claude Code.</summary>
    AnthropicMessages,

    /// <summary>OpenAI Chat Completions — opencode and most OpenAI SDKs.</summary>
    ChatCompletions,

    /// <summary>OpenAI Responses — Codex CLI.</summary>
    Responses,
}

/// <summary>How a request must be served.</summary>
public enum ProxyRouteKind
{
    /// <summary>Forward the body to Copilot essentially unchanged.</summary>
    Passthrough,

    /// <summary>Rewrite an Anthropic Messages request into an OpenAI Responses request, and the reply back.</summary>
    TranslateAnthropicToResponses,
}

/// <summary>The resolved upstream target for one request.</summary>
public sealed record ModelRoute(ProxyRouteKind Kind, string UpstreamPath, ProxyModelInfo Model);

/// <summary>
///     The routing table. Copilot serves three transports and every model advertises which of them it
///     honors, so the only question is whether the client's dialect matches one the model accepts.
///     Exactly one mismatch is worth translating: Anthropic Messages in, for a model that only speaks
///     Responses. Everything else either passes through or 404s.
/// </summary>
public static class ModelRouter
{
    /// <summary>Copilot's Anthropic Messages path.</summary>
    public const string MessagesPath = CopilotModelsResponse.MessagesEndpoint;

    /// <summary>Copilot's Anthropic token-counting path.</summary>
    public const string CountTokensPath = "/v1/messages/count_tokens";

    /// <summary>Copilot's OpenAI Chat Completions path.</summary>
    public const string ChatCompletionsPath = ProxyModelResolver.ChatCompletionsEndpoint;

    /// <summary>Copilot's OpenAI Responses path.</summary>
    public const string ResponsesPath = CopilotModelsResponse.ResponsesEndpoint;

    /// <summary>
    ///     Resolves how to serve <paramref name="model"/> for an inbound <paramref name="dialect"/>.
    ///     Returns null when the model cannot serve that dialect at all; the caller answers 404.
    ///
    ///     A model with NO endpoint metadata (pinned via COPILOT_ANTHROPIC_MODEL, or discovered from a
    ///     legacy /models shape) is treated as Anthropic-Messages-capable and nothing else, which is
    ///     precisely how this proxy behaved before endpoint metadata existed.
    /// </summary>
    public static ModelRoute? Resolve(ProxyDialect dialect, ProxyModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var noMetadata = model.Endpoints.Count == 0;

        return dialect switch
        {
            ProxyDialect.AnthropicMessages when noMetadata || model.Supports(MessagesPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                MessagesPath,
                model
            ),
            ProxyDialect.AnthropicMessages when model.Supports(ResponsesPath) => new ModelRoute(
                ProxyRouteKind.TranslateAnthropicToResponses,
                ResponsesPath,
                model
            ),
            ProxyDialect.ChatCompletions when noMetadata || model.Supports(ChatCompletionsPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                ChatCompletionsPath,
                model
            ),
            ProxyDialect.Responses when model.Supports(ResponsesPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                ResponsesPath,
                model
            ),
            _ => null,
        };
    }

    /// <summary>
    ///     The ids that can serve <paramref name="dialect"/>, in catalog order. Used to make a 404 body
    ///     actionable — telling a client its model is unavailable is only useful alongside the list of
    ///     models that are.
    /// </summary>
    public static IReadOnlyList<string> Servable(ProxyDialect dialect, ProxyModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Models.Where(m => Resolve(dialect, m) is not null).Select(m => m.Id).ToArray();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ModelRouteTests"
```
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Translation/ModelRoute.cs tests/CopilotAnthropicProxy.Tests/ModelRouteTests.cs
git commit -m "feat(proxy): add the dialect-to-endpoint routing table"
```

---

## Task 3: Family-aware fallback for unknown model ids

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs` (`ProxyModelResolver.SelectOutboundModel`)
- Test: `tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs`

**Interfaces:**
- Consumes: `ProxyModelCatalog.Find`, `ProxyModelCatalog.Models` (Task 1).
- Produces: no new signatures — `SelectOutboundModel(string?, ProxyModelCatalog)` changes behavior only.

**Background.** Claude Code does not send only your chosen model to `ANTHROPIC_BASE_URL`. Conversation-title generation, auto-mode classification, and summarization all resolve through `ANTHROPIC_SMALL_FAST_MODEL` → `ANTHROPIC_DEFAULT_HAIKU_MODEL` → a built-in default, and every one of them hits *this* proxy with an id like `claude-3-5-haiku-20241022`. That id is not in Copilot's catalog (Copilot has `claude-haiku-4.5`). With a plain default-fallback, every title generation would be billed against the default **opus** model. There is no separate base URL to point them at, so the proxy has to do the mapping.

The fix generalises: before falling back to the default, look for a catalog model in the same *family*. Three families, checked longest-first so `sonnet` cannot shadow anything.

- [ ] **Step 1: Write the failing test**

Add to `tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs`:

```csharp
private static ProxyModelCatalog FamilyCatalog =>
    new(
        "claude-opus-4.8",
        [
            new ProxyModelInfo("claude-opus-4.8", "Anthropic", [CopilotModelsResponse.MessagesEndpoint]),
            new ProxyModelInfo("claude-sonnet-4.5", "Anthropic", [CopilotModelsResponse.MessagesEndpoint]),
            new ProxyModelInfo("claude-haiku-4.5", "Anthropic", [CopilotModelsResponse.MessagesEndpoint]),
        ]
    );

[Theory]
[InlineData("claude-3-5-haiku-20241022", "claude-haiku-4.5")]
[InlineData("claude-haiku-4-5-20251001", "claude-haiku-4.5")]
[InlineData("claude-sonnet-4-20250514", "claude-sonnet-4.5")]
[InlineData("claude-3-opus-20240229", "claude-opus-4.8")]
public void SelectOutboundModel_maps_an_unknown_id_onto_its_own_family(string incoming, string expected)
{
    ProxyModelResolver.SelectOutboundModel(incoming, FamilyCatalog).Should().Be(expected);
}

[Fact]
public void SelectOutboundModel_falls_back_to_the_default_when_no_family_matches()
{
    ProxyModelResolver.SelectOutboundModel("some-unknown-model", FamilyCatalog).Should().Be("claude-opus-4.8");
}

[Fact]
public void SelectOutboundModel_still_prefers_an_exact_match_over_a_family_match()
{
    ProxyModelResolver.SelectOutboundModel("claude-haiku-4.5", FamilyCatalog).Should().Be("claude-haiku-4.5");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~SelectOutboundModel"
```
Expected: FAIL — the haiku and sonnet cases return `claude-opus-4.8`.

- [ ] **Step 3: Implement family-aware fallback**

Replace `SelectOutboundModel` in `ProxyModelResolver`:

```csharp
/// <summary>
///     Model families, longest name first so a shorter name cannot shadow a longer one.
///     Used to route side traffic that names a model this account does not have — Claude Code sends
///     conversation-title, classification and summarisation calls as <c>claude-3-5-haiku-*</c> to the
///     SAME base URL, and without this they would all bill against the default opus model.
/// </summary>
private static readonly string[] ModelFamilies = ["sonnet", "haiku", "opus"];

/// <summary>
///     Maps the model a client asked for onto a model this proxy serves:
///     exact match (case-insensitive) → same-family match → catalog default.
///     The dialect check downstream runs against the RESOLVED model (DEVIATION D2).
/// </summary>
public static string SelectOutboundModel(string? incomingModel, ProxyModelCatalog catalog)
{
    ArgumentNullException.ThrowIfNull(catalog);

    if (catalog.Find(incomingModel) is { } exact)
    {
        return exact.Id;
    }

    if (!string.IsNullOrWhiteSpace(incomingModel))
    {
        var family = ModelFamilies.FirstOrDefault(f => incomingModel.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (family is not null)
        {
            var sameFamily = catalog.Models.FirstOrDefault(m =>
                m.Id.Contains(family, StringComparison.OrdinalIgnoreCase)
            );
            if (sameFamily is not null)
            {
                return sameFamily.Id;
            }
        }
    }

    return catalog.Default;
}
```

- [ ] **Step 4: Run the full suite**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
```
Expected: PASS. Note `ModelDiscoveryPassthroughTests.Unrecognized_model_falls_back_to_the_discovered_default` (`:114`) — check the id it sends. If it contains `sonnet`/`haiku`/`opus` and the discovery fixture has a matching model, the family rule now applies; change the test's model to a genuinely family-less id such as `"some-unknown-model"` and keep the assertion.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Program.cs tests/CopilotAnthropicProxy.Tests/ModelResolverTests.cs tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs
git commit -m "feat(proxy): map unknown model ids onto their own family before defaulting"
```

---

## Task 4: OpenAI-dialect passthrough routes

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs:201-234` (routes), `:595-636` (`TryRewriteModel`), `:644-829` (`ForwardAsync`), `:1054-1065` (error writers)
- Test: `tests/CopilotAnthropicProxy.Tests/OpenAiDialectTests.cs`

**Interfaces:**
- Consumes: `ModelRouter.Resolve` / `.Servable`, `ProxyDialect`, `ProxyRouteKind` (Task 2); `ProxyModelInfo.IsAnthropic` (Task 1).
- Produces:
  - `ProxyModelResolver.TryRewriteModel(byte[] body, string model, out byte[] rewritten, out string? incomingModel, bool renameMaxTokens = false)` — the new optional parameter defaults to false, so the six existing call sites in `ModelRewriteTests.cs` still compile.
  - `ProxyHttp.ForwardAsync(HttpContext, ProxyModelCatalog, TimeSpan idleTimeout, TimeSpan keepAliveInterval, ProxyDialect dialect, bool isCountTokens = false)`
  - `ProxyHttp.WriteOpenAiErrorAsync(HttpContext, int status, string type, string message)`
  - `ProxyHttp.WriteErrorAsync(HttpContext, ProxyDialect, int status, string type, string message)`

**Background — why the token-field rename exists.** Live-probed on 2026-07-27 (`tests/CopilotLive.Tests/CopilotChatCompletionsProbeTests.cs`, 4/4 passing): Claude models accept `max_tokens` on `/chat/completions`, but GPT models return `400 "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."` on the *same* endpoint. It is per-model, not per-endpoint. opencode's `@ai-sdk/openai-compatible` path still emits the deprecated `max_tokens`, so a naive passthrough to a GPT model 400s. This is a one-field rewrite riding inside the body rewrite that already happens.

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/OpenAiDialectTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class OpenAiDialectTests
{
    private const string DiscoveryJson = """
    {"data":[
      {"id":"claude-opus-4.8","vendor":"Anthropic","supported_endpoints":["/v1/messages","/chat/completions"]},
      {"id":"gpt-5.4","vendor":"OpenAI","supported_endpoints":["/responses","/chat/completions"]},
      {"id":"gpt-5.3-codex","vendor":"OpenAI","supported_endpoints":["/responses"]}
    ]}
    """;

    /// <summary>
    ///     Builds a factory whose upstream answers startup discovery from <see cref="DiscoveryJson"/>
    ///     and hands every other request to <paramref name="onProxied"/>, recording the path it hit.
    /// </summary>
    private static ProxyWebAppFactory Factory(
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>> onProxied
    ) =>
        new(
            async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/models", StringComparison.Ordinal))
                {
                    return TestUpstream.Json(DiscoveryJson);
                }

                return await onProxied(request, path);
            },
            model: null
        );

    [Fact]
    public async Task Chat_completions_forwards_to_the_upstream_chat_completions_path()
    {
        string? seenPath = null;
        await using var factory = Factory(
            (_, path) =>
            {
                seenPath = path;
                return Task.FromResult(TestUpstream.Json("""{"id":"x","choices":[]}"""));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "claude-opus-4.8", messages = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPath.Should().Be("/chat/completions");
    }

    [Fact]
    public async Task Chat_completions_renames_max_tokens_for_a_non_anthropic_model()
    {
        string? body = null;
        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"x","choices":[]}""");
            }
        );
        using var client = factory.CreateClient();

        _ = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "gpt-5.4",
                max_tokens = 256,
                messages = Array.Empty<object>(),
            }
        );

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.TryGetProperty("max_tokens", out _).Should().BeFalse();
        sent.RootElement.GetProperty("max_completion_tokens").GetInt32().Should().Be(256);
    }

    [Fact]
    public async Task Chat_completions_keeps_max_tokens_for_an_anthropic_model()
    {
        string? body = null;
        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"x","choices":[]}""");
            }
        );
        using var client = factory.CreateClient();

        _ = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "claude-opus-4.8",
                max_tokens = 256,
                messages = Array.Empty<object>(),
            }
        );

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(256);
        sent.RootElement.TryGetProperty("max_completion_tokens", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Responses_forwards_to_the_upstream_responses_path()
    {
        string? seenPath = null;
        await using var factory = Factory(
            (_, path) =>
            {
                seenPath = path;
                return Task.FromResult(TestUpstream.Json("""{"id":"resp_1","output":[]}"""));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "gpt-5.3-codex", input = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPath.Should().Be("/responses");
    }

    [Fact]
    public async Task Responses_returns_an_openai_shaped_404_for_a_model_that_cannot_serve_it()
    {
        await using var factory = Factory((_, _) => throw new InvalidOperationException("must not be forwarded"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "claude-opus-4.8", input = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = error.RootElement.GetProperty("error").GetProperty("message").GetString();
        message.Should().Contain("claude-opus-4.8");
        message.Should().Contain("gpt-5.4").And.Contain("gpt-5.3-codex", "the 404 must name what IS servable");
    }

    [Fact]
    public async Task The_unprefixed_twins_are_bound_too()
    {
        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.Json("""{"id":"x","choices":[]}"""))
        );
        using var client = factory.CreateClient();

        var chat = await client.PostAsJsonAsync(
            "/chat/completions",
            new { model = "claude-opus-4.8", messages = Array.Empty<object>() }
        );
        var responses = await client.PostAsJsonAsync(
            "/responses",
            new { model = "gpt-5.3-codex", input = Array.Empty<object>() }
        );

        chat.StatusCode.Should().Be(HttpStatusCode.OK);
        responses.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~OpenAiDialectTests"
```
Expected: FAIL — the new paths hit `MapFallback` and return the Anthropic 404.

- [ ] **Step 3: Add the OpenAI error writer**

In `ProxyHttp`, next to `WriteAnthropicErrorAsync` (`Program.cs:1054-1065`):

```csharp
/// <summary>
///     Writes an OpenAI-shaped error envelope. No-op once the response has started — a half-written
///     body must not be capped with an error object.
/// </summary>
public static async Task WriteOpenAiErrorAsync(HttpContext ctx, int status, string type, string message)
{
    if (ctx.Response.HasStarted)
    {
        return;
    }

    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";

    var payload = JsonSerializer.SerializeToUtf8Bytes(
        new
        {
            error = new
            {
                message,
                type,
                param = (string?)null,
                code = (string?)null,
            },
        }
    );

    await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted).ConfigureAwait(false);
}

/// <summary>Writes an error in the envelope shape the INBOUND dialect expects.</summary>
public static Task WriteErrorAsync(HttpContext ctx, ProxyDialect dialect, int status, string type, string message) =>
    dialect == ProxyDialect.AnthropicMessages
        ? WriteAnthropicErrorAsync(ctx, status, type, message)
        : WriteOpenAiErrorAsync(ctx, status, type, message);
```

- [ ] **Step 4: Teach `TryRewriteModel` the token-field rename**

Add the optional parameter and the rename to `ProxyModelResolver.TryRewriteModel` (`Program.cs:595-636`). Signature:

```csharp
public static bool TryRewriteModel(
    byte[] body,
    string model,
    out byte[] rewritten,
    out string? incomingModel,
    bool renameMaxTokens = false
)
```

and, inside the existing `JsonNode` block, just before the body is re-serialised (after `obj.Remove("context_management")`):

```csharp
// GPT models on Copilot reject `max_tokens` on /chat/completions and demand
// `max_completion_tokens` — live-confirmed 2026-07-27. Claude models accept `max_tokens` on the
// same endpoint, so this is keyed on the model, not the route. Clone before removing: a JsonNode
// cannot be re-parented while it still belongs to the object.
if (renameMaxTokens && obj["max_tokens"] is { } maxTokens)
{
    var value = maxTokens.DeepClone();
    _ = obj.Remove("max_tokens");
    if (!obj.ContainsKey("max_completion_tokens"))
    {
        obj["max_completion_tokens"] = value;
    }
}
```

- [ ] **Step 5: Make `ForwardAsync` dialect-aware**

In `ProxyHttp.ForwardAsync` (`Program.cs:644-829`):

1. Add `ProxyDialect dialect` as a parameter before the existing `bool isCountTokens` (give `isCountTokens` a default of `false`).
2. After the outbound model is selected, resolve the route and reject a dialect mismatch:

```csharp
var outboundModel = ProxyModelResolver.SelectOutboundModel(ProxyModelResolver.PeekModel(inboundBody), catalog);

// A model resolved via the default/family fallback may not be in the catalog at all (pinned mode);
// treat it as metadata-free, which routes as Anthropic Messages.
var modelInfo = catalog.Find(outboundModel) ?? new ProxyModelInfo(outboundModel, string.Empty, []);
var route = ModelRouter.Resolve(dialect, modelInfo);

if (route is null)
{
    var alternatives = ModelRouter.Servable(dialect, catalog);
    await WriteErrorAsync(
            ctx,
            dialect,
            StatusCodes.Status404NotFound,
            "not_found_error",
            $"Model '{outboundModel}' is not available on this endpoint. "
                + $"Models that are: {(alternatives.Count == 0 ? "(none)" : string.Join(", ", alternatives))}."
        )
        .ConfigureAwait(false);
    return;
}
```

3. Replace the hard-coded upstream path at `:705`:

```csharp
var upstreamPath = isCountTokens ? ModelRouter.CountTokensPath : route.UpstreamPath;
```

4. Pass the rename flag into the body rewrite:

```csharp
var renameMaxTokens = dialect == ProxyDialect.ChatCompletions && !modelInfo.IsAnthropic;
if (!ProxyModelResolver.TryRewriteModel(inboundBody, outboundModel, out var outboundBody, out _, renameMaxTokens))
```

5. Replace every pre-send `WriteAnthropicErrorAsync(ctx, …)` call in `ForwardAsync` — the 400 on rewrite failure, and the 504/401/502 catch blocks — with `WriteErrorAsync(ctx, dialect, …)`, keeping the existing status codes and messages. Also update the `CopyBodyAsync` callback at `:813`:

```csharp
(c, status, message) => WriteErrorAsync(c, dialect, status, "api_error", message),
```

> Leave `ProxyRouteKind.TranslateAnthropicToResponses` unhandled for now — it cannot occur yet, because Task 2's router only returns it for a model that advertises `/responses` and *not* `/v1/messages`, and nothing routes those to `/v1/messages` until Task 9. Task 9 adds the branch. If you want a belt-and-braces guard in the meantime, treat a non-`Passthrough` kind exactly like `route is null`.

- [ ] **Step 6: Register the routes**

In `Program.cs:201-234`, update the two existing `MapPost` calls to pass `ProxyDialect.AnthropicMessages`, then add:

```csharp
// Each POST also binds its un-prefixed twin. Base-URL conventions differ per client: Claude Code
// appends /v1/messages to a bare host, the AI SDK appends only /messages to a ".../v1" base, and
// Codex joins "{base}/responses". Binding both forms removes a whole class of misconfiguration.
foreach (var path in new[] { "/v1/messages", "/messages" })
{
    _ = app.MapPost(
        path,
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                ProxyDialect.AnthropicMessages
            )
    );
    _ = app.MapPost(
        $"{path}/count_tokens",
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                ProxyDialect.AnthropicMessages,
                isCountTokens: true
            )
    );
}

foreach (var path in new[] { "/v1/chat/completions", "/chat/completions" })
{
    _ = app.MapPost(
        path,
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                ProxyDialect.ChatCompletions
            )
    );
}

foreach (var path in new[] { "/v1/responses", "/responses" })
{
    _ = app.MapPost(
        path,
        ctx => ProxyHttp.ForwardAsync(ctx, catalog, config.IdleTimeout, config.KeepAliveInterval, ProxyDialect.Responses)
    );
}
```

Delete the two original `MapPost("/v1/messages", …)` / `MapPost("/v1/messages/count_tokens", …)` lines — the first loop replaces them.

- [ ] **Step 7: Run the full suite**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
```
Expected: PASS. `PassthroughTests`, `EndpointBehaviorTests`, and `HeaderAllowlistTests` must all stay green — they exercise `/v1/messages`, whose behavior is unchanged.

- [ ] **Step 8: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Program.cs tests/CopilotAnthropicProxy.Tests/OpenAiDialectTests.cs
git commit -m "feat(proxy): serve /chat/completions and /responses as passthrough routes"
```

---

## Task 5: Models union shape and default port

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs:258` (port), `:1071-1092` (`BuildModelsStub`); `samples/CopilotAnthropicProxy.Sample/README.md`
- Test: `tests/CopilotAnthropicProxy.Tests/EndpointBehaviorTests.cs`, `tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs`

**Interfaces:**
- Consumes: `ProxyModelInfo` (Task 1).
- Produces: `ProxyHttp.BuildModelsStub(IReadOnlyList<ProxyModelInfo> models)` — same name, new parameter type (already changed to compile in Task 1 Step 6; this task fixes the body).

**Background — one body, both shapes.** Anthropic clients read `data[].type == "model"` and `display_name`; OpenAI clients read `object == "list"` and `data[].object == "model"` with `owned_by` and a numeric `created`. The two shapes do not conflict, so a single body carrying every field satisfies both and we never have to branch on who is asking.

The research agent found that Claude Code effectively never calls this endpoint (its models-list cache is hard-disabled and gateway discovery is triple-gated), opencode takes its model list from `models.dev` or `opencode.json`, and Codex decodes a third shape (`{models:[…]}`). So this endpoint is a convenience, not a critical path — which is exactly why it gets the cheap union treatment and nothing more.

**Background — the port.** Default moves `8787` → `8788` so the sample can run beside an existing proxy on the old port. `HostGuardTests.cs` declares its own `private const int Port = 8787;` and passes it explicitly, so it is unaffected. `ProxyConfig` is `internal` and cannot be unit-tested (see Global Constraints); verify this change by reading the constant and the README.

- [ ] **Step 1: Write the failing test**

Add to `tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs`:

```csharp
[Fact]
public async Task Models_endpoint_serves_a_body_both_dialects_can_read()
{
    await using var factory = DiscoveryFactory(_ => TestUpstream.Json("{}"));
    using var client = factory.CreateClient();

    using var doc = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));
    var root = doc.RootElement;

    root.GetProperty("object").GetString().Should().Be("list", "OpenAI clients key off this");
    root.GetProperty("has_more").GetBoolean().Should().BeFalse();

    var first = root.GetProperty("data")[0];
    first.GetProperty("type").GetString().Should().Be("model", "Anthropic clients key off this");
    first.GetProperty("object").GetString().Should().Be("model", "OpenAI clients key off this");
    first.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
    first.GetProperty("display_name").GetString().Should().NotBeNullOrWhiteSpace();
    first.GetProperty("owned_by").GetString().Should().NotBeNullOrWhiteSpace();
    first.GetProperty("created").GetInt64().Should().BeGreaterThan(0);
    first.GetProperty("created_at").GetString().Should().NotBeNullOrWhiteSpace();
}
```

Use whatever the file's existing `DiscoveryFactory` helper signature is — the file already defines one that answers startup `GET /models`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~Models_endpoint_serves_a_body"
```
Expected: FAIL — `object` and `owned_by` are missing.

- [ ] **Step 3: Rewrite `BuildModelsStub`**

Replace the body at `Program.cs:1071-1092`:

```csharp
/// <summary>
///     Builds a model list that BOTH dialects can parse. Anthropic clients read
///     <c>data[].type == "model"</c> and <c>display_name</c>; OpenAI clients read
///     <c>object == "list"</c>, <c>data[].object == "model"</c> and <c>owned_by</c>. The two shapes do
///     not conflict, so one body carrying every field serves both and we never branch on the caller.
///
///     <c>created</c> is a fixed epoch — Copilot's /models does not report a creation time, and the
///     field exists only because OpenAI SDKs deserialise into a struct that requires it.
/// </summary>
public static string BuildModelsStub(IReadOnlyList<ProxyModelInfo> models)
{
    ArgumentNullException.ThrowIfNull(models);

    const long CreatedEpochSeconds = 1735689600L; // 2025-01-01T00:00:00Z
    const string CreatedIso = "2025-01-01T00:00:00Z";

    var data = models
        .Select(m => new
        {
            type = "model",
            @object = "model",
            id = m.Id,
            display_name = m.Id,
            owned_by = string.IsNullOrEmpty(m.Vendor) ? "copilot" : m.Vendor,
            created = CreatedEpochSeconds,
            created_at = CreatedIso,
        })
        .ToArray();

    return JsonSerializer.Serialize(
        new
        {
            @object = "list",
            data,
            has_more = false,
            first_id = models.Count == 0 ? null : models[0].Id,
            last_id = models.Count == 0 ? null : models[^1].Id,
        }
    );
}
```

- [ ] **Step 4: Change the default port**

At `Program.cs:258`, change the fallback in `ProxyConfig.FromEnvironment` from `8787` to `8788`:

```csharp
Port = ParseInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_PORT"), 8788),
```

Then update every `8787` in `samples/CopilotAnthropicProxy.Sample/README.md` to `8788` (there are 7). Leave `tests/CopilotAnthropicProxy.Tests/HostGuardTests.cs` alone — its `Port` constant is local and deliberate.

```bash
grep -rn "8787" samples/CopilotAnthropicProxy.Sample/
```
Expected after editing: no hits.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
```
Expected: PASS. `EndpointBehaviorTests.Models_stub_advertises_the_resolved_model` stays green — the union only *adds* fields, and pinned mode still yields exactly one entry whose `id` is `ProxyWebAppFactory.ConfiguredModel`.

- [ ] **Step 6: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Program.cs samples/CopilotAnthropicProxy.Sample/README.md tests/CopilotAnthropicProxy.Tests/ModelDiscoveryPassthroughTests.cs
git commit -m "feat(proxy): serve a dual-dialect model list and default to port 8788"
```

**P0 is complete here.** Claude Code → Claude, opencode → Claude, opencode → dual-endpoint GPT, and Codex → any GPT all work. What remains is the one quadrant Copilot cannot serve directly.

---

## Task 6: Anthropic request → Responses request

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Translation/AnthropicToResponsesRequest.cs`
- Test: `tests/CopilotAnthropicProxy.Tests/AnthropicToResponsesRequestTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks — this is a pure JSON→JSON function.
- Produces:
  - `public static class AnthropicToResponsesRequest`
  - `public static string Translate(string anthropicJson)`
  - `public static JsonObject Translate(JsonObject source)`
  - `public const int MinimumOutputTokens = 16`

**Background — the allowlist is the security model.** The translator does not copy the inbound body and patch it; it builds a new object and copies only fields it understands. That is what makes items 6 and 7 from the research findings safe *by construction*: body-level `betas`, `cache_control` with `ttl`/`scope` sub-fields, `metadata.user_id`, and server tools like `web_search_20250305` are all simply never read, so none of them can reach Copilot and 400 the request.

**Background — the `max_output_tokens` floor.** Claude Code's very first request against a model it has not seen is a validation probe with `max_tokens: 1`. The Responses API rejects `max_output_tokens` below 16. Without the floor, every Responses-only GPT model would be rejected by Claude Code before the user ever got a turn — the feature would appear completely broken. The floor raises 1 to 16; the probe then succeeds, possibly producing zero content, which Task 7 and Task 8 handle.

**Background — mapping table.**

| Anthropic | Responses | Note |
|---|---|---|
| `model` | `model` | already rewritten to the outbound id by the caller |
| `stream` | `stream` | defaults to `false` |
| `system` (array of text blocks, or string) | `instructions` | flattened, blocks joined with a blank line |
| `max_tokens` | `max_output_tokens` | clamped to ≥ 16 |
| `temperature`, `top_p` | same | copied when present |
| `messages[].content` text | `input[]` message item, `input_text` / `output_text` | role decides the part type |
| `messages[].content` image | `input_image` with a data URL | base64 or URL source |
| `messages[].content` `tool_use` | top-level `function_call` item | `input` object → `arguments` JSON **string** |
| `messages[].content` `tool_result` | top-level `function_call_output` item | text flattened |
| `thinking` / `redacted_thinking` | — | dropped; see Known Limitations |
| `tools[]` with `input_schema` | `tools[]` `{type:"function", …}` | `input_schema` → `parameters` |
| `tools[]` without `input_schema` | — | dropped (server tools Copilot rejects) |
| `tool_choice` | `tool_choice` | `any` → `required`; `tool` → `{type:"function",name}` |
| — | `store: false` | always; the proxy is stateless and full history is resent each turn |
| `stop_sequences`, `betas`, `cache_control`, `metadata` | — | dropped |

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/AnthropicToResponsesRequestTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class AnthropicToResponsesRequestTests
{
    private static JsonElement TranslateToElement(string anthropicJson) =>
        JsonDocument.Parse(AnthropicToResponsesRequest.Translate(anthropicJson)).RootElement.Clone();

    [Fact]
    public void Translates_a_plain_text_turn()
    {
        var result = TranslateToElement(
            """
            {"model":"gpt-5.3-codex","max_tokens":1024,"stream":true,
             "messages":[{"role":"user","content":"Hello"}]}
            """
        );

        result.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");
        result.GetProperty("max_output_tokens").GetInt32().Should().Be(1024);
        result.GetProperty("stream").GetBoolean().Should().BeTrue();
        result.GetProperty("store").GetBoolean().Should().BeFalse();

        var item = result.GetProperty("input")[0];
        item.GetProperty("type").GetString().Should().Be("message");
        item.GetProperty("role").GetString().Should().Be("user");
        item.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("input_text");
        item.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello");
    }

    [Fact]
    public void Clamps_the_model_validation_probe_to_the_minimum_output_tokens()
    {
        // Claude Code's first request against any new model is `max_tokens: 1`. The Responses API
        // rejects max_output_tokens < 16, so without this clamp EVERY GPT model is rejected on sight.
        var result = TranslateToElement(
            """
            {"model":"gpt-5.3-codex","max_tokens":1,
             "messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}]}
            """
        );

        result.GetProperty("max_output_tokens").GetInt32().Should().Be(AnthropicToResponsesRequest.MinimumOutputTokens);
    }

    [Fact]
    public void Flattens_a_system_block_array_into_instructions()
    {
        // Claude Code ALWAYS sends `system` as an array of text blocks, never a bare string.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,
             "system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral","ttl":"1h"}},
                       {"type":"text","text":"Answer in English."}],
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        result.GetProperty("instructions").GetString().Should().Be("You are terse.\n\nAnswer in English.");
    }

    [Fact]
    public void Accepts_the_legacy_bare_string_system_prompt()
    {
        var result = TranslateToElement(
            """{"model":"m","max_tokens":100,"system":"Be brief.","messages":[{"role":"user","content":"Hi"}]}"""
        );

        result.GetProperty("instructions").GetString().Should().Be("Be brief.");
    }

    [Fact]
    public void Turns_tool_use_and_tool_result_into_top_level_items()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":[
                 {"type":"text","text":"checking"},
                 {"type":"tool_use","id":"call_1","name":"get_weather","input":{"city":"Paris"}}]},
              {"role":"user","content":[
                 {"type":"tool_result","tool_use_id":"call_1","content":[{"type":"text","text":"18C"}]}]}
            ]}
            """
        );

        var input = result.GetProperty("input");
        input.GetArrayLength().Should().Be(4);

        input[1].GetProperty("type").GetString().Should().Be("message");
        input[1].GetProperty("content")[0].GetProperty("type").GetString().Should().Be("output_text");

        input[2].GetProperty("type").GetString().Should().Be("function_call");
        input[2].GetProperty("call_id").GetString().Should().Be("call_1");
        input[2].GetProperty("name").GetString().Should().Be("get_weather");
        input[2].GetProperty("arguments").GetString().Should().Be("""{"city":"Paris"}""");

        input[3].GetProperty("type").GetString().Should().Be("function_call_output");
        input[3].GetProperty("call_id").GetString().Should().Be("call_1");
        input[3].GetProperty("output").GetString().Should().Be("18C");
    }

    [Fact]
    public void Maps_function_tools_and_drops_server_tools()
    {
        // web_search_20250305 has no input_schema. Copilot answers
        // 400 "The use of the web search tool is not supported." if it is forwarded.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[
               {"name":"get_weather","description":"Weather.","input_schema":{"type":"object","properties":{}}},
               {"type":"web_search_20250305","name":"web_search","max_uses":8}],
             "tool_choice":{"type":"any"}}
            """
        );

        var tools = result.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("type").GetString().Should().Be("function");
        tools[0].GetProperty("name").GetString().Should().Be("get_weather");
        tools[0].GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");

        result.GetProperty("tool_choice").GetString().Should().Be("required");
    }

    [Fact]
    public void Drops_fields_the_responses_api_would_reject()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"betas":["context-management-2025-06-27"],
             "stop_sequences":["</severity>"],"metadata":{"user_id":"abc"},
             "messages":[{"role":"user","content":[
               {"type":"thinking","thinking":"hmm","signature":"sig"},
               {"type":"text","text":"Hi","cache_control":{"type":"ephemeral","scope":"global"}}]}]}
            """
        );

        result.TryGetProperty("betas", out _).Should().BeFalse();
        result.TryGetProperty("stop_sequences", out _).Should().BeFalse();
        result.TryGetProperty("metadata", out _).Should().BeFalse();

        var content = result.GetProperty("input")[0].GetProperty("content");
        content.GetArrayLength().Should().Be(1, "thinking blocks are not replayed");
        content[0].GetProperty("text").GetString().Should().Be("Hi");
        content[0].TryGetProperty("cache_control", out _).Should().BeFalse();
    }

    [Fact]
    public void Maps_a_base64_image_block_to_a_data_url()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}
            """
        );

        var part = result.GetProperty("input")[0].GetProperty("content")[0];
        part.GetProperty("type").GetString().Should().Be("input_image");
        part.GetProperty("image_url").GetString().Should().Be("data:image/png;base64,AAAA");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~AnthropicToResponsesRequestTests"
```
Expected: compile error — `AnthropicToResponsesRequest` does not exist.

- [ ] **Step 3: Write the translator**

Create `samples/CopilotAnthropicProxy.Sample/Translation/AnthropicToResponsesRequest.cs`:

```csharp
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites an Anthropic Messages request into an OpenAI Responses request.
///
///     Builds a NEW object from an explicit allowlist rather than patching the inbound body. That is
///     deliberate: Claude Code sends a great deal that Copilot's Responses endpoint rejects outright —
///     body-level <c>betas</c> (~29 of them), <c>cache_control</c> with <c>ttl</c>/<c>scope</c>
///     sub-fields, <c>metadata.user_id</c>, and server tools such as <c>web_search_20250305</c>. An
///     allowlist drops all of it by construction; a patch-in-place would have to chase each one.
/// </summary>
public static class AnthropicToResponsesRequest
{
    /// <summary>
    ///     The smallest <c>max_output_tokens</c> the Responses API accepts.
    ///
    ///     Claude Code's FIRST request against a model it has not used is a validation probe with
    ///     <c>max_tokens: 1</c> and <c>maxRetries: 0</c>. Passed through literally it is a 400, and
    ///     Claude Code concludes the model is unusable — so every GPT model would appear broken before
    ///     the user ever got a turn. Clamping up costs nothing: the probe only checks that a
    ///     well-formed response comes back.
    /// </summary>
    public const int MinimumOutputTokens = 16;

    /// <summary>Translates an Anthropic request body. Throws <see cref="ArgumentException"/> if it is not a JSON object.</summary>
    public static string Translate(string anthropicJson)
    {
        ArgumentNullException.ThrowIfNull(anthropicJson);

        return JsonNode.Parse(anthropicJson) is JsonObject source
            ? Translate(source).ToJsonString()
            : throw new ArgumentException("An Anthropic request body must be a JSON object.", nameof(anthropicJson));
    }

    /// <summary>Translates a parsed Anthropic request body.</summary>
    public static JsonObject Translate(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new JsonObject
        {
            ["model"] = source["model"]?.DeepClone(),
            ["stream"] = (source["stream"] ?? JsonValue.Create(false)).DeepClone(),

            // The proxy holds no server-side state and the client resends full history every turn,
            // so opt out of the Responses store (whose default is true).
            ["store"] = false,
            ["input"] = BuildInput(source["messages"] as JsonArray),
        };

        var instructions = FlattenText(source["system"]);
        if (instructions.Length > 0)
        {
            target["instructions"] = instructions;
        }

        // Anthropic requires max_tokens; Responses treats max_output_tokens as optional. Omit rather
        // than invent a cap when the client did not send one.
        if (source["max_tokens"]?.GetValue<int>() is { } maxTokens)
        {
            target["max_output_tokens"] = Math.Max(maxTokens, MinimumOutputTokens);
        }

        foreach (var passthrough in new[] { "temperature", "top_p" })
        {
            if (source[passthrough] is { } value)
            {
                target[passthrough] = value.DeepClone();
            }
        }

        if (BuildTools(source["tools"] as JsonArray) is { Count: > 0 } tools)
        {
            target["tools"] = tools;
        }

        if (BuildToolChoice(source["tool_choice"]) is { } toolChoice)
        {
            target["tool_choice"] = toolChoice;
        }

        return target;
    }

    /// <summary>
    ///     Flattens an Anthropic text value into a plain string. Accepts a bare string, a single block,
    ///     or an array of blocks; non-text blocks are ignored. Claude Code always sends the array form
    ///     for <c>system</c>, but the string form is legal and older clients use it.
    /// </summary>
    private static string FlattenText(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return text;
            case JsonObject block:
                return block["type"]?.GetValue<string>() == "text" ? block["text"]?.GetValue<string>() ?? "" : "";
            case JsonArray blocks:
                var parts = blocks
                    .OfType<JsonObject>()
                    .Where(b => b["type"]?.GetValue<string>() == "text")
                    .Select(b => b["text"]?.GetValue<string>() ?? "")
                    .Where(t => t.Length > 0);
                return string.Join("\n\n", parts);
            default:
                return "";
        }
    }

    /// <summary>
    ///     Turns Anthropic's messages into a Responses <c>input</c> array.
    ///
    ///     The shapes differ structurally: Anthropic nests tool calls and tool results INSIDE message
    ///     content, while Responses makes them top-level items. So a message's text and image blocks
    ///     accumulate into one message item, and any tool block flushes that item and appends its own.
    ///     Order is preserved throughout — Responses pairs a function_call with its output by
    ///     <c>call_id</c>, but a sane ordering keeps transcripts readable and matches what Codex sends.
    /// </summary>
    private static JsonArray BuildInput(JsonArray? messages)
    {
        var input = new JsonArray();
        if (messages is null)
        {
            return input;
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = message["role"]?.GetValue<string>() ?? "user";
            var textPartType = role == "assistant" ? "output_text" : "input_text";
            JsonArray? pending = null;

            void Flush()
            {
                if (pending is { Count: > 0 })
                {
                    input.Add(
                        new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = role,
                            ["content"] = pending,
                        }
                    );
                }

                pending = null;
            }

            JsonArray Pending() => pending ??= [];

            switch (message["content"])
            {
                case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                    Pending().Add(new JsonObject { ["type"] = textPartType, ["text"] = text });
                    break;

                case JsonArray blocks:
                    foreach (var block in blocks.OfType<JsonObject>())
                    {
                        switch (block["type"]?.GetValue<string>())
                        {
                            case "text":
                                Pending()
                                    .Add(
                                        new JsonObject
                                        {
                                            ["type"] = textPartType,
                                            ["text"] = block["text"]?.GetValue<string>() ?? "",
                                        }
                                    );
                                break;

                            case "image" when ToImageUrl(block["source"] as JsonObject) is { } imageUrl:
                                Pending().Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageUrl });
                                break;

                            case "tool_use":
                                Flush();
                                input.Add(
                                    new JsonObject
                                    {
                                        ["type"] = "function_call",
                                        ["call_id"] = block["id"]?.GetValue<string>() ?? "",
                                        ["name"] = block["name"]?.GetValue<string>() ?? "",
                                        // Anthropic sends a JSON object; Responses wants a JSON STRING.
                                        ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString(),
                                    }
                                );
                                break;

                            case "tool_result":
                                Flush();
                                input.Add(
                                    new JsonObject
                                    {
                                        ["type"] = "function_call_output",
                                        ["call_id"] = block["tool_use_id"]?.GetValue<string>() ?? "",
                                        ["output"] = FlattenText(block["content"]),
                                    }
                                );
                                break;

                            // "thinking" / "redacted_thinking" are dropped: replaying them needs
                            // reasoning.encrypted_content round-tripping, which this sample does not do.
                            // Anything else is unknown and equally not forwarded.
                        }
                    }

                    break;
            }

            Flush();
        }

        return input;
    }

    /// <summary>Builds a data URL (or passes a plain URL through) for an Anthropic image source.</summary>
    private static string? ToImageUrl(JsonObject? imageSource)
    {
        if (imageSource is null)
        {
            return null;
        }

        return imageSource["type"]?.GetValue<string>() switch
        {
            "url" => imageSource["url"]?.GetValue<string>(),
            "base64" =>
                $"data:{imageSource["media_type"]?.GetValue<string>() ?? "image/png"};base64,{imageSource["data"]?.GetValue<string>() ?? ""}",
            _ => null,
        };
    }

    /// <summary>
    ///     Maps Anthropic tools onto Responses function tools. ONLY entries carrying an
    ///     <c>input_schema</c> are mapped — that filter is what silently drops Claude Code's server
    ///     tools (<c>web_search_20250305</c>, <c>advisor_20260301</c>), which Copilot rejects with
    ///     400 "The use of the web search tool is not supported."
    /// </summary>
    private static JsonArray BuildTools(JsonArray? tools)
    {
        var mapped = new JsonArray();
        if (tools is null)
        {
            return mapped;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["input_schema"] is not { } schema)
            {
                continue;
            }

            var mappedTool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool["name"]?.GetValue<string>() ?? "",
                ["parameters"] = schema.DeepClone(),
            };

            if (tool["description"]?.GetValue<string>() is { Length: > 0 } description)
            {
                mappedTool["description"] = description;
            }

            mapped.Add(mappedTool);
        }

        return mapped;
    }

    /// <summary>Maps Anthropic's tool_choice onto the Responses spelling.</summary>
    private static JsonNode? BuildToolChoice(JsonNode? toolChoice)
    {
        if (toolChoice is not JsonObject choice)
        {
            return null;
        }

        return choice["type"]?.GetValue<string>() switch
        {
            "auto" => JsonValue.Create("auto"),
            "none" => JsonValue.Create("none"),
            "any" => JsonValue.Create("required"),
            "tool" => new JsonObject { ["type"] = "function", ["name"] = choice["name"]?.GetValue<string>() ?? "" },
            _ => null,
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~AnthropicToResponsesRequestTests"
```
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Translation/AnthropicToResponsesRequest.cs tests/CopilotAnthropicProxy.Tests/AnthropicToResponsesRequestTests.cs
git commit -m "feat(proxy): translate Anthropic Messages requests into Responses requests"
```

---

## Task 7: Responses response → Anthropic Message (non-streaming)

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicJson.cs`
- Test: `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicJsonTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public static class ResponsesToAnthropicJson`
  - `public static string Translate(string responsesJson, string fallbackModel)`
  - `public static string DeriveStopReason(JsonObject response)` — also used by Task 8, so both agree on the rule.

**Background — `stop_reason`.** Anthropic clients branch on it, so getting it wrong changes client behavior. The rule, in order:

1. `response.output` contains any `function_call` item → `tool_use`
2. `response.incomplete_details.reason == "max_output_tokens"` → `max_tokens`
3. otherwise → `end_turn`

Caveat carried from the spec: `incomplete_details` is not parsed anywhere under `src/OpenAiResponsesProvider`, so its exact live shape is unconfirmed. Any unrecognised shape falls through to `end_turn` — an honest default. Task 10's live smoke test is what confirms it.

**Background — zero content is a valid answer.** The `max_tokens: 1` probe (clamped to 16) against a reasoning model can finish having emitted only reasoning, leaving `output` with no text and no function calls. The translator must then produce `content: []`, not a fabricated empty text block. Anthropic's own API does exactly this.

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicJsonTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ResponsesToAnthropicJsonTests
{
    private static JsonElement Translate(string responsesJson) =>
        JsonDocument.Parse(ResponsesToAnthropicJson.Translate(responsesJson, "fallback-model")).RootElement.Clone();

    [Fact]
    public void Translates_a_text_response()
    {
        var result = Translate(
            """
            {"id":"resp_1","model":"gpt-5.3-codex",
             "output":[{"type":"message","role":"assistant",
                        "content":[{"type":"output_text","text":"Hello there"}]}],
             "usage":{"input_tokens":12,"output_tokens":3}}
            """
        );

        result.GetProperty("id").GetString().Should().Be("resp_1");
        result.GetProperty("type").GetString().Should().Be("message");
        result.GetProperty("role").GetString().Should().Be("assistant");
        result.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        result.GetProperty("stop_sequence").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(12);
        result.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(3);

        var content = result.GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("Hello there");
    }

    [Fact]
    public void Translates_a_function_call_into_a_tool_use_block()
    {
        var result = Translate(
            """
            {"id":"resp_2","model":"m",
             "output":[{"type":"function_call","call_id":"call_9","name":"get_weather",
                        "arguments":"{\"city\":\"Paris\"}"}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("tool_use");

        var block = result.GetProperty("content")[0];
        block.GetProperty("type").GetString().Should().Be("tool_use");
        block.GetProperty("id").GetString().Should().Be("call_9");
        block.GetProperty("name").GetString().Should().Be("get_weather");
        block.GetProperty("input").GetProperty("city").GetString().Should().Be("Paris");
    }

    [Fact]
    public void Surfaces_a_reasoning_summary_as_a_thinking_block()
    {
        var result = Translate(
            """
            {"id":"r","model":"m",
             "output":[{"type":"reasoning","summary":[{"type":"summary_text","text":"weighing options"}]},
                       {"type":"message","content":[{"type":"output_text","text":"Done"}]}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        var content = result.GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("thinking");
        content[0].GetProperty("thinking").GetString().Should().Be("weighing options");
        content[1].GetProperty("type").GetString().Should().Be("text");
    }

    [Fact]
    public void Reports_max_tokens_when_the_response_was_truncated()
    {
        var result = Translate(
            """
            {"id":"r","model":"m","incomplete_details":{"reason":"max_output_tokens"},
             "output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}],
             "usage":{"input_tokens":1,"output_tokens":16}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("max_tokens");
    }

    [Fact]
    public void An_empty_output_yields_an_empty_content_array_not_a_placeholder_block()
    {
        // This is what Claude Code's `max_tokens: 1` model-validation probe can produce against a
        // reasoning model. The envelope must be well-formed; the content must NOT be invented.
        var result = Translate("""{"id":"r","model":"m","output":[],"usage":{"input_tokens":1,"output_tokens":0}}""");

        result.GetProperty("content").GetArrayLength().Should().Be(0);
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        result.GetProperty("type").GetString().Should().Be("message");
    }

    [Fact]
    public void Falls_back_to_the_supplied_model_when_the_response_omits_one()
    {
        var result = Translate("""{"id":"r","output":[]}""");

        result.GetProperty("model").GetString().Should().Be("fallback-model");
        result.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ResponsesToAnthropicJsonTests"
```
Expected: compile error — `ResponsesToAnthropicJson` does not exist.

- [ ] **Step 3: Write the translator**

Create `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicJson.cs`:

```csharp
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites a non-streaming OpenAI Responses reply into an Anthropic Message.
///     <see cref="ResponsesToAnthropicSse"/> does the same job for streaming replies and shares
///     <see cref="DeriveStopReason"/> so the two cannot drift apart.
/// </summary>
public static class ResponsesToAnthropicJson
{
    /// <summary>
    ///     Translates a Responses reply body. <paramref name="fallbackModel"/> is reported when the
    ///     reply omits <c>model</c>.
    /// </summary>
    public static string Translate(string responsesJson, string fallbackModel)
    {
        ArgumentNullException.ThrowIfNull(responsesJson);

        if (JsonNode.Parse(responsesJson) is not JsonObject response)
        {
            throw new ArgumentException("A Responses reply must be a JSON object.", nameof(responsesJson));
        }

        var usage = response["usage"] as JsonObject;

        var message = new JsonObject
        {
            ["id"] = response["id"]?.GetValue<string>() ?? "msg_proxy",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = response["model"]?.GetValue<string>() ?? fallbackModel,
            ["content"] = BuildContent(response["output"] as JsonArray),
            ["stop_reason"] = DeriveStopReason(response),
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["input_tokens"]?.GetValue<int>() ?? 0,
                ["output_tokens"] = usage?["output_tokens"]?.GetValue<int>() ?? 0,
            },
        };

        return message.ToJsonString();
    }

    /// <summary>
    ///     Derives Anthropic's <c>stop_reason</c> from a Responses reply, in priority order:
    ///     a function call outranks truncation, truncation outranks a normal finish.
    ///
    ///     Any unrecognised <c>incomplete_details</c> shape falls through to <c>end_turn</c>. That
    ///     field is not modelled anywhere under src/OpenAiResponsesProvider, so its live shape is
    ///     confirmed by the live smoke test rather than by a fixture.
    /// </summary>
    public static string DeriveStopReason(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (
            response["output"] is JsonArray output
            && output.OfType<JsonObject>().Any(item => item["type"]?.GetValue<string>() == "function_call")
        )
        {
            return "tool_use";
        }

        if ((response["incomplete_details"] as JsonObject)?["reason"]?.GetValue<string>() == "max_output_tokens")
        {
            return "max_tokens";
        }

        return "end_turn";
    }

    /// <summary>
    ///     Maps Responses output items onto Anthropic content blocks, in order. An empty result is a
    ///     legitimate answer — a truncated or reasoning-only turn genuinely produced no content — so
    ///     nothing is invented to fill the gap.
    /// </summary>
    private static JsonArray BuildContent(JsonArray? output)
    {
        var content = new JsonArray();
        if (output is null)
        {
            return content;
        }

        foreach (var item in output.OfType<JsonObject>())
        {
            switch (item["type"]?.GetValue<string>())
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (part["type"]?.GetValue<string>() == "output_text")
                        {
                            content.Add(
                                new JsonObject { ["type"] = "text", ["text"] = part["text"]?.GetValue<string>() ?? "" }
                            );
                        }
                    }

                    break;

                case "reasoning":
                    // Display only. The encrypted payload that would make reasoning replayable across
                    // turns is not carried — see the README's Known limitations.
                    var summary = string.Join(
                        "\n\n",
                        (item["summary"] as JsonArray ?? [])
                            .OfType<JsonObject>()
                            .Select(s => s["text"]?.GetValue<string>() ?? "")
                            .Where(t => t.Length > 0)
                    );

                    if (summary.Length > 0)
                    {
                        content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = summary });
                    }

                    break;

                case "function_call":
                    content.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = item["call_id"]?.GetValue<string>() ?? "",
                            ["name"] = item["name"]?.GetValue<string>() ?? "",
                            ["input"] = ParseArguments(item["arguments"]?.GetValue<string>()),
                        }
                    );
                    break;
            }
        }

        return content;
    }

    /// <summary>
    ///     Parses a function call's arguments, which Responses sends as a JSON STRING while Anthropic
    ///     expects an object. Malformed or empty arguments become an empty object rather than an error:
    ///     a client can recover from a tool call with no arguments, but not from a broken envelope.
    /// </summary>
    private static JsonNode ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ResponsesToAnthropicJsonTests"
```
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicJson.cs tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicJsonTests.cs
git commit -m "feat(proxy): translate non-streaming Responses replies into Anthropic messages"
```

---

## Task 8: Responses SSE → Anthropic SSE

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicSse.cs`
- Test: `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicSseTests.cs`

**Interfaces:**
- Consumes: `ResponsesToAnthropicJson.DeriveStopReason(JsonObject)` (Task 7).
- Produces:
  - `public sealed class ResponsesToAnthropicSse`
  - `public ResponsesToAnthropicSse(string messageId, string model)`
  - `public IReadOnlyList<string> Next(string responsesEventJson)` — returns complete `event: …\ndata: …\n\n` frames, possibly empty.

**Background — the two stream shapes.** Anthropic's stream is a block cursor: `message_start`, then for each content block `content_block_start` → deltas → `content_block_stop` at a monotonically increasing `index`, then `message_delta` carrying `stop_reason` and cumulative usage, then `message_stop`. Responses emits `response.*` events keyed by `output_index` / `content_index`. Because Responses works through its output items in order, tracking a single open block is enough — no index bookkeeping across interleaved blocks is needed.

**Background — why there is no `Finish()`.** Under the "never fabricate SSE frames" rule, if the upstream stream ends without `response.completed` or `response.incomplete`, we emit nothing further and let the client see the truncation. Claude Code detects exactly this (`Stream completed without receiving message_start event` / `…but no content blocks completed`) and falls back to a non-streaming retry. Synthesising a clean terminator would convert an upstream failure into a silently empty successful answer — the worst outcome of the three. So the class is `Next()` only; the caller stops writing when the upstream stops.

**Background — usage.** Responses does not report input tokens until the terminal event, so `message_start` reports zeros and `message_delta` carries the real figures. Emitting `input_tokens` in `message_delta` as well as `output_tokens` is what keeps client-side cost accounting correct.

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicSseTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ResponsesToAnthropicSseTests
{
    /// <summary>Feeds a scripted Responses stream and returns the concatenated Anthropic SSE output.</summary>
    private static string Run(params string[] events)
    {
        var translator = new ResponsesToAnthropicSse("msg_test", "gpt-5.3-codex");
        return string.Concat(events.SelectMany(translator.Next));
    }

    [Fact]
    public void Emits_a_well_formed_text_stream()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5.3-codex"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text","text":""}}""",
            """{"type":"response.output_text.delta","delta":"Hel"}""",
            """{"type":"response.output_text.delta","delta":"lo"}""",
            """{"type":"response.content_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":9,"output_tokens":2}}}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().Contain("\"id\":\"resp_1\"");
        output.Should().Contain("event: content_block_start");
        output.Should().Contain("\"index\":0");
        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"Hel\"");
        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"lo\"");
        output.Should().Contain("event: content_block_stop");
        output.Should().Contain("\"stop_reason\":\"end_turn\"");
        output.Should().Contain("\"input_tokens\":9");
        output.Should().Contain("\"output_tokens\":2");
        output.Should().EndWith("\n\n");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Streams_a_tool_call_as_an_input_json_delta_block()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call_1","name":"get_weather"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"city\":"}""",
            """{"type":"response.function_call_arguments.delta","delta":"\"Paris\"}"}""",
            """{"type":"response.output_item.done"}""",
            """{"type":"response.completed","response":{"output":[{"type":"function_call"}],"usage":{"input_tokens":1,"output_tokens":5}}}"""
        );

        output.Should().Contain("\"type\":\"tool_use\"");
        output.Should().Contain("\"id\":\"call_1\"");
        output.Should().Contain("\"name\":\"get_weather\"");
        output.Should().Contain("\"type\":\"input_json_delta\"");
        output.Should().Contain("\"stop_reason\":\"tool_use\"");
    }

    [Fact]
    public void Streams_a_reasoning_summary_as_a_thinking_block()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.reasoning_summary_part.added"}""",
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking..."}""",
            """{"type":"response.reasoning_summary_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"type\":\"thinking\"");
        output.Should().Contain("\"type\":\"thinking_delta\"");
    }

    [Fact]
    public void Closes_an_open_block_before_terminating()
    {
        // Upstream ends the response without closing its content part — the block must still be closed
        // before message_delta, or the Anthropic stream is malformed.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"hi"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.IndexOf("content_block_stop", StringComparison.Ordinal)
            .Should()
            .BeLessThan(output.IndexOf("message_delta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_response_with_no_content_still_produces_a_well_formed_envelope()
    {
        // Claude Code's `max_tokens: 1` validation probe. Zero content blocks, but message_start and
        // message_stop MUST both be present or the model is judged unusable.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":3,"output_tokens":0}}}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().Contain("event: message_delta");
        output.Should().Contain("event: message_stop");
        output.Should().NotContain("content_block_start", "no content is honest; a fabricated block is not");
    }

    [Fact]
    public void Reports_max_tokens_for_an_incomplete_response()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.incomplete","response":{"output":[],"incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":16}}}"""
        );

        output.Should().Contain("\"stop_reason\":\"max_tokens\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void A_truncated_stream_is_not_capped_with_a_fabricated_terminator()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"partial"}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().NotContain("message_stop", "the upstream never terminated; inventing one hides the failure");
    }

    [Fact]
    public void Ignores_unknown_and_malformed_events()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.some_future_event","data":{}}""",
            "not json at all",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("event: message_stop");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ResponsesToAnthropicSseTests"
```
Expected: compile error — `ResponsesToAnthropicSse` does not exist.

- [ ] **Step 3: Write the state machine**

Create `samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicSse.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Rewrites an OpenAI Responses SSE stream into an Anthropic Messages SSE stream, one upstream
///     event at a time.
///
///     Anthropic's stream is a block cursor — message_start, then per content block a
///     content_block_start / deltas / content_block_stop triple at a monotonically increasing index,
///     then message_delta with the stop reason and usage, then message_stop. Responses emits its
///     output items in order, so tracking ONE open block is sufficient.
///
///     There is deliberately no Finish(): if the upstream stream ends without a terminal event this
///     class emits nothing more, leaving the client to observe the truncation. Capping a failed stream
///     with a synthetic message_stop would turn an upstream error into a silently empty success.
/// </summary>
public sealed class ResponsesToAnthropicSse
{
    private readonly string _fallbackMessageId;
    private readonly string _fallbackModel;

    private bool _started;
    private bool _finished;
    private int _nextIndex;
    private bool _blockOpen;

    /// <summary>
    ///     <paramref name="messageId"/> and <paramref name="model"/> are used when the upstream stream
    ///     does not announce its own — Anthropic requires both in message_start.
    /// </summary>
    public ResponsesToAnthropicSse(string messageId, string model)
    {
        _fallbackMessageId = messageId;
        _fallbackModel = model;
    }

    /// <summary>
    ///     Feeds one Responses SSE <c>data:</c> payload and returns the Anthropic frames it produces
    ///     (often none). Unparseable or unrecognised payloads produce no frames rather than an error —
    ///     a stream must not die because the upstream added an event type we have not seen.
    /// </summary>
    public IReadOnlyList<string> Next(string responsesEventJson)
    {
        if (_finished || string.IsNullOrWhiteSpace(responsesEventJson))
        {
            return [];
        }

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(responsesEventJson) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        if (evt?["type"]?.GetValue<string>() is not { } type)
        {
            return [];
        }

        var frames = new List<string>();
        var response = evt["response"] as JsonObject;

        switch (type)
        {
            case "response.created":
                Start(frames, response);
                break;

            case "response.content_part.added" when (evt["part"] as JsonObject)?["type"]?.GetValue<string>() == "output_text":
                Start(frames, response);
                OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                break;

            case "response.output_text.delta":
                Start(frames, response);
                if (!_blockOpen)
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "text", ["text"] = "" });
                }

                Delta(frames, "text_delta", "text", evt["delta"]?.GetValue<string>() ?? "");
                break;

            case "response.reasoning_summary_part.added":
                Start(frames, response);
                OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                break;

            case "response.reasoning_summary_text.delta":
                Start(frames, response);
                if (!_blockOpen)
                {
                    OpenBlock(frames, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                }

                Delta(frames, "thinking_delta", "thinking", evt["delta"]?.GetValue<string>() ?? "");
                break;

            case "response.output_item.added" when (evt["item"] as JsonObject)?["type"]?.GetValue<string>() == "function_call":
                Start(frames, response);
                OpenBlock(
                    frames,
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = evt["item"]?["call_id"]?.GetValue<string>() ?? "",
                        ["name"] = evt["item"]?["name"]?.GetValue<string>() ?? "",
                        ["input"] = new JsonObject(),
                    }
                );
                break;

            case "response.function_call_arguments.delta":
                Start(frames, response);
                Delta(frames, "input_json_delta", "partial_json", evt["delta"]?.GetValue<string>() ?? "");
                break;

            case "response.content_part.done":
            case "response.output_item.done":
            case "response.reasoning_summary_part.done":
                CloseBlock(frames);
                break;

            case "response.completed":
            case "response.incomplete":
                Start(frames, response);
                CloseBlock(frames);
                Terminate(frames, response);
                break;
        }

        return frames;
    }

    /// <summary>Emits message_start once, taking the id and model from the upstream response if it offered them.</summary>
    private void Start(List<string> frames, JsonObject? response)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        var message = new JsonObject
        {
            ["id"] = response?["id"]?.GetValue<string>() ?? _fallbackMessageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = response?["model"]?.GetValue<string>() ?? _fallbackModel,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,

            // Responses does not report token counts until its terminal event; the real figures
            // arrive in message_delta.
            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
        };

        frames.Add(Frame("message_start", new JsonObject { ["type"] = "message_start", ["message"] = message }));
    }

    private void OpenBlock(List<string> frames, JsonObject contentBlock)
    {
        CloseBlock(frames);
        _blockOpen = true;

        frames.Add(
            Frame(
                "content_block_start",
                new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = _nextIndex,
                    ["content_block"] = contentBlock,
                }
            )
        );
    }

    private void Delta(List<string> frames, string deltaType, string field, string value)
    {
        if (!_blockOpen || value.Length == 0)
        {
            return;
        }

        frames.Add(
            Frame(
                "content_block_delta",
                new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _nextIndex,
                    ["delta"] = new JsonObject { ["type"] = deltaType, [field] = value },
                }
            )
        );
    }

    private void CloseBlock(List<string> frames)
    {
        if (!_blockOpen)
        {
            return;
        }

        frames.Add(Frame("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = _nextIndex }));
        _blockOpen = false;
        _nextIndex++;
    }

    private void Terminate(List<string> frames, JsonObject? response)
    {
        _finished = true;

        var usage = response?["usage"] as JsonObject;

        frames.Add(
            Frame(
                "message_delta",
                new JsonObject
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject
                    {
                        ["stop_reason"] = response is null ? "end_turn" : ResponsesToAnthropicJson.DeriveStopReason(response),
                        ["stop_sequence"] = null,
                    },
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = usage?["input_tokens"]?.GetValue<int>() ?? 0,
                        ["output_tokens"] = usage?["output_tokens"]?.GetValue<int>() ?? 0,
                    },
                }
            )
        );

        frames.Add(Frame("message_stop", new JsonObject { ["type"] = "message_stop" }));
    }

    private static string Frame(string eventName, JsonObject payload) =>
        $"event: {eventName}\ndata: {payload.ToJsonString()}\n\n";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~ResponsesToAnthropicSseTests"
```
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Translation/ResponsesToAnthropicSse.cs tests/CopilotAnthropicProxy.Tests/ResponsesToAnthropicSseTests.cs
git commit -m "feat(proxy): translate Responses SSE into Anthropic SSE"
```

---

## Task 9: Wire the translated branch into `/v1/messages`

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs` (`ProxyHttp`)
- Test: `tests/CopilotAnthropicProxy.Tests/TranslatedMessagesTests.cs`

**Interfaces:**
- Consumes: `ProxyRouteKind.TranslateAnthropicToResponses`, `ModelRoute` (Task 2); `AnthropicToResponsesRequest.Translate(string)` (Task 6); `ResponsesToAnthropicJson.Translate(string, string)` (Task 7); `ResponsesToAnthropicSse` (Task 8); the existing `ProxyHttp.ApplyRequestHeaderAllowlist`, `WriteAnthropicErrorAsync`.
- Produces: no new public surface — a private `ProxyHttp.TranslateAnthropicToResponsesAsync`.

**Background — the cancellation obligation.** The translated path does **not** go through `ProxyHttp.CopyBodyAsync`, so it does not inherit the two behaviors that file's comments spend most of their length on:

1. **Idle timeout reset per read.** `ForwardAsync` builds a `CancellationTokenSource` linked with `ctx.RequestAborted` and calls `CancelAfter(idleTimeout)` before *every* read, so the clock measures the gap between bytes rather than total request duration. A long generation must not be killed for being long.
2. **Keep-alive pings.** When `keepAliveInterval > TimeSpan.Zero`, a read that takes longer than the interval emits an SSE comment/ping so intermediaries do not drop an idle-looking connection.

Both must be re-implemented here. Losing them turns a slow model into a 504.

- [ ] **Step 1: Write the failing tests**

Create `tests/CopilotAnthropicProxy.Tests/TranslatedMessagesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class TranslatedMessagesTests
{
    private const string DiscoveryJson = """
    {"data":[
      {"id":"claude-opus-4.8","vendor":"Anthropic","supported_endpoints":["/v1/messages","/chat/completions"]},
      {"id":"gpt-5.3-codex","vendor":"OpenAI","supported_endpoints":["/responses"]}
    ]}
    """;

    private static ProxyWebAppFactory Factory(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> onProxied) =>
        new(
            async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/models", StringComparison.Ordinal))
                {
                    return TestUpstream.Json(DiscoveryJson);
                }

                return await onProxied(request, path);
            },
            model: null
        );

    [Fact]
    public async Task A_responses_only_model_is_reached_via_the_responses_endpoint()
    {
        string? path = null;
        string? body = null;

        await using var factory = Factory(
            async (request, seen) =>
            {
                path = seen;
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json(
                    """
                    {"id":"resp_1","model":"gpt-5.3-codex",
                     "output":[{"type":"message","content":[{"type":"output_text","text":"Hi there"}]}],
                     "usage":{"input_tokens":4,"output_tokens":2}}
                    """
                );
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1024,
                messages = new[] { new { role = "user", content = "Hello" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        path.Should().Be("/responses");

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(1024);
        sent.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();

        using var received = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        received.RootElement.GetProperty("type").GetString().Should().Be("message");
        received.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hi there");
    }

    [Fact]
    public async Task A_streaming_request_is_translated_frame_by_frame()
    {
        await using var factory = Factory(
            (_, _) =>
                Task.FromResult(
                    TestUpstream.Sse(
                        string.Concat(
                            """
                            event: response.created
                            data: {"type":"response.created","response":{"id":"resp_2","model":"gpt-5.3-codex"}}


                            """.ReplaceLineEndings("\n"),
                            """
                            event: response.output_text.delta
                            data: {"type":"response.output_text.delta","delta":"Hello"}


                            """.ReplaceLineEndings("\n"),
                            """
                            event: response.completed
                            data: {"type":"response.completed","response":{"output":[],"usage":{"input_tokens":4,"output_tokens":1}}}


                            """.ReplaceLineEndings("\n")
                        )
                    )
                )
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1024,
                stream = true,
                messages = new[] { new { role = "user", content = "Hello" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var stream = await response.Content.ReadAsStringAsync();
        stream.Should().Contain("event: message_start");
        stream.Should().Contain("\"type\":\"text_delta\",\"text\":\"Hello\"");
        stream.Should().Contain("event: message_stop");
        stream.Should().NotContain("response.created", "upstream frames must not leak through");
    }

    [Fact]
    public async Task The_model_validation_probe_returns_a_well_formed_empty_message()
    {
        // Claude Code's first request against any new model: max_tokens 1, one text block, no retries.
        string? body = null;

        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json(
                    """{"id":"resp_3","model":"gpt-5.3-codex","output":[],"usage":{"input_tokens":3,"output_tokens":0}}"""
                );
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = new[] { new { type = "text", text = "Hi" } } },
                },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_output_tokens")
            .GetInt32()
            .Should()
            .Be(16, "Responses rejects max_output_tokens below 16 and Claude Code would reject the model");

        using var received = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        received.RootElement.GetProperty("type").GetString().Should().Be("message");
        received.RootElement.GetProperty("content").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_upstream_error_is_reported_in_the_anthropic_error_shape()
    {
        await using var factory = Factory(
            (_, _) =>
                Task.FromResult(
                    TestUpstream.Json("""{"error":{"message":"model is overloaded"}}""", HttpStatusCode.ServiceUnavailable)
                )
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 100,
                messages = new[] { new { role = "user", content = "Hi" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("overloaded");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter "FullyQualifiedName~TranslatedMessagesTests"
```
Expected: FAIL with 404 — `ForwardAsync` still rejects a non-`Passthrough` route.

- [ ] **Step 3: Branch in `ForwardAsync`**

In `ProxyHttp.ForwardAsync`, immediately after the route is resolved and before the body is rewritten:

```csharp
if (route.Kind == ProxyRouteKind.TranslateAnthropicToResponses)
{
    await TranslateAnthropicToResponsesAsync(ctx, http, inboundBody, outboundModel, idleTimeout, keepAliveInterval, logger)
        .ConfigureAwait(false);
    return;
}
```

Use whatever local name `ForwardAsync` already gives the `HttpClient` it resolved from DI, and pass the same `ILogger` it already holds.

- [ ] **Step 4: Implement the translated path**

Add to `ProxyHttp`:

```csharp
/// <summary>
///     Serves an Anthropic Messages request from a model that only speaks OpenAI Responses.
///
///     This path does NOT go through <see cref="CopyBodyAsync"/>, so it re-implements that method's
///     two cancellation behaviors itself: the idle timeout is reset before EVERY read (so the clock
///     measures the gap between bytes, not the total request duration), and a slow read emits a
///     keep-alive ping. Losing either turns a slow generation into a 504.
/// </summary>
private static async Task TranslateAnthropicToResponsesAsync(
    HttpContext ctx,
    HttpClient http,
    byte[] inboundBody,
    string outboundModel,
    TimeSpan idleTimeout,
    TimeSpan keepAliveInterval,
    ILogger logger
)
{
    string translatedBody;
    bool isStreaming;

    try
    {
        var source = JsonNode.Parse(inboundBody) as JsonObject;
        if (source is null)
        {
            await WriteAnthropicErrorAsync(
                    ctx,
                    StatusCodes.Status400BadRequest,
                    "invalid_request_error",
                    "Request body must be a JSON object."
                )
                .ConfigureAwait(false);
            return;
        }

        source["model"] = outboundModel;
        isStreaming = source["stream"]?.GetValue<bool>() ?? false;
        translatedBody = AnthropicToResponsesRequest.Translate(source).ToJsonString();
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
    {
        logger.LogWarning(ex, "Could not translate an Anthropic request for {Model}", outboundModel);
        await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Request body could not be translated to the Responses API."
            )
            .ConfigureAwait(false);
        return;
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, ModelRouter.ResponsesPath)
    {
        Content = new StringContent(translatedBody, Encoding.UTF8, "application/json"),
    };
    ApplyRequestHeaderAllowlist(ctx.Request.Headers, request);

    using var idleCts = new CancellationTokenSource();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, idleCts.Token);
    idleCts.CancelAfter(idleTimeout);

    HttpResponseMessage upstream;
    try
    {
        upstream = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status504GatewayTimeout,
                "api_error",
                "Upstream did not respond before the idle timeout."
            )
            .ConfigureAwait(false);
        return;
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Upstream request to {Path} failed", ModelRouter.ResponsesPath);
        await WriteAnthropicErrorAsync(ctx, StatusCodes.Status502BadGateway, "api_error", "Upstream request failed.")
            .ConfigureAwait(false);
        return;
    }

    using (upstream)
    {
        if (!upstream.IsSuccessStatusCode)
        {
            var errorBody = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
            await WriteAnthropicErrorAsync(ctx, (int)upstream.StatusCode, "api_error", ExtractErrorMessage(errorBody))
                .ConfigureAwait(false);
            return;
        }

        if (!isStreaming)
        {
            var json = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ResponsesToAnthropicJson.Translate(json, outboundModel), ctx.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var translator = new ResponsesToAnthropicSse($"msg_{Guid.NewGuid():N}", outboundModel);

        await using var upstreamStream = await upstream
            .Content.ReadAsStreamAsync(linked.Token)
            .ConfigureAwait(false);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);

        while (true)
        {
            // Reset per read: the idle timeout measures the gap between bytes, never total duration.
            idleCts.CancelAfter(idleTimeout);

            string? line;
            try
            {
                line = await ReadLineWithKeepAliveAsync(ctx, reader, keepAliveInterval, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return; // The client hung up. Nothing to report to anyone.
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
                // Mid-stream failure. The response has already started, so no error envelope can be
                // written and no terminator may be fabricated — stop and let the client see truncation.
                logger.LogWarning(ex, "Translated stream from {Model} failed mid-flight", outboundModel);
                return;
            }

            if (line is null)
            {
                return;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            foreach (var frame in translator.Next(payload))
            {
                await ctx.Response.WriteAsync(frame, ctx.RequestAborted).ConfigureAwait(false);
            }

            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     Reads one line, emitting an Anthropic <c>ping</c> event whenever the wait exceeds
///     <paramref name="keepAliveInterval"/> so intermediaries do not drop a connection that merely
///     looks idle. A non-positive interval disables pings.
/// </summary>
private static async Task<string?> ReadLineWithKeepAliveAsync(
    HttpContext ctx,
    StreamReader reader,
    TimeSpan keepAliveInterval,
    CancellationToken cancellationToken
)
{
    var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
    if (keepAliveInterval <= TimeSpan.Zero)
    {
        return await readTask.ConfigureAwait(false);
    }

    while (true)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(keepAliveInterval, delayCts.Token);

        if (await Task.WhenAny(readTask, delay).ConfigureAwait(false) == readTask)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            return await readTask.ConfigureAwait(false);
        }

        await ctx.Response.WriteAsync("event: ping\ndata: {\"type\":\"ping\"}\n\n", cancellationToken)
            .ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Pulls a human-readable message out of an OpenAI-shaped error body, falling back to the raw text.</summary>
private static string ExtractErrorMessage(string body)
{
    try
    {
        if (JsonNode.Parse(body) is JsonObject obj && obj["error"]?["message"]?.GetValue<string>() is { } message)
        {
            return message;
        }
    }
    catch (JsonException)
    {
        // Not JSON — fall through and return what we got.
    }

    return string.IsNullOrWhiteSpace(body) ? "Upstream request failed." : body;
}
```

No new `using` directives are needed: `Program.cs:6-9` already imports `System.Text`, `System.Text.Json` and `System.Text.Json.Nodes`, and `IHttpResponseBodyFeature` is already used at `Program.cs:804`. Note `ApplyRequestHeaderAllowlist` takes an `IHeaderDictionary`, not an `HttpContext` — `Program.cs:1014`.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
```
Expected: PASS, including all pre-existing tests.

- [ ] **Step 6: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/Program.cs tests/CopilotAnthropicProxy.Tests/TranslatedMessagesTests.cs
git commit -m "feat(proxy): serve Responses-only models through the Anthropic Messages endpoint"
```

**P1 is complete here.** All four client/model quadrants work.

---

## Task 10: Documentation and live smoke tests

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/README.md`
- Modify: `tests/CopilotLive.Tests/CopilotAnthropicProxyLiveTests.cs`

**Interfaces:**
- Consumes: everything above. Adds no new production code.

**Background.** `tests/CopilotLive.Tests/` is outside `LmDotnetTools.sln`, so CI never runs it — these tests hit the real Copilot backend and are run by hand. They exist to confirm the parts a fixture cannot prove, above all the live shape of `incomplete_details` (Task 7's documented caveat). Follow the existing file's conventions: `[SkippableFact]`, `Skip.IfNot(_fixture.Available, _fixture.SkipReason)`, and `_output.WriteLine` for the raw payload so a failure is diagnosable from the log alone.

- [ ] **Step 1: Update the README**

Add to `samples/CopilotAnthropicProxy.Sample/README.md`:

```markdown
## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/messages`, `/messages` | Anthropic Messages. Passthrough for Claude models; translated to `/responses` for Responses-only GPT models. |
| `POST` | `/v1/messages/count_tokens`, `/messages/count_tokens` | Anthropic token counting. Claude models only. |
| `POST` | `/v1/chat/completions`, `/chat/completions` | OpenAI Chat Completions. Passthrough. |
| `POST` | `/v1/responses`, `/responses` | OpenAI Responses. Passthrough. |
| `GET` | `/v1/models` | Model list in a body both dialects can parse. |
| `GET` | `/health` | Liveness. |
| `ALL` | `/mcp`, `/mcp/readonly` | Unchanged MCP passthrough. |

Every `POST` binds both the `/v1`-prefixed and un-prefixed form, because clients disagree about where
the prefix belongs: Claude Code appends `/v1/messages` to a bare host, the Vercel AI SDK appends only
`/messages` to a `…/v1` base, and Codex joins `{base}/responses`.

## Configuring each client

```bash
# Claude Code — any model in the catalog, Claude or GPT
export ANTHROPIC_BASE_URL=http://127.0.0.1:8788
export ANTHROPIC_MODEL=gpt-5.3-codex

# Codex CLI — ~/.codex/config.toml
# base_url = "http://127.0.0.1:8788/v1"

# opencode — OpenAI-compatible provider
# baseURL: "http://127.0.0.1:8788/v1"
```

## Which models are served

Discovered from Copilot's `/models` at startup — nothing is hard-coded. A model is served when it
advertises at least one of `/v1/messages`, `/chat/completions` or `/responses`, and its vendor is
neither Google nor Microsoft. Models advertising no endpoints (including `text-embedding-*`) are
excluded so an embedding model can never surface as a chat model.

Requests naming an unknown model are mapped onto the same family when one exists
(`claude-3-5-haiku-*` → `claude-haiku-4.5`) and otherwise onto the catalog default. That matters
because Claude Code sends conversation-title, classification and summarisation traffic to this same
base URL under haiku model ids; without family mapping, every one of them would bill against Opus.

## Known limitations

These affect **only** the translated route — Anthropic Messages in, for a model that speaks only the
Responses API. Every passthrough route is byte-for-byte and has none of them.

- **`stop_sequences` is dropped.** The Responses API has no equivalent parameter, and emulating one
  means truncating a stream at a boundary that can straddle SSE chunks. Claude Code uses it only for
  auto-mode classification, which targets the haiku tier — an Anthropic model, hence passthrough.
- **Reasoning does not carry across turns.** Reasoning summaries are surfaced as `thinking` blocks for
  display; the encrypted payload that would let a GPT model resume its own reasoning through a tool
  loop is not round-tripped. Answers stay correct; the model re-derives its reasoning each turn.
- **Tool ids are passed through verbatim.** Responses mints `call_…`; Anthropic conventionally uses
  `toolu_…`. The round-trip works because the id we hand out is the id we accept back, but Claude Code
  cannot pattern-match these ids when explaining a tool-pairing failure, so that one error message is
  less specific than usual.
- **`count_tokens` serves Claude models only.** Clients degrade gracefully — Claude Code logs
  `countTokens API call failed` and falls back to a local estimate, which drives only the `/context`
  bar and the auto-compact threshold.
- **Prompt caching, batch and files APIs are not proxied.**
```

Also confirm the port is `8788` everywhere in the file (Task 5 changed it).

- [ ] **Step 2: Add the live smoke tests**

Append to `tests/CopilotLive.Tests/CopilotAnthropicProxyLiveTests.cs`, following the file's existing fixture and skip conventions:

```csharp
/// <summary>
///     THE GATE for the translated route: drive a Responses-only model through the ANTHROPIC endpoint
///     and require a well-formed Anthropic stream back. This is the one quadrant that is real
///     translation rather than passthrough.
/// </summary>
[SkippableFact]
public async Task Anthropic_endpoint_streams_a_responses_only_model()
{
    Skip.IfNot(_fixture.Available, _fixture.SkipReason);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

    var model = await PickResponsesOnlyModelAsync(cts.Token);
    _output.WriteLine($"model: {model}");

    var body = await PostProxyAsync(
        "/v1/messages",
        new
        {
            model,
            max_tokens = 128,
            stream = true,
            messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
        },
        cts.Token
    );

    _output.WriteLine(body);

    body.Should().Contain("event: message_start");
    body.Should().Contain("content_block_delta");
    body.Should().Contain("event: message_stop");
    body.Should().NotContain("response.created", "upstream Responses frames must not leak through");
}

/// <summary>
///     Confirms Claude Code's model-validation probe survives translation. It sends max_tokens: 1 with
///     maxRetries: 0 as the FIRST request against any new model; a 400 here makes the model look
///     unusable before the user ever gets a turn.
/// </summary>
[SkippableFact]
public async Task Anthropic_endpoint_survives_the_max_tokens_one_validation_probe()
{
    Skip.IfNot(_fixture.Available, _fixture.SkipReason);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

    var model = await PickResponsesOnlyModelAsync(cts.Token);

    var body = await PostProxyAsync(
        "/v1/messages",
        new
        {
            model,
            max_tokens = 1,
            messages = new[]
            {
                new { role = "user", content = new[] { new { type = "text", text = "Hi" } } },
            },
        },
        cts.Token
    );

    _output.WriteLine(body);

    using var doc = JsonDocument.Parse(body);
    doc.RootElement.GetProperty("type").GetString().Should().Be("message");
    doc.RootElement.GetProperty("role").GetString().Should().Be("assistant");
    doc.RootElement.TryGetProperty("stop_reason", out _).Should().BeTrue();

    // Confirms Task 7's documented caveat: the live shape of incomplete_details is unverified, so
    // record what came back rather than asserting a reason we have never observed.
    _output.WriteLine($"OBSERVED stop_reason: {doc.RootElement.GetProperty("stop_reason").GetString()}");
}

/// <summary>Codex's quadrant: a Responses request must reach Copilot unchanged.</summary>
[SkippableFact]
public async Task Responses_endpoint_passes_through()
{
    Skip.IfNot(_fixture.Available, _fixture.SkipReason);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

    var model = await PickResponsesOnlyModelAsync(cts.Token);

    var body = await PostProxyAsync(
        "/v1/responses",
        new
        {
            model,
            store = false,
            input = new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = "Reply with: ok" } },
                },
            },
        },
        cts.Token
    );

    _output.WriteLine(body);
    body.Should().Contain("\"output\"");
}
```

Reuse the file's existing proxy-hosting helper for `PostProxyAsync`; add `PickResponsesOnlyModelAsync` modelled on `CopilotChatCompletionsProbeTests.PickByEndpointAsync`, reading the live catalog rather than guessing an id from its name — guessing by name is what made an earlier probe pick a Responses-only model when it wanted a dual-endpoint one.

- [ ] **Step 3: Run the unit suite, then the live suite by hand**

```bash
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj
dotnet test tests/CopilotLive.Tests/CopilotLive.Tests.csproj
```
Expected: the first passes; the second passes or skips cleanly when no Copilot credentials are present. A live failure is evidence about the real backend, so the assertion describes something the code no longer does — the code is what needs to change.

- [ ] **Step 4: Verify the whole solution still builds**

```bash
dotnet build LmDotnetTools.sln
```
Expected: no errors, no new warnings. `EnforceCodeStyleInBuild=true` means IDE diagnostics are errors — `IDE0370` on an unnecessary `!` is the usual culprit.

- [ ] **Step 5: Commit**

```bash
git add samples/CopilotAnthropicProxy.Sample/README.md tests/CopilotLive.Tests/CopilotAnthropicProxyLiveTests.cs
git commit -m "docs(proxy): document the bidirectional surface and add live smoke tests"
```

---

## Verification checklist

Against the spec's success criteria:

| # | Criterion | Verified by |
|---|---|---|
| 1 | opencode drives Claude and dual-endpoint GPT models | `OpenAiDialectTests` (Task 4) |
| 2 | Codex CLI drives any GPT model | `OpenAiDialectTests.Responses_forwards_to_the_upstream_responses_path` (Task 4), `Responses_endpoint_passes_through` (Task 10) |
| 3 | Claude Code drives Responses-only GPT models | `TranslatedMessagesTests` (Task 9), `Anthropic_endpoint_streams_a_responses_only_model` (Task 10) |
| 4 | Nothing that works today breaks | The whole pre-existing suite stays green at every commit; deviations D1 and D2 are the two places that took explicit care |
| 5 | No hard-coded model ids | `grep -rn "claude-\|gpt-" samples/CopilotAnthropicProxy.Sample/ --include=*.cs` returns only comments |
