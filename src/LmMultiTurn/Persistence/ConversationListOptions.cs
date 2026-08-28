using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Which of a conversation's two timestamps a listing orders by.
/// </summary>
/// <remarks>
/// <para>
/// The two orderings answer different questions and a sidebar needs both. <see cref="LastUsed"/>
/// answers "what did I touch most recently", which is the right default for resuming work.
/// <see cref="Created"/> answers "what did I start most recently", which is the only stable one:
/// <c>last_updated</c> is bumped on EVERY completed run, so a conversation's position under
/// <see cref="LastUsed"/> moves under the reader's feet while they page through it. Offset paging
/// over a mutable sort key duplicates and drops rows; ordering by creation time is what makes a
/// long scroll-back reproducible.
/// </para>
/// <para>
/// The enum values are pinned (<c>LastUsed = 0</c>) so the default of a freshly constructed
/// <see cref="ConversationListOptions"/> is today's behavior rather than whichever member happens
/// to be declared first after a later edit.
/// </para>
/// </remarks>
public enum ConversationSortOrder
{
    /// <summary>Order by <see cref="ThreadMetadata.LastUpdated"/> descending. Today's behavior.</summary>
    LastUsed = 0,

    /// <summary>
    /// Order by the conversation's creation time descending, as derived by
    /// <see cref="ConversationListOptions.CreationTimestampOf"/>.
    /// </summary>
    Created = 1,
}

/// <summary>
/// The PRESENTATION shape of a conversation listing - which id spaces the caller is asking to be
/// left out, and which of the two timestamps to order by - bound before the query runs so the store
/// can apply both while it still has the whole candidate set.
/// </summary>
/// <remarks>
/// <para>
/// Listing is a FILTER, not a loop. This type exists because that principle was stated for the
/// authorization scope (see <see cref="ConversationListScope"/>) and then immediately violated one
/// statement later for the agent-owned exclusion: the controller took a page from the store and
/// only THEN dropped <c>subagent-*</c>/<c>workflow-*</c> rows from it. Because
/// <see cref="ThreadMetadata.LastUpdated"/> is bumped on every completed run and background
/// sub-agent and workflow runs are constant, those rows crowd the front of a
/// <c>last_updated DESC</c> ordering. On a live deployment of 302 threads, 256 of them agent-owned,
/// the top-50 page came back with 45 agent-owned rows: the sidebar showed five real conversations
/// and every older one silently vanished, with no signal anywhere that a page had been trimmed.
/// </para>
/// <para>
/// This is deliberately NOT folded into <see cref="ConversationListScope"/>, even though both end
/// up as a <c>WHERE</c> clause. The scope is an AUTHORIZATION predicate: it decides what the caller
/// is ALLOWED to see, it is derived from the principal rather than from the request, and getting it
/// wrong leaks another tenant's data. These options are a PRESENTATION predicate: they decide what
/// this particular SURFACE is asking for - the conversation sidebar excludes agent-owned threads,
/// the sub-agent panel is built from exactly those same threads - they are derived from the caller's
/// query rather than from their identity, and getting them wrong shows the wrong list to someone who
/// was entitled to both. Merging them would let a caller widen an authorization predicate by
/// changing a query string, and would make "the sub-agent panel asks for agent-owned threads"
/// indistinguishable from a privilege escalation at every site that inspects a scope.
/// </para>
/// <para>
/// A null <see cref="ConversationListOptions"/> at a call site means "no exclusion, last-used
/// order" - byte for byte the behavior every existing caller had before this type existed. That is
/// why every parameter is optional and why <see cref="Default"/> is the same thing spelled
/// explicitly: the fix must not change what a caller that did not ask for it receives.
/// </para>
/// </remarks>
public sealed record ConversationListOptions
{
    /// <summary>
    /// Thread-id prefixes to leave OUT of the listing, compared with
    /// <see cref="StringComparison.Ordinal"/>. Empty (the default) excludes nothing.
    /// </summary>
    /// <remarks>
    /// A prefix set rather than a predicate delegate on purpose: the SQL store has to express this
    /// same exclusion in a <c>WHERE</c> clause, and a delegate cannot cross that boundary. Anything
    /// richer than "starts with" would have no SQL spelling and would force the SQLite store back
    /// into fetch-then-filter, which is the bug this type exists to close.
    /// </remarks>
    public IReadOnlyList<string> ExcludedThreadIdPrefixes { get; init; } = [];

