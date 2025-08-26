using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Misc.Utils;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.Misc.Storage;

/// <summary>
/// SQLite-backed implementation of IKvStore
/// </summary>
public class SqliteKvStore : IKvStore
{
    private readonly SqliteConnection _connection;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _initialized = false;

    /// <summary>
    /// Creates a new SQLite key-value store using the specified connection
    /// </summary>
    /// <param name="connection">SQLite connection to use</param>
    /// <param name="jsonOptions">JSON serialization options, or null to use default options</param>
    public SqliteKvStore(SqliteConnection connection, JsonSerializerOptions? jsonOptions = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions { WriteIndented = false };
        InitializeDatabase();
    }

    /// <summary>
    /// Creates a new SQLite key-value store using the specified database path
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file</param>
    /// <param name="jsonOptions">JSON serialization options, or null to use default options</param>
    public SqliteKvStore(string dbPath, JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            throw new ArgumentNullException(nameof(dbPath));
        }

        var connectionString = $"Data Source={dbPath}";
        _connection = new SqliteConnection(connectionString);
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions { WriteIndented = false };
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        if (_initialized)
        {
            return;
        }

        if (_connection.State != ConnectionState.Open)
        {
            _connection.Open();
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            @"
      CREATE TABLE IF NOT EXISTS cache (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
      )";
        command.ExecuteNonQuery();
        _initialized = true;
    }

    /// <inheritdoc/>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentNullException(nameof(key));
        }

        InitializeDatabase();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        // Using async API
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }

        var serializedValue = reader.GetString(0);
        return JsonSerializer.Deserialize<T>(serializedValue, _jsonOptions);
    }

    /// <inheritdoc/>
    public async Task SetAsync<T>(
        string key,
        T value,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        InitializeDatabase();

        var serializedValue = JsonSerializer.Serialize(value, _jsonOptions);

        using var command = _connection.CreateCommand();
        command.CommandText =
            @"
      INSERT INTO cache (key, value) 
      VALUES (@key, @value)
      ON CONFLICT(key) DO UPDATE SET 
      value = @value";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", serializedValue);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IAsyncEnumerable<string>> EnumerateKeysAsync(
        CancellationToken cancellationToken = default
    )
    {
        InitializeDatabase();
        return Task.FromResult(GetKeysEnumerable(cancellationToken));
    }

    private async IAsyncEnumerable<string> GetKeysEnumerable(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key FROM cache ORDER BY key";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return reader.GetString(0);
        }
    }
}
