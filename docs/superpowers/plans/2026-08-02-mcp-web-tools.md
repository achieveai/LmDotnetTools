# MCP Web Tools Implementation Plan

**Status:** Implemented — shipped in `cc21abc9`. See `samples/CopilotAnthropicProxy.Sample/Mcp/`: `McpToolSnapshotStore.cs`, `McpJinaToolCatalog.cs`, `McpToolComposition.cs`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compose GitHub-first `web_search` and `web_fetch` tools into the existing `/mcp` endpoints, with local Jina fallback tools when a valid `JINA_API_KEY` is configured.

**Architecture:** Keep the existing MCP byte relay as the default. Add a narrow JSON-RPC interceptor for single-object `tools/list` and locally-owned `tools/call` requests, plus a small process-local snapshot store keyed by endpoint and GitHub session. Reuse the existing Jina provider, web-tool handlers, validation, sanitization, and configuration from `src/Misc`.

**Tech Stack:** .NET 9, ASP.NET Core minimal hosting, `System.Text.Json.Nodes`, MCP Streamable HTTP over JSON-RPC 2.0, existing `AchieveAi.LmDotnetTools.Misc` Jina tools, xUnit, FluentAssertions, `WebApplicationFactory<Program>`.

## Global Constraints

- Change only `/mcp` and `/mcp/readonly`; do not change Messages or Responses translation.
- GitHub's exact-name tool definition wins independently for `web_search` and `web_fetch`.
- Inject a local Jina fallback only when GitHub omits the exact name and `JINA_API_KEY` is nonblank with valid `WebToolsOptions`.
- Do not fail over during a tool call.
- Preserve GitHub `tools/list` transport, HTTP, and JSON-RPC failures.
- Compose only single-page `application/json` catalogs tied to an `Mcp-Session-Id`.
- Pass SSE-framed catalogs, paginated catalogs, and JSON-RPC batch arrays through unchanged.
- Scope local routing by endpoint, session ID, and the `X-MCP-*` header provenance from the advertised catalog.
- Preserve existing secret hygiene, SSRF protection, output limits, cancellation, and transparent MCP behavior outside the interceptor.
- Do not use `LegacyHandlerAdapter`; it loses `IsError` and cancellation.
- Do not create a second Jina implementation.
- Do not commit unless the user explicitly requests commits.

---

## File map

### Create

- `samples/CopilotAnthropicProxy.Sample/Mcp/McpToolSnapshotStore.cs` — owns endpoint/session/header-scoped local-tool routing snapshots.
- `samples/CopilotAnthropicProxy.Sample/Mcp/McpJinaToolCatalog.cs` — adapts existing Jina tool contracts and handlers to snake_case MCP definitions.
- `samples/CopilotAnthropicProxy.Sample/Mcp/McpToolComposition.cs` — parses the two intercepted JSON-RPC methods, composes JSON catalogs, executes local calls, and emits MCP result/error envelopes.
- `tests/CopilotAnthropicProxy.Tests/McpToolSnapshotStoreTests.cs` — snapshot and header-fingerprint unit tests.
- `tests/CopilotAnthropicProxy.Tests/McpJinaToolCatalogTests.cs` — key/config gating and schema tests.
- `tests/CopilotAnthropicProxy.Tests/McpToolCompositionTests.cs` — endpoint-level catalog, routing, transport, lifecycle, and security tests.

### Modify

- `samples/CopilotAnthropicProxy.Sample/CopilotAnthropicProxy.Sample.csproj:11-16` — reference `Misc` and correct the stale dependency comment.
- `samples/CopilotAnthropicProxy.Sample/Program.cs:100-150` — register web options, Jina provider/catalog, snapshot store, and composition service.
- `samples/CopilotAnthropicProxy.Sample/Program.cs:2452-2680` — capture the POST body once, delegate targeted requests, preserve default relay, and remove snapshots on DELETE/upstream 404.
- `tests/CopilotAnthropicProxy.Tests/ProxyWebAppFactory.cs:23-131` — configure Jina environment and inject a separate fake Jina HTTP client.
- `tests/CopilotAnthropicProxy.Tests/McpProxyTests.cs` — retain and extend transparent-relay regressions where the interception seam touches existing behavior.
- `samples/CopilotAnthropicProxy.Sample/README.md:236-253` — document targeted composition instead of claiming total JSON-RPC blindness.
- `samples/CopilotAnthropicProxy.Sample/README.md` environment table — document existing Jina/web-tool variables used by this host.

