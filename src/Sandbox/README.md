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

**Gateway compatibility:** error and operation semantics are synced to `SandboxedOstoolsMcpServer`
`origin/master` `7f19413` (v0.1.16), and CI pins image tag `0.1.16`: images, homes, backups, per-agent
state and leased browsers need it. Per-app git marketplaces (below) are built against the gateway commit
`bac5a32`, which is NOT in v0.1.16 — on v0.1.16 their routes do not exist. The working directory,
properties, skill tiers and the unified webhook (below) are synced to `origin/master` `8547483f`
(2026-10-18, after v0.1.16, unreleased): on v0.1.16 their routes do not exist, and a requested
`WorkingDir` fails the create (v0.1.16 echoes only the legacy `home` and does not make the workspace
read-only). That sync covers:

- REST error bodies (`{ error, code, error_code, retryable }`) read under a 64 KiB bound on every
  non-2xx except `401`/`403`, surfacing `ErrorCode`, `Retryable`, `Retry-After` (`RetryAfter`), and a
  backup refusal's `snapshots` (`SnapshotIds`).
- New kinds: `WorkspacePurged` (`410` / `workspace_purged`), `ImageRejected` (`image_not_allowed`,
  `image_unsupported_backend`, `image_not_found`, `image_unsupported` — `image_not_found` is not
  `NotFound`), `ExecutableNotRunnable` (operation `error_code`, #198), and `McpCallFailed` (a JSON-RPC
  `error` envelope, carrying `JsonRpcCode`).
- `409` → `Conflict` (incl. `workspace_in_use`, `only_partial_snapshots`); `503`, `workspace_restore_failed`,
  `image_pull_failed` (`502`) and `image_pull_timeout` (`504`) → `Unavailable`.
- Operations: a `failed` status with no exit code → `OperationFailed` carrying its `ErrorCode`, never the
  gateway's free-text reason (poll and stream); status `cancelled` or `internal_failure` → `OperationFailed` (poll and stream); the streaming `timeout_secs` follows
  `SandboxCommand.ExecutionTimeout` (1–120 s, default 30); the reserved operation ids `stream` and
  `capabilities` are refused client-side.

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
  session-discovery REST endpoint; `ListDiscoveredAsync(sessionId, agentId)` narrows it to one agent.
