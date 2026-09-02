using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// The thread-id convention for a sub-agent's own transcript: <c>subagent-{scope}-{agentId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// #705. A sub-agent's id is an ordinal (<c>agent-1</c>, <c>agent-2</c>, …) numbered under its ROOT
/// conversation, which makes it readable and referenceable but unique only within that conversation.
/// Thread ids are a store-wide key, so the transcript's id carries a 12-hex <em>scope</em>: a digest
/// of the root thread id. Every agent in one conversation's hierarchy shares the scope — a parent that
/// is itself <c>subagent-{scope}-agent-N</c> hands the same scope to its children — so a host can form
/// a grandchild's thread id from its parent's without knowing the root, and two conversations' <c>agent-1</c>s
/// never share a transcript.
/// </para>
/// <para>
/// Pre-#705 threads were <c>subagent-{12-hex guid}-{8-hex tag}</c> with the agent id being everything
/// after the prefix. <see cref="TryGetAgentId"/> still reads those so persisted rosters keep loading.
/// </para>
/// </remarks>
public static class SubAgentThreadIds
{
    /// <summary>Reserved prefix of every sub-agent transcript thread id.</summary>
    public const string Prefix = "subagent-";

    /// <summary>Prefix of an ordinal agent id (<c>agent-N</c>).</summary>
    public const string AgentIdPrefix = "agent-";

    /// <summary>Hex characters in the scope segment (48 bits of the root thread id's SHA-256).</summary>
    private const int ScopeTagLength = 12;

    private static readonly Regex OrdinalShape = new(
        @"^agent-[1-9][0-9]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex ScopedShape = new(
        @"^subagent-(?<scope>[0-9a-f]{12})-(?<agent>agent-[1-9][0-9]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>The ordinal agent id for <paramref name="ordinal"/> (1-based).</summary>
    public static string AgentIdFor(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return $"{AgentIdPrefix}{ordinal}";
    }

    /// <summary>True when <paramref name="agentId"/> is an ordinal id (<c>agent-N</c>, N ≥ 1).</summary>
    public static bool IsOrdinalAgentId(string? agentId) => agentId is not null && OrdinalShape.IsMatch(agentId);

    /// <summary>
    /// The transcript thread id of the sub-agent <paramref name="agentId"/> spawned by the agent whose
    /// thread is <paramref name="parentThreadId"/>. The parent may be the root conversation or another
    /// sub-agent; either way the child lands in the root's scope.
    /// </summary>
    /// <remarks>
    /// An id that is not an ordinal is a pre-#705 id (<c>{guid}-{tag}</c>), whose transcript lives at
    /// <c>subagent-{id}</c> with no scope segment — that shape is returned unchanged so a host resolving a
    /// persisted roster keeps finding old transcripts by the same call.
    /// </remarks>
    public static string For(string? parentThreadId, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return IsOrdinalAgentId(agentId) ? $"{Prefix}{ScopeTag(parentThreadId)}-{agentId}" : $"{Prefix}{agentId}";
    }

    /// <summary>
    /// The scope segment for children of <paramref name="parentThreadId"/>: inherited verbatim from a
    /// parent that is itself a scoped sub-agent thread, otherwise the digest of the parent (root) id.
    /// A null/empty parent (a CLI-backed parent with no thread) digests to a stable value so the shape
    /// stays uniform.
    /// </summary>
    public static string ScopeTag(string? parentThreadId)
    {
        if (parentThreadId is not null && ScopedShape.Match(parentThreadId) is { Success: true } scoped)
        {
            return scoped.Groups["scope"].Value;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(parentThreadId ?? string.Empty));
        return Convert.ToHexString(digest.AsSpan(0, ScopeTagLength / 2)).ToLowerInvariant();
    }

    /// <summary>
    /// Reads the agent id back out of a sub-agent thread id: the <c>agent-N</c> segment of a scoped id,
    /// or — for a legacy <c>subagent-{guid}-{tag}</c> thread — everything after the prefix. False for
    /// anything that is not a sub-agent thread at all.
    /// </summary>
    public static bool TryGetAgentId(string? threadId, out string agentId)
    {
        agentId = string.Empty;
        if (threadId is null || !threadId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (ScopedShape.Match(threadId) is { Success: true } scoped)
        {
            agentId = scoped.Groups["agent"].Value;
            return true;
        }

        agentId = threadId[Prefix.Length..];
        return agentId.Length > 0;
    }
}