---

### Task 1: Add the `Misc` dependency without changing behavior

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/CopilotAnthropicProxy.Sample.csproj:11-16`

**Interfaces:**
- Consumes: `AchieveAi.LmDotnetTools.Misc.Configuration.WebToolsOptions`, `Misc.Web.Jina.JinaWebProvider`, `Misc.Utils.WebSearchTool`, and `Misc.Utils.WebFetchTool` in later tasks.
- Produces: a buildable proxy project with access to the existing web-tool implementation.

- [ ] **Step 1: Record the clean baseline**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore
```

Expected: the existing proxy suite passes before the project reference changes.

- [ ] **Step 2: Add the project reference**

Change the project-reference group to:

```xml
<ItemGroup>
  <!-- Copilot transport and authentication live in GithubCopilotProvider. Web-tool
       contracts, validation, output hygiene, and Jina integration live in Misc. -->
  <ProjectReference Include="..\..\src\GithubCopilotProvider\AchieveAi.LmDotnetTools.GithubCopilotProvider.csproj" />
  <ProjectReference Include="..\..\src\Misc\AchieveAi.LmDotnetTools.Misc.csproj" />
</ItemGroup>
```

- [ ] **Step 3: Prove the dependency-only change is green**

Run:

```powershell
dotnet build samples/CopilotAnthropicProxy.Sample/CopilotAnthropicProxy.Sample.csproj --no-restore
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore
```

Expected: build succeeds with zero warnings and the existing suite remains green.

- [ ] **Step 4: Review checkpoint**

Run `git diff --check` and inspect only the `.csproj` diff. Do not commit unless explicitly authorized.

---

### Task 2: Add snapshot routing keyed by endpoint, session, and MCP headers

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Mcp/McpToolSnapshotStore.cs`
- Create: `tests/CopilotAnthropicProxy.Tests/McpToolSnapshotStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed record McpToolSnapshot(
    IReadOnlySet<string> LocalToolNames,
    string HeaderFingerprint
);

internal sealed class McpToolSnapshotStore
{
    public void Set(string endpointPath, string sessionId, McpToolSnapshot snapshot);
    public bool TryGet(string endpointPath, string sessionId, out McpToolSnapshot snapshot);
    public void Remove(string endpointPath, string sessionId);
    public static string ComputeHeaderFingerprint(IHeaderDictionary headers);
    public static bool HasToolFilterHeaders(IHeaderDictionary headers);
}
```

- Consumes: request headers from `HttpContext.Request.Headers`.
- Later tasks consume: `Set`, `TryGet`, `Remove`, and the fingerprint helpers.

- [ ] **Step 1: Write failing fingerprint and isolation tests**

Create tests with these exact cases:

```csharp
[Fact]
public void Fingerprint_is_case_and_order_independent()
{
    var first = new HeaderDictionary
    {
        ["X-MCP-Tools"] = "web_search,issues",
        ["X-MCP-Readonly"] = "true",
    };
    var second = new HeaderDictionary
    {
        ["x-mcp-readonly"] = "true",
        ["x-mcp-tools"] = "web_search,issues",
    };

    McpToolSnapshotStore.ComputeHeaderFingerprint(first)
        .Should().Be(McpToolSnapshotStore.ComputeHeaderFingerprint(second));
}

[Fact]
public void Same_session_is_isolated_by_endpoint()
{
    var store = new McpToolSnapshotStore();
    store.Set("/mcp", "session-1", new(new HashSet<string> { "web_search" }, "none"));

    store.TryGet("/mcp/readonly", "session-1", out _).Should().BeFalse();
}
```

Also test:

- changing any `X-MCP-*` value changes the fingerprint;
- non-`X-MCP-*` headers do not affect it;
- `HasToolFilterHeaders` is false with no `X-MCP-*` headers and true with one;
- a second `Set` atomically replaces local names;
- `Remove` clears only the matching endpoint/session entry.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolSnapshotStoreTests"
```

