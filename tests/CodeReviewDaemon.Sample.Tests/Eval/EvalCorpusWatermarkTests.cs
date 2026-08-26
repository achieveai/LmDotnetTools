using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The watermark on its own, rather than through a sweep. It is the piece of state #400 is really
/// about — "nobody advanced it" is the silent freeze the per-call window was introduced to end — and
/// every one of its edges was previously reachable only via <see cref="EvalCorpusSweep"/>, which
/// means a change to the sweep could have been what made them pass.
/// <para>
/// Driven over a real <see cref="ReviewStore"/> on a temp SQLite file, because every claim here is
/// about a row in <c>poll_cursor</c> and not about a field.
/// </para>
/// </summary>
public sealed class EvalCorpusWatermarkTests : IDisposable
{
    private const string CorpusId = "daemon-reviews";

    private readonly TempSqliteDatabase _db = new();
    private ReviewStore _store;

    public EvalCorpusWatermarkTests() => _store = new ReviewStore(_db.ConnectionString);

    public void Dispose()
    {
        _store.Dispose();
        _db.Dispose();
    }

    private void Restart()
    {
        _store.Dispose();
        _store = new ReviewStore(_db.ConnectionString);
    }

    private EvalCorpusWatermark Watermark(ILogger<EvalCorpusWatermark>? logger = null) =>
        new(_store, logger);

    /// <summary>Writes a cursor row at an arbitrary version, behind the watermark's back.</summary>
    private void WriteRawCursor(string payload, int version = EvalCorpusWatermark.CursorVersion) =>
        _store.SaveCursor(
            new OpaqueCursor
            {
                Provider = EvalCorpusWatermark.CursorProvider,
                Scope = CorpusId,
                CursorVersion = version,
                CursorPayload = payload,
                HighWaterMark = null,
            }
        );

    /// <summary>Reads this reader's row straight out of the store, at whatever version it holds.</summary>
    private OpaqueCursor? RawCursor(int version) =>
        _store.ReadCursor(EvalCorpusWatermark.CursorProvider, CorpusId, version).Cursor;

    // ---- the round trip ------------------------------------------------------------------------

    /// <summary>
    /// The claim the whole consumer rests on: what one sweep recorded is what the next one reads,
    /// across a process. A cursor held in memory satisfies every in-process test and fails in
    /// production on the first redeploy.
    /// </summary>
    [Fact]
    public void A_saved_edge_is_read_back_after_a_restart()
    {
        Watermark().Save(CorpusId, 4_242);

        Restart();

        Watermark().Read(CorpusId).Should().Be(4_242);
    }

    /// <summary>
    /// No row at all is the beginning of the recorded history, not an error. This is the first
    /// sweep on a fresh database, so it must be the quiet case — a warning here would fire once for
    /// every daemon that ever starts.
    /// </summary>
    [Fact]
    public void An_unrecorded_cursor_starts_at_the_beginning_of_the_history_without_warning()
    {
        var logger = new CapturingLogger<EvalCorpusWatermark>();

        Watermark(logger).Read(CorpusId).Should().Be(0);

        logger.WarningCount("unreadable payload").Should().Be(0);
    }

    /// <summary>
    /// A corpus this reader has never touched is a different scope, and scopes do not leak into one
    /// another — the pair (provider, scope) is the key, and reading the wrong one would silently
    /// hand a sweep somebody else's window.
    /// </summary>
    [Fact]
    public void A_cursor_is_scoped_to_its_corpus()
    {
        Watermark().Save(CorpusId, 99);

        Watermark().Read("some-other-corpus").Should().Be(0);
    }

    // ---- the payload's own edges ---------------------------------------------------------------

    /// <summary>
    /// A negative stored id is unreadable rather than usable. It cannot come from
    /// <see cref="EvalCorpusWatermark.Save"/>, which refuses one — so a negative id in the row means
    /// the payload was written by something else or corrupted, and handing it to
    /// <c>ListReviewRuns</c> would throw out of the sweep instead of costing it one restart.
    /// </summary>
    [Fact]
    public void A_negative_stored_id_is_unreadable_rather_than_a_window_edge()
    {
        WriteRawCursor("{\"AfterReviewRunId\":-5}");

        var logger = new CapturingLogger<EvalCorpusWatermark>();

        Watermark(logger).Read(CorpusId).Should().Be(0);
        logger.WarningCount("unreadable payload").Should().Be(1);
    }

    /// <summary>
    /// <see cref="EvalCorpusWatermark.Save"/> refuses a negative id outright rather than writing one
    /// for the reader above to reject: the reader's guard is the second line, and a value that could
    /// never legitimately be recorded must not be recordable.
    /// </summary>
    [Fact]
    public void Saving_a_negative_edge_is_refused()
    {
        var save = () => Watermark().Save(CorpusId, -1);

        save.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_blank_corpus_id_is_refused_on_both_sides()
    {
        var read = () => Watermark().Read("  ");
        var save = () => Watermark().Save("  ", 1);

        read.Should().Throw<ArgumentException>();
        save.Should().Throw<ArgumentException>();
    }

    // ---- versioning ----------------------------------------------------------------------------

    /// <summary>
    /// A row written at a version this reader does not support resyncs: <c>ReadCursor</c> refuses to
    /// hand over a payload it cannot promise the shape of, and for this reader resyncing means
    /// starting again from the beginning of the recorded history. Expensive, and correct —
    /// re-reading a review is idempotent where skipping one is not.
    /// </summary>
    [Fact]
    public void A_cursor_written_at_an_unsupported_version_resyncs_to_the_beginning()
    {
        WriteRawCursor(
            JsonSerializer.Serialize(new { AfterReviewRunId = 7_000L }),
            version: EvalCorpusWatermark.CursorVersion + 1
        );

        var logger = new CapturingLogger<EvalCorpusWatermark>();

        Watermark(logger).Read(CorpusId).Should().Be(0);

        // Quiet, deliberately, and this is the half that distinguishes a resync from corruption: a
        // version mismatch is a documented, expected outcome of a schema bump, where an unreadable
        // payload at the SUPPORTED version means something wrote nonsense. Warning on both would
        // make the warning stop identifying the second.
        logger.WarningCount("unreadable payload").Should().Be(0);
    }

    /// <summary>
    /// The downgrade, recorded as a decision rather than left to be discovered. A reader at an older
    /// version reads a newer row, is told to resync, restarts from the beginning — and then
    /// <see cref="EvalCorpusWatermark.Save"/> overwrites that newer row at its own version, so the
    /// newer reader's forward progress is gone.
    /// <para>
    /// That is accepted, on the same argument the rest of this design rests on: the cost of the
    /// clobber is that the newer reader re-reads history it has already covered, and re-reading a
    /// review is idempotent. Refusing to write instead would leave the older reader permanently
    /// unable to record its own progress — it would re-read the entire history on every sweep, for
    /// ever, which is the silent freeze itself rather than a recovery from one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_downgraded_reader_clobbers_a_newer_cursor_and_that_is_the_cheaper_loss()
    {
        WriteRawCursor(
            JsonSerializer.Serialize(new { AfterReviewRunId = 7_000L }),
            version: EvalCorpusWatermark.CursorVersion + 1
        );

        Watermark().Save(CorpusId, 12);

        RawCursor(EvalCorpusWatermark.CursorVersion + 1)
            .Should()
            .BeNull("the newer row is gone rather than kept alongside");
        Watermark().Read(CorpusId).Should().Be(12);
    }
}
