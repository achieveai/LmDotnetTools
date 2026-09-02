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

## Everything here is remote — the SDK never touches your host filesystem

Every path this SDK accepts is **workspace-relative inside the sandbox**, and every operation is an
HTTP call to the gateway. Nothing in this package opens, creates, resolves, or deletes a file or
directory on the machine running your code:

- `SandboxCreateRequest.Workspace` is a **logical** workspace identifier, not a host path. A value
  that *looks* like one (`/srv/data`, `C:\work`) is passed through as an opaque label — it does not
  resolve to, and does not create, anything on your disk.
- `ReadTextFileAsync`/`WriteTextFileAsync`/`ListDirectoryAsync`/`ExecuteAsync` paths are resolved by
  the **gateway**, under the sandbox's workspace mount.
- Command stdout/stderr artifacts live in the sandbox and are fetched over HTTP; no temp file is
  written locally at any point.

**Everything returned is materialized in process memory.** `ReadTextFileAsync` returns one `string`
and `ReadFileBytesAsync` one `byte[]`, each holding the whole file; `SandboxCommandResult` holds the
whole stdout and stderr. There is no streaming or range-read surface — the gateway's files API has no
range read — so a caller's working set scales with the largest file or output it asks for. The
64&#160;MiB direct-read cap below is a defensive ceiling, not a memory budget: sizing that is the
caller's job, and `ReadTextFileAsync(sessionId, path, maxBytes)` / `ReadFileBytesAsync(…, maxBytes)`
exist so a caller can impose a much tighter one.

## Gateway contract baseline

The SDK speaks the gateway's **direct** operations/files/directories REST API (ADR 0031 /
`achieveai/SandboxedOstoolsMcpServer#119` — that ADR and its issue live in the **gateway** repo, not
this one, so a bare `#119` elsewhere in these docs is a cross-repo citation and not this repo's issue
119) for commands and file transfer, plus the REST control plane for lifecycle, marketplace preview, and
session discovery. Its effective minimum gateway is a release carrying that direct API — pinned in CI
by image tag in
[`.github/workflows/sandbox-contract.yml`](../../.github/workflows/sandbox-contract.yml), which runs
`tests/Sandbox.Integration.Tests` against the real gateway with `AUTH_ENFORCE=true` and fails (never
silently skips) when that gateway is unavailable.

Issue #187 named `SandboxedOstoolsMcpServer@c0dc9cfee3e3aeafd4c3d203ef7153255a990bb6` as the original
baseline; it remains the pinned reference for the **catalog and session-discovery** wire shapes, which
this SDK still speaks unchanged. It is *not* a runnable floor: the direct API did not exist at that
commit, so a gateway pinned there cannot serve this SDK's command or file operations. The image tag in
the workflow is the operative pin.

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
- **Operation cleanup:** `DeleteOperationAsync(sessionId, operationId)` — reclaim one finished command's
  gateway-side record and its on-disk stdout/stderr artifacts. `ExecuteAsync` already calls it for you
  whenever a command succeeds; this is the manual entry point for the operations it deliberately leaves
  behind. See [Artifact retention and cleanup](#artifact-retention-and-cleanup) below.
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
  performs the atomic replace (temp write plus same-directory rename); the SDK does not stream chunks,
  verify a temp's digest, or issue a separate finalize. Either the write succeeds and the target is
  atomically replaced, or it fails and the target is left untouched — the gateway never exposes a
  partially-written file. The SDK's only end-to-end check is that the gateway's reported `bytes_written`
  matches what was sent. The direct files API does **not** create the target's parent directory (and has
  no directory-create endpoint), so to preserve the "a write creates its parents" behaviour of the old
  path, a nested write whose parent is missing is self-healed: the SDK runs one `mkdir -p` operation for
  the parent and retries the PUT once. A top-level (parentless) write is always a single PUT with no
  operation.
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

That block is transcribed into a compiled sample,
[`tests/Sandbox.Tests/ReadmeUsageSample.cs`](../../tests/Sandbox.Tests/ReadmeUsageSample.cs), so a
rename or signature change breaks the build rather than quietly rotting this page. Change both in the
same commit. The same flow runs end-to-end against a real gateway in
`tests/Sandbox.Integration.Tests/SandboxLiveContractTests.cs`.

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

### Recovering a command whose response was lost

Every failure of a command that has an operation id raises a `SandboxException` carrying `OperationId`
— the submit and every poll, whether lost to a transport timeout, refused by an unreachable gateway, or
rejected by the gateway; and the stdout/stderr artifact fetch that follows a command which already ran.
That id is the recovery handle: call `ExecuteAsync` again with
`new SandboxCommand(argv, operationId: thatId)` and the gateway answers with the existing operation's
status instead of running the command a second time (subject to the gateway-scoped, non-durable
retention above).

**A present `OperationId` is not a licence to retry.** It identifies the operation; it does not promise
the failure is transient. The id is stamped on deterministic failures too — a `401`/`403`, a refused
redirect — where re-issuing simply fails the same way. Gate on `Kind` and re-issue only for the
**ambiguous** failures, where the response was lost and the command may or may not have run:

```csharp
string[] argv = ["git", "push"];
try
{
    return await client.ExecuteAsync(sessionId, new SandboxCommand(argv));
}
catch (SandboxException ex)
    when (ex.OperationId is { } operationId
        && ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Unavailable
    )
{
    // Re-poll the SAME operation rather than running a side-effecting command a second time.
    return await client.ExecuteAsync(sessionId, new SandboxCommand(argv, operationId: operationId));
}
```

