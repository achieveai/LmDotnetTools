namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The leased slot's store is structurally unusable (missing/broken <c>.git</c>, failed health probe) and
/// must be re-cloned before prepare can be retried. Drives the recovery ladder's re-clone escalation.
/// </summary>
internal sealed class SlotNeedsRecloneException(string message) : Exception(message);

/// <summary>
/// A prepare git step failed in a way classified as slot corruption (a stale lock that survived cleaning, a
/// dirty tree, a broken object, or a submodule that would not initialize). Like
/// <see cref="SlotNeedsRecloneException"/> it drives the re-clone escalation, but it originates mid-sequence
/// rather than from the up-front health probe.
/// </summary>
internal sealed class SlotCorruptException(string message) : Exception(message);