Expected: compile failure because `McpToolSnapshotStore` does not exist.

- [ ] **Step 3: Implement the minimal store**

Use a private `ConcurrentDictionary<(string EndpointPath, string SessionId), McpToolSnapshot>`.

For the fingerprint:

1. select every header whose name starts with `X-MCP-`, case-insensitively;
2. normalize the header name to lower invariant;
3. preserve each header's value sequence and join it with ``;
4. order entries by normalized name using `StringComparer.Ordinal`;
5. serialize as `name=value\n` and hash with SHA-256;
6. return lowercase hexadecimal;
7. return a stable sentinel such as `"none"` when no `X-MCP-*` header exists.

Do not curate a subset of `X-MCP-*` headers. Over-inclusion safely routes a mismatch to GitHub instead of incorrectly invoking Jina.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 test command again.

Expected: all snapshot-store tests pass.

- [ ] **Step 5: Run formatting and review checks**

Run:

```powershell
dotnet csharpier check samples/CopilotAnthropicProxy.Sample/Mcp/McpToolSnapshotStore.cs tests/CopilotAnthropicProxy.Tests/McpToolSnapshotStoreTests.cs
git diff --check
```

If CSharpier reports changes needed, run `dotnet csharpier format` on these two files and rerun the tests. Do not commit unless authorized.

---

### Task 3: Adapt existing Jina handlers into MCP definitions

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Mcp/McpJinaToolCatalog.cs`
- Create: `tests/CopilotAnthropicProxy.Tests/McpJinaToolCatalogTests.cs`

**Interfaces:**
- Consumes: `WebToolsOptions`, `JinaWebProvider`, `WebSearchTool`, `WebFetchTool`, `FunctionContract.GetJsonSchema()`, and `ToolHandler`.
- Produces:

```csharp
internal sealed record McpLocalTool(
    string Name,
    JsonObject Definition,
    ToolHandler Handler
);

internal sealed class McpJinaToolCatalog
{
    public McpJinaToolCatalog(
        JinaWebProvider provider,
        WebToolsOptions options,
        ILoggerFactory loggerFactory
    );

    public bool IsEnabled { get; }
    public IReadOnlyDictionary<string, McpLocalTool> Tools { get; }
}
```

- `Definition` has MCP fields `name`, `description`, and `inputSchema`.
- Later tasks consume `IsEnabled` and `Tools`.

- [ ] **Step 1: Write failing catalog tests**

Cover these exact rules:

```csharp
[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("   ")]
public void Blank_key_disables_both_tools(string? key)
{
    var options = new WebToolsOptions { JinaApiKey = key };
    var catalog = CreateCatalog(options);

    catalog.IsEnabled.Should().BeFalse();
    catalog.Tools.Should().BeEmpty();
}

[Fact]
public void Valid_key_exposes_snake_case_tools_with_existing_contract_schemas()
{
    var options = new WebToolsOptions { JinaApiKey = "secret-key" };
    var catalog = CreateCatalog(options);

    catalog.Tools.Keys.Should().BeEquivalentTo("web_search", "web_fetch");
    catalog.Tools["web_search"].Definition["inputSchema"]!["properties"]!["query"]
        .Should().NotBeNull();
    catalog.Tools["web_fetch"].Definition["inputSchema"]!["properties"]!["url"]
        .Should().NotBeNull();
}
```

Also assert:

- invalid `Backend`, nonpositive `OutputCap`, or nonpositive `TimeoutMs` disables both tools;
- required arrays contain `query` and `url` respectively;
- optional search properties are `count`, `country`, and `language`;
- optional fetch properties are `targetSelector` and `noCache`;
- definitions do not contain the Jina key;
- each MCP definition uses its snake_case name while the handler remains the existing `ToolHandler`.

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpJinaToolCatalogTests"
```

Expected: compile failure because the catalog does not exist.