- **Per-agent state:** `ListAgentsAsync`, `DeleteAgentAsync`. See [Per-agent state](#per-agent-state).
- **Command execution:** `ExecuteAsync(sessionId, command)` — run a non-interactive native command
  (an executable plus argv, no shell) in the sandbox via the gateway's direct operations API and get
  its exact captured output back. See [Command execution](#command-execution) below.
- **Operation cleanup:** `DeleteOperationAsync(sessionId, operationId)` — reclaim one finished command's
  gateway-side record and its on-disk stdout/stderr artifacts. `ExecuteAsync` already calls it for you
  when it minted the operation id itself and the command succeeded; this is the manual entry point for
  the records it deliberately leaves behind — a caller-supplied `OperationId`, or a failed command. See
  [Artifact retention and cleanup](#artifact-retention-and-cleanup) below.
- **Exact file transfer:** `ReadTextFileAsync`, `WriteTextFileAsync`, `ListDirectoryAsync` — exact,
  integrity-verified UTF-8 file round-trips and directory listing over a workspace-relative POSIX
  path. See [File transfer](#file-transfer) below.

## Images, working directory and workspace backup

- **Image** (ADR 0039): `new SandboxCreateRequest(ws, image: "debian:bookworm-slim")` (or `repo@sha256:…`).
  The gateway's allow-list decides; a refusal is `ImageRejected` with `ErrorCode` `image_not_allowed`,
  `image_not_found`, `image_unsupported` (with `SandboxException.ImageReport`) or
  `image_unsupported_backend`. The create result carries `Image`, `ImageDigest` and `ImageReport`
  (conformance + `Warnings`, e.g. "image has no pwsh"). `GetAsync` reports `Image`/`ImageDigest` when the
  gateway's GET carries them (a session recorded without an image reports the server default); older GETs
  and every `ListAsync` result leave them `null`, and `ImageReport` is create-only.
- **Working directory** (ADR 0044, formerly "home", issue #142): `new SandboxCreateRequest(ws,
  workingDir: "agents/a1")`, sent as top-level `working_dir` beside top-level `workspace` (the deprecated
  `home` alias is never sent). **It is the only place agents can write**: the MCP tools and shells see the
  rest of the workspace read-only (OS-enforced on Docker and Linux; on Windows AppContainer an ACL split that
  stops a cooperative agent but is not a security boundary while an operation runs, ADR 0044), while the direct
  file API and operations still write everywhere. Operations without a `WorkingDirectory` run at the
  workspace ROOT, not in it, and `HOME` moves to `/home/sbx` on Docker. A git worktree cannot be the working
  directory (its commits land in the read-only parent `.git`). `CreateAsync` requires
  `SandboxInfo.WorkspaceWorkingDir` (`volumes.workspace.working_dir`) to equal the normalized request
  (`./a//b/` → `a/b`); on a missing or different acknowledgement, including a legacy `home`-only echo, it
  deletes the sandbox and throws `Protocol`.
- **Backup** (issue #191): `ListBackupSnapshotsAsync(workspace)`, `RestoreBackupSnapshotAsync(workspace,
  snapshotId)`, `PurgeBackupWorkspaceAsync(workspace)` and `GetBackupCostsAsync(since, groupBy)`. Every call
  sends `app_id` = `AppId`, which the gateway needs with `AUTH_ENFORCE` off and ignores with it on. A
  restore is refused (`Conflict`, `workspace_attached`, retryable) while a sandbox still mounts the
  workspace: delete it first. **Purge is destructive**: it deletes this gateway's sandboxes on the workspace,
  then its snapshots and directory. `backup_disabled` is `Unavailable` (not `NotFound`),
  `snapshot_not_found` is `NotFound`, `backup_unavailable` is `Unavailable`, and the `400 invalid_*` /
  `app_id_required` refusals are `Protocol` with `ErrorCode` set. A partitioned app passes its partition
  values to the overloads taking `partitionProperties` (see below).

## Properties, skill tiers and the sandbox webhook (gateway `8547483f`)

- **Properties and partitions** (ADR 0042): `new SandboxCreateRequest(ws, properties: …)`; the create and
  GET results carry `Properties` (`null` from an older gateway) and `Partition`. `GetPropertiesAsync` /
  `UpdatePropertiesAsync` read and change the bag (`null` value = unset). Keys in the app's registry
  `partition` list are required at create, become workspace directory levels and are immutable: changing
  one is `Conflict` (`partition_property_immutable`, keys on `InvalidKeys`). Backup calls for a partitioned
  app take the partition values (`properties.<key>=` query / `properties` body).
- **Skill tiers** (ADR 0041): `new SandboxCreateRequest(ws, skills: new SandboxSkillsPolicy(…))` sets the
  default tier, the included list and the unlisted-skills trailer; the create result echoes the resolved
  policy on `SkillsPolicy`. `GetSkillsAsync(sessionId, agentId, tier, loaded)` returns one agent's catalog;
  `UpdateSkillTiersAsync(sessionId, set, reset, agentId)` moves skills between tiers (works before the
  agent's first call). MCP clients must re-run `tools/list` afterwards (`ToolsListChanged`).
  `SandboxAgentInfo.IncludedSkills` counts the listed skills.
- **Sandbox webhook** (gateway PR #205): `new SandboxCreateRequest(ws, webhook: new
  SandboxWebhookSettings(url, authHeader, events))` registers one URL for every event (`SandboxWebhookEvents`:
  `sandbox_created`, `sandbox_deleted`, `context_discovery`, `auth_request`, `auth_post_response_failure`,
  `task_completed`). `GetAsync` reports it on `Webhook` without the auth header value. The SDK only sends the
  registration; receiving and deduplicating events (`Idempotency-Key`) is the host's job. Register either
  this or the legacy `SandboxDiscoverySettings`, not both: with both, every discovery arrives twice. A
  gateway at or below v0.1.16 ignores `webhook`, so keep the legacy block for it. LmAgentInfra's
  `SandboxSessionRegistry` picks one through `WebhookOptions.UseSandboxWebhook` (default off).

## HTTP MCP libraries (gateway v0.1.18)

Use the create request's additive `Mcp` property to let sandbox scripts call declared HTTP MCP servers:

```csharp
var request = new SandboxCreateRequest(
    "workspace",
    webhook: new SandboxWebhookSettings(callbackUrl, sessionSecret,
        [SandboxWebhookEvents.AuthRequest, SandboxWebhookEvents.AuthPostResponseFailure]))
{
    Mcp = new SandboxMcpPolicy(SandboxMcpEgress.Auto,
        new Dictionary<string, SandboxMcpServerPolicy>
        {
            ["stub"] = new(auth: SandboxMcpAuthMode.Injected,
                headers: ["Authorization"], cacheTtlSeconds: 60,
                url: "https://mcp.example/mcp"),
        }),
};
var session = await client.CreateAsync(request);
```

The gateway derives egress rules and a secret-free manifest from the selected plugins' `.mcp.json` files.
Docker images ship `sbx-mcp`, Python `sbx_mcp` and Node `@sbx/mcp`; native backends require those helpers
to be provisioned separately. The proxy asks the authenticated app callback for the **complete** credential
header. The SDK registers policy; it does not host a credential callback or install helpers.

Never put tokens in sandbox `Env` or marketplace files: resolved environment headers become visible in the
manifest. Use `SandboxMcpAuthMode.None` for public servers. Generic SDK policy URLs also support HTTP;
credential-bearing app bindings require HTTPS. `Manual` leaves rule authoring to the app. Omitting `Mcp`
keeps existing requests unchanged, including the original constructor signature.

The streaming sample integrates owner-bound callbacks through `SandboxGateway:Mcp`; see
[its configuration guide](../../samples/LmStreaming.Sample/SandboxWorkspaceGuide.md#http-mcp-libraries).
Resolved-manifest/catalog wrappers, generated plugin orchestration and forced OAuth rejection refresh
are outside this integration.

The gateway's [v0.1.18 MCP contract](https://github.com/achieveai/SandboxedOstoolsMcpServer/blob/1f5b4ada20ba6c9467c16c679a5b0935c7475b10/Docs/mcp-servers.md#L174-L208)
derives an endpoint-less webhook provider named `mcp:<server>` for injected authentication, in both
automatic and manual egress modes. An app-authored manual rule may reference that provider without
adding its own `auth_providers` descriptor. Supplying a duplicate descriptor conflicts with gateway
derivation. The provider uses the sandbox's unified `auth_request` subscription and a 15-second timeout.
Use v0.1.18 or later with matching helper images; this SDK does not negotiate these capabilities when
adopting a gateway.

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
- **Executable could not run** (gateway `error_code` `executable_not_runnable`) →
  `SandboxErrorKind.ExecutableNotRunnable` with a fixed message; the gateway's free-text `error` is never
  copied into an exception (ADR 0001).
- **Failed with no exit code** (a pre-spawn rejection or a signal kill), or **cancelled by the gateway**
  → `SandboxErrorKind.OperationFailed`.
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

**`ExecuteAsync` reclaims only the operations it named itself.** When you leave
`SandboxCommand.OperationId` null, the SDK mints the id, nobody else can name that record, and a
successful command ends with `DELETE .../operations/{operation_id}` — issued once both artifacts have
been downloaded and decoded, and only then, because the same `DELETE` removes the artifact directory it
just read. That is what keeps a long-lived session under the gateway's
`OPERATION_MAX_RECORDS_PER_SESSION` cap (default 256), whose terminal-TTL reaper would otherwise take an
hour to free each slot and would leave the files behind even then. **A caller-supplied `OperationId` is
never released by the SDK** — passing an id is your statement that you own that record's lifecycle
(replay, artifact reads, and the eventual `DeleteOperationAsync`), and reclaiming it here would silently
break all three; `OperationRecordReleased` is always `false` for such a command. The release is otherwise
**best-effort**: it never throws and never changes the command's outcome, and
`SandboxCommandResult.OperationRecordReleased` reports whether it happened — a caller running many
commands with SDK-minted ids on one session should report a persistent `false` **once per session**,
since it is the early warning for `503 operation_capacity_exhausted`. A **failing** `ExecuteAsync`
likewise releases nothing on purpose: the operation id is the idempotent-replay recovery handle described
above, and deleting the record under (say) a lost artifact download would force a re-run of a
side-effecting command.

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
// An SDK-minted id needs no cleanup: ExecuteAsync released the record and its artifacts, and
// result.OperationRecordReleased says whether that succeeded.
var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["git", "status"]));
Console.WriteLine(result.StandardOutput);

try
{
    // Supplying an id keeps the record replayable — and makes reclaiming it yours to do.
    var replayable = await client.ExecuteAsync(
        sessionId,
        new SandboxCommand(["git", "status"], operationId: id)
    );
    Console.WriteLine(replayable.StandardOutput);
    await client.DeleteOperationAsync(sessionId, replayable.OperationId);
}
catch (SandboxException ex) when (ex.OperationId is { } failed)
{
    // A failure keeps its record on purpose, so the id stays a replay handle. Reclaim it explicitly
    // once you have given up on re-reading that operation's output.
    await client.DeleteOperationAsync(sessionId, failed);
}
```

**When to call it: for the operations `ExecuteAsync` leaves behind** — a command you gave your own
`OperationId`, or one that failed and whose output you no longer intend to re-read.
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


## Leased browsers (CDP and MCP relay)

The gateway leases a Chrome per `(app, browser id)`; the first request with a new id binds one. Ids are
`[A-Za-z0-9._:-]{1,128}`. Every relay call goes through the client's auth stamping, so the
`X-Sbx-App-*` headers (and any future per-request header) are added in one place.

- `GetBrowserCdpUri` / `GetBrowserCdpWebSocketUri` — the relay's HTTP and `ws`/`wss` URLs.
- `GetBrowserVersionAsync` — `/json/version`, with `BrowserInstance` and the debugger target reduced
  to a path on this gateway.
- `ConnectBrowserCdpAsync` — an authenticated CDP WebSocket. Targets must stay under the browser's own
  `/cdp/` endpoint. A gateway close `1011 browser_restarted` surfaces from `ReceiveTextAsync` as
  `SandboxErrorKind.BrowserRestarted` (retryable): reconnect to get a fresh browser.
- `ConnectBrowserMcpAsync(id, BrowserMcpServer.Playwright | DevTools)` — a `BrowserMcpSession` whose
  `Client` is a streamable-HTTP `McpClient` (ModelContextProtocol.Core). Its `IsBrowserReplaced` turns
  true when a later response names a different `X-Sbx-Browser-Instance`.
- `CloseBrowserAsync` — idempotent (`204` even for an unknown id). A `404` means the gateway has
  browsers disabled and throws `BrowserUnsupported`.
- `BrowserRequestSignature` — verifies the `X-Sbx-Browser-Signature` the gateway adds to pinned
  browser egress (HMAC-SHA256, ±300 s skew, constant-time compare).

Relay error codes: `browser_capacity` / `browser_launch_failed` / `browser_unreachable` →
`Unavailable` (capacity carries `RetryAfter`); `invalid_browser_id` / `invalid_cdp_path` /
`unknown_browser_mcp` / `body_too_large` → `Protocol`; `cdp_upgrade_rejected` → `NotFound`.

## Per-app git marketplaces (gateway ADR 0045)

An app can register public git repositories as its own plugin marketplaces. The gateway clones each one
and mounts it at `/marketplaces/<alias>/` next to the operator's marketplaces. Requires gateway v0.1.17
or later (commit `bac5a32`; absent from v0.1.16) with `MARKETPLACE_STORE_PATH` set.

- `RegisterAppMarketplaceAsync(alias, url, gitRef?)` — register, or re-point an existing alias (a different
  URL or ref replaces it). `gitRef` null means `latest` (follow the default branch); a tag, branch or commit
  pins it. The gateway clones before answering, so a bad URL or ref fails here, not at sandbox create.
- `ListAppMarketplacesAsync()` — the app's registrations as `SandboxAppMarketplace` (`ResolvedSha`,
  `LastError` of the last failed background sync, timestamps as text).
- `SyncAppMarketplaceAsync(alias)` — fetch `latest` now; a pin is returned unchanged.
- `RemoveAppMarketplaceAsync(alias)` — `false` when the alias was not registered. Sandboxes already
  using it keep their commit.

**Behaviour change:** creating a sandbox without `Marketplaces` now mounts the operator default set
**plus all of the app's registrations**. Name aliases to mount fewer. `PreviewMarketplacesAsync` lists
the registrations' plugins too. Every marketplace call (and the preview) sends `app_id`, which a gateway
with `AUTH_ENFORCE` off requires and an enforcing gateway ignores.

Error codes: `marketplaces_disabled` (404), `git_failed` (502), `store_failed` / `registry_failed`
(500) → `Unavailable`; `alias_taken` (409, the alias is an operator marketplace's) → `Conflict`;
`marketplace_not_registered` → `NotFound`; `invalid_alias` / `invalid_url` / `host_not_allowed` /
`invalid_ref` / `ref_not_found` / `app_id_required` (400) → `Protocol`. `ErrorCode` names the reason.

Not supported upstream: a "cloud" marketplace whose catalog only points at plugins in other git repos
(`github` / `url` / `git-subdir` plugin sources are skipped). Tracked as gateway issue #202.

## Per-agent state

Several agents can share one sandbox session (gateway ADR 0040). Each MCP call names its agent in
the `X-Sbx-Agent-ID` header. The gateway then keeps that agent's read-before-write marks, loaded
skills, tasks and PowerShell contexts apart from every other agent's. A call with no header belongs
to the default agent, whose id is `""`.

- **Agent ids:** 1–64 characters of `A-Z a-z 0-9 . _ -`. Use `SandboxAgentId.IsValid` to check one.
  Send no header for the default agent. An empty header is a JSON-RPC `-32602` error.
- **Freshness is per agent:** an agent's `Edit` fails with "File has been modified since you read
  it" when anyone else changed the file after this agent last read it. That agent must read the
  file again.
- **`ListAgentsAsync(sessionId)`:** every agent the session has seen, as `SandboxAgentInfo`.
  `IsDefault` is true for the default agent.
- **`DeleteAgentAsync(sessionId, agentId)`:** forgets one named agent's state. An unknown agent is
  `NotFound` (404). The default agent cannot be deleted: that is `Conflict` (409). A busy sandbox is
  `Unavailable` (503). The SDK refuses an invalid id before sending.
- **`ListDiscoveredAsync(sessionId, agentId)`:**
  - `null` lists every row.
  - `""` lists the rows the default agent was notified of.
  - A named id lists that agent's notified rows.
  - Each item carries `DiscoveredAt`, `Notified`, `Plugin` and a sub-agent's `Frontmatter`.
    Treat `Frontmatter` as untrusted.

Only the gateway's `/mcp` endpoint reads the agent header. No REST call takes it, so the SDK has
none to send.

## Skill tiers per agent (gateway ADR 0041, v0.1.17)

The create `skills` block, `GET` and `PATCH /api/v1/sandboxes/{id}/skills`. Below v0.1.17 these
routes do not exist.

Each agent sees every admitted skill in one of three tiers. `Included` skills are listed in that
agent's `Skill` tool description. `Available` skills are not listed but load by exact name.
`Unavailable` skills are refused. Whether the agent has loaded a skill is a separate flag.

- **Create:** `new SandboxCreateRequest(..., skills: new SandboxSkillsPolicy(defaultTier, included,
  unlistedHint))`. `DefaultTier` is `Included` or `Available`; `null` uses the gateway's default.
  `Included` takes skill names (`plugin:skill`) or skill paths. `UnlistedHint`: `null` keeps the
  gateway's trailer, `""` removes it. No policy sends no `skills` field, so old requests are unchanged.
  `SandboxInfo.SkillsPolicy` is the policy the gateway resolved (its `Included` holds skill paths).
- **`GetSkillsAsync(sessionId, agentId, tier, loaded)`:** one agent's catalog: marketplaces,
  then plugins (name, version, description), then skills (name, path, description, tier, tier origin,
  loaded, loaded at, notified, shadowed), plus workspace skills, the default tier and counts.
  `tier` and `loaded` filter the rows; `Counts` always covers the whole catalog.
- **`UpdateSkillTiersAsync(sessionId, set, reset, agentId)`:** sets tiers for one agent only, by skill
  name or path. All or nothing; loads are untouched. Returns the catalog with `ToolsListChanged`
  true. The gateway sends no MCP `list_changed`, so re-run `tools/list` for that agent afterwards.
- **Agent id:** `null` or `""` is the default agent (sent as no `agent_id` query). Any other id must
  pass `SandboxAgentId.IsValid`.
- **Errors:** `400 skill_not_found`, `skill_ambiguous`, `invalid_skills` and `invalid_agent_id` are
  `Protocol`; `ErrorCode` says which.
- **Default tier tip:** `Available` keeps a newly discovered skill from changing an agent's
  description: it loads by name at once but is listed only after a `PATCH` includes it.

Live contract: `SandboxSkillTiersLiveContractTests` needs a gateway of v0.1.17 or later that serves at
least one marketplace plugin with two skills. CI mounts `tests/Sandbox.Integration.Tests/Fixtures/marketplace`
for this through `PLUGINS_DIRS` (see `.github/workflows/sandbox-contract.yml`).

## Plugin MCP servers (gateway issue #224, v0.1.18)

`GET /api/v1/sandboxes/{id}/mcp-servers`, read by `GetMcpServersAsync(sessionId)`. Below v0.1.18 this
route does not exist: the call fails as `NotFound`, the same kind as an unknown or foreign session.

- **Rows:** the MCP servers the session's admitted plugins declare, as authored, sorted by marketplace,
  plugin and name. Each carries `Type`, `Supported` (with `Reason` when not), `Url`, `Headers` and the
  rest of the entry in `Extra`. `${VAR}` references come back unexpanded.
- **Not proxied:** the gateway lists the servers but does not proxy them; the caller connects itself.
- **Errors:** `503 marketplaces_unavailable` is `Unavailable`: retry. The gateway never answers with a
  shorter list instead.
- **Secret:** a row's url, header values and `Extra` can carry credentials. `SandboxMcpServer.ToString()`
  names only the marketplace, plugin, server name and type.

## Resolved MCP state and library manifest (gateway v0.1.18)

`CreateAsync` and `GetAsync` expose `SandboxInfo.Mcp`, a typed resolved policy with a
server **list**. This differs from the create request's named override map. Create returns
its snapshot; GET re-resolves URLs and unresolved variable names against current session
configuration. Keeping the create object does not turn it into current state.

```csharp
var created = await client.CreateAsync(request, ct);
if (request.Mcp is not null && created.Mcp is null)
{
    // No readable acknowledgment is available. Keep created.SessionId and decide how to recover.
    // Inspect with GetAsync, retain it, or explicitly DeleteAsync if appropriate.
}
var manifest = await client.GetMcpManifestAsync(created.SessionId, ct);
```

An omitted, null or uninterpretable optional response block means no readable MCP acknowledgment
is available to this SDK. It does not prove the gateway omitted the field or ignored the request.
A readable block with an empty server list is valid acknowledgment. Current gateways can return
MCP state even when the create request omitted MCP. List results remain shallow and have `Mcp == null`.
This optional presence check never deletes, recreates, retries or mutates a session. It proves
a readable block was returned, not exact override equality, working credentials or proxy enforcement.

`GetMcpManifestAsync` reads the library view through the existing authenticated transport.
It returns schema/session identity, per-server transport support, resolved URL and URL template,
literal headers, auth mode/provider/header names, egress state/note, unresolved names and generated
library metadata. HTTP's omitted `supported` flag means true. The authored `GetMcpServersAsync`
view remains separate; its transport support can differ from in-sandbox library support.

Manifest `Stale` is producer-reported egress state after a host/port change. It does not mean
generated tools are stale. A successful REST read does not prove the in-sandbox manifest file
was synchronized. URL templates and literal values may contain sensitive configuration despite
producer redaction. Access them deliberately; do not log whole objects or serialize them to logs.
The SDK preserves explicit configuration values; it does not apply a second redaction engine.
The pinned producer strips URL userinfo and credential-looking literal headers, but environment-resolved
headers and URL path/query values can still contain sensitive data. Resolved policy Headers are names
only; manifest Headers are values. Response collections are immutable snapshots, and default object
formatting does not print values.

Absent pre-feature manifests and unsupported routes return `NotFound`; the producer's
`mcp_manifest_unavailable` code distinguishes that specific response when supplied. A valid empty
manifest returns an empty list. Marketplace resolution failures remain `Unavailable`; authorization
and ownership use existing errors. The explicit manifest API rejects unknown schemas, malformed
data and unsupported response charsets as `Protocol`, with fixed diagnostics and no raw payload
exception attached. Optional unreadable create/get MCP echoes instead return `Mcp == null` while
preserving valid base session data. Whole-document JSON and required base fields remain strict.

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
covers malformed required response data; unreadable optional MCP echoes follow the tolerant behavior
above. Malformed response data does not surface as a raw
`ArgumentException`/`NullReferenceException`/`InvalidOperationException`:

For malformed create/get responses, `SandboxException.InnerException` no longer carries the original
`JsonException`. This deliberate diagnostic change applies to all callers: parser messages may echo
sensitive payload values. Kind/status and fixed diagnostics remain available. A valid base response
with an unreadable optional MCP echo succeeds with `Mcp == null`; this does not relax required base
fields or the explicit manifest read.

- A 2xx REST body that is well-formed JSON but semantically invalid — a missing/`null` required field
  (e.g. a marketplace alias or discovered-item kind/path — a discovered item's `name` is genuinely
  optional per the gateway's contract, e.g. a `"context_file"` item never has one) or a `null`
  collection element in any lifecycle/catalog/discovery list; a malformed operation-status or
  directory-listing body; or a write whose reported `bytes_written` does not match what was sent.
- A non-success REST or direct-API response carries the gateway's stable `{ error, code, error_code,
  retryable }` body; only the closed-vocabulary `error_code` is mapped to a `SandboxErrorKind` and
  surfaced on `SandboxException.ErrorCode` (with `Retryable`, `RetryAfter`, `SnapshotIds` beside it) —
  the gateway-controlled free-text `error` message is never copied into the exception (see Security
  below). A body that is missing, malformed, or over 64 KiB falls back to classification by status.
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
  closed-vocabulary `error_code` (plus `retryable`, `Retry-After`, and snapshot ids) are surfaced from a
  REST error. A JSON-RPC `error.message` is never copied either: `McpCallFailed` carries only
  `JsonRpcCode`. One gateway text *is* quoted, length-bounded: an operation's `error` reason (the
  gateway allow-lists and sanitizes it before sending).
