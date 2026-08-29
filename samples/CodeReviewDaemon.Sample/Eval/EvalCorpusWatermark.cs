using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Where the eval corpus reader got to, persisted so it survives the process.
/// <para>
/// This is the piece of state <c>#400</c> is really about. <see cref="DaemonCorpusReader"/> reads a
/// window and returns the edge it reached; something has to remember that edge between sweeps, and
/// "nobody advanced it" is the same silent freeze the per-call window was introduced to end — the
/// snapshot hash stays stable, so every comparability refusal downstream stays perfectly happy,
/// because the corpus genuinely has not changed. Whoever wires the consumer owns this value, so it
/// is a named type with its own tests rather than a field somewhere.
/// </para>
/// <para>
/// It is stored in <c>poll_cursor</c> through the store's existing
/// <see cref="ReviewStore.SaveCursor"/> / <see cref="ReviewStore.ReadCursor"/> pair rather than in a
/// table of its own. That table is already the daemon's general "(provider, scope) → where this
/// reader got to" record: it is keyed exactly that way, its payload is documented as opaque to the
/// storage layer, and it already tolerates a missing, blank or version-mismatched row by telling the
/// caller to resync. A migration adding a second table with those same semantics would be a second
/// answer to a question the schema already answers.
/// </para>
/// </summary>
internal sealed class EvalCorpusWatermark
{
    /// <summary>
    /// The <c>poll_cursor.provider</c> partition these cursors live under. Not a PR provider —
    /// <c>github</c> and <c>azuredevops</c> name where reviews come <i>from</i>, and this names a
    /// reader over what they produced, so the two can never collide on a scope.
    /// </summary>
    public const string CursorProvider = "eval-corpus";

    /// <summary>
    /// Payload schema version. A bump makes every stored cursor unreadable and
    /// <see cref="ReviewStore.ReadCursor"/> answers <i>resync</i>, which for this reader means
    /// starting again from the beginning of the recorded history: expensive, and correct, because
    /// re-reading a review is idempotent where skipping one is not.
    /// </summary>
    public const int CursorVersion = 1;

    private readonly ReviewStore _store;
    private readonly ILogger<EvalCorpusWatermark>? _logger;

    public EvalCorpusWatermark(ReviewStore store, ILogger<EvalCorpusWatermark>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>
    /// The exclusive lower edge the next window starts at, or <c>0</c> when none is recorded — the
    /// beginning of the history.
    /// </summary>
    /// <param name="corpusId">The corpus whose cursor to read; the <c>scope</c> half of the key.</param>
    public long Read(string corpusId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);

        var result = _store.ReadCursor(CursorProvider, corpusId, CursorVersion);

        if (result.ShouldResync || result.Cursor is not { } cursor)
        {
            return 0;
        }

        // A payload this reader wrote and cannot now read is a real event and not a shrug: the
        // sweep silently restarts from the beginning of the history, which is safe but re-reads
        // everything. Zero is returned rather than thrown on for the reason the store resyncs
        // rather than throwing — a corrupt cursor must not wedge the daemon — but it is said out
        // loud, because a silent reset is indistinguishable from a cursor that never advanced.
        if (!TryParsePayload(cursor.CursorPayload, out var afterReviewRunId) || afterReviewRunId < 0)
        {
            _logger?.LogWarning(
                "Eval corpus cursor for '{CorpusId}' holds an unreadable payload; the next sweep "
                    + "restarts from the beginning of the recorded history.",
                corpusId
            );
            return 0;
        }

        return afterReviewRunId;
    }

    /// <summary>Records the edge a load reached, so the next one starts after it.</summary>
    /// <param name="corpusId">The corpus whose cursor to write; the <c>scope</c> half of the key.</param>
    /// <param name="afterReviewRunId">The review run id the last window reached. Never negative.</param>
    public void Save(string corpusId, long afterReviewRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterReviewRunId);

        _store.SaveCursor(
            new OpaqueCursor
            {
                Provider = CursorProvider,
                Scope = corpusId,
                CursorVersion = CursorVersion,
                CursorPayload = JsonSerializer.Serialize(new EvalCursorPayload(afterReviewRunId)),

                // The same number in a second column would be a second source of truth for one
                // fact, and the two can disagree. The payload is the one ReadCursor validates
                // non-blank, so it is the one that carries the value.
                HighWaterMark = null,
            }
        );
    }

    private static bool TryParsePayload(string payload, out long afterReviewRunId)
    {
        afterReviewRunId = 0;

        try
        {
            if (JsonSerializer.Deserialize<EvalCursorPayload>(payload) is not { } parsed)
            {
                return false;
            }

            // An ABSENT field is not a zero. The property is nullable precisely so that this
            // reads as unparseable rather than as "start from the beginning": a positional record
            // with a non-nullable long binds `{}` to default(long) without throwing, so a payload
            // that lost its only field would deserialize cleanly to cursor 0 and restart the sweep
            // over the whole history — silently, which is the one outcome the warning below exists
            // to prevent. `{"AfterReviewRunId":null}` lands here too, where it used to arrive as a
            // JsonException; both are the same fact and now take the same path.
            if (parsed.AfterReviewRunId is not { } value)
            {
                return false;
            }

            afterReviewRunId = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The cursor payload's shape. Named fields rather than a bare number so a future
    /// window dimension is additive.
    /// <para>
    /// The id is <b>nullable on the way in</b> even though a written cursor always carries one:
    /// that is what lets the reader tell an absent field from a recorded zero. A non-nullable
    /// <c>long</c> here would let <c>{}</c> bind to 0 and read as a legitimate "start from the
    /// beginning".
    /// </para>
    /// </summary>
    private sealed record EvalCursorPayload(long? AfterReviewRunId);
}
