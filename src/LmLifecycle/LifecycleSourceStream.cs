using System.Diagnostics.CodeAnalysis;

namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The stream kinds that own a canonical ordering of lifecycle events.
/// </summary>
/// <remarks>
/// Ordering is per conversation, never global. A process-wide counter would leak cross-tenant
/// activity volume and would make a gap in one stream indistinguishable from unrelated traffic
/// in another.
/// </remarks>
public static class LifecycleSourceStreamKinds
{
    /// <summary>Events produced by an agent thread. Identified by a thread id.</summary>
    public const string Thread = "thread";

    /// <summary>Events produced by a sandbox session. Identified by a session id.</summary>
    public const string Sandbox = "sandbox";
}

/// <summary>
/// Builds and parses the typed source-stream identifiers that carry per-stream ordering.
/// </summary>
/// <remarks>
/// A source-stream identifier has the form <c>{kind}:{id}</c> — for example
/// <c>thread:8f2c1a</c> or <c>sandbox:3b9e04</c>. The identifier is opaque to subscribers
/// beyond its kind prefix, and it is <b>not</b> an authorization subject: an owner is never
/// inferred from it (see ADR 0005).
/// </remarks>
public static class LifecycleSourceStream
{
    private const char Separator = ':';

    /// <summary>Builds the source-stream identifier for an agent thread.</summary>
    /// <param name="threadId">The thread identifier. Must be non-empty.</param>
    /// <returns><c>thread:{threadId}</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="threadId"/> is null or whitespace.</exception>
    public static string ForThread(string threadId) =>
        Build(LifecycleSourceStreamKinds.Thread, threadId, nameof(threadId));

    /// <summary>Builds the source-stream identifier for a sandbox session.</summary>
    /// <param name="sessionId">The sandbox session identifier. Must be non-empty.</param>
    /// <returns><c>sandbox:{sessionId}</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null or whitespace.</exception>
    public static string ForSandbox(string sessionId) =>
        Build(LifecycleSourceStreamKinds.Sandbox, sessionId, nameof(sessionId));

    /// <summary>
    /// Splits a source-stream identifier into its kind and subject id.
    /// </summary>
    /// <param name="sourceStreamId">The identifier to parse.</param>
    /// <param name="kind">The stream kind, when parsing succeeds.</param>
    /// <param name="subjectId">The subject identifier, when parsing succeeds.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="sourceStreamId"/> is a well-formed
    /// <c>{kind}:{id}</c> pair with both parts non-empty.
    /// </returns>
    /// <remarks>
    /// The kind is deliberately <b>not</b> validated against
    /// <see cref="LifecycleSourceStreamKinds"/>: a subscriber compiled today must be able to
    /// forward a stream kind introduced tomorrow.
    /// </remarks>
    public static bool TryParse(
        string? sourceStreamId,
        [NotNullWhen(true)] out string? kind,
        [NotNullWhen(true)] out string? subjectId
    )
    {
        kind = null;
        subjectId = null;

        if (string.IsNullOrEmpty(sourceStreamId))
        {
            return false;
        }

        var separatorIndex = sourceStreamId.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex == sourceStreamId.Length - 1)
        {
            return false;
        }

        kind = sourceStreamId[..separatorIndex];
        subjectId = sourceStreamId[(separatorIndex + 1)..];
        return true;
    }

    private static string Build(string kind, string id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException(
                "A source-stream subject id must be non-empty.",
                parameterName
            );
        }

        return string.Concat(kind, Separator.ToString(), id);
    }
}
