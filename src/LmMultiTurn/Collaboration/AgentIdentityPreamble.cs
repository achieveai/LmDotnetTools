namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// The one or two sentences that tell a collaborating agent who it is.
/// </summary>
/// <remarks>
/// <para>
/// Production evidence (1279 persisted conversations) shows models addressing peers as <c>lead</c>,
/// <c>parent</c>, <c>manager</c> and <c>Revobot</c> — role words and product names, never a mistyped
/// ordinal. The model was never told a name to use, so it invented one. The fix is to state the name
/// where the model cannot miss it.
/// </para>
/// <para>
/// The parent is named by NAME rather than id, because the entire purpose of the line is that the
/// child can address it. When the parent is not resolvable — it has not registered yet, or the row is
/// gone after a restart — the clause is omitted rather than filled with an id the model would then
/// send messages to. A missing sentence is recoverable; a wrong address is not.
/// </para>
/// <para>
/// Deliberately short. It is prepended to EVERY turn's system prompt for every agent, so each extra
/// clause is paid for on every request in the conversation.
/// </para>
/// </remarks>
internal static class AgentIdentityPreamble
{
    /// <summary>The identity block, or null when this agent is not in a collaboration.</summary>
    internal static string? Compose(AgentCollaborationSetup? collaboration)
    {
        if (collaboration is null)
        {
            return null;
        }

        var identity =
            $"You are `{collaboration.Name}` (`{collaboration.AgentId}`). "
            + $"Other agents address you as `{collaboration.Name}`.";

        var parentName = ParentNameOf(collaboration);
        return parentName is null ? identity : $"{identity} You report to `{parentName}`.";
    }

    /// <summary>
    /// The caller's prompt with the identity block in front of it, or the prompt unchanged when there
    /// is no collaboration — a non-collaborating loop must keep byte-identical prompts.
    /// </summary>
    internal static string? Prepend(string? systemPrompt, AgentCollaborationSetup? collaboration)
    {
        if (Compose(collaboration) is not { } identity)
        {
            return systemPrompt;
        }

        return string.IsNullOrWhiteSpace(systemPrompt) ? identity : $"{identity}\n\n{systemPrompt}";
    }

    private static string? ParentNameOf(AgentCollaborationSetup collaboration)
    {
        if (collaboration.Context.ParentAgentId is not { } parentId)
        {
            return null;
        }

        return collaboration.Directory.FindById(parentId)?.Name;
    }
}
