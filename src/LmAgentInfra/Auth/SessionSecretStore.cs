using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// File-backed store for per-sandbox-session webhook secrets. Persists one file per session
/// (<c>&lt;baseDir&gt;/&lt;sanitized-session-id&gt;.secret</c>), replacing the single
/// process-wide <c>AuthSharedSecret</c>: every sandbox session gets its own secret, so a
/// restart doesn't invalidate live sessions (the secret reloads from disk) and two different
/// sessions' secrets can never be cross-validated against each other.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on <see cref="FileOAuthTokenStore"/>'s persistence discipline: writes are atomic
/// (temp-file + <see cref="File.Move(string, string, bool)"/>) and all operations are
/// serialized by a single per-store <see cref="SemaphoreSlim"/>. Deliberately has no in-memory
/// cache layer — <see cref="MatchesAsync"/> reads straight from disk on every call, exactly
/// like <see cref="FileOAuthTokenStore.GetAsync"/> serves the same kind of webhook-call hot
/// path today.
/// </para>
/// <para>
/// SECRET — the persisted secret values must never be logged. Only session ids are logged.
/// </para>
/// </remarks>
public sealed class SessionSecretStore
{
    private readonly string _baseDirectory;
    private readonly ILogger<SessionSecretStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates a new <see cref="SessionSecretStore"/>, ensuring the base directory exists.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory under which per-session secret files are stored. Created if missing.
    /// </param>
    /// <param name="logger">Logger for structured diagnostics (never receives secret values).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="baseDirectory"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public SessionSecretStore(string baseDirectory, ILogger<SessionSecretStore> logger)
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

    /// <summary>
    /// Persists <paramref name="secret"/> for <paramref name="sessionId"/>, overwriting any
    /// existing secret for that session.
    /// </summary>
    public async Task SaveAsync(string sessionId, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id cannot be null or whitespace.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrEmpty(secret);

        var filePath = GetFilePath(sessionId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Ensure the directory still exists (it may have been removed out-of-band).
            _ = Directory.CreateDirectory(_baseDirectory);

            // Atomic write: stage to a temp file, then move over the target so a reader never
            // observes a partially written file.
            var tempFilePath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempFilePath, secret, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tempFilePath, filePath, overwrite: true);
        }
        finally
        {
            _ = _gate.Release();
        }

        // SECURITY: log only the session id. Never the secret value.
        _logger.LogInformation("Saved webhook secret for session {SessionId}", sessionId);
    }

    /// <summary>
    /// Constant-time comparison of <paramref name="presented"/> against the secret stored for
    /// <paramref name="sessionId"/>. Returns false when <paramref name="presented"/> is
    /// null/empty, or when no secret file exists for <paramref name="sessionId"/> — which is
    /// what makes cross-session validation impossible.
    /// </summary>
    /// <remarks>
    /// Unlike <c>AuthSharedSecret.Matches</c>, this can't be fully constant-time end-to-end — it
    /// must branch on "does a secret file exist for this session id" before it can even reach
    /// the hash compare. Accepted: the trust boundary here is the sandbox gateway, not a public
    /// adversarial endpoint.
    /// </remarks>
    public async Task<bool> MatchesAsync(string sessionId, string? presented, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var filePath = GetFilePath(sessionId);

        string secret;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            secret = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    /// <summary>
    /// Removes the persisted secret for <paramref name="sessionId"/>, if any.
    /// </summary>
    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        var filePath = GetFilePath(sessionId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Removed webhook secret for session {SessionId}", sessionId);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Maps a session id to its on-disk file path, sanitizing the id into a safe file name.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sessionId"/> is null/whitespace or contains no characters
    /// that survive sanitization (which prevents path traversal via separators or <c>..</c>).
    /// </exception>
    private string GetFilePath(string sessionId)
    {
        var fileName = SanitizeSessionId(sessionId);
        return Path.Combine(_baseDirectory, fileName + ".secret");
    }

    /// <summary>
    /// Reduces a session id to a safe file-name stem. Only <c>[a-z0-9_-]</c> survive
    /// (lowercased); every other character — including path separators and <c>.</c> — is
    /// dropped, which makes path traversal (e.g. <c>..</c> or <c>../etc</c>) impossible.
    /// </summary>
    private static string SanitizeSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id cannot be null or whitespace.", nameof(sessionId));
        }

        var builder = new StringBuilder(sessionId.Length);
        foreach (var ch in sessionId)
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
                $"Session id '{sessionId}' did not yield a valid file name after sanitization.",
                nameof(sessionId));
        }

        return builder.ToString();
    }
}
