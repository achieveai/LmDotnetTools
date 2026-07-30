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
/// </summary>
internal sealed class FakeMultiTurnAgent : IMultiTurnAgent, IDeadlineBoundedReviewLoop
{
    /// <summary><see cref="Lifecycle"/> entry appended at the start of every <see cref="ExecuteRunAsync"/>.</summary>
    public const string RunEvent = "run";

    /// <summary><see cref="Lifecycle"/> entry appended when the agent is disposed.</summary>
    public const string DisposeEvent = "dispose";

    private readonly List<TurnScript> _turns = [];

    public FakeMultiTurnAgent(string runId, params IMessage[] scripted)
    {
        CurrentRunId = runId;
        _turns.Add(new TurnScript(scripted, null));
    }

    private FakeMultiTurnAgent(string runId, Exception throwOnRun)
    {
        CurrentRunId = runId;
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

    public string? CurrentRunId { get; private set; }

    public string ThreadId => "fake-thread";

    public bool IsRunning => false;

    public void UseDeadline(DateTimeOffset deadlineUtc) => Deadlines.Add(deadlineUtc);

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        // Capture the script BEFORE recording the input so the turn index is 0-based into _turns.
        var turn = _turns[Math.Min(ReceivedInputs.Count, _turns.Count - 1)];
        ReceivedInputs.Add(userInput);
        Lifecycle.Add(RunEvent);
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
