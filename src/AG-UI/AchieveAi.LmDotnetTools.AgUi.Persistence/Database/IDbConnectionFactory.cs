using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Database;

/// <summary>
/// Factory interface for creating SQLite database connections.
/// </summary>
/// <remarks>
/// Implementations should provide connection pooling and proper resource management.
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Creates and opens a new database connection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open SQLite connection that must be disposed by the caller.</returns>
    Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default);
}
