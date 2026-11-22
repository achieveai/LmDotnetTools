extern alias Reviewd;

using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

// The no-write checks compare ReviewdDatabaseBytes before and after a step. Under WAL a commit can stay in the -wal
// until a checkpoint, so the helper must see a commit the main file does not hold yet.
public sealed class ReviewdDatabaseBytesTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-bytes-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_commit_held_only_in_the_wal_changes_what_ReadAsync_returns()
    {
        using (var create = PrThreadBindings.Connection(_database))
        {
            _ = new SqliteKvStore(create);
        }

        // The last writer to close checkpoints, so the baseline holds every earlier write and there is no -wal.
        ReviewdSqlitePools.Clear(_database);
        var mainBefore = ReadMainFile();
        var before = await ReviewdDatabaseBytes.ReadAsync(_database);

        // An open connection does not checkpoint on commit, so this write lives only in the -wal.
        using var writer = PrThreadBindings.Connection(_database);
        using var insert = writer.CreateCommand();
        insert.CommandText = "INSERT INTO cache(key,value) VALUES('k','v')";
        insert.ExecuteNonQuery();

        ReadMainFile().Should().Equal(mainBefore, "the commit is still only in the -wal");
        (await ReviewdDatabaseBytes.ReadAsync(_database))
            .Should()
            .NotEqual(before, "a write held in the -wal is still a write");
    }

    public void Dispose()
    {
        ReviewdSqlitePools.Clear(_database);
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_database) + "*"))
        {
            File.Delete(file);
        }
    }

    // Shares the file, because the writer still holds it open.
    private byte[] ReadMainFile()
    {
        using var stream = new FileStream(_database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
