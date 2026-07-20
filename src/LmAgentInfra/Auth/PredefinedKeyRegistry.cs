using System.Collections.Concurrent;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// The runtime aggregate for predefined egress keys: one <see cref="PredefinedKeyProvider"/> per
/// entry, disk persistence of the entry definitions (a single gitignored JSON file), CRUD, and
/// resolution by provider id. Entries are created at RUNTIME via the CRUD API (not startup config),
/// so this is the second provider source the webhook consults after the fixed DI-registered OAuth
/// providers. Minted OAuth tokens ride the shared <see cref="IOAuthTokenStore"/> (keyed by
/// <c>predefined-&lt;id&gt;</c>); only the entry definitions live in this registry's file.
/// </summary>
/// <remarks>SECRET: the persisted file contains credential material — it lives under the gitignored token dir and is never logged.</remarks>
/// <remarks>
/// Public only so it can be a constructor dependency of the public <c>AuthWebhookController</c> /
/// <c>EgressKeysController</c>; its members are internal (all consumers are in this assembly).
/// </remarks>
public sealed class PredefinedKeyRegistry
{
    /// <summary>Provider-id prefix; <c>ProviderId = "predefined-{entryId}"</c>.</summary>
    internal const string ProviderIdPrefix = "predefined-";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly IOAuthTokenStore _tokenStore;
    private readonly OAuthTokenEndpointClient _endpoint;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PredefinedKeyRegistry> _logger;
    private readonly ConcurrentDictionary<string, PredefinedKeyProvider> _providers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistGate = new(1, 1);

    /// <summary>
    /// Creates the registry and loads any persisted entries from <c>{baseDirectory}/predefined-keys.json</c>.
    /// Takes an <see cref="HttpClient"/> (public) and builds the internal token-endpoint client itself so
    /// the public constructor exposes no internal types.
    /// </summary>
    public PredefinedKeyRegistry(
        string baseDirectory,
        IOAuthTokenStore tokenStore,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        ArgumentNullException.ThrowIfNull(httpClient);
        _endpoint = new OAuthTokenEndpointClient(httpClient);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<PredefinedKeyRegistry>();
        _filePath = Path.Combine(Path.GetFullPath(baseDirectory), "predefined-keys.json");
        Load();
    }

    /// <summary>Loads persisted entry definitions at startup (best-effort; a corrupt file logs + starts empty).</summary>
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<List<PredefinedKeyEntry>>(json, JsonOptions) ?? [];
            foreach (var entry in entries)
            {
                _providers[entry.Id] = NewProvider(entry);
            }

            _logger.LogInformation("Loaded {Count} predefined egress key(s) from {File}.", entries.Count, _filePath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse predefined egress keys file {File}; starting with none.", _filePath);
        }
    }

    private PredefinedKeyProvider NewProvider(PredefinedKeyEntry entry) =>
        new(entry, _tokenStore, _endpoint, _loggerFactory.CreateLogger<PredefinedKeyProvider>());

    /// <summary>Resolves the provider for a <c>predefined-&lt;id&gt;</c> provider id, or null when not a predefined key.</summary>
    internal PredefinedKeyProvider? TryResolve(string? providerId)
    {
        if (string.IsNullOrEmpty(providerId) || !providerId.StartsWith(ProviderIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = providerId[ProviderIdPrefix.Length..];
        return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    /// <summary>A snapshot of the current entry definitions (for sandbox rule building and the masked CRUD list).</summary>
    internal IReadOnlyList<PredefinedKeyEntry> Entries => [.. _providers.Values.Select(p => p.Entry)];

    /// <summary>The current entry for <paramref name="id"/>, or null when unknown (for edit-preserves-secret merges).</summary>
    internal PredefinedKeyEntry? Find(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider.Entry : null;

    /// <summary>
    /// Creates a new entry or updates an existing one (same id). Persists the candidate set FIRST and
    /// only publishes the in-memory change when the write succeeds, so a failed/cancelled write never
    /// leaves memory and disk divergent. The whole mutation is serialized under <see cref="_persistGate"/>.
    /// </summary>
    internal async Task UpsertAsync(PredefinedKeyEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _persistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ = _providers.TryGetValue(entry.Id, out var existing);
            var credentialChanged = existing is null || PredefinedKeyProvider.CredentialChanged(existing.Entry, entry);

            var candidate = _providers.Values
                .Select(p => p.Entry)
                .Where(e => !string.Equals(e.Id, entry.Id, StringComparison.Ordinal))
                .Append(entry)
                .ToList();

            // Invalidate the stale minted token BEFORE the new definition is made durable. This is a
            // RELIABLE (not best-effort) step: if removal fails the whole upsert throws before anything is
            // persisted or published, so we never leave the new definition live with the old access/rotated
            // token reloadable under the unchanged provider id. CancellationToken.None so a disconnecting
            // caller can't strand it mid-invalidation.
            if (existing is not null && credentialChanged)
            {
                await _tokenStore.RemoveAsync($"{ProviderIdPrefix}{entry.Id}", CancellationToken.None).ConfigureAwait(false);
            }

            await AtomicJsonFile.WriteAsync(_filePath, candidate, JsonOptions, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                await existing.UpdateEntry(entry, credentialChanged, ct).ConfigureAwait(false);
            }
            else
            {
                _providers[entry.Id] = NewProvider(entry);
            }
        }
        finally
        {
            _ = _persistGate.Release();
        }
    }

    /// <summary>
    /// Removes an entry. Persists the candidate (without it) FIRST, then removes it from memory and
    /// drops its persisted minted token — so a failed write never orphans the on-disk entry. Returns
    /// false when the id is unknown. Serialized under <see cref="_persistGate"/>.
    /// </summary>
    internal async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        await _persistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_providers.TryGetValue(id, out var provider))
            {
                return false;
            }

            var candidate = _providers.Values
                .Select(p => p.Entry)
                .Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal))
                .ToList();
            await AtomicJsonFile.WriteAsync(_filePath, candidate, JsonOptions, ct).ConfigureAwait(false);

            _ = _providers.TryRemove(id, out _);
            // Drop the persisted minted token so the secret does not linger — genuinely best-effort so a
            // cleanup failure/cancellation never fails the (already-committed) deletion or strands the caller.
            await TryRemovePersistedTokenAsync(id).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ = _persistGate.Release();
        }
    }

    /// <summary>
    /// Best-effort removal of an entry's persisted minted token. Uses a NON-request cancellation token so a
    /// disconnecting caller cannot strand the cleanup, and never throws — a failure is logged (never the
    /// secret) and the operation proceeds; a leftover token is harmless (it is re-validated / overwritten on
    /// the next mint and is not reachable without its now-removed definition).
    /// </summary>
    private async Task TryRemovePersistedTokenAsync(string entryId)
    {
        try
        {
            await _tokenStore.RemoveAsync($"{ProviderIdPrefix}{entryId}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort cleanup of the persisted token for predefined key {Id} failed.", entryId);
        }
    }
}
