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
    /// <summary>How long a catalog that WAS read is reused before the gateway is asked again.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a catalog that could NOT be read is reused. Far shorter than <see cref="CacheDuration"/>,
    /// and the asymmetry is the point: the cache exists to spare a healthy gateway repeated reads, but
    /// holding a FAILURE for the same full window does the opposite — it keeps answering "unavailable"
    /// from memory for half a minute after the gateway has come up, so a cold start that finally
    /// succeeded is still refused by everything that asks in the next 30 seconds. Not zero, because a
    /// gateway that is genuinely down should not be hammered once per request either.
    /// </summary>
    private static readonly TimeSpan UnavailableCacheDuration = TimeSpan.FromSeconds(3);

    private readonly SandboxGatewayOptions _gatewayOptions;
    private readonly TimeProvider _timeProvider;
    private readonly CatalogCache _browseCatalog;
    private readonly CatalogCache _sessionCatalog;

    /// <param name="client">
    /// Best-effort gateway catalog reader, used by <see cref="EvaluateAsync"/> and the mutation
    /// validations. Its short transport budget is a feature for a browse and a listing.
    /// </param>
    /// <param name="gatewayOptions">
    /// Required, not optional: it supplies the configured default marketplace list that an empty
    /// workspace selection falls back to. Defaulting it to <c>null</c> would let a deployment that
    /// HAS configured defaults silently validate against the wrong (unnarrowed) set — the failure
    /// mode would be accepting plugins the session then refuses, which is worse than a compile error.
    /// </param>
    /// <param name="timeProvider">Clock for the catalog cache; defaults to the system clock.</param>
    /// <param name="sessionClient">
    /// Optional longer-budget reader for <see cref="ValidateForSessionAsync"/> only — see
    /// <see cref="ISessionMarketplaceCatalogClient"/> for why the fail-closed path must not share the
    /// browse client's fail-fast timeout. Null (nothing registered) keeps the pre-split behaviour of
    /// reading the catalog through <paramref name="client"/>, cache and all: the two share ONE cache in
    /// that case, so a host that wires only the browse client sees no extra gateway traffic.
    /// </param>
    public WorkspaceCatalogCompatibilityService(
        IMarketplaceCatalogClient client,
        SandboxGatewayOptions gatewayOptions,
        TimeProvider? timeProvider = null,
        ISessionMarketplaceCatalogClient? sessionClient = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _browseCatalog = new CatalogCache(client, _timeProvider);

        // Separate caches, not just separate clients: a browse that gave up after 10 seconds must not
        // publish "unavailable" into the window a 2-minute session check is still legitimately waiting
        // out, and a session check must not be answered from a snapshot the browse budget produced.
        _sessionCatalog = sessionClient is null ? _browseCatalog : new CatalogCache(sessionClient, _timeProvider);
    }

    public async Task<WorkspaceCompatibilityResult> EvaluateAsync(Workspace workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Evaluate(workspace, await _browseCatalog.GetAsync(ct).ConfigureAwait(false));
    }

    private static WorkspaceCompatibilityResult Evaluate(Workspace workspace, CatalogSnapshot catalog)
    {
        if (!catalog.Available)
        {
            // Unavailable, never Incompatible: no marketplace was compared against anything, so the
            // empty UnsupportedMarketplaces below is "nothing was checked", not "nothing failed".
            return new WorkspaceCompatibilityResult(WorkspaceCompatibility.Unavailable, [], [], catalog.Error);
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
            unsupported.Count == 0 ? WorkspaceCompatibility.Compatible : WorkspaceCompatibility.Incompatible,
            unsupported,
            available
        );
    }

    public async Task ValidateForMutationAsync(IReadOnlyList<string> marketplaces, CancellationToken ct = default)
    {
        var probe = new Workspace
        {
            Id = "validation",
            Name = "validation",
            DirectoryRelPath = "validation",
            Marketplaces = marketplaces,
        };

        // The BROWSE client, deliberately: a mutation is an interactive request a person is waiting on,
        // so it keeps the fail-fast budget it always had. Only starting a session is worth waiting a
        // cold gateway out for.
        var snapshot = await _browseCatalog.GetAsync(ct).ConfigureAwait(false);
        Validate(Evaluate(probe, snapshot), snapshot);
    }

    /// <summary>
    /// Fail-closed validation for STARTING a sandbox session, read through
    /// <see cref="ISessionMarketplaceCatalogClient"/> when one is registered.
    /// </summary>
    public async Task ValidateForSessionAsync(Workspace workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var snapshot = await _sessionCatalog.GetAsync(ct).ConfigureAwait(false);
        Validate(Evaluate(workspace, snapshot), snapshot);
    }

    /// <summary>
    /// Validates an explicit, non-null plugin selection against the current catalog. A <c>null</c>
    /// <paramref name="pluginSelection"/> (legacy-all) is always valid and never touches the catalog —
    /// only explicit selections are checked, per spec Section 8's fail-closed rule.
    /// </summary>
    /// <param name="marketplaces">
    /// The marketplace aliases selected on the workspace being mutated, or an EMPTY list to mean "no
    /// preference", which resolves to the configured global default exactly as session creation does.
    /// Every selected plugin must sit under one of the resolved aliases: the cached catalog is
    /// deliberately fetched UNFILTERED (see <c>CatalogCache.RefreshAsync</c>) and is shared process-wide,
    /// so a plugin identity can be known to the gateway while belonging to a marketplace this workspace
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
        CancellationToken ct = default
    )
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
            if (
                candidate is null
                || string.IsNullOrWhiteSpace(candidate.Marketplace)
                || string.IsNullOrWhiteSpace(candidate.Plugin)
            )
            {
                malformed.Add(index);
            }
        }

        if (malformed.Count > 0)
        {
            throw new MalformedWorkspacePluginSelectionException(malformed);
        }

        var snapshot = await _browseCatalog.GetAsync(ct).ConfigureAwait(false);

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
    /// <param name="result">The evaluated compatibility answer to act on.</param>
    /// <param name="snapshot">
    /// The snapshot <paramref name="result"/> was derived from, carried alongside it purely for its
    /// <see cref="CatalogSnapshot.Cause"/>. The result's <c>Error</c> is a flattened string; the cause is
    /// the exception chain that says WHICH failure this was — a transport timeout reads identically to a
    /// rejected credential once it has been reduced to text, and the caller that turns this into a 503
    /// logs the exception, not the sentence.
    /// </param>
    private static void Validate(WorkspaceCompatibilityResult result, CatalogSnapshot snapshot)
    {
        switch (result.Compatibility)
        {
            case WorkspaceCompatibility.Compatible:
                return;
            case WorkspaceCompatibility.Incompatible:
                throw new UnsupportedWorkspaceMarketplacesException(
                    result.UnsupportedMarketplaces,
                    result.AvailableMarketplaces
                );
            case WorkspaceCompatibility.Unavailable:
            default:
                throw new WorkspaceGatewayCatalogUnavailableException(
                    result.Error ?? "Sandbox gateway marketplace catalog is unavailable.",
                    snapshot.Cause
                );
        }
    }

    /// <summary>
    /// One client's view of the gateway catalog: a single-flight refresh plus the last snapshot it
    /// produced. Instance-per-client rather than static so the browse and session readers, which have
    /// different transport budgets and therefore different notions of "unavailable", cannot answer each
    /// other's questions.
    /// </summary>
    private sealed class CatalogCache(IMarketplaceCatalogClient client, TimeProvider timeProvider)
    {
        /// <summary>
        /// The most transports ONE <see cref="GetAsync"/> call may spend. Two, not unbounded: a caller that
        /// resumes onto a snapshot which has already aged out has to be able to ask again, or a single
        /// unlucky interleaving pins an answer nobody can refresh. But every extra attempt adds a WHOLE
        /// transport budget to the SAME caller's wait, and the session client's budget is minutes — so the
        /// bound is a constant, and the unavailable path (below) does not spend even this one.
        /// </summary>
        private const int MaxTransportsPerCall = 2;

        private readonly object _sync = new();
        private Task<CatalogSnapshot>? _refresh;
        private CatalogSnapshot? _cached;

        public async Task<CatalogSnapshot> GetAsync(CancellationToken ct)
        {
            for (var transports = 1; ; transports++)
            {
                Task<CatalogSnapshot> refresh;
                lock (_sync)
                {
                    var now = timeProvider.GetUtcNow();
                    if (_cached is { } cached && IsFresh(cached, now))
                    {
                        return cached;
                    }

                    // Drop an in-flight slot that can no longer produce an answer worth having BEFORE
                    // joining it. A waiter whose token fired leaves its refresh behind (the `finally`
                    // below only clears a slot that had already completed), so without this the next
                    // caller silently adopts that abandoned flight — and adopts its verdict too, whether
                    // that is a snapshot fetched minutes ago, an exception, or a cancellation. "The
                    // gateway said so, some time before anyone was listening" is not a current read.
                    if (_refresh is { } pending && !CanStillAnswer(pending, now))
                    {
                        _refresh = null;
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
                    // Freshness is decided HERE, at the boundary the value is actually handed back, not at
                    // the boundary the wait began on. Between those two points a whole transport budget
                    // elapsed, so the check at the top of the loop says nothing about what this caller is
                    // about to act on.
                    var now = timeProvider.GetUtcNow();

                    // Publication is monotonic in FetchedAt. An unconditional assignment lets a caller
                    // that waited out a long budget overwrite a NEWER snapshot published while it waited,
                    // which does not merely lose information — it re-authorizes whatever the newer
                    // snapshot had just refused, for the newer snapshot's whole remaining window.
                    if (_cached is null || value.FetchedAt >= _cached.FetchedAt)
                    {
                        _cached = value;
                    }

                    // …and the same ordering decides what THIS caller returns. A newer snapshot that is
                    // still fresh is the current answer for everyone, including the caller holding an
                    // older one: returning the older success here would be a session starting on a
                    // catalog the cache has already recorded as unreadable.
                    if (
                        _cached is { } published
                        && !ReferenceEquals(published, value)
                        && published.FetchedAt > value.FetchedAt
                        && IsFresh(published, now)
                    )
                    {
                        return published;
                    }

                    if (IsFresh(value, now))
                    {
                        return value;
                    }

                    // An UNAVAILABLE snapshot that aged out while this caller was resuming fails closed
                    // for THIS caller and stops there. Asking again would cost a second full transport
                    // budget on top of the one that just expired — the session budget is minutes, so the
                    // caller that was already waiting the longest is the one made to wait twice as long,
                    // and the daemon stage deadline it is racing does not double with it. The snapshot is
                    // deliberately caller-local (never published): publishing an "unavailable" that no
                    // transport produced would suppress the real re-read for the whole unavailable
                    // window. Recovery is the next caller's job, and the next caller finds no fresh cache
                    // and goes to the gateway.
                    if (!value.Available)
                    {
                        return Expired(value, now);
                    }
                }

                // An aged-out SUCCESS may be revalidated, bounded by MaxTransportsPerCall. It is the one
                // case where asking again is cheap in the way the unavailable case is not: the flight
                // that produced it returned an answer rather than burning its budget to a timeout.
                if (transports >= MaxTransportsPerCall)
                {
                    return value;
                }
            }
        }

        /// <summary>
        /// Whether joining <paramref name="refresh"/> can still yield a usable snapshot. A flight that is
        /// still running can (that is the single-flight this cache exists for). A COMPLETED one can only
        /// if it completed normally and its snapshot is still within its own window — a faulted or
        /// cancelled flight has no answer at all, and a stale one has an answer that expired unobserved.
        /// </summary>
        private static bool CanStillAnswer(Task<CatalogSnapshot> refresh, DateTimeOffset now) =>
            !refresh.IsCompleted || (refresh.Status == TaskStatus.RanToCompletion && IsFresh(refresh.Result, now));

        private static bool IsFresh(CatalogSnapshot snapshot, DateTimeOffset now) =>
            now - snapshot.FetchedAt < MaxAge(snapshot);

        /// <summary>
        /// The caller-local fail-closed answer for a snapshot that aged out before it could be returned.
        /// Keeps the original error and cause — that is still the reason the gateway could not be read —
        /// but is stamped now so no caller mistakes it for a current successful check.
        /// </summary>
        private static CatalogSnapshot Expired(CatalogSnapshot stale, DateTimeOffset now) =>
            new(false, [], stale.Error, now, [], null, stale.Cause);

        private static TimeSpan MaxAge(CatalogSnapshot snapshot) =>
            snapshot.Available ? CacheDuration : UnavailableCacheDuration;

        private async Task<CatalogSnapshot> RefreshAsync()
        {
            try
            {
                var catalog = await client.GetCatalogAsync(null, CancellationToken.None);
                var aliases = catalog.Marketplaces.Select(x => x.Alias).Distinct(StringComparer.Ordinal).ToArray();
                var availablePlugins = catalog
                    .Marketplaces.SelectMany(m => m.Plugins.Select(p => new PluginRef(m.Alias, p.Name)))
                    .ToArray();
                return new CatalogSnapshot(
                    true,
                    aliases,
                    null,
                    timeProvider.GetUtcNow(),
                    availablePlugins,
                    catalog.Capabilities.PluginFiltering
                );
            }
            catch (MarketplaceCatalogUnavailableException ex)
            {
                // An unreachable gateway advertises nothing, so the capability stays null and any explicit
                // plugin selection fails closed rather than being validated against an empty catalog.
                //
                // The failed snapshot REPLACES a previously good one rather than falling back to it. That
                // is deliberate: this snapshot is what authorizes an explicit plugin selection, and
                // "the catalog said so 40 seconds ago" is not permission. Fast re-read
                // (UnavailableCacheDuration), not stale trust, is how a recovered gateway gets back in.
                return new CatalogSnapshot(false, [], ex.Message, timeProvider.GetUtcNow(), [], null, ex);
            }
        }
    }

    private sealed record CatalogSnapshot(
        bool Available,
        IReadOnlyList<string> Aliases,
        string? Error,
        DateTimeOffset FetchedAt,
        IReadOnlyList<PluginRef> AvailablePlugins,
        bool? PluginFilteringSupported,
        Exception? Cause = null
    );
}

public sealed class UnsupportedWorkspaceMarketplacesException : InvalidOperationException
{
    public UnsupportedWorkspaceMarketplacesException(IReadOnlyList<string> unsupported, IReadOnlyList<string> available)
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
    /// <param name="message">The flattened reason, echoed to the caller as the 503 detail.</param>
    /// <param name="innerException">
    /// The failure that made the catalog unreadable, kept because the message alone cannot tell a cold
    /// gateway apart from a rejected credential once both have been reduced to a sentence. Optional so
    /// existing single-argument call sites keep compiling.
    /// </param>
    public WorkspaceGatewayCatalogUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
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
        IReadOnlyList<PluginRef> availablePlugins
    )
        : base(
            $"Unsupported plugins: {string.Join(", ", unsupportedPlugins.Select(p => $"{p.Marketplace}/{p.Plugin}"))}"
        )
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
                + $"name, at index: {string.Join(", ", indexes)}."
        )
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
        : base("The gateway does not support plugin filtering; an explicit plugin selection cannot be applied.") { }
}