    /// <summary>
    /// Which timestamp the listing is ordered by, descending. Defaults to
    /// <see cref="ConversationSortOrder.LastUsed"/>, which is what every caller got before this
    /// type existed.
    /// </summary>
    public ConversationSortOrder SortOrder { get; init; } = ConversationSortOrder.LastUsed;

    /// <summary>
    /// The explicit spelling of "what a null options argument means": no exclusion, last-used order.
    /// </summary>
    /// <remarks>
    /// Handed to stores so their null-coalescing has one shared answer, and so a test that wants to
    /// assert "options changed nothing" has something to name. If this ever stops matching the
    /// behavior of passing <c>null</c>, the null path is the bug.
    /// </remarks>
    public static ConversationListOptions Default { get; } = new();

    /// <summary>
    /// Whether the given row survives the PRESENTATION filter. Prefix exclusion ONLY - this
    /// deliberately says nothing about whether the caller is permitted to see the row, which is
    /// <see cref="ConversationListScope.Admits"/>'s job and only its job.
    /// </summary>
    /// <remarks>
    /// The single in-memory spelling of the SQL exclusion, so the file and in-memory stores cannot
    /// drift from the SQLite one - the same reason <see cref="ConversationListScope.Admits"/>
    /// exists.
    /// </remarks>
    /// <param name="metadata">The candidate row.</param>
    public bool Admits(ThreadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (ExcludedThreadIdPrefixes.Count == 0)
        {
            return true;
        }

        foreach (var prefix in ExcludedThreadIdPrefixes)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                // An empty prefix matches every id, which would return an empty listing rather than
                // an unfiltered one. Treat it as "no exclusion asked for" instead of silently
                // emptying the sidebar - a caller that built its prefix list from configuration and
                // got a blank entry deserves the harmless reading, not the catastrophic one.
                continue;
            }

