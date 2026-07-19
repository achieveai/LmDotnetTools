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

    /// <summary>Creates a new entry or updates an existing one in place (same id), then persists.</summary>
    internal async Task UpsertAsync(PredefinedKeyEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_providers.TryGetValue(entry.Id, out var existing))
        {
            existing.UpdateEntry(entry);
        }
        else
        {
            _providers[entry.Id] = NewProvider(entry);
        }

        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes an entry (and its persisted minted token), then persists. Returns false when the id is unknown.</summary>
    internal async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!_providers.TryRemove(id, out var provider))
        {
            return false;
        }

        // Best-effort: drop any minted-token record for this entry so the secret does not linger.
        await provider.SignOutAsync(ct).ConfigureAwait(false);
        await PersistAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Atomically writes the full entry set to disk under the persist gate.</summary>
    private async Task PersistAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_filePath, Entries, JsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _persistGate.Release();
        }
    }
}
