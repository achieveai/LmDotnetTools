using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Hand-written <see cref="IMultiTurnAgent"/> double for the agent-collection tests. It records the
/// <see cref="UserInput"/> each run received and replays a scripted message stream from
/// <see cref="ExecuteRunAsync"/>, so the collect-only logic can be verified without constructing a real
/// provider loop. Background-loop members throw — these tests drive only the single-run entry point.
/// <para>
/// Turns are scripted independently (<see cref="ThenReplies"/>/<see cref="ThenThrows"/>) so the two-turn
/// review lifecycle — collect-only provisional, then authoritative synthesis on the SAME agent — can be
/// driven with a different answer (or a failure) per turn. The LAST script repeats for any further turn,
/// which keeps single-script agents behaving exactly as before.
/// </para>
/// <para>
/// It also implements <see cref="IDeadlineBoundedReviewLoop"/> (recording every pushed deadline in
/// <see cref="Deadlines"/>) and logs run/dispose transitions to <see cref="Lifecycle"/>, so a test can
/// assert that both turns received the SAME absolute deadline and that disposal happens only after the
/// whole collect → barrier → synthesize sequence.
/// </para>
/// <para>
/// It DECLARES <see cref="IReviewLoopSubAgentSurface"/> with a real (counting, no-op) suppression scope,
/// because the executor treats a loop that declares nothing — or one whose scope is null — as unable to keep
/// the synthesis turn from fanning out, and refuses to run it. Declaring it says "this double provably has
/// the surface it says it has"; a test that wants the refusal nulls <see cref="SuppressSpawning"/>.
/// </para>
/// <para>
/// It also implements <see cref="IResumableReviewTurn"/> with the same one-shot semantics as the hosted loop
/// it stands in for: an armed turn either REJOINS an input the host already accepted (recorded in
/// <see cref="RejoinedInputIds"/>, nothing newly accepted) or reports the id it just accepted
/// (<see cref="AcceptedInputIds"/>, from <see cref="NextInputId"/>) before producing anything.
/// </para>
/// </summary>
internal sealed class FakeMultiTurnAgent
    : IMultiTurnAgent, IDeadlineBoundedReviewLoop, IResumableReviewTurn, IReviewLoopSubAgentSurface
{
    /// <summary><see cref="Lifecycle"/> entry appended at the start of every <see cref="ExecuteRunAsync"/>.</summary>
    public const string RunEvent = "run";

    /// <summary><see cref="Lifecycle"/> entry appended when the agent is disposed.</summary>
    public const string DisposeEvent = "dispose";

    private readonly List<TurnScript> _turns = [];

    public FakeMultiTurnAgent(string runId, params IMessage[] scripted)
    {
        CurrentRunId = runId;
        SuppressSpawning = OpenCountingSuppression;
        _turns.Add(new TurnScript(scripted, null));
    }

    private FakeMultiTurnAgent(string runId, Exception throwOnRun)
    {
        CurrentRunId = runId;
        SuppressSpawning = OpenCountingSuppression;
        _turns.Add(new TurnScript([], throwOnRun));
    }

    /// <summary>An agent whose <see cref="ExecuteRunAsync"/> throws <paramref name="ex"/> when driven,
    /// modelling a provider that rejects the request (e.g. the model API's context-window 400) so the
    /// consumer's error/degrade path can be exercised.</summary>
    public static FakeMultiTurnAgent Throwing(string runId, Exception ex) => new(runId, ex);

    /// <summary>Scripts the NEXT turn's message stream (turn 2, then 3, …).</summary>
    public FakeMultiTurnAgent ThenReplies(params IMessage[] scripted)
    {
        _turns.Add(new TurnScript(scripted, null));
        return this;
    }

    /// <summary>Scripts the NEXT turn to fail with <paramref name="ex"/>.</summary>
    public FakeMultiTurnAgent ThenThrows(Exception ex)
    {
        _turns.Add(new TurnScript([], ex));
        return this;
    }

    /// <summary>Every <see cref="UserInput"/> passed to <see cref="ExecuteRunAsync"/>, in order.</summary>
    public List<UserInput> ReceivedInputs { get; } = [];

    /// <summary>Absolute deadlines pushed in via <see cref="UseDeadline"/>, in order — one per turn.</summary>
    public List<DateTimeOffset> Deadlines { get; } = [];

    /// <summary><see cref="RunEvent"/>/<see cref="DisposeEvent"/> transitions in the order they happened.</summary>
    public List<string> Lifecycle { get; } = [];

    /// <summary>The completion source the executor should poll for this double's children. Null (the default)
    /// = this loop provably has no in-process children, so the barrier falls back to the injected source.</summary>
    public IReviewSubAgentCompletionSource? CompletionSource { get; set; }

    /// <summary>
    /// The spawn-suppression scope factory. Defaults to a counting no-op scope rather than null because the
    /// executor treats a missing scope on a spawn-capable path as a defect and refuses the run; tests that
    /// want to exercise that refusal set this to null explicitly. <see cref="SuppressionScopesOpened"/> /
    /// <see cref="SuppressionScopesClosed"/> let a test assert the scope was opened AND balanced.
    /// </summary>
    public Func<IDisposable>? SuppressSpawning { get; set; }

    /// <summary>How many times the default <see cref="SuppressSpawning"/> scope was opened.</summary>
    public int SuppressionScopesOpened { get; private set; }

    /// <summary>How many times the default <see cref="SuppressSpawning"/> scope was disposed.</summary>
    public int SuppressionScopesClosed { get; private set; }

    private IDisposable OpenCountingSuppression()
    {
        SuppressionScopesOpened++;
        return new CallbackDisposable(() => SuppressionScopesClosed++);
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            onDispose();
        }
    }

    public string? CurrentRunId { get; private set; }

    public string ThreadId => "fake-thread";

    public bool IsRunning => false;

    public void UseDeadline(DateTimeOffset deadlineUtc) => Deadlines.Add(deadlineUtc);

    /// <summary>The id this double reports as newly accepted when an armed turn is SENT rather than rejoined.</summary>
    public string NextInputId { get; set; } = "input-1";

    /// <summary>Every value passed to <see cref="ArmTurnCheckpoint"/>, in order (null = "send a new turn").</summary>
    public List<string?> ArmedResumeInputIds { get; } = [];

    /// <summary>Ids reported to the caller as newly accepted — i.e. turns this double SENT.</summary>
    public List<string> AcceptedInputIds { get; } = [];

    /// <summary>Ids of turns REJOINED rather than sent; nothing new was queued for these.</summary>
    public List<string> RejoinedInputIds { get; } = [];

    private string? _armedResumeInputId;
    private Action<string>? _onInputAccepted;

    public void ArmTurnCheckpoint(string? resumeInputId, Action<string> onInputAccepted)
    {
        ArgumentNullException.ThrowIfNull(onInputAccepted);
        ArmedResumeInputIds.Add(resumeInputId);
        _armedResumeInputId = resumeInputId;
        _onInputAccepted = onInputAccepted;
    }

    /// <summary>Applies the one-shot arming to the turn that is starting, exactly as the hosted loop does:
    /// consumed either way, so a later unarmed turn neither rejoins a spent input nor re-reports one.</summary>
    private void ResolveArmedTurn()
    {
        var rejoin = _armedResumeInputId;
        var onAccepted = _onInputAccepted;
        _armedResumeInputId = null;
        _onInputAccepted = null;
        if (onAccepted is null)
        {
            return;
        }

        if (rejoin is not null)
        {
            RejoinedInputIds.Add(rejoin);
            return;
        }

        AcceptedInputIds.Add(NextInputId);
        onAccepted(NextInputId);
    }

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        // Capture the script BEFORE recording the input so the turn index is 0-based into _turns.
        var turn = _turns[Math.Min(ReceivedInputs.Count, _turns.Count - 1)];
        ReceivedInputs.Add(userInput);
        Lifecycle.Add(RunEvent);
        ResolveArmedTurn();
        if (turn.Throw is not null)
        {
            throw turn.Throw;
        }

        foreach (var message in turn.Messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => throw new NotSupportedException();

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => throw new NotSupportedException();

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RunAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Lifecycle.Add(DisposeEvent);
        return ValueTask.CompletedTask;
    }

    private sealed record TurnScript(IReadOnlyList<IMessage> Messages, Exception? Throw);
}
