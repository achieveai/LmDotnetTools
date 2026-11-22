extern alias Reviewd;

using System.Runtime.InteropServices;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

// Reviewd keeps its database in WAL. The live databases already are; a database Reviewd creates must be too, or a
// commit deletes a -journal that a Windows scanner may hold, fails, and leaves a hot journal that every read-only
// connection refuses with "attempt to write a readonly database".
public sealed class ReviewdDatabaseJournalTests : LoggingTestBase
{
    private static readonly string[] S_sidecars = ["-journal", "-wal", "-shm"];

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-journal-{Guid.NewGuid():N}.db");

    public ReviewdDatabaseJournalTests(ITestOutputHelper output)
        : base(output)
    {
        // The file name only: the temp folder's full path carries the user's account name.
        Logger.LogDebug(
            "Journal test setup: database {DatabaseFile} on {OS} ({Architecture}), SQLite {SqliteVersion}",
            Path.GetFileName(_database),
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture,
            SqliteVersion()
        );
    }

    [Fact]
    public void A_database_reviewd_creates_is_in_wal_mode()
    {
        CreateWithRow("v1");
        LogSidecars("after create");

        JournalMode().Should().Be("wal");
    }

    // A database made before the seam set WAL is in rollback mode. The first open through the seam converts it, and
    // the conversion keeps what it held.
    [Fact]
    public void An_existing_rollback_mode_database_is_converted_to_wal_with_its_rows()
    {
        using (
            var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database }.ToString())
        )
        {
            legacy.Open();
            using var mode = legacy.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode=DELETE";
            _ = mode.ExecuteScalar();
            InsertRow(legacy, "v1");
        }

        ReviewdSqlitePools.Clear(_database);
        LogSidecars("after the rollback-mode create");
        JournalMode().Should().Be("delete", "the file starts in rollback mode");

        using (PrThreadBindings.Connection(_database)) { }

        ReviewdSqlitePools.Clear(_database);
        LogSidecars("after the seam's open");
        JournalMode().Should().Be("wal", "the seam converts the file it opens");
        Read().Should().Be("v1");
    }

    // A read-only command (pr pick, capacity, the batch's reads) can be the first to open the database after every
    // writer has closed it, when SQLite has removed the -wal and -shm. It must still read.
    [Fact]
    public void A_read_only_open_reads_a_fresh_wal_database_no_writer_holds()
    {
        CreateWithRow("v1");
        ReviewdSqlitePools.Clear(_database);
        LogSidecars("after every writer closed");
        File.Exists(_database + "-wal").Should().BeFalse("the last writer to close removes the WAL");
        File.Exists(_database + "-shm").Should().BeFalse("the last writer to close removes the shared-memory index");

        Read().Should().Be("v1");
    }

    // A scanner holding the database's sidecars open, without delete sharing, is what failed the batch tests under
    // load. In WAL a commit deletes nothing, so it still succeeds and read-only readers see it.
    [SkippableFact]
    public void A_commit_succeeds_while_a_scanner_holds_every_sidecar_open()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "only Windows refuses to delete a file another handle holds");
        CreateWithRow("v1");
        using var writer = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _database, Mode = SqliteOpenMode.ReadWrite }.ToString()
        );
        writer.Open();
        // Plain BEGIN/COMMIT rather than a SqliteTransaction: after a failed commit, disposing one throws a rollback
        // error that would hide the commit's own.
        _ = Execute(writer, "BEGIN");
        Execute(writer, "UPDATE cache SET value='v2' WHERE key='k'").Should().Be(1);
        LogSidecars("inside the write transaction");
        var held = S_sidecars
            .Select(suffix => _database + suffix)
            .Where(File.Exists)
            .Select(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            .ToList();
        Logger.LogDebug(
            "Scanner holds {HeldCount} sidecars: {HeldFiles}",
            held.Count,
            held.Select(stream => Path.GetFileName(stream.Name)).ToArray()
        );
        try
        {
            held.Should().NotBeEmpty("an open write transaction has a journal or a WAL on disk");
            writer.Invoking(open => Execute(open, "COMMIT")).Should().NotThrow();
        }
        finally
        {
            held.ForEach(stream => stream.Dispose());
        }

        LogSidecars("after the commit");
        Read().Should().Be("v2");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReviewdSqlitePools.Clear(_database);
            var files = Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_database) + "*");
            Logger.LogDebug(
                "Journal test teardown deletes {FileCount} files: {Files}",
                files.Length,
                files.Select(Path.GetFileName).ToArray()
            );
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }

        base.Dispose(disposing);
    }

    private void CreateWithRow(string value)
    {
        using var connection = PrThreadBindings.Connection(_database);
        InsertRow(connection, value);
    }

    private static void InsertRow(SqliteConnection connection, string value)
    {
        _ = new SqliteKvStore(connection);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO cache(key,value) VALUES('k',@value)";
        insert.Parameters.AddWithValue("@value", value);
        insert.ExecuteNonQuery();
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    // Which sidecars exist and how big they are; a leftover -journal is the hot journal that breaks read-only opens.
    private void LogSidecars(string stage)
    {
        var state = S_sidecars.ToDictionary(
            suffix => suffix,
            suffix => File.Exists(_database + suffix) ? new FileInfo(_database + suffix).Length : (long?)null
        );
        Logger.LogDebug("Sidecars {Stage}: {@Sidecars} (null = absent)", stage, state);
    }

    private SqliteConnection ReadOnly() =>
        new(new SqliteConnectionStringBuilder { DataSource = _database, Mode = SqliteOpenMode.ReadOnly }.ToString());

    private string? JournalMode()
    {
        using var reader = ReadOnly();
        reader.Open();
        using var command = reader.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return command.ExecuteScalar() as string;
    }

    private string? Read()
    {
        using var reader = ReadOnly();
        reader.Open();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key='k'";
        return command.ExecuteScalar() as string;
    }

    private static string SqliteVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection.ServerVersion;
    }
}
