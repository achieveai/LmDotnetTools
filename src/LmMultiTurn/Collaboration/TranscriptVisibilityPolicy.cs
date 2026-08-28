namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>Why a transcript read was allowed or denied.</summary>
public static class TranscriptAccessReasons
{
    /// <summary>The reader is asking about itself.</summary>
    public const string Self = "self";

    /// <summary>The reader is above the target in the hierarchy.</summary>
    public const string Ancestor = "ancestor";

    /// <summary>The collaboration is configured to let any member read any other member.</summary>
    public const string OpenCollaboration = "open_collaboration";

    /// <summary>The reader is not registered in this collaboration.</summary>
    public const string UnknownReader = "unknown_reader";

    /// <summary>The target is not registered in this collaboration.</summary>
    public const string UnknownTarget = "unknown_target";

    /// <summary>Reader and target belong to different collaborations.</summary>
    public const string CrossCollaboration = "cross_collaboration";

    /// <summary>The reader is neither the target nor above it, and the collaboration is not open.</summary>
    public const string NotAnAncestor = "not_an_ancestor";
}

/// <summary>The outcome of a transcript authorization check.</summary>
/// <param name="IsAllowed">Whether the read may proceed.</param>
/// <param name="Reason">
/// A code from <see cref="TranscriptAccessReasons"/>. Content-free by construction, so a denial can be
/// logged and returned to the caller without disclosing anything about the target.
/// </param>
public readonly record struct TranscriptAccessDecision(bool IsAllowed, string Reason);

/// <summary>
/// Decides whether one agent may read another agent's transcript.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static on purpose. This is the single choke point for the collaboration's only real
/// disclosure boundary — reading another agent's reasoning and content — and a pure function of two
/// snapshots plus a mode is a decision that can be exhaustively tested as a truth table, with no
/// ordering, no lifetime, and no shared state to get wrong.
/// </para>
/// <para>
/// Contact and read are separated deliberately. Every member can address every other member, because
/// that is what makes peer collaboration work; reading is narrower, because a peer's transcript can
/// contain material its own caller never intended to publish. Under
/// <see cref="TranscriptVisibilityMode.Ancestors"/> only the chain above a target — which already sees
/// that material through its own children — may read it.
/// </para>
/// </remarks>
public static class TranscriptVisibilityPolicy
{
    /// <summary>Decides whether <paramref name="reader"/> may read <paramref name="target"/>.</summary>
    /// <param name="reader">The agent asking, or null when it is not registered.</param>
    /// <param name="target">The agent being asked about, or null when it is not registered.</param>
    /// <param name="mode">The collaboration's configured visibility.</param>
    /// <remarks>
    /// An unregistered reader or target is denied rather than treated as an error: the caller is
    /// frequently a model-driven tool invocation with an arbitrary string, and "no" is the honest and
    /// safe answer to a question about an agent that does not exist.
    /// </remarks>
    public static TranscriptAccessDecision Evaluate(
        AgentDirectoryEntry? reader,
        AgentDirectoryEntry? target,
        TranscriptVisibilityMode mode
    )
    {
        if (reader is null)
        {
            return Deny(TranscriptAccessReasons.UnknownReader);
        }

        if (target is null)
        {
            return Deny(TranscriptAccessReasons.UnknownTarget);
        }

        // Checked before anything else that could allow: a shared identifier across two collaborations
        // must never be enough to read across the boundary between them.
        if (!string.Equals(reader.CollaborationId, target.CollaborationId, StringComparison.Ordinal))
        {
            return Deny(TranscriptAccessReasons.CrossCollaboration);
        }

        if (string.Equals(reader.AgentId, target.AgentId, StringComparison.Ordinal))
        {
            return Allow(TranscriptAccessReasons.Self);
        }

        if (target.AncestorAgentIds.Contains(reader.AgentId, StringComparer.Ordinal))
        {
            return Allow(TranscriptAccessReasons.Ancestor);
        }

        return mode == TranscriptVisibilityMode.Open
            ? Allow(TranscriptAccessReasons.OpenCollaboration)
            : Deny(TranscriptAccessReasons.NotAnAncestor);
    }

    private static TranscriptAccessDecision Allow(string reason)
    {
        return new TranscriptAccessDecision(true, reason);
    }

    private static TranscriptAccessDecision Deny(string reason)
    {
        return new TranscriptAccessDecision(false, reason);
    }
}
