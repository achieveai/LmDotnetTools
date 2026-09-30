using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// Makes the two layers one agent to everything outside them. The planner acts on every input the
/// pair receives; the executor acts only on the planner's tool calls and, when the host shares it, sees a
/// copy of those inputs for reference.
/// </summary>
/// <remarks>
/// <para>
/// Two taps. On the planner, <see cref="PlannerInputMirror"/> copies each accepted input to the
/// executor's reference queue and lets it through. On the executor loop, <see cref="ExecutorInputRedirect"/>
/// sends everything that is not a delegation (a sub-agent's completion notice, a peer agent's message,
/// a relayed question) to the planner instead, where the mirror then copies it back as reference. So a
/// notice raised inside the executor reaches the layer that decides, and the executor never starts a run
/// of its own.
/// </para>
/// <para>
/// The reference copies cost no model call: the executor reads them at the start of its next delegated
/// call, inside a <c>&lt;conversation-context&gt;</c> block.
/// </para>
/// </remarks>
public static class DualLayerInputRouting
{
    /// <summary>Installs both taps. Call once both loops exist and before the first input arrives.</summary>
    /// <param name="planner">The planner loop.</param>
    /// <param name="executorLoop">The executor loop.</param>
    /// <param name="executor">The planner's route to the executor.</param>
    /// <param name="logger">Logs each redirected input.</param>
    /// <param name="shareReferenceContext">
    /// False keeps the executor's reference copy empty: it then sees only the planner's calls. Its inputs
    /// are still redirected to the planner. For measuring what the reference context is worth.
    /// </param>
    public static void Link(
        MultiTurnAgentBase planner,
        MultiTurnAgentBase executorLoop,
        AgentDelegatedToolExecutor executor,
        ILogger? logger = null,
        bool shareReferenceContext = true
    )
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(executorLoop);
        ArgumentNullException.ThrowIfNull(executor);

        planner.InputTap = new PlannerInputMirror(executor, executorLoop, shareReferenceContext);
        executorLoop.InputTap = new ExecutorInputRedirect(planner, logger);
    }

    /// <summary>
    /// The executor loop linked to <paramref name="planner"/>, when it is the planner of a pair. The
    /// executor owns the pair's sub-agents and collaboration, so a host that looks up a conversation's
    /// agents through its pooled loop must look through the planner to it.
    /// </summary>
    public static bool TryGetExecutorLoop(
        MultiTurnAgentBase planner,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MultiTurnAgentBase? executorLoop
    )
    {
        ArgumentNullException.ThrowIfNull(planner);
        executorLoop = (planner.InputTap as PlannerInputMirror)?.ExecutorLoop;
        return executorLoop is not null;
    }

    /// <summary>
    /// True for the one kind of input the executor runs: a single user turn the host composed for it,
    /// a delegated call with or without a reference block ahead of it.
    /// </summary>
    internal static bool IsDelegation(UserInput input) =>
        input.Messages is [TextMessage { Role: Role.User, Text: { } text }]
        && (
            text.StartsWith('<' + DualLayerPrompts.DelegationTag + '>', StringComparison.Ordinal)
            || text.StartsWith('<' + DualLayerPrompts.ReferenceTag + '>', StringComparison.Ordinal)
        );

    /// <summary>Copies every input the planner accepts to the executor's reference queue.</summary>
    internal sealed class PlannerInputMirror(
        AgentDelegatedToolExecutor executor,
        MultiTurnAgentBase executorLoop,
        bool shareReferenceContext = true
    ) : IInputTap
    {
        public MultiTurnAgentBase ExecutorLoop { get; } = executorLoop;

        public ValueTask<InputTapDecision> OnInputAsync(UserInput input, CancellationToken cancellationToken)
        {
            if (shareReferenceContext)
            {
                executor.Shadow(input.Messages);
            }

            return ValueTask.FromResult(InputTapDecision.Queue);
        }
    }

    /// <summary>Sends everything but a delegation to the planner. The executor never runs on its own.</summary>
    internal sealed class ExecutorInputRedirect(MultiTurnAgentBase planner, ILogger? logger) : IInputTap
    {
        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        public async ValueTask<InputTapDecision> OnInputAsync(UserInput input, CancellationToken cancellationToken)
        {
            if (IsDelegation(input))
            {
                return InputTapDecision.Queue;
            }

            _logger.LogInformation(
                "Redirecting {MessageCount} message(s) ({Kinds}) from executor to planner {PlannerThreadId}",
                input.Messages.Count,
                string.Join(",", input.Messages.Select(m => m.GetType().Name)),
                planner.ThreadId
            );
            _ = await planner.SendAsync(input, cancellationToken).ConfigureAwait(false);
            return InputTapDecision.Taken;
        }
    }
}
