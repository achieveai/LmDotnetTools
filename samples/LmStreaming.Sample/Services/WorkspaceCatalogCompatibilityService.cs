using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

public enum WorkspaceCompatibility
{
    Compatible,
    Incompatible,
    Unknown,
}

public sealed record WorkspaceCompatibilityResult(
    WorkspaceCompatibility Compatibility,
    IReadOnlyList<string> UnsupportedMarketplaces,
    IReadOnlyList<string> AvailableMarketplaces,
    string? Error = null
);

/// <summary>Validates persisted workspace marketplace selections against the active gateway.</summary>
public sealed class WorkspaceCatalogCompatibilityService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly IMarketplaceCatalogClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private Task<CatalogSnapshot>? _refresh;
    private CatalogSnapshot? _cached;

    public WorkspaceCatalogCompatibilityService(
        IMarketplaceCatalogClient client,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkspaceCompatibilityResult> EvaluateAsync(
        Workspace workspace,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var catalog = await GetCatalogAsync(ct);
        if (!catalog.Available)
        {
            return new WorkspaceCompatibilityResult(
                WorkspaceCompatibility.Unknown,
                [],
                [],
                catalog.Error
            );
        }

        var available = catalog.Aliases;
        var availableSet = new HashSet<string>(available, StringComparer.Ordinal);
        var unsupported = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in workspace.Marketplaces)
        {
            if (seen.Add(alias) && !availableSet.Contains(alias))
            {
                unsupported.Add(alias);
            }
        }

        return new WorkspaceCompatibilityResult(
            unsupported.Count == 0
                ? WorkspaceCompatibility.Compatible
                : WorkspaceCompatibility.Incompatible,
            unsupported,
            available
        );
    }

    public async Task ValidateForMutationAsync(
        IReadOnlyList<string> marketplaces,
        CancellationToken ct = default)
    {
        var probe = new Workspace
        {
            Id = "validation",
            Name = "validation",
            DirectoryRelPath = "validation",
            Marketplaces = marketplaces,
        };
        await ValidateResultAsync(await EvaluateAsync(probe, ct));
    }

    public async Task ValidateForSessionAsync(Workspace workspace, CancellationToken ct = default) =>
        await ValidateResultAsync(await EvaluateAsync(workspace, ct));

    private static Task ValidateResultAsync(WorkspaceCompatibilityResult result)
    {
        return result.Compatibility switch
        {
            WorkspaceCompatibility.Compatible => Task.CompletedTask,
            WorkspaceCompatibility.Incompatible => Task.FromException(
                new UnsupportedWorkspaceMarketplacesException(
                    result.UnsupportedMarketplaces,
                    result.AvailableMarketplaces
                )
            ),
            _ => Task.FromException(
                new WorkspaceGatewayCatalogUnavailableException(
                    result.Error ?? "Sandbox gateway marketplace catalog is unavailable."
                )
            ),
        };
    }

    private async Task<CatalogSnapshot> GetCatalogAsync(CancellationToken ct)
    {
        Task<CatalogSnapshot> refresh;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cached is not null && now - _cached.FetchedAt < CacheDuration)
            {
                return _cached;
            }

            _refresh ??= RefreshAsync();
            refresh = _refresh;
        }

        CatalogSnapshot value;
        try
        {
            value = await refresh.WaitAsync(ct);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_refresh, refresh) && refresh.IsCompleted)
                {
                    _refresh = null;
                }
            }
        }

        lock (_sync)
        {
            _cached = value;
        }
        return value;
    }

    private async Task<CatalogSnapshot> RefreshAsync()
    {
        try
        {
            var catalog = await _client.GetCatalogAsync(null, CancellationToken.None);
            var aliases = catalog.Marketplaces
                .Select(x => x.Alias)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new CatalogSnapshot(true, aliases, null, _timeProvider.GetUtcNow());
        }
        catch (MarketplaceCatalogUnavailableException ex)
        {
            return new CatalogSnapshot(false, [], ex.Message, _timeProvider.GetUtcNow());
        }
    }

    private sealed record CatalogSnapshot(
        bool Available,
        IReadOnlyList<string> Aliases,
        string? Error,
        DateTimeOffset FetchedAt
    );
}

public sealed class UnsupportedWorkspaceMarketplacesException : InvalidOperationException
{
    public UnsupportedWorkspaceMarketplacesException(
        IReadOnlyList<string> unsupported,
        IReadOnlyList<string> available)
        : base($"Unsupported marketplace aliases: {string.Join(", ", unsupported)}.")
    {
        UnsupportedMarketplaces = unsupported;
        AvailableMarketplaces = available;
    }

    public IReadOnlyList<string> UnsupportedMarketplaces { get; }
    public IReadOnlyList<string> AvailableMarketplaces { get; }
}

public sealed class WorkspaceGatewayCatalogUnavailableException : InvalidOperationException
{
    public WorkspaceGatewayCatalogUnavailableException(string message)
        : base(message) { }
}
