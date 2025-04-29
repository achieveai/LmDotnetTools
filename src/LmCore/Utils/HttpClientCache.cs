namespace AchieveAi.LmDotnetTools.LmCore.Utils;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

/// <summary>
/// A thread-safe wrapper around HttpClient that caches responses in a SQLite database.
/// Supports both regular HTTP responses and Server-Sent Events (SSE).
/// </summary>
public class HttpClientCache : IDisposable
{
  private readonly HttpClient _httpClient;
  private readonly SqliteConnection _connection;
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of the <see cref="HttpClientCache"/> class.
  /// </summary>
  /// <param name="httpClient">The HTTP client to use for making requests.</param>
  /// <param name="connection">An open SQLite connection to use for caching.</param>
  public HttpClientCache(HttpClient httpClient, SqliteConnection connection)
  {
    _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    
    InitializeDatabase();
  }

  /// <summary>
  /// Gets a string from the specified URL, using the cache if available.
  /// </summary>
  /// <param name="url">The URL to request.</param>
  /// <param name="ct">A cancellation token.</param>
  /// <returns>The response as a string.</returns>
  public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
  {
    if (string.IsNullOrEmpty(url))
      throw new ArgumentException("URL cannot be null or empty", nameof(url));

    string key = ComputeKey(url);
    return await ExecuteLockedAsync(key, async () =>
    {
      // Check cache first
      if (TryGetCache(key, out bool isSse, out string cachedResponse))
      {
        if (!isSse)
          return cachedResponse;
        
        // We have SSE data in cache, but caller wants a single string
        throw new InvalidOperationException(
          $"URL {url} was previously cached as SSE data. Use GetSseAsync instead.");
      }

      // Cache miss - fetch from network
      string response = await _httpClient.GetStringAsync(url, ct);
      SaveResponse(key, response, false);
      return response;
    }, ct);
  }

  /// <summary>
  /// Gets Server-Sent Events from the specified URL, using the cache if available.
  /// </summary>
  /// <param name="url">The URL to request.</param>
  /// <param name="ct">A cancellation token.</param>
  /// <returns>An async enumerable of SSE data chunks.</returns>
  public async IAsyncEnumerable<string> GetSseAsync(
    string url,
    [EnumeratorCancellation] CancellationToken ct = default)
  {
    if (string.IsNullOrEmpty(url))
      throw new ArgumentException("URL cannot be null or empty", nameof(url));

    string key = ComputeKey(url);
    var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    
    await semaphore.WaitAsync(ct);
    try
    {
      // Check cache first
      if (TryGetCache(key, out bool isSse, out _))
      {
        if (!isSse)
        {
          throw new InvalidOperationException(
            $"URL {url} was previously cached as a regular response. Use GetStringAsync instead.");
        }

        // Return cached SSE events with 1ms delay between them
        foreach (var sseEvent in LoadSseEvents(key))
        {
          yield return sseEvent;
          await Task.Delay(1, ct); // 1ms delay between events
        }
        
        yield break;
      }

      // Cache miss - fetch from network
      using var request = new HttpRequestMessage(HttpMethod.Get, url);
      using var response = await _httpClient.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, ct);
      
      response.EnsureSuccessStatusCode();
      
      using var stream = await response.Content.ReadAsStreamAsync(ct);
      using var reader = new System.IO.StreamReader(stream);
      
      // Mark this URL as having SSE data
      SaveResponse(key, string.Empty, true);
      
      int sequence = 0;
      string line;
      
      while ((line = await reader.ReadLineAsync(ct) ?? string.Empty).Length > 0)
      {
        if (ct.IsCancellationRequested)
          break;
          
        if (line.StartsWith("data:"))
        {
          string data = line.Substring(5).Trim();
          SaveSseEvent(key, sequence++, data);
          yield return data;
        }
      }
    }
    finally
    {
      semaphore.Release();
    }
  }

  /// <summary>
  /// Clears all cached data.
  /// </summary>
  public void ClearCache()
  {
    using var command = _connection.CreateCommand();
    command.CommandText = @"
      DELETE FROM CacheEntries;
      DELETE FROM SseEvents;";
    command.ExecuteNonQuery();
  }

  /// <summary>
  /// Disposes resources used by this instance.
  /// </summary>
  public void Dispose()
  {
    if (_disposed)
      return;
      
    foreach (var semaphore in _locks.Values)
    {
      semaphore.Dispose();
    }
    
    _locks.Clear();
    if (_connection != null)
    {
      _connection.Dispose();
    }
    _disposed = true;
    
    GC.SuppressFinalize(this);
  }

  #region Private Methods

  private void InitializeDatabase()
  {
    // Enable WAL mode for better concurrency
    using (var cmd = _connection.CreateCommand())
    {
      cmd.CommandText = "PRAGMA journal_mode = WAL;";
      cmd.ExecuteNonQuery();
    }
    
    // Create tables if they don't exist
    using (var cmd = _connection.CreateCommand())
    {
      cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS CacheEntries(
          Key       TEXT PRIMARY KEY,
          IsSse     INTEGER,
          Response  TEXT
        );
        
        CREATE TABLE IF NOT EXISTS SseEvents(
          Key       TEXT,
          Sequence  INTEGER,
          Data      TEXT,
          PRIMARY KEY(Key, Sequence)
        );";
      cmd.ExecuteNonQuery();
    }
  }

  private async Task<T> ExecuteLockedAsync<T>(string key, Func<Task<T>> work, CancellationToken ct)
  {
    var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    await semaphore.WaitAsync(ct);
    try
    {
      return await work();
    }
    finally
    {
      semaphore.Release();
    }
  }

  private string ComputeKey(string url)
  {
    using var sha256 = SHA256.Create();
    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
    return Convert.ToHexString(hashBytes).ToLowerInvariant();
  }

  private bool TryGetCache(string key, out bool isSse, out string response)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT IsSse, Response FROM CacheEntries WHERE Key = @Key";
    command.Parameters.AddWithValue("@Key", key);
    
    using var reader = command.ExecuteReader();
    if (reader.Read())
    {
      isSse = reader.GetInt32(0) == 1;
      response = reader.GetString(1);
      return true;
    }
    
    isSse = false;
    response = string.Empty;
    return false;
  }

  private IEnumerable<string> LoadSseEvents(string key)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT Data FROM SseEvents WHERE Key = @Key ORDER BY Sequence";
    command.Parameters.AddWithValue("@Key", key);
    
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      yield return reader.GetString(0);
    }
  }

  private void SaveResponse(string key, string response, bool isSse)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = @"
      INSERT OR REPLACE INTO CacheEntries (Key, IsSse, Response)
      VALUES (@Key, @IsSse, @Response)";
    command.Parameters.AddWithValue("@Key", key);
    command.Parameters.AddWithValue("@IsSse", isSse ? 1 : 0);
    command.Parameters.AddWithValue("@Response", response);
    command.ExecuteNonQuery();
  }

  private void SaveSseEvent(string key, int sequence, string data)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = @"
      INSERT OR REPLACE INTO SseEvents (Key, Sequence, Data)
      VALUES (@Key, @Sequence, @Data)";
    command.Parameters.AddWithValue("@Key", key);
    command.Parameters.AddWithValue("@Sequence", sequence);
    command.Parameters.AddWithValue("@Data", data);
    command.ExecuteNonQuery();
  }

  #endregion
}
