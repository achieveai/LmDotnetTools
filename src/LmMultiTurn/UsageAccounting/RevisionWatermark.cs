namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Tracks a per-conversation monotonic revision sequence and the highest <b>gap-free prefix</b> that
///     has been committed. Publishing prefix <c>N</c> proves every revision <c>1..N</c> is committed — the
///     complete-prefix watermark invariant for conversation usage (issue #196): a projection stamped with
///     <c>N</c> cannot have skipped an earlier revision.
/// </summary>
/// <remarks>
///     Allocation and commit are deliberately separable so a caller can reserve a revision before its
///     record is durably visible. The prefix only advances across a contiguous run of committed revisions,
///     so an out-of-order or delayed commit never lets the watermark jump past a still-missing revision.
/// </remarks>
public sealed class RevisionWatermark
{
    private readonly object _gate = new();
    private readonly SortedSet<long> _committedAbovePrefix = [];
    private long _allocated;
    private long _prefix;

    /// <summary>Reserves the next revision. It does not count toward the prefix until committed.</summary>
    public long Allocate()
    {
        lock (_gate)
        {
            return ++_allocated;
        }
    }

    /// <summary>
    ///     Marks <paramref name="revision" /> committed and advances the gap-free prefix as far as the
    ///     contiguous run of committed revisions allows. Idempotent and safe out-of-order.
    /// </summary>
    public void Commit(long revision)
    {
        lock (_gate)
        {
            if (revision <= _prefix)
            {
                return;
            }

            _ = _committedAbovePrefix.Add(revision);
            while (_committedAbovePrefix.Remove(_prefix + 1))
            {
                _prefix++;
            }
        }
    }

    /// <summary>The highest <c>N</c> such that every revision <c>1..N</c> has been committed (no gaps).</summary>
    public long Prefix
    {
        get
        {
            lock (_gate)
            {
                return _prefix;
            }
        }
    }
}
