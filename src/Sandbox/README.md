# AchieveAi.LmDotnetTools.Sandbox

Typed .NET SDK for the sandbox gateway's control plane. Targets `net8.0`/`net9.0`, has no
ASP.NET or LLM-provider dependencies, and owns only gateway protocol/local transport concerns —
spawning/adopting the gateway, host-path resolution, session caching, credential selection, and
OAuth/network/discovery policy remain the caller's responsibility.

> **All programmatic sandbox gateway access must go through this SDK** — see
> [ADR 0001](../../docs/adrs/0001-route-gateway-access-through-sandbox-sdk.md). Do not open a raw
> `HttpClient`/MCP connection to a gateway endpoint; the SDK is the single audited place that
> enforces authentication, transport hardening, credential-replay protection, and the typed error
> taxonomy.

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
- **Command execution:** `ExecuteAsync(sessionId, command)` — run a non-interactive native command
  (an executable plus argv, no shell) in the sandbox via the gateway's direct operations API and get
  its exact captured output back. See [Command execution](#command-execution) below.
- **Exact file transfer:** `ReadTextFileAsync`, `WriteTextFileAsync`, `ListDirectoryAsync` — exact,
  integrity-verified UTF-8 file round-trips and directory listing over a workspace-relative POSIX
  path. See [File transfer](#file-transfer) below.

## File transfer

All three take a workspace-relative POSIX `path`, validated exactly like a command's
`WorkingDirectory` (rooted, drive/UNC/device-qualified, backslash-bearing, `..`-escaping, or
NUL-bearing values are rejected); the gateway remains authoritative for symlink containment. The
gateway owns byte-exactness and atomicity — the SDK speaks the direct files/directories REST API
(ADR 0031 / issue #119) and does no chunking, digest reassembly, or temp-file bookkeeping of its own.

- **`ReadTextFileAsync(sessionId, path)`** — a single
  `GET .../files/{mount_id}?path=...` that returns the file's exact current bytes as
  `application/octet-stream`, decoded as **strict UTF-8**. There is nothing to reassemble or re-verify;
  the SDK returns EXACTLY those bytes decoded. Bytes that are not well-formed UTF-8 fail
  `SandboxErrorKind.Integrity` rather than being replacement-substituted. As a defensive bound the SDK
  refuses a response whose declared `Content-Length` exceeds a 64&#160;MiB cap before buffering it.
- **`WriteTextFileAsync(sessionId, path, content)`** — a single
  `PUT .../files/{mount_id}?path=...` carrying the exact new bytes as the request body. The **gateway**
  performs the atomic replace (temp write plus same-directory rename) and creates any missing parent
  directories; the SDK does not stream chunks, verify a temp's digest, or issue a separate finalize.
  Either the write succeeds and the target is atomically replaced, or it fails and the target is left
  untouched — the gateway never exposes a partially-written file. The SDK's only end-to-end check is
  that the gateway's reported `bytes_written` matches what was sent.
- **`ListDirectoryAsync(sessionId, path)`** — one or more paginated
  `GET .../directories/{mount_id}?path=...` pages (the gateway's opaque `next_cursor` is threaded
  verbatim), returning the non-recursive entry names (dotfiles included, `.`/`..` excluded).

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

    var clone = await client.ExecuteAsync(
        sandbox.SessionId,
        new SandboxCommand(["git", "clone", "https://example.com/repo.git", "repo"]));
    var build = await client.ExecuteAsync(
        sandbox.SessionId,
        new SandboxCommand(["dotnet", "build"], workingDirectory: "repo"));
    Console.WriteLine($"exit={build.ExitCode}\n{build.CombinedOutput}");

    await client.WriteTextFileAsync(sandbox.SessionId, "repo/notes.md", "# build passed\n");
    var notes = await client.ReadTextFileAsync(sandbox.SessionId, "repo/notes.md");
    var entries = await client.ListDirectoryAsync(sandbox.SessionId, "repo");
}
finally
{
    await client.DeleteAsync(sandbox.SessionId); // explicit teardown — never implicit on dispose
}
```

To share a caller-managed `HttpClient` (e.g. from `IHttpClientFactory`) instead of letting the
client own its own transport, use the two-argument constructor: `new SandboxClient(options,
httpClient)`. The SDK never mutates a borrowed client's `DefaultRequestHeaders` or `Timeout`. Every
request (the control-plane REST calls, the direct file/command/directory API, and the internal
`/health` probe) is resolved as an absolute URI against the constructor-validated
`SandboxClientOptions.ServerAddress` — the borrowed client's own `HttpClient.BaseAddress` is never
consulted, so a `null` or mismatched `BaseAddress` on the borrowed client can neither break requests
nor redirect credentials to the wrong host.

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

## Command execution

`ExecuteAsync(sessionId, command)` runs one non-interactive command in the sandbox via the gateway's
direct operations API (ADR 0031 / issue #119). `SandboxCommand` is validated at construction:

- **`Arguments`** — a non-empty, ordered **native argv** vector: the program name first, its arguments
  passed verbatim with **no shell involved**, so a hostile argument can never break out of its token or
  inject a second command. A bare program name is resolved on the sandbox `PATH`; invoke a shell
  explicitly when you want one (`["sh", "-c", "…"]`). A NUL byte in any token is rejected.
- **`WorkingDirectory`** (optional) — a workspace-relative POSIX path. Rooted paths, Windows
  drive/UNC/device roots, backslash/mixed-separator forms, and any `..` segment are rejected,
  independent of the host OS. This is a necessary lexical guard only — the gateway remains
  authoritative for filesystem containment (e.g. symlink traversal).
- **`OperationId`** (optional) — a bounded, control-character-free idempotency/recovery key. When
  omitted the SDK generates one and returns it on the result.

`SandboxCommandResult` exposes `ExitCode`, the exact `StandardOutput` and `StandardError` (each
downloaded byte-for-byte from the operation's gateway-owned stdout/stderr artifact through the files
API), and `CombinedOutput` (stdout then stderr; a convenience concatenation, not a real-time
interleaving). Output is decoded as **strict UTF-8**: bytes that are not well-formed UTF-8 surface as
`SandboxErrorKind.Integrity` (carrying the operation id) rather than being silently rewritten with
replacement characters. Output is never truncated — the gateway terminalizes an operation that would
exceed its output cap (`output_limit_exceeded`) rather than silently cutting it. As a defensive bound
the SDK refuses an artifact download whose declared `Content-Length` exceeds a 64&#160;MiB cap.

### Flow

The SDK submits `POST .../operations` carrying the resolved operation id. A fresh submission is
answered `202 Accepted`; an identical-request replay of an existing operation id is answered `200 OK`
— both carry the same status-snapshot shape. While the snapshot is not yet terminal the SDK polls
`GET .../operations/{operation_id}` with a bounded, deadline-based exponential backoff (the configured
`ExecutionTimeout` plus a short grace) until the gateway reports a terminal status. Once terminal, the
command's stdout/stderr artifacts are downloaded verbatim through the files API and decoded as strict
UTF-8.

### Outcomes

- **Gateway execution timeout** (or the SDK's own poll deadline elapsing while the operation is still
  running) → `SandboxException` with `SandboxErrorKind.ExecutionTimeout`.
- **Output-cap violation** → `SandboxErrorKind.OutputLimitExceeded` (the output is intentionally not
  returned, since the result would be incomplete).
- **Client-side transport timeout / lost response** → `SandboxErrorKind.TransportTimeout`, carrying the
  recoverable `SandboxException.OperationId`.
- **Caller cancellation** → a plain `OperationCanceledException`.

Neither timeout claims the remote process tree was terminated — the gateway may still be running the
command after the client stops waiting. Cancelling the token only abandons the SDK's local wait; it
does not ask the gateway to terminate the remote command (terminating the remote process tree is out
of scope for V1).

### Idempotency is gateway-scoped, not durable

The `OperationId` is the **gateway's** idempotency key. Reusing the same id re-submits the same
request, and the gateway answers with the existing operation's current (or terminal) status rather
than running it again — **but only while the gateway retains that operation's state**. A gateway
restart drops it, so reusing an operation id after a restart may start a genuinely new execution.
Consumers must not assume a persisted 24-hour idempotency guarantee. This SDK keeps **no** local
manifest, digest, lease, or artifact bookkeeping of its own — the gateway is the sole source of truth
for both idempotency and the stdout/stderr artifacts, and it (not the SDK) owns byte-exactness and
cleanup.

Because the gateway may rematerialize a lost container and retry the underlying invocation, command
execution is **at-least-once**: a non-idempotent command can run more than once even though the SDK
returns a single result.


## Errors

Every gateway/transport failure other than caller cancellation raises `SandboxException`, which
carries a stable `SandboxErrorKind` (`Authorization`, `NotFound`, `TransportTimeout`, `Protocol`,
plus `ExecutionTimeout`, `OutputLimitExceeded`, `Conflict`, `Unavailable`, `WorkspaceRequired`, and
`Integrity` — raised by [command execution](#command-execution) for a gateway execution-timeout,
output-cap violation, and a non-UTF-8 artifact respectively, and by [file transfer](#file-transfer)
for a read/write conflict or UTF-8 failure). A direct-API failure also carries the gateway's stable
machine-readable `SandboxException.ErrorCode` (e.g. `path_not_found`, `session_not_found`), so a
caller can distinguish a genuinely missing path from an evicted session even though both classify as
`NotFound`. Caller cancellation always surfaces as a plain `OperationCanceledException`. `Protocol`
covers every malformed-response case, and the SDK never lets one surface as a raw
`ArgumentException`/`NullReferenceException`/`InvalidOperationException`:

- A 2xx REST body that is well-formed JSON but semantically invalid — a missing/`null` required field
  (e.g. a marketplace alias or discovered-item kind/path — a discovered item's `name` is genuinely
  optional per the gateway's contract, e.g. a `"context_file"` item never has one) or a `null`
  collection element in any lifecycle/catalog/discovery list; a malformed operation-status or
  directory-listing body; or a write whose reported `bytes_written` does not match what was sent.
- A non-success direct-API response carries the gateway's stable `{ error, code, error_code,
  retryable }` body; only the closed-vocabulary `error_code` is mapped to a `SandboxErrorKind` and
  surfaced on `SandboxException.ErrorCode` — the gateway-controlled free-text `error` message is never
  copied into the exception (see Security below).
- An observed `3xx` redirect (which the SDK refuses rather than follows).

## Security

- `SandboxClientOptions.ClientSecret` is validated at construction and never appears in `ToString()`,
  exception messages, logs, or URLs.
- A non-loopback `ServerAddress` must use HTTPS unless `AllowInsecureDevelopmentTransport` is
  explicitly set for local development.
- The owned transport disables automatic redirects; a borrowed `HttpClient` must do the same
  (`AllowAutoRedirect = false`) — see the borrowed-client note above. The SDK never follows a
  redirect itself and rejects any `3xx` it observes.
- A `401`/`403` response body is never read at all (an auth rejection is the response most likely to
  echo credential material), and a direct-API error's gateway-controlled free-text `error` message is
  never copied into a `SandboxException`: both are untrusted, potentially secret-bearing content (e.g.
  echoed credential material or captured tool output). Only a `SandboxException.StatusCode` and the
  closed-vocabulary `error_code` are ever surfaced.
