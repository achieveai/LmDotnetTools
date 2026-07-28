namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// What an attempt to resolve a deferred tool call did.
/// </summary>
/// <remarks>
/// <para>
/// The distinction that matters to a caller is <b>"can I retry this?"</b>. A webhook receiver that
/// gets a transport error has to decide between redelivering and dropping the result on the floor,
/// and an exception tells it nothing about which. So every terminal state is a value here:
/// <see cref="Resolved"/> and <see cref="Duplicate"/> are success, <see cref="NotFound"/> and
/// <see cref="Conflict"/> are permanent rejections not worth retrying, and
/// <see cref="StoreFailed"/> and <see cref="Cancelled"/> left the call untouched and are worth
/// retrying exactly as sent.
/// </para>
/// <para>
/// <see cref="MultiTurnAgentLoop.ResolveToolCallAsync"/> keeps its throwing shape for callers that
/// want failures to propagate; this is the same operation with the failure reported rather than
/// raised.
/// </para>
/// </remarks>
public enum ResolveToolCallOutcome
{
    /// <summary>The call was outstanding and this attempt resolved it.</summary>
    Resolved,

    /// <summary>
    /// The call was already resolved with identical content. Nothing changed and nothing needed to
    /// — this is what a redelivered webhook looks like, and it is a success.
    /// </summary>
    Duplicate,

    /// <summary>
    /// No tool call with that identifier is deferred or resolved on this thread. Retrying will not
    /// change that.
    /// </summary>
    NotFound,

    /// <summary>
    /// The call is already resolved, or is being resolved right now, with <em>different</em>
    /// content. The first resolution stands; nothing was overwritten.
    /// </summary>
    Conflict,

    /// <summary>
    /// The durable store could not take the resolution. The call is still deferred and the attempt
    /// is safe to retry unchanged.
    /// </summary>
    StoreFailed,

    /// <summary>
    /// The attempt was cancelled before it committed. The call is still deferred and the attempt is
    /// safe to retry unchanged.
    /// </summary>
    Cancelled,
}
