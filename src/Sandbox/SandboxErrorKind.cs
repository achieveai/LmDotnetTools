namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Stable classification of failures a <see cref="SandboxClient"/> operation can raise. Callers
/// switch on this instead of parsing exception messages or inspecting transport-specific
/// exception types, which keeps error handling stable even as the underlying wire details evolve.
/// Caller-initiated cancellation is deliberately NOT one of these kinds — it always surfaces as a
/// plain <see cref="OperationCanceledException"/> so cancellation-aware call sites keep working
/// with the standard .NET pattern.
/// </summary>
public enum SandboxErrorKind
{
    /// <summary>
    /// The gateway rejected the presented credential (e.g. an empty or non-empty <c>401</c>/<c>403</c>
    /// response). Distinct from <see cref="TransportTimeout"/>: the gateway was reachable and
    /// responded, it just refused the request.
    /// </summary>
    Authorization,

    /// <summary>
    /// The gateway reported the target sandbox/session does not exist. Foreign (owned by a
    /// different app id) and genuinely missing sessions both map here uniformly — the SDK never
    /// tries to distinguish the two from the response alone, so a caller cannot probe for another
    /// app's sessions by observing a different error shape.
    /// </summary>
    NotFound,

    /// <summary>
    /// The gateway-side execution deadline for a command elapsed. Raised by
    /// <see cref="SandboxClient.ExecuteAsync"/> when the operation's <c>timeout_secs</c> fires
    /// (terminal status <c>timed_out</c>) before the command completes — distinct from
    /// <see cref="TransportTimeout"/> (the client-side call deadline).
    /// </summary>
    ExecutionTimeout,

    /// <summary>
    /// The SDK's own HTTP/direct-API transport failed: the configured <see cref="SandboxClientOptions.TransportTimeout"/>
    /// elapsed, or the gateway could not be reached at all (DNS/connection failure). Distinct from
    /// <see cref="ExecutionTimeout"/>, which is a gateway-side deadline on the remote operation
    /// itself, not the client-side transport call.
    /// </summary>
    TransportTimeout,

    /// <summary>
    /// The gateway returned a response the SDK could not make sense of: an unexpected status code,
    /// a malformed body, a missing required field, or an unrecognized operation status.
    /// </summary>
    Protocol,

    /// <summary>
    /// Content the gateway delivered exact-byte failed the SDK's strict decode. Raised by
    /// <see cref="SandboxClient.ReadTextFileAsync(string, string, CancellationToken)"/> and
    /// <see cref="SandboxClient.ExecuteAsync"/> when a
    /// file's bytes or a command's captured stdout/stderr are not valid UTF-8 — the SDK surfaces text
    /// only, so it refuses to substitute U+FFFD replacement characters. The gateway (not the SDK) now
    /// owns byte-exactness and atomicity, so there is no length/digest reassembly check left here.
    /// </summary>
    Integrity,

    /// <summary>
    /// The gateway refused a request because it conflicts with existing server-side state: an
    /// operation id reused with a different request payload (<c>409 idempotency_conflict</c>), a
    /// delete of a still-running operation (<c>409 operation_running</c>), or a write blocked by a
    /// concurrent holder of the target file (<c>409 target_locked</c>), a create on a workspace that is
    /// busy being written or backed up (<c>409 workspace_in_use</c>, retryable), a create on a workspace
    /// whose only backups are partial (<c>409 only_partial_snapshots</c> — restore one explicitly first;
    /// <see cref="SandboxException.SnapshotIds"/> lists them), or any other <c>409</c>. Distinct from
    /// <see cref="Integrity"/> (a local/verification failure) — here the gateway is authoritative.
    /// </summary>
    Conflict,

    /// <summary>
    /// The gateway could not service the request right now for a reason that is not the caller's
    /// fault and may succeed on retry: the sandbox agent predates the command-operation protocol
    /// (<c>424 operation_api_unavailable</c>), a transient probe failure (<c>503
    /// operation_probe_failed</c>), a concurrency/capacity limit (<c>503 operation_concurrency_limit</c>
    /// / <c>operation_capacity_exhausted</c> / <c>too_many_concurrent_requests</c>), a briefly
    /// quiescing session (<c>503 sandbox_busy</c>), a failed workspace restore on create
    /// (<c>503 workspace_restore_failed</c>), an image pull that failed or timed out on create
    /// (<c>502 image_pull_failed</c> / <c>504 image_pull_timeout</c> — classified here rather than as
    /// <see cref="ImageRejected"/> because the gateway or its registry, not the requested reference, is
    /// what failed), or any other <c>503</c>. The <c>424</c> case is NOT retryable without a newer agent
    /// image; the <c>503</c> cases are. <see cref="SandboxException.Retryable"/> and
    /// <see cref="SandboxException.RetryAfter"/> carry the gateway's own hint when it sent one.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The operation requires a writable workspace mount the session does not have
    /// (<c>409 workspace_required</c>): operation stdout/stderr artifacts must live under a writable
    /// workspace, so a session created without one cannot host <see cref="SandboxClient.ExecuteAsync"/>.
    /// </summary>
    WorkspaceRequired,

