using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Outcome of checking a workspace's marketplace selection against the gateway catalog.
/// </summary>
/// <remarks>
/// The three members are deliberately about WHETHER THE CHECK RAN as much as about its answer, and
/// that separation is the whole point of the type.
/// <para>
/// A single <c>Unknown</c> member used to carry both "the catalog could not be read" and, by the way
/// every consumer read it, "not vouched for — withhold it". On a host with no sandbox gateway
/// <c>/api/marketplaces</c> answers 503 permanently, so EVERY workspace came back <c>Unknown</c> and
/// the SPA's picker had nothing selectable — an artifact nobody intended, and one no caller could
/// diagnose because the value it read could not tell "no" apart from "don't know".
/// </para>
/// <para>
/// So: <see cref="Incompatible"/> is the only value that means the check ran and the workspace
/// failed it, and it is therefore the only value that is a reason to withhold a row from a picker.
/// <see cref="Unavailable"/> means no check happened at all, and a caller must decide for itself
/// whether that is fatal — the picker shows such rows (unverified), while anything that actually
/// STARTS a sandbox session still fails closed, because running is where an unchecked marketplace
/// would really bite.
/// </para>
/// </remarks>
public enum WorkspaceCompatibility
{
    /// <summary>The catalog was read and every selected marketplace is in it.</summary>
    Compatible,

    /// <summary>
    /// The catalog was read and the workspace names marketplaces it does not offer. A checked "no":
    /// the only value that justifies withholding the workspace from a selection UI.
    /// </summary>
    Incompatible,

    /// <summary>
    /// The catalog could not be read, so nothing was checked. NOT a "no" — see the remarks on
    /// <see cref="WorkspaceCompatibility"/>. Serialized to clients as <c>"unavailable"</c>; a client
    /// old enough to expect the retired <c>"unknown"</c> simply falls through its cases and treats
    /// the row as it treated <c>unknown</c> before, which is the behaviour it already had.
    /// </summary>
    Unavailable,
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
    private readonly SandboxGatewayOptions _gatewayOptions;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private Task<CatalogSnapshot>? _refresh;
    private CatalogSnapshot? _cached;