- [ ] **Step 3: Implement catalog construction**

Implement these rules:

```csharp
IsEnabled = !string.IsNullOrWhiteSpace(options.JinaApiKey)
    && options.Validate().Count == 0;
```

When enabled:

1. create `WebSearchTool` and `WebFetchTool` using the same `JinaWebProvider` and options;
2. convert each `FunctionContract.GetJsonSchema()` to a `JsonObject` by serializing with the repository's normal `JsonSerializer` options and parsing as `JsonNode`;
3. build definitions with snake_case names but preserve existing descriptions and schemas;
4. retain each modern `ToolHandler` directly.

When disabled, expose an empty read-only dictionary. Do not attempt keyless `web_fetch` on this MCP surface.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 3 test command again.

Expected: all catalog tests pass.

- [ ] **Step 5: Run existing Misc security tests**

Run:

```powershell
dotnet test tests/Misc.Tests/Misc.Tests.csproj --no-restore --filter "FullyQualifiedName~JinaSecretHygieneTests|FullyQualifiedName~WebInputValidatorTests|FullyQualifiedName~WebToolOutputTests"
```

Expected: existing Jina secret, SSRF, and output-hygiene tests remain green.

---

### Task 4: Compose `tools/list` while preserving unsupported transport shapes

**Files:**
- Create: `samples/CopilotAnthropicProxy.Sample/Mcp/McpToolComposition.cs`
- Create: `tests/CopilotAnthropicProxy.Tests/McpToolCompositionTests.cs`
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs:2452-2680`
- Modify: `tests/CopilotAnthropicProxy.Tests/ProxyWebAppFactory.cs:23-131`

**Interfaces:**
- Consumes: `McpJinaToolCatalog`, `McpToolSnapshotStore`, the authenticated Copilot `HttpClient`, existing request-header filtering, response-header copying, and body relay.
- Produces:

```csharp
internal sealed class McpToolComposition
{
    public McpToolComposition(
        McpJinaToolCatalog localCatalog,
        McpToolSnapshotStore snapshots,
        ILoggerFactory loggerFactory
    );

    public static bool TryParseSingleRequest(ReadOnlySpan<byte> body, out JsonObject? request);
    public static bool IsTargetMethod(JsonObject request);

    public Task<bool> TryHandleAsync(
        HttpContext context,
        JsonObject request,
        byte[] rawBody,
        HttpClient copilotClient,
        TimeSpan idleTimeout,
        TimeSpan keepAliveInterval
    );
}
```

`TryHandleAsync` returns `true` after writing the downstream response; `false` means `ProxyMcp` must forward the original raw body through its existing path.

- [ ] **Step 1: Extend the test host for a separate Jina client**

Add optional constructor parameters to `ProxyWebAppFactory`:

```csharp
string? jinaApiKey = null,
int? webToolsOutputCap = null,
int? webToolsTimeoutMs = null,
Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? jinaUpstream = null
```

Set and clear `JINA_API_KEY`, `WEB_TOOLS_OUTPUT_CAP`, and `WEB_TOOLS_TIMEOUT_MS` just like existing proxy environment variables.

In `ConfigureTestServices`, replace `JinaWebProvider` only when `jinaUpstream` is supplied:

```csharp
services.RemoveAll<JinaWebProvider>();
services.AddSingleton(sp => new JinaWebProvider(
    new HttpClient(new FakeHttpMessageHandler(jinaUpstream)),
    sp.GetRequiredService<WebToolsOptions>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<JinaWebProvider>()
));
```

Do not reuse the fake Copilot handler for Jina.

- [ ] **Step 2: Write failing `tools/list` endpoint tests**

Create helpers that send JSON-RPC requests with an explicit `Mcp-Session-Id`. Add these tests:

- `Github_web_tool_definitions_win_and_remain_structurally_equal`
- `Missing_web_tools_are_injected_with_valid_jina_key`
- `Mixed_catalog_can_use_github_search_and_jina_fetch`
- `Missing_or_invalid_jina_configuration_injects_nothing`
- `Github_tools_list_json_rpc_error_passes_through_unchanged`
- `Github_tools_list_http_error_passes_through_unchanged`
- `Sessionless_catalog_injects_nothing`
- `Paginated_catalog_with_nextCursor_passes_through_unchanged`
- `Sse_catalog_passes_through_unchanged`
- `Json_rpc_batch_array_passes_through_unchanged`

For structural preservation, parse the original GitHub tool and the downstream tool and assert `JsonNode.DeepEquals`.

For passthrough tests, assert status, content type, and body are unchanged. For the SSE case, return `TestUpstream.SseStream(...)` and ensure no Jina tool is added and existing streaming still completes.

- [ ] **Step 3: Run list tests and verify RED**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolCompositionTests"
```

