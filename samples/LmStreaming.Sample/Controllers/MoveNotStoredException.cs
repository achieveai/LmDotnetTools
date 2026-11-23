namespace LmStreaming.Sample.Controllers;

/// <summary>
/// The sandbox release route's commit did not store the conversation's move after its sandbox was confirmed gone:
/// the conversation or mode store failed, the thread was deleted, or its mode kept changing. Thrown by the route's
/// commit and caught only at the release await, which answers 503 <c>sandbox_release_failed</c>; it tells this
/// failure apart from a gateway refusal of the DELETE.
/// </summary>
internal sealed class MoveNotStoredException(Exception inner)
    : Exception("The conversation's new working directory was not stored.", inner);