    /// <param name="client">Gateway marketplace catalog reader.</param>
    /// <param name="gatewayOptions">
    /// Required, not optional: it supplies the configured default marketplace list that an empty
    /// workspace selection falls back to. Defaulting it to <c>null</c> would let a deployment that
    /// HAS configured defaults silently validate against the wrong (unnarrowed) set — the failure
    /// mode would be accepting plugins the session then refuses, which is worse than a compile error.
    /// </param>
    /// <param name="timeProvider">Clock for the catalog cache; defaults to the system clock.</param>
    public WorkspaceCatalogCompatibilityService(
        IMarketplaceCatalogClient client,
        SandboxGatewayOptions gatewayOptions,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
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
            // Unavailable, never Incompatible: no marketplace was compared against anything, so the
            // empty UnsupportedMarketplaces below is "nothing was checked", not "nothing failed".
            return new WorkspaceCompatibilityResult(
                WorkspaceCompatibility.Unavailable,
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

    /// <summary>
    /// Validates an explicit, non-null plugin selection against the current catalog. A <c>null</c>
    /// <paramref name="pluginSelection"/> (legacy-all) is always valid and never touches the catalog —
    /// only explicit selections are checked, per spec Section 8's fail-closed rule.
    /// </summary>
    /// <param name="marketplaces">
    /// The marketplace aliases selected on the workspace being mutated, or an EMPTY list to mean "no
    /// preference", which resolves to the configured global default exactly as session creation does.
    /// Every selected plugin must sit under one of the resolved aliases: the cached catalog is
    /// deliberately fetched UNFILTERED (see <see cref="RefreshAsync"/>) and is shared process-wide, so
    /// a plugin identity can be known to the gateway while belonging to a marketplace this workspace
    /// does not run under. Membership of the catalog alone is therefore not sufficient.
    /// </param>
    /// <param name="pluginSelection">
    /// The explicit selection to validate, or <c>null</c> for the legacy "all plugins" behaviour.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="GatewayPluginFilteringUnsupportedException">
    /// The gateway does not advertise plugin filtering, or advertises nothing at all (unknown is not
    /// permission).
    /// </exception>
    /// <exception cref="UnsupportedWorkspacePluginsException">
    /// One or more selected plugins are absent from the catalog, or sit under a marketplace not listed
    /// in <paramref name="marketplaces"/>. Both causes share one exception because the caller's remedy
    /// is identical: pick from the reported set.
    /// </exception>
    public async Task ValidatePluginsForMutationAsync(
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<PluginRef>? pluginSelection,
        CancellationToken ct = default)
    {
        if (pluginSelection is null)
        {
            return;
        }

        // Reject malformed entries FIRST, before the catalog is even fetched. `PluginRef` is a
        // reference type, so a body like `"pluginSelection": [null]` deserializes to a null ELEMENT
        // regardless of the non-nullable annotation — and a null (or blank-field) entry can never
        // match anything in `selectable`, so it lands in `unsupported` and the exception's own
        // message formatter dereferences it. That turned invalid input into a 500 instead of the
        // controlled 400 the caller can act on. Checking here rather than at the catalog step also
        // makes the answer deterministic: malformed input is a client error whatever the gateway is
        // doing, so it must not depend on the catalog being reachable or on plugin filtering being
        // supported.
        var malformed = new List<int>();
        for (var index = 0; index < pluginSelection.Count; index++)
        {
            var candidate = pluginSelection[index];
            if (candidate is null
                || string.IsNullOrWhiteSpace(candidate.Marketplace)
                || string.IsNullOrWhiteSpace(candidate.Plugin))
            {
                malformed.Add(index);
            }
        }

        if (malformed.Count > 0)
        {
            throw new MalformedWorkspacePluginSelectionException(malformed);
        }

        var snapshot = await GetCatalogAsync(ct).ConfigureAwait(false);

        if (snapshot.PluginFilteringSupported != true)
        {
            throw new GatewayPluginFilteringUnsupportedException();
        }

        // Narrow the shared, unfiltered catalog to the aliases THIS workspace actually runs under.
        // Doing it here rather than at fetch time keeps the process-wide 30s cache single-flight and
        // lets EvaluateAsync keep reporting the gateway's full alias list. The narrowed set is also
        // what the failure reports, so the error never offers back a plugin it just rejected.
        //
        // "Actually runs under" is NOT `marketplaces` verbatim: an empty workspace list means "names
        // no preference", and the session-create path resolves it to the configured global default.
        // Reading it as "enables nothing" made `selectable` empty and rejected EVERY plugin. Both
        // sides go through MarketplaceAliases.ResolveEffective so they cannot drift apart again.
        var effective = MarketplaceAliases.ResolveEffective(marketplaces, _gatewayOptions.Marketplaces);
        IReadOnlyList<PluginRef> selectable;
        if (effective is null)
        {
            // Nothing configured anywhere, so the gateway applies its own default marketplaces to the
            // session. The cached catalog was fetched unfiltered, which is that same full set — it is
            // already the correct selectable universe and narrowing it further would invent a limit
            // the session would not honour.
            selectable = snapshot.AvailablePlugins;
        }
        else
        {
            var enabled = new HashSet<string>(effective, StringComparer.Ordinal);
            selectable = [.. snapshot.AvailablePlugins.Where(p => enabled.Contains(p.Marketplace))];
        }

        var unsupported = pluginSelection.Where(p => !selectable.Contains(p)).ToArray();

        if (unsupported.Length > 0)
        {
            throw new UnsupportedWorkspacePluginsException(unsupported, selectable);
        }
    }

    /// <summary>
    /// Fails closed on <see cref="WorkspaceCompatibility.Unavailable"/>, deliberately, even though the
    /// picker treats that value as selectable. The two answers are not in tension: showing a row is a
    /// statement about what the user may CHOOSE, while this method guards a mutation or a live sandbox
    /// session, where acting on an unchecked marketplace set is what actually breaks. Distinguishing
    /// the values is what lets those two callers disagree without either of them guessing.
    /// </summary>
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
            var availablePlugins = catalog.Marketplaces
                .SelectMany(m => m.Plugins.Select(p => new PluginRef(m.Alias, p.Name)))
                .ToArray();
            return new CatalogSnapshot(
                true,
                aliases,
                null,
                _timeProvider.GetUtcNow(),
                availablePlugins,
                catalog.Capabilities.PluginFiltering
            );
        }
        catch (MarketplaceCatalogUnavailableException ex)
        {
            // An unreachable gateway advertises nothing, so the capability stays null and any explicit
            // plugin selection fails closed rather than being validated against an empty catalog.
            return new CatalogSnapshot(false, [], ex.Message, _timeProvider.GetUtcNow(), [], null);
        }
    }

