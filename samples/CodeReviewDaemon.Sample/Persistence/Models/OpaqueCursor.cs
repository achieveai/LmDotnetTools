namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// A provider-opaque poll cursor (plan §12). GitHub and ADO have incompatible pagination models, so
/// the daemon never interprets <see cref="CursorPayload"/> at the storage layer — it stores a
/// versioned opaque blob plus optional well-known fields. Readers tolerate missing / invalid / old /
/// future-version cursors by resyncing (see <see cref="CursorReadResult"/>).
/// </summary>
internal sealed record OpaqueCursor
{
    public required string Provider { get; init; }

    /// <summary>Repo/query identity this cursor advances (e.g. <c>owner/repo:open-prs</c>).</summary>
    public required string Scope { get; init; }

    /// <summary>Schema version of <see cref="CursorPayload"/>; lets readers reject old/future shapes.</summary>
    public required int CursorVersion { get; init; }

    /// <summary>Opaque provider payload (JSON/TEXT). Never parsed by the storage layer.</summary>
    public required string CursorPayload { get; init; }

    public string? HighWaterMark { get; init; }
    public string? Etag { get; init; }
    public string? Continuation { get; init; }
    public string? SinceTimestamp { get; init; }
}

/// <summary>
/// Outcome of reading a cursor for a poll. <see cref="ShouldResync"/> is <c>true</c> whenever the
/// caller must restart from scratch — the cursor is absent, has an unparseable/empty payload, or its
/// version does not match the reader's supported version (older or newer). When <c>false</c>,
/// <see cref="Cursor"/> is the usable cursor.
/// </summary>
internal readonly record struct CursorReadResult(bool ShouldResync, OpaqueCursor? Cursor)
{
    public static CursorReadResult Resync() => new(true, null);

    public static CursorReadResult Usable(OpaqueCursor cursor) => new(false, cursor);
}
