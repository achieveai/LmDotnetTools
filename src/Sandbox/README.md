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
- **Command execution:** `ExecuteAsync(sessionId, command)` — run a non-interactive command in a
  Bash/POSIX-capable sandbox and get its exact output back, with recovery across an ambiguous lost
  response. See [Command execution](#command-execution) below.

Exact-byte file transfer is a later SDK capability and is not part of this release.

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

## Command execution

`ExecuteAsync(sessionId, command)` runs one non-interactive command in a gateway Bash/POSIX-capable
sandbox. `SandboxCommand` is validated at construction:

- **`Arguments`** — a non-empty, ordered argv. The SDK POSIX-quotes every token into a single
  `/bin/sh -c` string, so a hostile argument can never break out of its token or inject a second
  command; a NUL byte is rejected (it cannot occur in a shell word). V1 is POSIX-only and does not
  claim native cross-platform argv semantics.
- **`WorkingDirectory`** (optional) — a workspace-relative POSIX path. Rooted paths, Windows
  drive/UNC/device roots, backslash/mixed-separator forms, and any `..` segment are rejected,
  independent of the host OS. This is a necessary lexical guard only — the gateway remains
  authoritative for filesystem containment (e.g. symlink traversal).
- **`OperationId`** (optional) — a bounded, control-character-free recovery key. When omitted the SDK
  generates one and returns it on the result; it is never used directly as a filesystem path (it is
  hashed into a fixed-length artifact directory name).

`SandboxCommandResult` exposes `ExitCode`, the exact `StandardOutput` and `StandardError` (reassembled
beyond the gateway's 20&#160;KB/500-line `exec` truncation — the gateway's unstable `output_*.txt`
file is never used), and `CombinedOutput` (stdout then stderr; a convenience concatenation, not a
real-time interleaving). Output is decoded as **strict UTF-8**: because V1 exposes output as text, bytes
that are not well-formed UTF-8 surface as `SandboxErrorKind.Integrity` (carrying the operation id) rather
than being silently rewritten with replacement characters. The completion signal the SDK reads back
through that truncating `exec` is a single, deliberately small line: only a compact per-stream
digest/length (plus a bounded inline copy of *small* streams) travels on it, and each larger stream is
reassembled from integrity-checked chunk reads — so the signal itself stays provably under the
truncation limit regardless of how large the command's output is.

### Outcomes

- **Gateway execution timeout** → `SandboxException` with `SandboxErrorKind.ExecutionTimeout`.
- **Client-side transport timeout / lost response** → `SandboxErrorKind.TransportTimeout`, carrying the
  recoverable `SandboxException.OperationId`. Artifacts are retained; re-issue the same command with
  the same operation id to recover.
- **Caller cancellation** → a plain `OperationCanceledException`.

Neither timeout claims the remote process tree was terminated — the gateway may still be running the
command after the client stops waiting.

### Single submission, recovery, and at-least-once

The SDK makes exactly **one** side-effecting Bash submission per operation and never resubmits it.
State probes, chunked output reads, and cleanup are idempotent reads. An atomic persisted claim (a
single `mkdir`) elects one submitter; concurrent callers using the same operation id observe the claim
and poll the persisted manifest instead of running again. Recovery from a lost response polls that
manifest with a bounded, deadline-based backoff derived from the execution timeout — never a
resubmission. A canonical, versioned digest (over the session id, argv, normalized working directory,
and execution timeout) is bound to each operation: reusing an operation id with a *different* command
fails with `SandboxErrorKind.Integrity` and never submits.

**Same-id reuse never re-runs — within a bounded retention window.** On a verified success the SDK
reclaims the (unbounded) captured output immediately but retains a bounded, credential-free **completion
marker** (the manifest plus its lease/created timestamps). The **operation-id idempotency/recovery
retention window is 24 hours** from the operation's creation. For that window a later call with the same
operation id is answered from the marker: the result is returned verbatim when the output was small
enough to have been inlined, or — when a larger output had already been reclaimed — the duplicate is
rejected as `SandboxErrorKind.Integrity` *without re-running the command*. The window is **inclusive** of
its 24-hour boundary (a same-id retry at exactly 24 hours is still recovered, never re-run). Once the
window elapses the bounded stale sweep may reclaim the marker, after which reusing the operation id is
treated as a **new** operation and may re-execute. The SDK deliberately does **not** promise idempotency
forever — retention is bounded so artifacts cannot accumulate without limit.

**Abandoned claims self-recover on a same-id retry.** A submitter that crashes after claiming (leaving an
expired lease and no manifest) is recovered in place by the next same-id call — it does **not** wait for
the 24h sweep and does **not** depend on any unrelated command. The recovery deletes the abandoned claim
and re-elects exactly one new claimant, but only under a sibling per-operation **GC lock** (created by an
atomic `mkdir`) and only after re-validating, under that lock, that the claim is still expired and
uncommitted. Claim creation and the stale-sweep purge both respect that lock — only the lock winner may
revalidate and delete, losing purgers never delete — so a purge in progress can never be raced into a
double-run and a second purger can never delete a replacement active claim. A still-active or
still-establishing (lease-less) claim is never recovered: a same-id caller there reports PENDING and
polls the manifest rather than resubmitting. A crashed holder's stale GC lock is reclaimed within a
bounded time so it can never block recovery permanently.

Because the gateway may rematerialize a lost container and retry the underlying invocation once,
command execution is **at-least-once**: a non-idempotent command can run more than once even though the
SDK submits once and returns a single result.

### Artifacts

Command wrapper artifacts (a manifest plus captured output) are persisted under a reserved,
per-session path inside the workspace with **no** credentials. A restrictive `umask` applies only to
the SDK's own artifacts — the caller's command runs under its normal inherited umask, so files the
command itself creates are not force-hardened. The manifest is published atomically (written to a
restrictive sibling temp file and renamed), so a concurrent probe never observes a partial manifest.

A verified successful operation **reclaims its large output immediately** while retaining the bounded
completion marker described above; an interrupted, transport-timed-out, or integrity-failed operation
retains all of its artifacts for recovery. Each successful command also runs a bounded, session-scoped
stale sweep that deletes only artifacts whose lease has expired and that are **strictly older than the
24-hour retention window** (active operations are protected, and an operation exactly at the boundary is
still retained). The sweep re-validates each directory's *current* lease/age in the
sandbox immediately before deleting — never from the earlier listing snapshot, so a refreshed operation
is never deleted — and every candidate directory name is validated as fixed-length lowercase hex before
use. Sandbox deletion remains the final cleanup boundary.


## Errors

Every gateway/transport failure other than caller cancellation raises `SandboxException`, which
carries a stable `SandboxErrorKind` (`Authorization`, `NotFound`, `TransportTimeout`, `Protocol`,
plus `ExecutionTimeout` and `Integrity` — raised by [command execution](#command-execution) for a
gateway execution-timeout and an output/digest verification failure respectively; `Integrity` is also
reserved for later file capabilities). Caller cancellation always surfaces as a plain
`OperationCanceledException`. `Protocol` covers every malformed-response case, and the SDK never lets
one surface as a raw `ArgumentException`/`NullReferenceException`/`InvalidOperationException`:

- A 2xx REST body that is well-formed JSON but semantically invalid — a missing/`null` required field
  (e.g. a marketplace alias or discovered-item kind/path — a discovered item's `name` is genuinely
  optional per the gateway's contract, e.g. a `"context_file"` item never has one) or a `null`
  collection element in any lifecycle/catalog/discovery list.
- A 2xx MCP reply that is not a complete JSON-RPC 2.0 envelope — a non-object root, a missing/wrong
  `jsonrpc`, an `id` that does not match the request, both or neither of `result`/`error`, or a
  non-object `error`. A JSON-RPC `error` envelope is likewise `Protocol` — and its gateway-controlled
  `message` (and every other error field) is never copied into the exception; only the numeric `code`,
  when present, is surfaced (see Security below).
- An observed `3xx` redirect (which the SDK refuses rather than follows).

## Security

- `SandboxClientOptions.ClientSecret` is validated at construction and never appears in `ToString()`,
  exception messages, logs, or URLs.
- A non-loopback `ServerAddress` must use HTTPS unless `AllowInsecureDevelopmentTransport` is
  explicitly set for local development.
- The owned transport disables automatic redirects; a borrowed `HttpClient` must do the same
  (`AllowAutoRedirect = false`) — see the borrowed-client note above. The SDK never follows a
  redirect itself and rejects any `3xx` it observes.
- Non-2xx REST/MCP response bodies are never read, and a JSON-RPC `error.message` (or any other
  error field) is never copied into a `SandboxException`: both are gateway-controlled content the
  SDK treats as untrusted and potentially secret-bearing (e.g. echoed credential material or upstream
  tool output). Only a `SandboxException.StatusCode` and, for MCP errors, a plain numeric JSON-RPC
  `code` are ever surfaced.
