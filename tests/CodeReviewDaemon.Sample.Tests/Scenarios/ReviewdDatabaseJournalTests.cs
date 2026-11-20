extern alias Reviewd;

using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

// Reviewd keeps its database in WAL. The live databases already are; a database Reviewd creates must be too, or a
// commit deletes a -journal that a Windows scanner may hold, fails, and leaves a hot journal that every read-only
// connection refuses with "attempt to write a readonly database".
public sealed class ReviewdDatabaseJournalTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-journal-{Guid.NewGuid():N}.db");

    [Fact]
    public void A_database_reviewd_creates_is_in_wal_mode()
    {
        CreateWithRow("v1");

        using var reader = ReadOnly();
        reader.Open();
        using var command = reader.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        command.ExecuteScalar().Should().Be("wal");
    }

    // A read-only command (pr pick, capacity, the batch's reads) can be the first to open the database after every
    // writer has closed it, when SQLite has removed the -wal and -shm. It must still read.
    [Fact]
    public void A_read_only_open_reads_a_fresh_wal_database_no_writer_holds()
    {
        CreateWithRow("v1");
        ReviewdSqlitePools.Clear(_database);
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
        var held = new[] { "-journal", "-wal", "-shm" }
            .Select(suffix => _database + suffix)
            .Where(File.Exists)
            .Select(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            .ToList();
        try
        {
            held.Should().NotBeEmpty("an open write transaction has a journal or a WAL on disk");
            writer.Invoking(open => Execute(open, "COMMIT")).Should().NotThrow();
        }
        finally
        {
            held.ForEach(stream => stream.Dispose());
        }

        Read().Should().Be("v2");
    }

    public void Dispose()
    {
        ReviewdSqlitePools.Clear(_database);
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_database) + "*"))
        {
            File.Delete(file);
        }
    }

    private void CreateWithRow(string value)
    {
        using var connection = PrThreadBindings.Connection(_database);
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

    private SqliteConnection ReadOnly() =>
        new(new SqliteConnectionStringBuilder { DataSource = _database, Mode = SqliteOpenMode.ReadOnly }.ToString());

    private string? Read()
    {
        using var reader = ReadOnly();
        reader.Open();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key='k'";
        return command.ExecuteScalar() as string;
    }
}
