# AchieveAi.LmDotnetTools.Sandbox

Typed .NET SDK for the sandbox gateway's control plane. Targets `net8.0`/`net9.0`, has no
ASP.NET or LLM-provider dependencies, and owns only gateway protocol/local transport concerns —
spawning/adopting the gateway, host-path resolution, session caching, credential selection, and
OAuth/network/discovery policy remain the caller's responsibility.

## What this release covers

- **Lifecycle:** `CreateAsync`, `GetAsync`, `ListAsync`, `DeleteAsync` — explicit sandbox
  create/get/list/delete. Disposing a `SandboxClient` never deletes a sandbox.
  `ListAsync` reads the gateway's Docker-level container inventory (`GET /api/v1/sandboxes`), a
  different wire shape from `CreateAsync`/`GetAsync` — it carries no workspace/volume info, so every
  returned `SandboxInfo.WorkspaceContainerPath` is `null`, and an entry the gateway hasn't attributed
  to any session is omitted (it cannot be represented without a session id).
- **Marketplace preview:** `PreviewMarketplacesAsync` — a read-only browse of plugins, skills, and
  agents that requires no sandbox session.
- **Session discovery:** `ListDiscoveredAsync(sessionId)` — a narrow read over the existing
  session-discovery REST endpoint.

Command execution and exact-byte file transfer are later SDK capabilities and are not part of this
release.

## Usage

```csharp
using AchieveAi.LmDotnetTools.Sandbox;

var options = new SandboxClientOptions(
    serverAddress: new Uri("https://sandbox.internal:3443"),
    appId: "my-app",
    clientSecret: myBase64Secret,
    executionTimeout: TimeSpan.FromMinutes(10),
    transportTimeout: TimeSpan.FromSeconds(30));

using var client = new SandboxClient(options); // owns its HttpClient

var sandbox = await client.CreateAsync(new SandboxCreateRequest(workspace: "my-workspace"));
try
{
    var catalog = await client.PreviewMarketplacesAsync();
    var discovered = await client.ListDiscoveredAsync(sandbox.SessionId);
}
finally
{
    await client.DeleteAsync(sandbox.SessionId); // explicit teardown — never implicit on dispose
}
```

To share a caller-managed `HttpClient` (e.g. from `IHttpClientFactory`) instead of letting the
client own its own transport, use the two-argument constructor: `new SandboxClient(options,
httpClient)`. The SDK never mutates a borrowed client's `DefaultRequestHeaders` or `Timeout`. Every
request (REST, MCP, and the internal `/health` probe) is resolved as an absolute URI against the
constructor-validated `SandboxClientOptions.ServerAddress` — the borrowed client's own
`HttpClient.BaseAddress` is never consulted, so a `null` or mismatched `BaseAddress` on the borrowed
client can neither break requests nor redirect credentials to the wrong host.

> **Security precondition for a borrowed `HttpClient`:** configure its handler with
> `AllowAutoRedirect = false`. This SDK authenticates with custom `X-Sbx-App-Id`/`X-Sbx-App-Key`
> headers, and .NET's automatic-redirect logic only strips the standard `Authorization` header on a
> cross-origin redirect — it re-sends every custom header (including these credential headers) to the
> redirect target. If the borrowed handler follows a `3xx` internally it does so *before* the SDK
> ever sees a response, so the SDK cannot observe or prevent that replay: the leak is only
> preventable by the caller disabling auto-redirect. The owned-transport constructor (`new
> SandboxClient(options)`) disables auto-redirect for you. As defense in depth, any `3xx` the SDK
> *does* observe is rejected as `Protocol` rather than followed — the SDK never chases a redirect
> itself.

## Errors

Every gateway/transport failure other than caller cancellation raises `SandboxException`, which
carries a stable `SandboxErrorKind` (`Authorization`, `NotFound`, `TransportTimeout`, `Protocol`,
plus `ExecutionTimeout`/`Integrity` reserved for later command/file capabilities). Caller
cancellation always surfaces as a plain `OperationCanceledException`. `Protocol` covers every
malformed-response case, and the SDK never lets one surface as a raw
`ArgumentException`/`NullReferenceException`/`InvalidOperationException`:

- A 2xx REST body that is well-formed JSON but semantically invalid — a missing/`null` required field
  (e.g. a marketplace alias or discovered-item kind/name/path) or a `null` collection element in any
  lifecycle/catalog/discovery list.
- A 2xx MCP reply that is not a complete JSON-RPC 2.0 envelope — a non-object root, a missing/wrong
  `jsonrpc`, an `id` that does not match the request, both or neither of `result`/`error`, or a
  non-object `error`. A JSON-RPC `error` envelope is likewise `Protocol`.
- An observed `3xx` redirect (which the SDK refuses rather than follows).

## Security

- `SandboxClientOptions.ClientSecret` is validated at construction and never appears in `ToString()`,
  exception messages, logs, or URLs.
- A non-loopback `ServerAddress` must use HTTPS unless `AllowInsecureDevelopmentTransport` is
  explicitly set for local development.
- The owned transport disables automatic redirects; a borrowed `HttpClient` must do the same
  (`AllowAutoRedirect = false`) — see the borrowed-client note above. The SDK never follows a
  redirect itself and rejects any `3xx` it observes.
