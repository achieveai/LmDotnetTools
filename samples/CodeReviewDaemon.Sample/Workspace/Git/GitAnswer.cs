namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>
/// What a git probe said about a yes/no question — including the case where it said NOTHING.
/// <para>
/// The distinction is the whole reason this is not a <see cref="bool"/>. A probe that could not run, or whose
/// output did not survive, has not answered "no"; it has left the question open. Collapsing the two lets a
/// FAILURE of the check masquerade as a RESULT of the check, which is the shape behind both defects this type
/// was introduced for: an empty <c>status</c> read as a clean tree, and a failed blob lookup read as "these
/// bytes are not the recorded blob". Which way an <see cref="Unknown"/> is then resolved is the caller's
/// decision to make deliberately — the point here is only that it is a decision the caller can still see.
/// </para>
/// <para>
/// <see cref="Unknown"/> is <c>default</c> on purpose: a value nobody assigned reports as unanswered rather
/// than as a negative answer somebody meant.
/// </para>
/// </summary>
internal enum GitAnswer
{
    /// <summary>git did not answer — the probe failed, or its output did not arrive. NOT <see cref="No"/>.</summary>
    Unknown = 0,

    /// <summary>git ran and answered in the affirmative.</summary>
    Yes,

    /// <summary>git ran and answered in the negative.</summary>
    No,
}