    private sealed record CatalogSnapshot(
        bool Available,
        IReadOnlyList<string> Aliases,
        string? Error,
        DateTimeOffset FetchedAt,
        IReadOnlyList<PluginRef> AvailablePlugins,
        bool? PluginFilteringSupported
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

/// <summary>
/// Thrown when a workspace's explicit plugin selection references a plugin that is not selectable for
/// that workspace — either absent from the gateway catalog outright, or present but under a
/// marketplace the workspace has not enabled.
/// </summary>
public sealed class UnsupportedWorkspacePluginsException : Exception
{
    /// <summary>Creates a new <see cref="UnsupportedWorkspacePluginsException"/>.</summary>
    /// <param name="unsupportedPlugins">The rejected plugins, in the order they were selected.</param>
    /// <param name="availablePlugins">
    /// The plugins the workspace could legally have picked — already narrowed to the marketplaces it
    /// effectively runs under, NOT the gateway's full catalog. Narrowed deliberately: an unnarrowed
    /// list would offer back the very plugin this exception just rejected.
    /// </param>
    public UnsupportedWorkspacePluginsException(
        IReadOnlyList<PluginRef> unsupportedPlugins,
        IReadOnlyList<PluginRef> availablePlugins)
        : base($"Unsupported plugins: {string.Join(", ", unsupportedPlugins.Select(p => $"{p.Marketplace}/{p.Plugin}"))}")
    {
        UnsupportedPlugins = unsupportedPlugins;
        AvailablePlugins = availablePlugins;
    }

    /// <summary>The rejected plugins.</summary>
    public IReadOnlyList<PluginRef> UnsupportedPlugins { get; }

    /// <summary>The selectable plugins, narrowed to the workspace's enabled marketplaces.</summary>
    public IReadOnlyList<PluginRef> AvailablePlugins { get; }
}

/// <summary>
/// Thrown when a plugin selection carries an entry that is not a usable reference at all — a null
/// element, or one with a blank marketplace or plugin name. Distinct from
/// <see cref="UnsupportedWorkspacePluginsException"/>, which reports well-formed references the
/// gateway does not offer: this one says the request itself could not be read, so there is nothing
/// to compare against a catalog and no gateway call worth making.
/// </summary>
public sealed class MalformedWorkspacePluginSelectionException : Exception
{
    /// <summary>Creates a new <see cref="MalformedWorkspacePluginSelectionException"/>.</summary>
    /// <param name="indexes">Positions in the submitted selection that could not be read.</param>
    public MalformedWorkspacePluginSelectionException(IReadOnlyList<int> indexes)
        : base(
            "Plugin selection contains entries that are null or have a blank marketplace or plugin "
                + $"name, at index: {string.Join(", ", indexes)}.")
    {
        Indexes = indexes;
    }

    /// <summary>Positions in the submitted selection that could not be read.</summary>
    public IReadOnlyList<int> Indexes { get; }
}

/// <summary>Thrown when an explicit plugin selection is supplied but the gateway does not (or is not known to) support plugin filtering.</summary>
public sealed class GatewayPluginFilteringUnsupportedException : Exception
{
    public GatewayPluginFilteringUnsupportedException()
        : base("The gateway does not support plugin filtering; an explicit plugin selection cannot be applied.")
    {
    }
}
