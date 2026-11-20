namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Reads what a Reviewd database holds on disk, for tests that prove a step wrote nothing.
/// </summary>
internal static class ReviewdDatabaseBytes
{
    /// <summary>
    /// Returns the database file's bytes followed by its <c>-wal</c>'s, if it has one. Reviewd's database is in WAL,
    /// so a commit lands in the <c>-wal</c> and reaches the main file only at a checkpoint; a connection that is still
    /// open, or a read-only one that closes last, never makes that checkpoint. The main file alone can miss a write.
    /// Reads share the files, so an open connection does not block them.
    /// </summary>
    public static async Task<byte[]> ReadAsync(string database)
    {
        using var copy = new MemoryStream();
        foreach (var path in new[] { database, database + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await stream.CopyToAsync(copy);
        }

        return copy.ToArray();
    }
}
