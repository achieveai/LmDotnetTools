namespace LmStreaming.Sample.Identity;

/// <summary>
/// The one place the existence-hiding "unknown thread" refusal payload is built, for every surface
/// that has to answer for a conversation id it will not admit exists.
/// </summary>
/// <remarks>
/// <para>
/// The refusal only hides anything while every surface writes it IDENTICALLY. A caller who can tell
/// the WebSocket handshake's 404 from the REST route's - by a different phrasing, a different code, a
/// different field order - has learned which of the two refused, and from that whether the id names
/// something. Both surfaces answer for the same ids, so the two bodies are one fact, not two.
/// </para>
/// <para>
/// Shared as a FACTORY rather than as a copied literal on purpose. Two literals agreeing today is not
/// a property anything maintains: an edit to one is a normal-looking change that silently reopens the
/// oracle, and no compiler or reviewer sees the other. With one factory the bodies cannot drift,
/// because there is only one of them.
/// </para>
/// <para>
/// This is scoped to the existence-HIDING refusal specifically. A refusal that already admits the id
/// names something - a viewer grantee refused a write, say - keeps its own 403 and its own reason,
/// and must NOT be routed through here: making those identical too would withhold information from a
/// caller the host has already shown the conversation to, and misdescribe the refusal while doing it.
/// </para>
/// </remarks>
public static class UnknownThreadRefusal
{
    /// <summary>The refusal code, carried in the body and in the refusal-code header.</summary>
    public const string Code = "unknown_thread";

    /// <summary>
    /// The refusal payload for <paramref name="threadId"/>. Field order is part of the body: a
    /// different order serialises to different bytes and is enough to tell two surfaces apart.
    /// </summary>
    /// <param name="threadId">The conversation id to report as not found.</param>
    public static object Body(string threadId) =>
        new { error = $"Conversation '{threadId}' not found.", code = Code };
}
