using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// The one file operation <see cref="SandboxProcessExitObserver"/> needs: read a workspace-relative
/// file's bytes. Narrowed from <see cref="IWorkspaceFileBrowser"/> (per #161's interface-segregation
/// direction) so the observer is testable without the registry and can never reach write/execute
/// surfaces it has no business holding.
/// </summary>
public interface ISandboxWaitFileReader
{
    /// <summary>
    /// Reads one workspace file's raw bytes, capped at <paramref name="maxBytes"/> (null = the SDK's
    /// 64 MiB ceiling). Propagates <see cref="SandboxException"/> — notably
    /// <see cref="SandboxErrorKind.NotFound"/> when the path does not exist.
    /// </summary>
    Task<byte[]> ReadAsync(string relativePath, long? maxBytes, CancellationToken ct);
}

/// <summary>
/// Adapts a conversation's live sandbox session on the registry's credentialed file surface to the
/// narrow <see cref="ISandboxWaitFileReader"/> seam the observer polls through.
/// </summary>
public sealed class WorkspaceWaitFileReader(IWorkspaceFileBrowser browser, string sessionId) : ISandboxWaitFileReader
{
    private readonly IWorkspaceFileBrowser _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    private readonly string _sessionId = !string.IsNullOrWhiteSpace(sessionId)
        ? sessionId
        : throw new ArgumentException("sessionId is required.", nameof(sessionId));

    /// <inheritdoc />
    public Task<byte[]> ReadAsync(string relativePath, long? maxBytes, CancellationToken ct) =>
        _browser.ReadWorkspaceFileBytesAsync(_sessionId, relativePath, maxBytes, ct);
}

/// <summary>
/// The real <see cref="IProcessExitObserver"/> for issue #142: observes a sandbox Bash process's exit
/// through the workspace files API, without owning the process's spawn/kill lifecycle (that stays in
/// the Bash tool's confinement, per the issue's own boundary note).
/// </summary>
/// <remarks>
/// <para>
/// <b>Convention.</b> The agent backgrounds its own work through the Bash tool and captures the
/// outcome into files under <see cref="WaitRootRelativePath"/> at the workspace root:
/// <code>
/// mkdir -p .lm-waits/&lt;handle&gt; &amp;&amp; { cmd &gt; .lm-waits/&lt;handle&gt;/out 2&gt;&amp;1; echo $? &gt; .lm-waits/&lt;handle&gt;/exit; } &amp;
/// </code>
/// then arms <c>{kind:"process", handle:"&lt;handle&gt;"}</c>. The files — not any MCP tool output —
/// are the source of truth for the exit code and stdout, which resolves #107's flagged ambiguity.
/// </para>
/// <para>
/// <b>Level-triggered by construction.</b> The exit file persists after the process exits, so a
/// subscriber that starts observing late still sees the exit — exactly the observer invariant
/// <see cref="ProcessTriggerSource"/>'s arm-window comment requires of a real implementation. An
/// exit recorded before <see cref="WaitForExitAsync"/> is called completes on the first poll.
/// </para>
/// <para>
/// <b>Fault policy.</b> A missing exit file is the healthy "not exited yet" state. Any other read
/// fault (gateway unavailable, transport timeout, auth refusal) is treated as transient: the loop
/// keeps polling — the wait's own TTL is the ultimate bound — but a persistent streak is surfaced
/// through the logger via <see cref="PollFaultStreak"/>, so a structurally blind observer (session
/// torn down, credential revoked) never reads as "the process just hasn't exited" without a trace
/// (#161's silent-inertness discipline). An exit file whose content never parses is counted on the
/// same streak: one unparseable read is a mid-write race (<c>echo $? &gt; exit</c> creates then
/// writes), a persistent one is a broken convention worth a warning.
/// </para>
/// <para>
/// <b>Stdout is for predicate matching only.</b> The returned <see cref="ProcessExit.Stdout"/> feeds
/// <see cref="ProcessTriggerSource"/>'s <c>stdoutPattern</c> check and is never forwarded — the fire
/// payload stays metadata-only. If the <c>out</c> file is missing or unreadable, stdout resolves to
/// the empty string (logged), so an exit-code-only wait still fires while a stdout-pattern wait
/// simply does not match.
/// </para>
/// </remarks>
public sealed class SandboxProcessExitObserver : IProcessExitObserver
{
    /// <summary>Workspace-root-relative directory the handle convention lives under.</summary>
    public const string WaitRootRelativePath = ".lm-waits";

    /// <summary>Default delay between exit-file polls.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Longest accepted handle. Also bounds the composed relative path.</summary>
    public const int MaxHandleLength = 64;

    /// <summary>
    /// Cap for the exit file read: an exit code is a handful of bytes, and a "exit file" larger than
    /// this is not one — the over-cap read faults and lands on the fault streak rather than buffering
    /// an arbitrary file the convention never produced.
    /// </summary>
    private const long ExitFileMaxBytes = 256;

    /// <summary>
    /// Cap for the one-shot stdout read. Stdout feeds only the arm's regex predicate
    /// (non-backtracking, linear), never the fire payload, so a generous-but-bounded cap keeps a
    /// runaway log from being buffered wholesale; an over-cap read resolves to empty stdout.
    /// </summary>
    private const long StdoutMaxBytes = 4 * 1024 * 1024;

    // Streak thresholds: warnAfter absorbs one-tick races (a mid-write exit file, a gateway blip)
    // without a line of noise; repeatEvery keeps an hours-long fault visible without a line per tick.
    private const int WarnAfterConsecutiveFaults = 10;
    private const int RepeatWarningEveryFaults = 60;

    private readonly ISandboxWaitFileReader _reader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<SandboxProcessExitObserver>? _logger;