    /// <summary>
    /// A command's combined stdout+stderr exceeded the operation's output cap and the gateway
    /// terminalized it rather than silently truncating (<c>status: output_limit_exceeded</c>). Raised
    /// by <see cref="SandboxClient.ExecuteAsync"/>; the output is intentionally not returned, since the
    /// result would be incomplete.
    /// </summary>
    OutputLimitExceeded,

    /// <summary>
    /// An operation ended terminally without a usable exit code, or a command the SDK ran on the caller's
    /// behalf failed. Raised when:
    /// <list type="bullet">
    /// <item>the <c>mkdir -p</c> the SDK issues to create a nested write's missing parent directory exits
    /// non-zero (e.g. the parent path is read-only or occupied by a non-directory) — the message carries the
    /// exit code and a bounded stderr snippet;</item>
    /// <item><see cref="SandboxClient.ExecuteAsync"/> or <see cref="SandboxClient.ExecuteStreamingAsync"/> sees
    /// status <c>failed</c> with NO exit code and no more specific <c>error_code</c> — the command was
    /// rejected before it spawned (e.g. not found on <c>PATH</c>, or refused by policy) or was killed by a
    /// signal; or the gateway reported <c>cancelled</c> or <c>internal_failure</c>. The
    /// message carries the gateway's sanitized <c>error</c> text.</item>
    /// </list>
    /// Distinct from <see cref="Protocol"/> (reserved for a malformed/unrecognized gateway response): here the
    /// gateway answered coherently and the operation simply did not succeed. A user command that RAN and
    /// exited non-zero is still returned on <see cref="SandboxCommandResult.ExitCode"/>, not thrown.
    /// <see cref="SandboxException.OperationId"/> is set when the operation had one.
    /// </summary>
    OperationFailed,

    /// <summary>
    /// The gateway rejected a submitted environment-variable map (<c>400 invalid_env</c>) — on
    /// sandbox create or on <see cref="SandboxClient.PatchEnvAsync"/> — because one or more keys are
    /// disallowed (e.g. reserved or malformed names). The whole request is rejected; nothing is
    /// applied. <see cref="SandboxException.InvalidKeys"/> names the offending keys when the gateway
    /// reported them.
    /// </summary>
    InvalidEnv,

    /// <summary>
    /// The workspace's backups were purged, so the gateway refuses to create or revive a sandbox on it
    /// (<c>410 workspace_purged</c>). Not retryable: the workspace has to be recreated under a new name or
    /// its purge state cleared server-side.
    /// </summary>
    WorkspacePurged,

    /// <summary>
    /// The gateway refused the sandbox image the create request named: not on the allow-list
    /// (<c>400 image_not_allowed</c>), not supported by the sandbox backend
    /// (<c>400 image_unsupported_backend</c>), absent from the registry (<c>404 image_not_found</c>), or
    /// failing the conformance probe (<c>422 image_unsupported</c>). Deliberately NOT
    /// <see cref="NotFound"/> — that kind means the SESSION is gone, and a missing image says nothing about
    /// any session. Retrying the same reference fails the same way; branch on
    /// <see cref="SandboxException.ErrorCode"/> for the reason. Pull failures and pull timeouts are
    /// <see cref="Unavailable"/> instead.
    /// </summary>
    ImageRejected,

    /// <summary>
    /// The command's executable exists and passed policy checks, but the operating system refused to run
    /// it (operation status <c>failed</c> with <c>error_code executable_not_runnable</c>, issue #198) —
    /// typically a missing execute bit or a <c>noexec</c> mount. The message carries the gateway's
    /// sanitized explanation; <see cref="SandboxException.ErrorCode"/> is <c>executable_not_runnable</c>.
    /// </summary>
    ExecutableNotRunnable,

    /// <summary>
    /// The gateway answered an MCP JSON-RPC call (for example <see cref="SandboxClient.CallBrowserAsync"/>)
    /// with an <c>error</c> envelope instead of a <c>result</c>. <see cref="SandboxException.JsonRpcCode"/>
    /// carries the JSON-RPC code (<c>-32602</c> invalid params, <c>-32601</c> unknown method, <c>-32000</c>
    /// a tool/server failure). The gateway's error message is untrusted and never copied into the exception.
    /// </summary>
    McpCallFailed,

    /// <summary>
    /// The leased browser behind a browser id was destroyed mid-use (idle reap, crash, or delete): HTTP
    /// <c>502 browser_restarted</c>, or a CDP WebSocket closed with <c>1011 browser_restarted</c>. The next
    /// request binds a FRESH browser, so retrying is safe, but every page, target, CDP socket and browser MCP
    /// session from the old instance is gone: reconnect and redo the page setup. Distinct from
    /// <see cref="Unavailable"/> (capacity, launch failure, unreachable), where nothing was lost.
    /// </summary>
    BrowserRestarted,

    /// <summary>
    /// The gateway does not enable leased browsers (<c>404 browser_unsupported</c>). Deliberately NOT
    /// <see cref="NotFound"/>: a browser id is bound implicitly on first use, so a <c>404</c> on a browser
    /// route never means "that browser is gone". Retrying cannot help; configure a browser-enabled gateway.
    /// </summary>
    BrowserUnsupported,
}
