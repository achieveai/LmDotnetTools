using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// The smallest <see cref="IMultiTurnAgent"/> that can answer one collect-only turn. Only
/// <see cref="ExecuteRunAsync"/> is implemented, because that is the only member the judge's
/// default transport touches; every other member throws so a future change that starts using one
/// fails loudly instead of quietly reading a default.
/// </summary>
internal sealed class ScriptedAgent(string reply) : IMultiTurnAgent
{
    /// <summary>The prompts this agent was sent, in call order.</summary>
    public List<string> Prompts { get; } = [];

    public string? CurrentRunId => "run-1";

    public string ThreadId => "thread-1";

    public bool IsRunning => false;

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        Prompts.AddRange(userInput.Messages.OfType<TextMessage>().Select(m => m.Text));
        await Task.Yield();
        yield return new TextMessage
        {
            Text = reply,
            Role = Role.Assistant,
            RunId = "run-1",
            GenerationId = "gen-1",
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    public Task StopAsync(TimeSpan? timeout = null) => throw new NotSupportedException();
}
