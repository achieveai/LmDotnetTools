using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// The one collect-only drive of an <see cref="IMultiTurnAgent"/>: it sends the prompt as a single
/// user turn and gathers the assistant's prose. Shared by every headless consumer that needs an
/// agent's answer as a string — the code-review daemon's agents and the LmEval judge harness alike —
/// because the generation-id reconstruction below is subtle enough that a second implementation
/// would drift from this one rather than agree with it. Thinking text (<see cref="TextMessage.IsThinking"/> / <see cref="TextUpdateMessage.IsThinking"/>)
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
public static class AgentTextCollector
{
    /// <summary>Drives one collect-only turn and returns the assistant text it produced.</summary>
    /// <param name="agent">The agent to drive. Its lifetime stays with the caller.</param>
    /// <param name="input">The single user turn to send.</param>
    /// <param name="cancellationToken">Cancellation.</param>
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
        string? finalizedGenerationId = null;
        // Fallback: incremental assistant TextUpdateMessage deltas accumulated in arrival order.
        var streamedText = new StringBuilder();
        string? streamedGenerationId = null;
        string? runId = null;

        await foreach (var message in agent.ExecuteRunAsync(userInput, cancellationToken).ConfigureAwait(false))
        {
            switch (message)
            {
                case TextMessage finalized:
                    runId ??= finalized.RunId;
                    if (
                        !finalized.IsThinking
                        && finalized.Role == Role.Assistant
                        && !string.IsNullOrEmpty(finalized.Text)
                    )
                    {
                        // Keep only the LATEST generation's answer. A multi-turn tool-using agent narrates
                        // its process in intermediate turns and emits the finished answer in the final turn,
                        // each a distinct generation; concatenating all of them leaks the narration into the
                        // collected result. Resetting on a new GenerationId keeps just the final turn's text
                        // (mirrors SubAgentManager's sub-agent-result reconstruction).
                        if (!string.Equals(finalizedGenerationId, finalized.GenerationId, StringComparison.Ordinal))
                        {
                            finalizedGenerationId = finalized.GenerationId;
                            _ = finalizedText.Clear();
                            finalizedCount = 0;
                        }

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
                        // Same reconstruction as the finalized path: keep only the latest generation's
                        // streamed deltas so a tool-using agent's inter-turn narration is dropped and the
                        // result is just the final answer.
                        if (!string.Equals(streamedGenerationId, update.GenerationId, StringComparison.Ordinal))
                        {
                            streamedGenerationId = update.GenerationId;
                            _ = streamedText.Clear();
                        }

                        _ = streamedText.Append(update.Text);
                    }

                    break;

                case StreamRecoveryMessage recovery:
                    // The stream ended because this consumer was dropped, not because the run
                    // finished. Returning what was collected so far would hand the caller a silently
                    // truncated answer that reads exactly like a complete one.
                    throw new InvalidOperationException(
                        $"The agent's message stream was severed ({recovery.Reason}) before the run completed, "
                            + "so the collected text would be a silent truncation of the agent's answer."
                    );

                default:
                    break;
            }
        }

        // Prefer the provider's finalized message(s) when present; otherwise fall back to the accumulated
        // streaming deltas. One or the other — never summed — so the text is never doubled.
        var (text, assistantMessageCount) =
            finalizedCount > 0
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
public sealed record AgentTextResult(string Text, string? RunId, int AssistantMessageCount);