Expected: list-composition tests fail because interception is not implemented.

- [ ] **Step 4: Register web services in `Program.cs`**

Near the existing service registrations, add process-lifetime services:

```csharp
var webToolsOptions = WebToolsOptions.FromEnvironment();
services.AddSingleton(webToolsOptions);
services.AddSingleton(sp => new JinaWebProvider(
    webToolsOptions,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<JinaWebProvider>()
));
services.AddSingleton<McpJinaToolCatalog>();
services.AddSingleton<McpToolSnapshotStore>();
services.AddSingleton<McpToolComposition>();
```

Log one bounded warning when a nonblank key is present but options are invalid. Do not log option values or the key. Blank key is normal and should not warn.

- [ ] **Step 5: Add the narrow interception seam in `ProxyMcp`**

Refactor POST buffering so `inboundBody` remains available after reading. Before constructing/sending the normal upstream request:

```csharp
if (
    inboundBody is not null
    && McpToolComposition.TryParseSingleRequest(inboundBody, out var rpcRequest)
    && rpcRequest is not null
    && McpToolComposition.IsTargetMethod(rpcRequest)
)
{
    var composition = services.GetRequiredService<McpToolComposition>();
    if (
        await composition.TryHandleAsync(
            ctx,
            rpcRequest,
            inboundBody,
            httpClient,
            idleTimeout,
            keepAliveInterval
        )
    )
    {
        return;
    }
}
```

Expose focused `internal` helpers from `ProxyMcp` or `ProxyHttp` rather than duplicating policy:

```csharp
internal static void ApplyRequestHeaderAllowlist(...)
internal static Task WriteMcpErrorAsync(...)
```

Use the existing `ProxyHttp.CopyResponseHeaders` and `ProxyHttp.CopyBodyAsync` for unchanged upstream responses.

- [ ] **Step 6: Implement the list branch minimally**

The list branch must:

1. send the original raw request once to GitHub using the same authenticated client and request-header policy;
2. preserve non-success HTTP responses and JSON-RPC errors unchanged;
3. inspect `Content-Type` before reading the body;
4. relay SSE unchanged and record no snapshot;
5. buffer only an `application/json` list response;
6. require `result.tools` to be an array, `Mcp-Session-Id` to be nonblank, and `result.nextCursor` to be absent/null/empty;
7. preserve all existing JSON nodes and append only missing Jina definitions;
8. atomically store the injected names plus header fingerprint;
9. serialize the modified root as JSON with the original request ID untouched.

If any shape guard fails, return the original buffered bytes unchanged and remove any previous snapshot for that endpoint/session so stale local ownership is not retained.

- [ ] **Step 7: Run list tests and verify GREEN**

Run the Task 4 focused command again.

Expected: all list composition and passthrough tests pass.

- [ ] **Step 8: Run the original MCP regression suite**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpProxyTests|FullyQualifiedName~McpToolCompositionTests"
```

Expected: existing raw forwarding, headers, SSE, DELETE, and errors remain green alongside composition.

---

### Task 5: Route locally advertised `tools/call` requests

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Mcp/McpToolComposition.cs`
- Modify: `tests/CopilotAnthropicProxy.Tests/McpToolCompositionTests.cs`

**Interfaces:**
- Consumes: latest `McpToolSnapshot`, local `McpLocalTool.Handler`, JSON-RPC `params.name` and `params.arguments`.
- Produces MCP call results with this exact shape:

