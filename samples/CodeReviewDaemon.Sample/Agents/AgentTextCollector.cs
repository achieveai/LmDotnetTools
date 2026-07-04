using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The one collect-only drive of an <see cref="IMultiTurnAgent"/> shared by the daemon agents
/// (Review, Judge, Knowledge). It sends the prompt as a single user turn and gathers the assistant's
/// prose. Thinking text (<see cref="TextMessage.IsThinking"/> / <see cref="TextUpdateMessage.IsThinking"/>)
/// is the agent's scratch work, not output, and is skipped.
/// <para>
/// A headless consumer of <see cref="IMultiTurnAgent.ExecuteRunAsync"/> must gather the streamed
/// <see cref="TextUpdateMessage"/> deltas itself: the loop publishes those deltas to subscribers BEFORE
/// its <c>MessageUpdateJoinerMiddleware</c> synthesizes the finalized <see cref="TextMessage"/>, so with
/// providers whose streaming path emits only deltas (e.g. the Copilot-backed Anthropic agent) the joined
/// message never reaches this subscriber. So the assistant text is accumulated from the incremental
/// deltas, and a provider-emitted finalized <see cref="TextMessage"/> (when one does arrive) takes
/// precedence — never both, so the text is not double-counted.
/// </para>
/// No posting, no provider/sandbox wiring — only the agent seam — so each agent's logic stays verifiable
/// against a fake.
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

        // Finalized assistant TextMessages, if the provider emits any.
        var finalizedText = new StringBuilder();
        var finalizedCount = 0;
        // Fallback: incremental assistant TextUpdateMessage deltas accumulated in arrival order.
        var streamedText = new StringBuilder();
        string? runId = null;

        await foreach (
            var message in agent.ExecuteRunAsync(userInput, cancellationToken).ConfigureAwait(false)
        )
        {
            switch (message)
            {
                case TextMessage finalized:
                    runId ??= finalized.RunId;
                    if (!finalized.IsThinking && finalized.Role == Role.Assistant && !string.IsNullOrEmpty(finalized.Text))
                    {
                        if (finalizedText.Length > 0)
                        {
                            _ = finalizedText.Append('\n');
                        }

                        _ = finalizedText.Append(finalized.Text);
                        finalizedCount++;
                    }

                    break;

                case TextUpdateMessage update:
                    runId ??= update.RunId;
                    if (!update.IsThinking && update.Role == Role.Assistant && !string.IsNullOrEmpty(update.Text))
                    {
                        _ = streamedText.Append(update.Text);
                    }

                    break;

                default:
                    break;
            }
        }

        // Prefer the provider's finalized message(s) when present; otherwise fall back to the accumulated
        // streaming deltas. One or the other — never summed — so the text is never doubled.
        var (text, assistantMessageCount) = finalizedCount > 0
            ? (finalizedText.ToString(), finalizedCount)
            : (streamedText.ToString(), streamedText.Length > 0 ? 1 : 0);

        return new AgentTextResult(text, runId ?? agent.CurrentRunId, assistantMessageCount);
    }
}

/// <summary>
/// The collected assistant text, the run id that produced it (the first run id seen, falling back to the
/// agent's <see cref="IMultiTurnAgent.CurrentRunId"/>), and how many assistant messages were joined (a
/// finalized-message count, or 1 when the text was assembled from streaming deltas).
/// </summary>
internal sealed record AgentTextResult(string Text, string? RunId, int AssistantMessageCount);
