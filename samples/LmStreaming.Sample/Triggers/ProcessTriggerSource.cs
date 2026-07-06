using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Abstraction over the Bash-tool process registry: lets a trigger await one process's exit
/// without owning its spawn/kill lifecycle (that stays in the Bash tool's confinement).
/// </summary>
public interface IProcessExitObserver
{
    /// <summary>Completes when the process identified by <paramref name="handle"/> exits.</summary>
    Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct);
}

/// <summary>The observed outcome of a Bash-tool-managed process's exit.</summary>
public readonly record struct ProcessExit(int ExitCode, string Stdout);

/// <summary>
/// Notify/block source that fires when a Bash-tool-managed process exits and matches an
/// exit-code / stdout-regex predicate. Registered only when Sandbox is enabled. Not restorable —
/// a process can't be resumed across a restart.
/// </summary>
public sealed class ProcessTriggerSource : ITriggerSource
{
    /// <summary>The registered kind token.</summary>
    public const string KindName = "process";

    /// <summary>Human-readable args hint for the tool contract.</summary>
    public const string ArgsSchemaText =
        "{ handle: \"<bash process handle>\", expectExitCode?: <int>, stdoutPattern?: \"<regex>\" }";

    /// <summary>Capabilities: block + notify, no restore.</summary>
    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    // Regex match timeout — paired with RegexOptions.NonBacktracking below, matching the same
    // linear-time-plus-independent-backstop defense used by FileTailTriggerSource.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IProcessExitObserver _observer;

    public ProcessTriggerSource(IProcessExitObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
    }

    /// <inheritdoc />
    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var (handle, expectCode, stdoutPattern) = ParseArgs(request.ArgsJson);
        Regex? regex = null;
        if (!string.IsNullOrEmpty(stdoutPattern))
        {
            try
            {
                regex = new Regex(stdoutPattern, RegexOptions.NonBacktracking, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"process 'stdoutPattern' is not a valid regex: {ex.Message}", ex);
            }
            catch (NotSupportedException ex)
            {
                throw new ArgumentException(
                    $"process 'stdoutPattern' uses a construct unsupported by non-backtracking matching: {ex.Message}",
                    ex);
            }
        }

        var armed = new ProcessArmedTrigger(request.WaitId, handle, expectCode, regex, _observer, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(armed);
    }

    private static (string Handle, int? ExpectCode, string? StdoutPattern) ParseArgs(string argsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"process args is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("process args must be a JSON object.");
            }

            var handle = root.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new ArgumentException("process requires a 'handle'.");
            }

            int? expect = root.TryGetProperty("expectExitCode", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : null;
            var pattern = root.TryGetProperty("stdoutPattern", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

            return (handle, expect, pattern);
        }
    }

    private static bool SafeMatch(Regex regex, string s)
    {
        try
        {
            return regex.IsMatch(s);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Per-arm handle. Modeled on
    /// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.Sources.TimerTriggerSource"/>'s
    /// yield-before-fire + dispose-cancels pattern: awaits exactly one
    /// <see cref="IProcessExitObserver.WaitForExitAsync"/> call, applies the exit-code/stdout
    /// predicate, and fires at most once. Disposal cancels the wait so no fire occurs afterward.
    /// </summary>
    private sealed class ProcessArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _watch;
        private int _disposed;

        public ProcessArmedTrigger(
            string waitId,
            string handle,
            int? expectCode,
            Regex? regex,
            IProcessExitObserver observer,
            ITriggerEventSink sink)
        {
            WaitId = waitId;
            _watch = RunAsync(handle, expectCode, regex, observer, sink, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(
            string handle,
            int? expectCode,
            Regex? regex,
            IProcessExitObserver observer,
            ITriggerEventSink sink,
            CancellationToken ct)
        {
            // Yield first so the fire is always asynchronous — never synchronous within ArmAsync.
            await Task.Yield();

            ProcessExit exit;
            try
            {
                exit = await observer.WaitForExitAsync(handle, ct);
            }
            catch (OperationCanceledException)
            {
                return; // disposed/cancelled before the process exited — no fire.
            }

            var codeOk = expectCode is null || exit.ExitCode == expectCode.Value;
            var stdoutOk = regex is null || SafeMatch(regex, exit.Stdout);
            if (!codeOk || !stdoutOk)
            {
                return; // predicate mismatch — the observed exit doesn't satisfy the arm's criteria.
            }

            var payload = JsonSerializer.Serialize(new { handle, exitCode = exit.ExitCode, stdout = exit.Stdout });
            await sink.FireAsync(new TriggerFireEvent(payload), ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync();

            // Do NOT await _watch here (same reasoning as TimerTriggerSource): disposal is
            // typically invoked from within the runtime's own fire-handling callback, and awaiting
            // our own still-running task would deadlock. Dispose the CTS once it settles instead.
            _ = _watch.ContinueWith(
                _ =>
                {
                    try
                    {
                        _cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed — nothing to do.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

/// <summary>
/// Placeholder <see cref="IProcessExitObserver"/> registered until a real Bash-tool exit observer
/// is wired in. Its <see cref="WaitForExitAsync"/> never completes on its own — only when the arm
/// is cancelled (disposed, or the wait's own ceiling elapses) — so a <c>process</c> wait armed
/// against it simply parks harmlessly rather than throwing or fabricating a fake exit. Wiring a
/// real observer over the Bash-tool process registry (so this kind can actually fire in
/// production) is a documented follow-up, not part of this task.
/// </summary>
public sealed class NoopProcessExitObserver : IProcessExitObserver
{
    public static NoopProcessExitObserver Instance { get; } = new();

    private NoopProcessExitObserver()
    {
    }

    /// <inheritdoc />
    public Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
}
