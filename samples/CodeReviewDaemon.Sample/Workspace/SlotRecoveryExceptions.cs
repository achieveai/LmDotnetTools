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

/// <summary>
/// A host path the daemon was about to create in, walk, or wipe could not be established as CONTAINED: it is a
/// symlink or junction, or its attributes could not be read at all (see <see cref="HostPathGuard"/>).
/// <para>
/// It is deliberately NOT one of the two above, and the difference is the whole reason it exists. Those two mean
/// "the store's CONTENT is unusable", and the answer to both is a re-clone — which deletes the store and writes a
/// fresh one at the same path. This one means "the daemon cannot establish where that path leads", and a re-clone
/// is then the one response that must not happen: it wipes and then clones through the very entry the refusal
/// declined to cross. A handler that lumps this in with corruption performs the redirected write itself.
/// </para>
/// <para>
/// It also says something the other two do not: the condition belongs to the ADDRESS and not to the attempt.
/// Nothing about a later try makes a planted entry contained or a denied directory listable, so a slot whose
/// paths raised this is retired rather than retried — see <see cref="IReviewSlotPool.RetireAsync"/>.
/// </para>
/// </summary>
internal sealed class SlotHostPathRefusedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
