using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Hand-written <see cref="IMultiTurnAgent"/> double for the agent-collection tests. It records the
/// <see cref="UserInput"/> each run received and replays a scripted message stream from
/// <see cref="ExecuteRunAsync"/>, so the collect-only logic can be verified without constructing a real
/// provider loop. Background-loop members throw — these tests drive only the single-run entry point.
/// </summary>
internal sealed class FakeMultiTurnAgent : IMultiTurnAgent
{
    private readonly IReadOnlyList<IMessage> _scripted;
    private readonly Exception? _throwOnRun;

    public FakeMultiTurnAgent(string runId, params IMessage[] scripted)
    {
        CurrentRunId = runId;
        _scripted = scripted;
    }

    private FakeMultiTurnAgent(string runId, Exception throwOnRun)
    {
        CurrentRunId = runId;
        _scripted = [];
        _throwOnRun = throwOnRun;
    }

    /// <summary>An agent whose <see cref="ExecuteRunAsync"/> throws <paramref name="ex"/> when driven,
    /// modelling a provider that rejects the request (e.g. the model API's context-window 400) so the
    /// consumer's error/degrade path can be exercised.</summary>
    public static FakeMultiTurnAgent Throwing(string runId, Exception ex) => new(runId, ex);

    /// <summary>Every <see cref="UserInput"/> passed to <see cref="ExecuteRunAsync"/>, in order.</summary>
    public List<UserInput> ReceivedInputs { get; } = [];

    public string? CurrentRunId { get; private set; }

    public string ThreadId => "fake-thread";

    public bool IsRunning => false;

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ReceivedInputs.Add(userInput);
        if (_throwOnRun is not null)
        {
            throw _throwOnRun;
        }

        foreach (var message in _scripted)
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
