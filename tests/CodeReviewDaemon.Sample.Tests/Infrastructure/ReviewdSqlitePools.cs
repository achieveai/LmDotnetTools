using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Releases the idle pooled handles Reviewd.Sample left open on one database file, so a test can delete it.
/// Use it instead of <see cref="SqliteConnection.ClearAllPools"/>, which clears every pool in the process and
/// can dispose a connection another test is still opening.
/// </summary>
internal static class ReviewdSqlitePools
{
    /// <summary>
    /// Clears the pool of each connection string Reviewd.Sample builds for <paramref name="database"/>. The pool is
    /// keyed by that exact string, so these mirror Reviewd's builders: plain, <c>ReadOnly</c> and <c>ReadWrite</c>.
    /// A variant missed here leaves the file open, and the test's own delete of it then fails.
    /// </summary>
    public static void Clear(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        string[] connectionStrings =
        [
            new SqliteConnectionStringBuilder { DataSource = database }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite }.ToString(),
        ];
        foreach (var connectionString in connectionStrings)
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }
}
