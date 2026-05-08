using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// Factory interface for creating SQLite connections with connection pooling support.
/// </summary>
public interface ISqliteConnectionFactory : IAsyncDisposable
{
    /// <summary>
    /// Gets an open SQLite connection from the pool.
    /// The connection should be disposed after use to return it to the pool.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open SQLite connection.</returns>
    Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default);
}
