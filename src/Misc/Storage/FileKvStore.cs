using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Storage;

/// <summary>
///     File-based implementation of IKvStore that stores cached values as individual files
///     with SHA256-based filenames in a configurable directory.
/// </summary>
public class FileKvStore : IKvStore, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    /// <summary>
    ///     Creates a new file-based key-value store using the specified directory
    /// </summary>
    /// <param name="cacheDirectory">Directory where cache files will be stored</param>
    /// <param name="jsonOptions">JSON serialization options, or null to use default options</param>
    public FileKvStore(string cacheDirectory, JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("Cache directory cannot be null or empty", nameof(cacheDirectory));
        }

        CacheDirectory = Path.GetFullPath(cacheDirectory);
        _jsonOptions =
            jsonOptions
            ?? new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        _semaphore = new SemaphoreSlim(1, 1);

        // Ensure the cache directory exists
        _ = Directory.CreateDirectory(CacheDirectory);
    }

    /// <summary>
    ///     Gets the cache directory path
    /// </summary>
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _semaphore?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        }

        var filePath = GetFilePath(key);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return default;
            }

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);

            return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            // If JSON is corrupted, delete the file and return default
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Ignore deletion errors
            }

            return default;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        }

        var filePath = GetFilePath(key);
        var json = JsonSerializer.Serialize(value, _jsonOptions);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Ensure directory exists (in case it was deleted)
            _ = Directory.CreateDirectory(CacheDirectory);

            // Write to temporary file first, then move to final location
            // This ensures atomic writes and prevents corruption
            var tempFilePath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8, cancellationToken);
            File.Move(tempFilePath, filePath, true);
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public Task<IAsyncEnumerable<string>> EnumerateKeysAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return Task.FromResult(EnumerateKeysInternal(cancellationToken));
    }

    private async IAsyncEnumerable<string> EnumerateKeysInternal(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                yield break;
            }

            var files = Directory.EnumerateFiles(CacheDirectory, "*.json", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(fileName))
                {
                    yield return fileName;
                }
            }
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <summary>
    ///     Clears all cached files from the cache directory
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(CacheDirectory, "*.json", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Continue deleting other files even if one fails
                }
            }
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <summary>
    ///     Gets the total number of cached files
    /// </summary>
    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return !Directory.Exists(CacheDirectory)
                ? 0
                : Directory.GetFiles(CacheDirectory, "*.json", SearchOption.TopDirectoryOnly).Length;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    /// <summary>
    ///     Generates a file path for the given key using SHA256 hash
    /// </summary>
    private string GetFilePath(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{hashString}.json");
    }

    /// <summary>
    ///     Throws ObjectDisposedException if the store has been disposed
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileKvStore));
        }
    }
}