            if (metadata.ThreadId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// When the conversation was created, in Unix milliseconds - the ONE shared spelling of that
    /// question, derived from the thread id because <see cref="ThreadMetadata"/> has no creation
    /// field to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ThreadMetadata"/> records <see cref="ThreadMetadata.LastUpdated"/> and nothing
    /// else about time; there is no <c>created_at</c> anywhere in the record or in the SQLite
    /// schema. What DOES carry the creation instant is the id itself: the client mints a thread as
    /// <c>thread-{epochMillis}-{random}</c>, so the middle segment is the creation timestamp,
    /// recorded at creation and immutable thereafter - which is exactly the property
    /// <see cref="ThreadMetadata.LastUpdated"/> lacks.
    /// </para>
    /// <para>
    /// Not every id is minted that way. Agent-owned threads use their own
    /// <c>subagent-*</c>/<c>workflow-*</c> shapes, and conversations provisioned before the server
    /// adopted this form carry a bare <c>thread-{guid:N}</c> that is already persisted and cannot be
    /// rewritten. For any id that
    /// does not parse, this falls back to <see cref="ThreadMetadata.LastUpdated"/>. The fallback is
    /// approximate by construction - a long-running conversation sorts as though it were created at
    /// its most recent run - and that is the deliberate trade: an unparseable id has NO better
    /// evidence of its creation time available, and ordering it by the one timestamp that does exist
    /// keeps it in the list. Dropping it, or sorting it to position zero, would lose a real
    /// conversation from the sidebar, which is the failure this whole change exists to fix.
    /// </para>
    /// <para>
    /// It lives here, as one static helper, rather than being open-coded at each store, precisely so
    /// the file and in-memory stores cannot drift to different orderings for the same row. The
    /// SQLite store does not reimplement it in SQL for the same reason - it refuses the
    /// <see cref="ConversationSortOrder.Created"/> ordering outright rather than approximate this
    /// with a <c>substr</c>/<c>CAST</c> expression that could silently diverge from it.
    /// </para>
    /// </remarks>
    /// <param name="metadata">The row whose creation time is wanted.</param>
    /// <returns>
    /// The epoch-millisecond timestamp encoded in the thread id, or
    /// <see cref="ThreadMetadata.LastUpdated"/> when the id does not carry one.
    /// </returns>
    public static long CreationTimestampOf(ThreadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        const string MintedPrefix = "thread-";

        var threadId = metadata.ThreadId;
        if (!threadId.StartsWith(MintedPrefix, StringComparison.Ordinal))
        {
            return metadata.LastUpdated;
        }

        // The trailing "-{random}" segment is required, not optional. Without it a "thread-{guid:N}"
        // id whose hex happened to be all digits would parse as an absurd timestamp, and the row
        // would sort to the top of a Created listing forever. Demanding the delimiter means only the
        // shape the client actually mints is read as a timestamp.
        var timestampStart = MintedPrefix.Length;
        var timestampEnd = threadId.IndexOf('-', timestampStart);
        if (timestampEnd < 0)
        {
            return metadata.LastUpdated;
        }

        var timestampSpan = threadId.AsSpan(timestampStart, timestampEnd - timestampStart);
        return long.TryParse(timestampSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var createdAt)
            ? createdAt
            : metadata.LastUpdated;
    }

    /// <summary>
    /// Orders a candidate set by this listing's <see cref="SortOrder"/>, descending - the shared
    /// in-memory spelling used by the file and in-memory stores so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tie-break on <see cref="ThreadMetadata.ThreadId"/> is not cosmetic, and it applies to
    /// BOTH orderings. Offset paging asks the store for a page, then asks again for the next one;
    /// if two rows compare equal and nothing else separates them, their relative order is decided
    /// by whatever sequence the store happened to enumerate. Should that sequence differ between
    /// the two calls, one row is returned twice and another is never returned at all - and the one
    /// that is never returned simply vanishes from the sidebar, which is the failure this whole
    /// type exists to prevent.
    /// </para>
    /// <para>
    /// It would be a mistake to think only <see cref="ConversationSortOrder.Created"/> needs this.
    /// It is if anything MORE necessary for <see cref="ConversationSortOrder.LastUsed"/>, which is
    /// the default: <see cref="ThreadMetadata.LastUpdated"/> is bumped on every completed run, so
    /// that key genuinely moves between one page request and the next, and
    /// <see cref="FileConversationStore"/> synthesizes it from <c>UtcNow</c> at read time for a
    /// directory that has no metadata file - a value that is different on every call. Ties are not
    /// exotic either: <see cref="InMemoryConversationStore"/> enumerates a
    /// <c>ConcurrentDictionary</c>, whose order is undocumented and shifts as it resizes, and LINQ's
    /// sort is stable, which means equal rows inherit exactly that unstable order.
    /// </para>
    /// <para>
    /// Thread ids are unique, so this is a total order in both modes - there is no residual pair
    /// for enumeration order to decide. Any store that answers a listing in SQL must spell the same
    /// tie-break in its <c>ORDER BY</c>, or it will page differently from these two.
    /// </para>
    /// </remarks>
    /// <param name="candidates">The rows to order.</param>
    internal IOrderedEnumerable<ThreadMetadata> Order(IEnumerable<ThreadMetadata> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return SortOrder == ConversationSortOrder.Created
            ? candidates
                .OrderByDescending(CreationTimestampOf)
                .ThenByDescending(m => m.ThreadId, StringComparer.Ordinal)
            : candidates
                .OrderByDescending(m => m.LastUpdated)
                .ThenByDescending(m => m.ThreadId, StringComparer.Ordinal);
    }
}