```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": {
    "content": [{ "type": "text", "text": "..." }],
    "isError": false
  }
}
```

For `ToolHandlerResult.Deferred`, return a resolved MCP error result because this endpoint has no deferred-resolution channel:

```json
{
  "content": [{ "type": "text", "text": "Deferred tool execution is not supported by this MCP endpoint." }],
  "isError": true
}
```

- [ ] **Step 1: Write failing call-routing tests**

Add exact tests:

- `Locally_injected_search_call_uses_jina_and_preserves_request_id`
- `Locally_injected_fetch_call_uses_jina`
- `Github_owned_call_is_forwarded_and_never_hits_jina`
- `Github_call_failure_does_not_fall_back_to_jina`
- `A_second_successful_list_can_change_ownership`
- `Matching_filter_headers_allow_local_dispatch`
- `No_filter_headers_allow_latest_snapshot_dispatch`
- `Mismatched_filter_headers_forward_to_github`
- `No_matching_snapshot_forwards_to_github`
- `Local_validation_failure_sets_isError_true`
- `Client_cancellation_reaches_local_handler`
- `Local_fetch_still_blocks_loopback_and_private_targets`
- `Local_output_respects_WEB_TOOLS_OUTPUT_CAP`
- `Local_errors_and_logs_do_not_contain_JINA_API_KEY_or_raw_jina_body`

Use the real `JinaWebProvider` with a fake Jina HTTP handler. Count GitHub and Jina requests separately to prove routing and no mid-call fallback.

- [ ] **Step 2: Run call tests and verify RED**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolCompositionTests"
```

Expected: call-routing tests fail because local dispatch is not implemented.

- [ ] **Step 3: Implement local dispatch**

Parse conservatively:

```csharp
var name = request["params"]?["name"]?.GetValue<string>();
var arguments = request["params"]?["arguments"];
```

Route locally only when all are true:

- endpoint and `Mcp-Session-Id` match a snapshot;
- snapshot contains the exact name;
- either the call has no `X-MCP-*` headers, or its fingerprint matches the snapshot;
- the local catalog still contains the name.

Otherwise return `false` so the existing relay sends the raw request to GitHub.

Invoke the modern handler:

```csharp
var handlerResult = await localTool.Handler(
    arguments?.ToJsonString() ?? "{}",
    new ToolCallContext { ToolCallId = request["id"]?.ToJsonString() },
    context.RequestAborted
);
```

Pattern-match `ToolHandlerResult.Resolved` and `Deferred`; do not read `ResultText` blindly. Build `result.content` from `Payload.Text`, propagate `Payload.IsError`, and retain `Payload.ErrorCode` only if the MCP result shape has an agreed extension point—otherwise omit it rather than inventing a wire field.

Malformed intercepted local calls return JSON-RPC `-32602` with the original `id`. Unknown or GitHub-owned tool names pass through.

- [ ] **Step 4: Run call tests and verify GREEN**

Run the Task 5 focused command again.

Expected: all call-routing, error, cancellation, SSRF, cap, and hygiene tests pass.

- [ ] **Step 5: Re-run existing Jina security tests**

Run the Task 3 Misc security command again.

Expected: existing security tests and new MCP end-to-end security tests are green.

---

### Task 6: Clean snapshots on DELETE and expired GitHub sessions

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/Program.cs:2501-2635`
- Modify: `tests/CopilotAnthropicProxy.Tests/McpToolCompositionTests.cs`

**Interfaces:**
- Consumes: `McpToolSnapshotStore.Remove(endpointPath, sessionId)`.
- Produces: cleanup after forwarded DELETE and any upstream 404 for a session-bound request.

- [ ] **Step 1: Write failing lifecycle tests**

Add:

- `Delete_removes_snapshot_even_when_github_returns_405`
- `Upstream_404_removes_snapshot`
- `Cleanup_for_mcp_does_not_remove_readonly_snapshot_with_same_session_id`

Test cleanup behavior by:

1. composing a local tool snapshot;
2. sending DELETE or a request that receives 404;
3. sending a matching `tools/call` afterward;
4. asserting it forwards to GitHub rather than Jina.

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run:

