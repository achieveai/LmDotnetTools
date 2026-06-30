using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A throwaway on-disk SQLite database in the temp directory. On-disk (not <c>:memory:</c>) so the
/// WAL journal mode and re-open semantics the migration runner relies on behave as they do in
/// production. Deletes the file (and WAL/SHM siblings) on dispose.
/// </summary>
internal sealed class TempSqliteDatabase : IDisposable
{
    public TempSqliteDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codereviewdaemon-db-tests",
            Guid.NewGuid().ToString("N") + ".db");
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path }.ToString();
    }

    public string Path { get; }

    public string ConnectionString { get; }

    public void Dispose()
    {
        // Pooled connections keep the file handle open; clear the pool so the file can be deleted.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
