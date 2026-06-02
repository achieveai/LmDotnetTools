using System.Text;
using System.Text.Json;

namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// File-backed implementation of <see cref="IOAuthTokenStore"/>. Persists one JSON file per
/// provider (<c>&lt;baseDir&gt;/&lt;sanitized-provider&gt;.json</c>) for local development use.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic (temp-file + <see cref="File.Move(string, string, bool)"/>) and all operations
/// are serialized by a single per-store <see cref="SemaphoreSlim"/>, making concurrent
/// Get/Save/Remove calls (even across different providers) safe.
/// </para>
/// <para>
/// SECURITY: the persisted JSON contains secret token material. <see cref="OAuthTokenRecord.RefreshToken"/>
/// and <see cref="OAuthTokenRecord.AccessToken"/> values are NEVER written to logs — only the
/// provider id, account, and access-token expiry are logged. Callers should point the base
/// directory at a git-ignored location (e.g. <c>oauth-tokens/</c>).
/// </para>
/// </remarks>
public sealed class FileOAuthTokenStore : IOAuthTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _baseDirectory;
    private readonly ILogger<FileOAuthTokenStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates a new <see cref="FileOAuthTokenStore"/>, ensuring the base directory exists.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory under which per-provider token files are stored. Created if missing. Callers
    /// typically pass something like <c>Path.Combine(AppContext.BaseDirectory, "oauth-tokens")</c>.
    /// </param>
    /// <param name="logger">Logger for structured diagnostics (never receives token values).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="baseDirectory"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public FileOAuthTokenStore(string baseDirectory, ILogger<FileOAuthTokenStore> logger)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or whitespace.", nameof(baseDirectory));
        }

        ArgumentNullException.ThrowIfNull(logger);

        _baseDirectory = Path.GetFullPath(baseDirectory);
        _logger = logger;
        _ = Directory.CreateDirectory(_baseDirectory);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default)
    {
        var filePath = GetFilePath(provider);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OAuthTokenRecord>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Corrupt/unparseable file: surface a warning but never throw — the caller treats a
            // missing/invalid token the same as "not stored" and re-authenticates. Token values
            // are intentionally excluded from the log.
            _logger.LogWarning(ex, "Failed to parse OAuth token file for provider {Provider}", provider);
            return null;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var filePath = GetFilePath(record.Provider);
        var json = JsonSerializer.Serialize(record, JsonOptions);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Ensure the directory still exists (it may have been removed out-of-band).
            _ = Directory.CreateDirectory(_baseDirectory);

            // Atomic write: stage to a temp file, then move over the target so a reader never
            // observes a partially written file.
            var tempFilePath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tempFilePath, filePath, overwrite: true);
        }
        finally
        {
            _ = _gate.Release();
        }

        // SECURITY: log only non-secret metadata (provider + expiry). Never the token values.
        _logger.LogInformation(
            "Saved OAuth token for provider {Provider} (expires {ExpiresAt})",
            record.Provider,
            record.AccessTokenExpiresAtUtc);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string provider, CancellationToken ct = default)
    {
        var filePath = GetFilePath(provider);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Removed OAuth token for provider {Provider}", provider);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Maps a provider id to its on-disk file path, sanitizing the id into a safe file name.
    /// </summary>
    /// <param name="provider">Provider id (e.g. <c>"anthropic"</c>).</param>
    /// <returns>Absolute path to the provider's token file inside the base directory.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="provider"/> is null/whitespace or contains no characters that
    /// survive sanitization (which prevents path traversal via separators or <c>..</c>).
    /// </exception>
    private string GetFilePath(string provider)
    {
        var fileName = SanitizeProvider(provider);
        return Path.Combine(_baseDirectory, fileName + ".json");
    }

    /// <summary>
    /// Reduces a provider id to a safe file-name stem. Only <c>[a-z0-9_-]</c> survive (lowercased);
    /// every other character — including path separators and <c>.</c> — is dropped, which makes
    /// path traversal (e.g. <c>..</c> or <c>../etc</c>) impossible.
    /// </summary>
    private static string SanitizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider cannot be null or whitespace.", nameof(provider));
        }

        var builder = new StringBuilder(provider.Length);
        foreach (var ch in provider)
        {
            var lower = char.ToLowerInvariant(ch);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            {
                _ = builder.Append(lower);
            }
        }

        if (builder.Length == 0)
        {
            throw new ArgumentException(
                $"Provider '{provider}' did not yield a valid file name after sanitization.",
                nameof(provider));
        }

        return builder.ToString();
    }
}