    public SandboxProcessExitObserver(
        ISandboxWaitFileReader reader,
        TimeProvider timeProvider,
        TimeSpan? pollInterval = null,
        ILogger<SandboxProcessExitObserver>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Poll interval must be positive.");
        }

        _reader = reader;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the call that actually rejects an invalid handle AT ARM TIME:
    /// <see cref="ProcessTriggerSource.ArmAsync"/> invokes it before constructing the armed
    /// trigger, so the throw propagates to the runtime's <c>invalid_args</c> rejection. A throw
    /// from <see cref="WaitForExitAsync"/> cannot do that — the source's watch machinery converts
    /// it into a faulted, never-awaited task by design (#598 review F-001).
    /// </remarks>
    public void ValidateHandle(string handle) => ValidateHandleCore(handle);

    /// <inheritdoc />
    public Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct)
    {
        // Defense-in-depth for DIRECT callers of this method only. The arm-time rejection lives in
        // ValidateHandle above — a throw here does NOT reject an arm (ObserveExit swallows it into
        // the watch task); it merely keeps an unvalidated handle from ever composing a path.
        ValidateHandleCore(handle);
        return PollForExitAsync(handle, ct);
    }

    /// <summary>
    /// Rejects any handle that is not a strict `[A-Za-z0-9._-]` token (max <see cref="MaxHandleLength"/>,
    /// no leading dot). The allowlist — not an escape step — is the guarantee: nothing resembling a path
    /// separator, a traversal, or a hidden-file prefix ever reaches the composed workspace path.
    /// </summary>
    private static void ValidateHandleCore(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException("process 'handle' is required.", nameof(handle));
        }

        if (handle.Length > MaxHandleLength)
        {
            throw new ArgumentException(
                $"process 'handle' is longer than {MaxHandleLength} characters.",
                nameof(handle)
            );
        }

        if (handle[0] == '.')
        {
            throw new ArgumentException("process 'handle' must not start with '.'.", nameof(handle));
        }

        foreach (var c in handle)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                throw new ArgumentException(
                    "process 'handle' may only contain letters, digits, '.', '_' and '-'.",
                    nameof(handle)
                );
            }
        }
    }

    private async Task<ProcessExit> PollForExitAsync(string handle, CancellationToken ct)
    {
        var exitPath = $"{WaitRootRelativePath}/{handle}/exit";
        var streak = new PollFaultStreak(WarnAfterConsecutiveFaults, RepeatWarningEveryFaults);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int? exitCode = null;
            try
            {
                var bytes = await _reader.ReadAsync(exitPath, ExitFileMaxBytes, ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes).Trim();
                if (int.TryParse(text, out var parsed))
                {
                    exitCode = parsed;
                    if (streak.RecordSuccess())
                    {
                        _logger?.LogInformation(
                            "process wait '{Handle}': exit file became readable again after a fault streak.",
                            handle
                        );
                    }
                }
                else if (streak.RecordFailure())
                {
                    // One unparseable read is the `echo $? > exit` create-then-write race; a streak of
                    // them means the convention was not followed (or the file is not ours).
                    _logger?.LogWarning(
                        "process wait '{Handle}': exit file exists but has not parsed as an exit code for {Consecutive} consecutive polls.",
                        handle,
                        streak.Consecutive
                    );
                }
            }
            catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
            {
                // Healthy: the process has not exited (or has not been started) yet. Keyed on the
                // gateway's explicit path_not_found — NEVER on the NotFound kind alone, which also
                // covers an evicted session/mount; treating those as "not exited yet" would make a
                // dead session poll exactly like a long-running process, silently, until TTL.
                if (streak.RecordSuccess())
                {
                    _logger?.LogInformation("process wait '{Handle}': polling recovered after a fault streak.", handle);
                }
            }
            catch (SandboxException ex)
            {
                // Transient by policy: the wait's TTL bounds the loop, but a persistent streak must
                // not stay silent — this arm includes session/mount eviction (NotFound without
                // path_not_found), which is the structurally blind case #161 named.
                if (streak.RecordFailure())
                {
                    _logger?.LogWarning(
                        ex,
                        "process wait '{Handle}': exit-file poll has failed {Consecutive} consecutive times ({Kind}).",
                        handle,
                        streak.Consecutive,
                        ex.Kind
                    );
                }
            }

            if (exitCode is { } code)
            {
                var stdout = await ReadStdoutAsync(handle, ct).ConfigureAwait(false);
                return new ProcessExit(code, stdout);
            }

            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One-shot read of the convention's <c>out</c> file once the exit code is known. Missing or
    /// unreadable stdout degrades to the empty string (an exit-code-only wait still fires; a
    /// stdout-pattern wait simply does not match) — logged so the degradation is visible.
    /// </summary>
    private async Task<string> ReadStdoutAsync(string handle, CancellationToken ct)
    {
        var outPath = $"{WaitRootRelativePath}/{handle}/out";
        try
        {
            var bytes = await _reader.ReadAsync(outPath, StdoutMaxBytes, ct).ConfigureAwait(false);
            // Lossy decode on purpose: stdout may be arbitrary bytes, and it is only ever regex-matched
            // here — never forwarded — so replacement characters beat refusing the exit outright.
            return Encoding.UTF8.GetString(bytes);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            // The command produced no out file (or the convention skipped the redirect) — a plain
            // "no stdout", not a fault worth a warning line.
            return string.Empty;
        }
        catch (SandboxException ex)
        {
            _logger?.LogWarning(
                ex,
                "process wait '{Handle}': exit code was read but stdout was unreadable ({Kind}); matching against empty stdout.",
                handle,
                ex.Kind
            );
            return string.Empty;
        }
    }
}
