namespace LmStreaming.Sample.Controllers;

/// <summary>
/// The sandbox release route's commit could not store the conversation's new working directory after its sandbox was
/// confirmed gone. Thrown by the route's commit and caught only at the release await, which answers 503
/// <c>sandbox_release_failed</c>; it tells this failure apart from a gateway refusal of the DELETE.
/// </summary>
internal sealed class MoveNotStoredException(Exception inner)
    : Exception("The conversation's new working directory could not be stored.", inner);