```powershell
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolCompositionTests"
```

Expected: lifecycle tests fail because snapshots survive.

- [ ] **Step 3: Add cleanup to the common relay path**

After receiving the upstream status, and before returning from `ForwardAsync`, remove the matching snapshot when:

```csharp
requestMethodIsDelete || upstream.StatusCode == HttpStatusCode.NotFound
```

Use the request endpoint path and inbound `Mcp-Session-Id`. Cleanup must run even when DELETE returns 405, matching the approved spec.

Ensure the composition-owned GitHub `tools/list` round-trip applies the same 404 cleanup rule before it returns.

- [ ] **Step 4: Run lifecycle tests and verify GREEN**

Run the Task 6 focused command again.

Expected: all lifecycle and endpoint-isolation tests pass.

---

### Task 7: Document behavior and run final verification

**Files:**
- Modify: `samples/CopilotAnthropicProxy.Sample/README.md:236-253`
- Modify: `samples/CopilotAnthropicProxy.Sample/README.md` environment/configuration section
- Modify: `tests/CopilotAnthropicProxy.Tests/McpProxyTests.cs` only if a final regression gap is found

**Interfaces:**
- Produces user-facing documentation for configuration, availability rules, transport limitations, and the optional live smoke test.

- [ ] **Step 1: Update the MCP documentation**

Replace the claim that the endpoint performs no JSON-RPC parsing with a precise statement:

- most MCP traffic remains a transparent relay;
- single-page JSON `tools/list` may gain Jina fallbacks;
- locally advertised `tools/call` requests execute locally;
- GitHub exact-name definitions win;
- SSE/paginated catalogs and batches pass through unchanged;
- routing is tied to endpoint/session/filter context.

- [ ] **Step 2: Document configuration**

Document:

- `JINA_API_KEY` enables both local MCP fallbacks;
- `WEB_TOOLS_BACKEND` must remain `jina`;
- `WEB_TOOLS_OUTPUT_CAP` defaults to `50000`;
- `WEB_TOOLS_TIMEOUT_MS` defaults to `30000`;
- invalid configuration disables only local fallbacks and leaves GitHub MCP available.

Do not claim a `WEB_TOOLS_MAX_QUERY_LENGTH` environment variable; none exists.

- [ ] **Step 3: Document the conditional live probe**

Provide a short manual checklist rather than embedding credentials or adding a permanently skipped test:

1. start the proxy with GitHub credentials and optional `JINA_API_KEY`;
2. send `initialize` and retain `Mcp-Session-Id`;
3. send `tools/list`;
4. verify each exact name appears at most once;
5. call each advertised web tool with arguments matching its advertised schema;
6. confirm GitHub wins when present and Jina fills only missing names.

- [ ] **Step 4: Run focused and full verification**

Run:

```powershell
dotnet csharpier check samples/CopilotAnthropicProxy.Sample tests/CopilotAnthropicProxy.Tests
dotnet build samples/CopilotAnthropicProxy.Sample/CopilotAnthropicProxy.Sample.csproj --no-restore
dotnet test tests/CopilotAnthropicProxy.Tests/CopilotAnthropicProxy.Tests.csproj --no-restore
dotnet test tests/Misc.Tests/Misc.Tests.csproj --no-restore --filter "FullyQualifiedName~JinaSecretHygieneTests|FullyQualifiedName~WebInputValidatorTests|FullyQualifiedName~WebToolOutputTests|FullyQualifiedName~WebSearchToolTests|FullyQualifiedName~WebFetchToolTests"
git diff --check
git status --short
```

Expected:

- formatting check passes;
- builds have zero warnings and errors;
- all proxy tests pass;
- all focused Misc web-tool/security tests pass;
- `git diff --check` emits no output;
- status lists only intended source, test, documentation, spec, plan, and conversation scratchpad changes.

- [ ] **Step 5: Final proof summary**

Report:

- exact tests and counts;
- whether the live authenticated probe ran or was skipped;
- which backend owned each tool in the probe;
- any unsupported upstream shape observed;
- no completion claim if any required test is failing.

Do not commit unless explicitly authorized.
