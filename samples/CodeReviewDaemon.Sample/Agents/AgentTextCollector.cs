using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The one collect-only drive of an <see cref="IMultiTurnAgent"/> shared by the daemon agents
/// (Review, Judge, Knowledge). It sends the prompt as a single user turn and gathers the assistant's
/// <b>finalized</b> prose: <see cref="TextUpdateMessage"/> streaming deltas are a distinct type and are
/// skipped (the finalizing <see cref="TextMessage"/> carries the full text), and thinking text
/// (<see cref="TextMessage.IsThinking"/>) is the agent's scratch work, not output. Multiple assistant
/// messages join on a newline. No posting, no provider/sandbox wiring — only the agent seam — so each
/// agent's logic stays verifiable against a fake.
/// </summary>
internal static class AgentTextCollector
{
    public static async Task<AgentTextResult> CollectAsync(
        IMultiTurnAgent agent,
        string input,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var userInput = new UserInput([new TextMessage { Text = input, Role = Role.User }]);

        var text = new StringBuilder();
        var assistantMessageCount = 0;
        string? runId = null;

        await foreach (
            var message in agent.ExecuteRunAsync(userInput, cancellationToken).ConfigureAwait(false)
        )
        {
            if (message is not TextMessage finalized)
            {
                continue;
            }

            runId ??= finalized.RunId;

            if (finalized.IsThinking || finalized.Role != Role.Assistant || string.IsNullOrEmpty(finalized.Text))
            {
                continue;
            }

            if (text.Length > 0)
            {
                _ = text.Append('\n');
            }

            _ = text.Append(finalized.Text);
            assistantMessageCount++;
        }

        return new AgentTextResult(text.ToString(), runId ?? agent.CurrentRunId, assistantMessageCount);
    }
}

/// <summary>
/// The collected assistant text, the run id that produced it (the first run id seen, falling back to the
/// agent's <see cref="IMultiTurnAgent.CurrentRunId"/>), and how many finalized assistant messages were
/// joined.
/// </summary>
internal sealed record AgentTextResult(string Text, string? RunId, int AssistantMessageCount);