This matters most when you did **not** supply an operation id: the SDK generated one and put it on the
wire, so the exception is the only place that id is ever surfaced. Read it off the exception before
discarding it, or a side-effecting command becomes one you can neither observe nor safely re-run.

### Artifact retention and cleanup

A command's stdout/stderr artifacts are created, retained, and deleted **by the gateway**, inside the
sandbox, under the reserved `.mcp-gateway/operations/<operation_id>/<generation>/` prefix. This SDK
writes no artifact, keeps no manifest, lease, or bookkeeping of its own, and runs no cleanup pass — so
there is no SDK-side retention window to configure, and no stale-artifact sweep that a caller could
trigger or tune. Cleanup is per-operation: one named operation is reclaimed at a time and the gateway
does the deleting.

**`ExecuteAsync` reclaims its own operation when the command succeeds.** Once it has downloaded and
decoded both artifacts — and only then, because the same `DELETE` removes the artifact directory it just
read — it issues `DELETE .../operations/{operation_id}` itself. That is what keeps a long-lived session
under the gateway's `OPERATION_MAX_RECORDS_PER_SESSION` cap (default 256), whose terminal-TTL reaper
would otherwise take an hour to free each slot and would leave the files behind even then. The release
is **best-effort**: it never throws and never changes the command's outcome, and
`SandboxCommandResult.OperationRecordReleased` reports whether it happened — a caller running many
commands on one session should report a persistent `false` **once per session**, since it is the early
warning for `503 operation_capacity_exhausted`. A **failing** `ExecuteAsync` releases nothing on
purpose: the operation id is the idempotent-replay recovery handle described above, and deleting the
record under (say) a lost artifact download would force a re-run of a side-effecting command.

**Artifact files are not time-expired.** ADR 0031 §5's reaper prunes terminal **in-memory operation
records** after `OPERATION_TERMINAL_TTL_SECS` (default 3600); it does **not** delete the on-disk files.
Those persist under the workspace until one of two things happens: the operation is explicitly deleted,
or the sandbox is. A practical consequence: once the TTL has pruned the record, a later poll of that
operation returns `operation_not_found` **while its output files are still sitting on disk** — the
record and the bytes expire on completely different schedules. A gateway restart has the same effect
immediately, since records are process-local.

**Reclaiming one command's footprint: `DeleteOperationAsync`.** It wraps the gateway's per-operation
primitive — `DELETE /api/v1/sandboxes/{session_id}/operations/{operation_id}` — which removes a terminal
or reserved operation's record and best-effort deletes its generation-scoped artifact directory:

```csharp
try
{
    var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["git", "status"], operationId: id));
    Console.WriteLine(result.StandardOutput);
    // Nothing to clean up here: ExecuteAsync already released the record and its artifacts, and
    // result.OperationRecordReleased says whether that succeeded.
}
catch (SandboxException ex) when (ex.OperationId is { } failed)
{
    // A failure keeps its record on purpose, so the id stays a replay handle. Reclaim it explicitly
    // once you have given up on re-reading that operation's output.
    await client.DeleteOperationAsync(sessionId, failed);
}
```

**When to call it: for the operations `ExecuteAsync` leaves behind** — a command that failed and whose
output you no longer intend to re-read, or an operation you submitted and tracked yourself.
`DeleteAsync` remains the **bulk** cleanup — tearing down the whole sandbox removes every artifact with
it — and nothing about it changes; `DeleteOperationAsync` is what lets you bound the footprint *without*
reaching for that boundary.

Three behaviours to code against:

- **It is not cancellation.** ADR 0031 puts cancellation out of scope, so a still-**running** operation is
  refused with `409 operation_running` → `SandboxErrorKind.Conflict` with `ErrorCode ==
  "operation_running"`. Branch on the **error code**, not the kind: `idempotency_conflict` and
  `target_locked` are also `Conflict`, and unlike this one they are not cleared by waiting. Wait for the
  operation to go terminal, then delete.
- **It is not idempotent.** The gateway answers `204 No Content` for a record it removed and a uniform
  `404 operation_not_found` for one it does not hold — an already-deleted operation, a TTL-pruned record,
  a record dropped by a gateway restart, and an id that never existed are all the same answer (the
  no-existence-oracle boundary). Best-effort cleanup should treat `SandboxErrorKind.NotFound` as
  "nothing left to reclaim" rather than as a failure.
- **Delete promptly, not in a nightly sweep.** Because the reaper drops records without touching files, an
  operation whose record has already expired by TTL can no longer be deleted through this route *at all* —
  and its artifacts then live until the sandbox does.

What it reclaims is the **bytes**, not every inode: the gateway's cleanup is generation-scoped, so it
removes `.mcp-gateway/operations/<operation_id>/<generation>/` and leaves the now-empty
`.mcp-gateway/operations/<operation_id>/` directory behind until the sandbox is deleted. That scoping is
deliberate — it is what stops a delayed delete of one reservation from reaping a later re-reservation's
artifacts — and the residue is an empty directory per deleted operation, not accumulating output.

Two further consequences worth stating plainly:

- Artifacts are ordinary workspace files, so `ListDirectoryAsync` does **not** filter them out — the SDK
  has no reserved directory of its own to exclude, and the prefix is reserved from *writes*, never
  hidden from *reads*.
- Conversely, `WriteTextFileAsync` and `WriteFileBytesAsync` are **refused** under that prefix: the
  gateway rejects a write below `.mcp-gateway/operations` with `403 reserved_path` before touching disk,
  which reaches you as `SandboxErrorKind.Authorization`. It is gateway-owned bookkeeping — readable, not
  writable. Pick any other path for your own files.


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
