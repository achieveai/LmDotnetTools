# Copilot and Jina Web Tools Implementation Plan

**Status:** Implemented — shipped in `b3699ed4` (#267). See `samples/LmStreaming.Sample/Services/CopilotWebSearchRegistration.cs`, wired at both Copilot paths in `Program.cs`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose Copilot's hosted `web_search` through its readonly MCP endpoint for every Copilot provider path, retain Jina `WebFetch`, and fall back to Jina `WebSearch` when hosted search is unavailable.

**Architecture:** Keep the existing transparent `/mcp` proxy unchanged. Add one narrow sample service that creates an authenticated Streamable-HTTP MCP client with `X-MCP-Tools: web_search`, imports its sole contract into a supplied `FunctionRegistry`, and returns the client for conversation-lifetime disposal. Both dynamically discovered Copilot models and the plain `copilot` CLI path use this service; existing `WebToolRegistrationPolicy` continues to add Jina tools, with one flag suppressing Jina search after hosted search succeeds.

**Tech Stack:** .NET 9, C#, ModelContextProtocol.Client 1.3.0, existing `McpMiddleware`, `GithubCopilotProvider`, xUnit, FluentAssertions.

## Global Constraints

- Reuse existing files and abstractions; do not create versioned or enhanced variants.
- Do not rewrite the existing `/mcp` or `/mcp/readonly` proxy.
- Select only `web_search` with `X-MCP-Tools: web_search`; do not import the full hosted catalog.
- Copilot MCP failures degrade gracefully and never prevent provider creation.
- Never read, log, copy, or commit the user's `JINA_API_KEY` or Copilot credentials.
- Preserve mode-level `EnabledTools` filtering and the existing provider capability policy.
- Do not commit changes unless the user explicitly requests it.

## File Structure

- Create `samples/LmStreaming.Sample/Services/CopilotWebSearchRegistration.cs` — owns the narrow authenticated MCP connection/import operation and its result type.
- Create `tests/LmStreaming.Sample.Tests/Services/CopilotWebSearchRegistrationTests.cs` — deterministic fake-upstream coverage for discovery, invocation, filtering, failure, and disposal handoff.
- Modify `samples/LmStreaming.Sample/Services/WebToolRegistrationPolicy.cs` — add a `suppressWebSearch` input while leaving `WebFetch` and all existing gates intact.
- Modify `tests/LmStreaming.Sample.Tests/Services/WebToolRegistrationPolicyTests.cs` — prove hosted-search success suppresses only Jina search.
- Modify `samples/LmStreaming.Sample/Program.cs` — wire the helper into dynamically discovered and plain Copilot paths and append clients to owned resources.
- Modify `tests/CopilotAnthropicProxy.Tests/McpProxyTests.cs` — make the MCP control-header proof use the actual `web_search` selector.
- Create `tests/CopilotLive.Tests/CopilotMcpLiveTests.cs` — opt-in proof that the real readonly endpoint exposes exactly `web_search`.
- Modify `samples/CopilotAnthropicProxy.Sample/README.md` — document the verified selector and clarify that no hosted `web_fetch` exists.

---

### Task 1: Narrow Copilot Hosted Search Registration

**Files:**
- Create: `samples/LmStreaming.Sample/Services/CopilotWebSearchRegistration.cs`
- Create: `tests/LmStreaming.Sample.Tests/Services/CopilotWebSearchRegistrationTests.cs`

**Interfaces:**
- Consumes: `FunctionRegistry`, `ICopilotTokenProvider`, `CopilotSessionContext`, `CopilotOptions`, `ILoggerFactory`, `McpClientFunctionProvider`, `HttpClientTransport`.
- Produces:

```csharp
internal sealed record CopilotWebSearchRegistrationResult(
    bool Registered,
    McpClient? Client,
    string Status
);

internal static class CopilotWebSearchRegistration
{
    internal const string ToolName = "web_search";

    public static CopilotWebSearchRegistrationResult TryRegister(
        FunctionRegistry registry,
        IReadOnlyList<string>? enabledTools,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext session,
        CopilotOptions options,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? innerHandler = null
    );
}
```

`innerHandler` is a test seam only. Production passes `null`, allowing `CopilotHttpClientFactory` to use its normal transport.

- [ ] **Step 1: Write failing mode-gate and successful-discovery tests**

Create a fake Streamable-HTTP MCP handler that:

1. records request headers;
2. returns an `initialize` SSE response with `Mcp-Session-Id`;
3. accepts `notifications/initialized`;
4. returns one `tools/list` entry named `web_search` with required `query`;
5. returns a citation-bearing result for `tools/call`.

Add tests equivalent to:

```csharp
[Fact]
public void TryRegister_WhenModeEnablesWebSearch_ImportsBareHostedTool()
{
    var registry = new FunctionRegistry();
    using var upstream = new FakeCopilotMcpHandler();

    var result = CopilotWebSearchRegistration.TryRegister(
        registry,
        enabledTools: ["web_search"],
        new StaticTokenProvider("test-token"),
        new CopilotSessionContext(),
        new CopilotOptions(),
        NullLoggerFactory.Instance,
        upstream
    );

    result.Registered.Should().BeTrue();
    result.Client.Should().NotBeNull();
    registry.Build().Contracts.Select(x => x.Name).Should().ContainSingle().Which.Should().Be("web_search");
    upstream.RequestHeaders.SelectMany(x => x).Should().Contain(x =>
        x.Key.Equals("X-MCP-Tools", StringComparison.OrdinalIgnoreCase)
        && x.Value.Contains("web_search")
    );
}

[Theory]
[InlineData(null, true)]
[InlineData("web_search", true)]
[InlineData("WebSearch", false)]
[InlineData("WebFetch", false)]
public void TryRegister_RespectsExactModeToolName(string? enabledTool, bool expected)
{
    IReadOnlyList<string>? enabled = enabledTool is null ? null : [enabledTool];
    // Arrange the same fake MCP handler, invoke TryRegister, and assert Registered == expected.
}
```

For the `null` case, pass `enabledTools: null`; an empty list must also receive a dedicated assertion returning `Registered == false` without contacting the upstream.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter FullyQualifiedName~CopilotWebSearchRegistrationTests
```

Expected: FAIL because `CopilotWebSearchRegistration` does not exist.

- [ ] **Step 3: Implement the minimal authenticated MCP import**

Implement `TryRegister` with this flow:

```csharp
if (enabledTools is not null && !enabledTools.Contains(ToolName))
{
    return new(false, null, "Copilot web_search disabled by mode");
}

McpClient? client = null;
try
{
    var httpClient = CopilotHttpClientFactory.Create(
        options.BaseUrl,
        tokenProvider,
        session,
        options,
        innerHandler: innerHandler
    );
    var transport = new HttpClientTransport(
        new HttpClientTransportOptions
        {
            Name = "copilot-web-search",
            Endpoint = new Uri(new Uri(options.BaseUrl), "/mcp/readonly"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["X-MCP-Tools"] = ToolName,
            },
        },
        httpClient,
        loggerFactory,
        ownsHttpClient: true
    );

    client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
    var provider = McpClientFunctionProvider.CreateAsync(
        client,
        "copilot-web-search",
        "CopilotWebSearch",
        loggerFactory.CreateLogger<McpClientFunctionProvider>(),
        omitServerPrefix: true
    ).GetAwaiter().GetResult();

    var functions = provider.GetFunctions().ToList();
    if (functions.Count != 1 || !string.Equals(functions[0].Contract.Name, ToolName, StringComparison.Ordinal))
    {
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return new(false, null, "Copilot web_search unavailable");
    }

    _ = registry.AddProvider(provider);
    return new(true, client, "Copilot web_search registered");
}
catch (Exception ex)
{
    if (client is not null)
    {
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    logger.LogWarning(ex, "Copilot hosted web_search is unavailable; using configured fallback");
    return new(false, null, "Copilot web_search unavailable");
}
```

Match the installed MCP package's exact overload names at compile time. Keep the method synchronous because the surrounding agent factory is synchronous and existing MCP connection helpers use the same bounded sync-over-async pattern.

- [ ] **Step 4: Add failure and invocation tests**

Add deterministic tests asserting:

- an upstream initialization exception returns `Registered == false`, `Client == null`, and leaves the registry empty;
- a catalog with zero or more than one tool is rejected and disposed;
- invoking the registered handler with `{"query":"current .NET release"}` sends `tools/call` for `web_search` and returns the fake citation text;
- no captured log or status contains the static bearer token.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Task 1 test command again.

Expected: PASS.

- [ ] **Step 6: Run formatter on touched C# files**

Run:

```powershell
dotnet csharpier format samples/LmStreaming.Sample/Services/CopilotWebSearchRegistration.cs tests/LmStreaming.Sample.Tests/Services/CopilotWebSearchRegistrationTests.cs
```

Expected: formatter exits successfully.

---

### Task 2: Jina Fallback Precedence

**Files:**
- Modify: `samples/LmStreaming.Sample/Services/WebToolRegistrationPolicy.cs:28-143`
- Modify: `tests/LmStreaming.Sample.Tests/Services/WebToolRegistrationPolicyTests.cs:21-307`

**Interfaces:**
- Consumes: Task 1's `CopilotWebSearchRegistrationResult.Registered`.
- Produces: updated `WebToolRegistrationPolicy.Apply(..., bool suppressWebSearch = false)`.

- [ ] **Step 1: Write failing fallback-precedence tests**

Add:

```csharp
[Fact]
public void Apply_WhenHostedSearchRegistered_StillRegistersJinaFetchButSuppressesJinaSearch()
{
    var registry = new FunctionRegistry();
    var (provider, options) = Backend(ApiKey);

    _ = WebToolRegistrationPolicy.Apply(
        registry,
        "claude-sonnet-5",
        enabledTools: ["web_search", "WebFetch", "WebSearch"],
        provider,
        options,
        NullLoggerFactory.Instance,
        isCopilotBackedModel: true,
        suppressWebSearch: true
    );

    RegisteredNames(registry).Should().Contain("WebFetch").And.NotContain("WebSearch");
}

[Fact]
public void Apply_WhenHostedSearchUnavailable_RegistersJinaSearchAndFetch()
{
    // Same arrangement with suppressWebSearch: false; assert both Jina tools exist.
}
```

- [ ] **Step 2: Run the focused policy tests and verify RED**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter FullyQualifiedName~WebToolRegistrationPolicyTests
```

Expected: compile failure because `suppressWebSearch` is absent.

- [ ] **Step 3: Add the single suppression gate**

Extend `Apply` with `bool suppressWebSearch = false`. Change only the search branch:

```csharp
if (suppressWebSearch)
{
    statuses.Add("WebSearch skipped: Copilot web_search registered");
}
else if (string.IsNullOrWhiteSpace(options.JinaApiKey))
{
    // existing missing-key branch
}
else if (ModeEnables(WebSearchTool.ToolName))
{
    // existing registration branch
}
```

Do not alter Jina `WebFetch`, provider eligibility, collision handling, or mode matching.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 test command again.

Expected: PASS, including all pre-existing provider/mode/collision cases.

- [ ] **Step 5: Format the two touched files**

Run:

```powershell
dotnet csharpier format samples/LmStreaming.Sample/Services/WebToolRegistrationPolicy.cs tests/LmStreaming.Sample.Tests/Services/WebToolRegistrationPolicyTests.cs
```

Expected: success.

---

### Task 3: Wire Every Copilot Provider Path and Resource Lifetime

**Files:**
- Modify: `samples/LmStreaming.Sample/Program.cs:985-1019,1024-1250,3045-3147`
- Test: `tests/LmStreaming.Sample.Tests/Services/CopilotWebSearchRegistrationTests.cs`
- Test: existing `tests/LmStreaming.Sample.Tests/Agents/MultiTurnAgentPoolTests.cs` only if an end-to-end disposal assertion needs the pool boundary.

**Interfaces:**
- Consumes: `CopilotWebSearchRegistration.TryRegister` and `WebToolRegistrationPolicy.Apply(... suppressWebSearch)`.
- Produces: hosted search plus Jina fallback for dynamically discovered Copilot models and plain `copilot` CLI conversations.

- [ ] **Step 1: Add a helper-level composition test before changing Program**

Add a test that creates one registry, registers hosted search successfully, then applies Jina policy with `suppressWebSearch: result.Registered`. Assert exact names:

```csharp
names.Should().BeEquivalentTo("web_search", "WebFetch");
```

Repeat with failed hosted MCP and assert:

```csharp
names.Should().BeEquivalentTo("WebSearch", "WebFetch");
```

This is the deterministic RED/GREEN proof of the intended composition without booting the full sample.

- [ ] **Step 2: Run the composition tests**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~CopilotWebSearchRegistrationTests|FullyQualifiedName~WebToolRegistrationPolicyTests"
```

Expected before wiring: helper-level tests PASS. Program behavior remains unimplemented.

- [ ] **Step 3: Wire dynamically discovered Copilot models**

In the middleware-provider path:

1. initialize `ownedResources` as an empty `List<IAsyncDisposable>` instead of nullable;
2. change existing MCP branches from assignment to `ownedResources.AddRange(...)`;
3. after `isCopilotBackedModel` is resolved and before Jina policy runs, call `TryRegister` when true;
4. append `result.Client` when non-null;
5. pass `suppressWebSearch: result.Registered` to `WebToolRegistrationPolicy.Apply`;
6. pass `ownedResources.Count == 0 ? null : ownedResources` into `AgentCreationResult` at the existing return site.

Do not connect Copilot MCP for non-Copilot providers.

- [ ] **Step 4: Wire the plain `copilot` CLI path through its dynamic function bridge**

Before the early return at `Program.cs:985`:

1. clone `functionRegistry` into a conversation-local registry so the singleton is never mutated;
2. call `TryRegister` against the clone using the selected mode;
3. call `WebToolRegistrationPolicy.Apply` with `providerId: "copilot"`, `isCopilotBackedModel: true`, and `suppressWebSearch: hostedResult.Registered` so Jina `WebFetch` and fallback `WebSearch` are available;
4. pass the clone to `CreateCopilotAgentLoop`;
5. return `new AgentCreationResult(loop, hostedResult.Client is null ? null : [hostedResult.Client])`.

Do not add the same hosted endpoint through `extraMcpServers`: the CLI config would require embedding a bearer token that cannot refresh. The dynamic bridge reuses the authenticated in-process MCP client and existing `CopilotToolPolicyEngine`.

- [ ] **Step 5: Preserve exact mode behavior**

Ensure Research Assistant's existing `enabledTools` entries continue to enable:

- hosted `web_search`;
- Jina `WebFetch`;
- Jina `WebSearch` fallback.

Do not add tools to modes whose `EnabledTools` is empty. Keep `Prompts.yaml` unchanged unless a failing test proves the current entries insufficient.

- [ ] **Step 6: Compile and run focused sample tests**

Run:

```powershell
dotnet build samples/LmStreaming.Sample/LmStreaming.Sample.csproj
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~CopilotWebSearchRegistrationTests|FullyQualifiedName~WebToolRegistrationPolicyTests|FullyQualifiedName~MultiTurnAgentPool"
```

Expected: build succeeds with zero new warnings; focused tests pass.

- [ ] **Step 7: Format Program and rerun compile**

Run:

```powershell
dotnet csharpier format samples/LmStreaming.Sample/Program.cs
dotnet build samples/LmStreaming.Sample/LmStreaming.Sample.csproj
```

Expected: success.

---

### Task 4: Proxy Contract and Opt-In Live Proof

**Files:**
- Modify: `tests/CopilotAnthropicProxy.Tests/McpProxyTests.cs:84-113`
- Create: `tests/CopilotLive.Tests/CopilotMcpLiveTests.cs`
- Modify: `samples/CopilotAnthropicProxy.Sample/README.md:228-253,290-338`

**Interfaces:**
- Consumes: real Copilot `/mcp/readonly`, `CopilotLiveFixture`, existing proxy header forwarding.
- Produces: regression proof and operator documentation; no production proxy change.

- [ ] **Step 1: Tighten the deterministic proxy header test**

Change the representative selector in `X_mcp_control_headers_are_forwarded` from:

```csharp
"get_file_contents,search_code"
```

to:

```csharp
"web_search"
```

Keep assertions for `X-MCP-Readonly` and `X-MCP-Host`. This confirms the exact production selector is transparently forwarded by both existing proxy routes without changing proxy code.

- [ ] **Step 2: Run proxy tests**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter FullyQualifiedName~McpProxyTests
```

Expected: PASS.

- [ ] **Step 3: Add the opt-in live catalog test**

Create `CopilotMcpLiveTests` using `IClassFixture<CopilotLiveFixture>`. The test must:

1. skip when `_fixture.Available` is false;
2. create `HttpClient` with `CopilotHttpClientFactory`;
3. POST MCP `initialize` to `/mcp/readonly` with `X-MCP-Tools: web_search`, `Accept: application/json, text/event-stream`, and protocol `2025-06-18`;
4. capture `Mcp-Session-Id` without printing it;
5. POST `notifications/initialized` and `tools/list` with the same session/header set;
6. parse the SSE `data:` JSON;
7. assert exactly one tool named `web_search`, whose schema requires `query`;
8. never emit authorization headers, token values, or session IDs to test output.

Use a 30-second cancellation timeout and `Skip.If` for missing credentials only; protocol or catalog drift must fail visibly.

- [ ] **Step 4: Run the opt-in live proof**

Run:

```powershell
dotnet test tests/CopilotLive.Tests/CopilotLive.Tests.csproj --filter FullyQualifiedName~CopilotMcpLiveTests
```

Expected with valid local Copilot auth: PASS. Without auth: SKIP with the fixture's existing message.

- [ ] **Step 5: Update proxy documentation**

In the README's MCP section, add:

- `X-MCP-Tools: web_search` exposes exactly Copilot's hosted general web-search tool on `/mcp/readonly`;
- its contract takes required `query` and returns citation-bearing output;
- no hosted `web_fetch` appeared in the verified catalog as of 2026-07-30;
- `LmStreaming.Sample` therefore uses Jina for `WebFetch` and as search fallback.

Replace the old “planned, not scheduled” paragraph that says MCP-backed web search is unimplemented; after this change, that statement would be stale for `LmStreaming.Sample`. Keep the proxy's hosted-tool stripping explanation intact because translating `/responses` hosted tools remains out of scope.

- [ ] **Step 6: Format and run the affected tests**

Run:

```powershell
dotnet csharpier format tests/CopilotAnthropicProxy.Tests/McpProxyTests.cs tests/CopilotLive.Tests/CopilotMcpLiveTests.cs
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --filter FullyQualifiedName~McpProxyTests
```

Expected: PASS.

---

### Task 5: Full Verification

**Files:**
- Verify all files listed above.
- Do not modify `.env` or any credential store.

**Interfaces:**
- Consumes: completed Tasks 1-4.
- Produces: build/test evidence suitable for completion reporting.

- [ ] **Step 1: Verify secret files remain untouched**

Run:

```powershell
git status --short
git diff -- .env samples/LmStreaming.Sample/.env
```

Expected: no `.env` diff; only intended source/test/doc/spec/plan files plus the pre-existing `.claude/scratchpad/` entry.

- [ ] **Step 2: Run all directly affected test projects**

Run:

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --logger "trx;LogFileName=copilot-jina-web-tools.trx" --results-directory .logs/test-results
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --logger "trx;LogFileName=copilot-mcp-proxy.trx" --results-directory .logs/test-results
```

Expected: both projects PASS with no skipped deterministic tests.

- [ ] **Step 3: Run solution build**

Run:

```powershell
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
```

Expected: success with no new warnings.

- [ ] **Step 4: Run the opt-in live MCP proof once more**

Run:

```powershell
dotnet test tests/CopilotLive.Tests/CopilotLive.Tests.csproj --filter FullyQualifiedName~CopilotMcpLiveTests --logger "trx;LogFileName=copilot-mcp-live.trx" --results-directory .logs/test-results
```

Expected on this authorized development machine: PASS and exactly one discovered `web_search` contract.

- [ ] **Step 5: Review the final diff**

Run:

```powershell
git diff --check
git diff --stat
git status --short
```

Expected: no whitespace errors, no generated binaries/logs/secrets, and no unrelated project changes.

- [ ] **Step 6: Report completion without committing**

Report:

- proxy behavior verified rather than rewritten;
- hosted `web_search` registration paths covered;
- Jina `WebFetch` and fallback behavior covered;
- deterministic and live test outcomes;
- any skipped live test or failure verbatim.

Do not create a commit unless the user explicitly asks.
