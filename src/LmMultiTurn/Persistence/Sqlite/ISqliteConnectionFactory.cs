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

    /// <summary>
    /// Gets an open SQLite connection for a write: a write transaction or a statement that changes the database.
    /// The connection should be disposed after use, as soon as the write commits.
    /// </summary>
    /// <remarks>
    /// An implementation may let only one write connection out at a time, so a caller holding one must not ask for
    /// another before disposing it. It may ask for a read connection. The default is <see cref="GetConnectionAsync"/>.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open SQLite connection.</returns>
    Task<SqliteConnection> GetWriteConnectionAsync(CancellationToken ct = default) => GetConnectionAsync(ct);
}
