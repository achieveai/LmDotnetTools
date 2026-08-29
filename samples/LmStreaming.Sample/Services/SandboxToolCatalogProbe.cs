using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using ModelContextProtocol.Client;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Enumerates the tools the sandbox gateway currently offers, so the Modes editor can list real
///     workspace tools instead of a guess.
/// </summary>
/// <remarks>
///     An interface purely so the catalog can be tested against a gateway that answers and a gateway
///     that is down — neither reachable from a unit test through the concrete probe's
///     <c>SandboxSessionRegistry</c> dependency.
/// </remarks>
public interface ISandboxToolCatalogProbe
{
    /// <summary>Returns the current sandbox tool listing, from cache when it is still fresh.</summary>
    Task<SandboxToolCatalog> GetAsync(TimeProvider timeProvider, CancellationToken ct = default);
}

/// <summary>
///     Enumerates the tools the sandbox gateway currently offers, so the Modes editor can list real
///     workspace tools instead of a guess.
/// </summary>
/// <remarks>
///     <para>
///         There is no static list to read: the gateway's tool surface depends on which marketplace
///         plugins a workspace has installed, so the only authoritative source is the gateway's own
///         <c>tools/list</c>. This probe asks it once and caches the answer for
///         <see cref="CacheTtl" />, because the catalog is fetched every time the Modes modal opens
///         and establishing a session per open would be absurd.
///     </para>
///     <para>
///         <b>Failure is not fatal and must not be silent.</b> When the gateway is unreachable the
///         probe returns <see cref="StaticBaseline" /> together with a warning string, and the caller
///         propagates that warning to the UI. Returning the baseline unlabelled would present a
///         possibly-incomplete list as the whole truth — and the user would then build a mode whose
///         allow-list is missing tools that actually exist.
///     </para>
/// </remarks>
public sealed class SandboxToolCatalogProbe(
    SandboxSessionRegistry sessionRegistry,
    SandboxGatewayLifetime gatewayLifetime,
    ILoggerFactory loggerFactory
) : ISandboxToolCatalogProbe
{
    /// <summary>How long a successful listing is reused before the gateway is asked again.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How long the whole live probe may take before the labelled baseline is served instead.
    /// </summary>
    /// <remarks>
    ///     A gateway that REFUSES a connection fails fast and needs no bound. One that accepts the
    ///     socket and then never answers does not: without this, session creation and
    ///     <c>McpClient.CreateAsync</c> would both wait forever while holding the single-entry lock,
    ///     turning a degraded-but-usable Modes editor into a hang. The fallback below is triggered by
    ///     failure, so it has to be reachable by timeout and not only by exception.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The tools the gateway has always shipped. Used only as a labelled fallback when the live
    ///     listing fails — never as the primary source, because it cannot know about plugin-provided
    ///     tools such as <c>Skill</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> StaticBaseline =
    [
        "Bash",
        "PowerShell",
        "Read",
        "Write",
        "Edit",
        "Glob",
        "Grep",
        "Skill",
    ];

    private readonly ILogger _logger = loggerFactory.CreateLogger<SandboxToolCatalogProbe>();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SandboxToolCatalog? _cached;
    private DateTimeOffset _cachedAt;

    /// <inheritdoc />
    public async Task<SandboxToolCatalog> GetAsync(TimeProvider timeProvider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var now = timeProvider.GetUtcNow();
        if (_cached is { } fresh && now - _cachedAt < CacheTtl)
        {
            return fresh;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: several clients opening the Modes modal at once would otherwise
            // each establish their own session behind the same expiry.
            now = timeProvider.GetUtcNow();
            if (_cached is { } stillFresh && now - _cachedAt < CacheTtl)
            {
                return stillFresh;
            }

            var probed = await ProbeAsync(timeProvider, ct).ConfigureAwait(false);

            // Only a SUCCESSFUL listing is cached. Caching a failure would pin the baseline for the
            // whole TTL, so a gateway that comes up thirty seconds later would still read as down.
            if (probed.IsLive)
            {
                _cached = probed;
                _cachedAt = now;
            }

            return probed;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    private async Task<SandboxToolCatalog> ProbeAsync(TimeProvider timeProvider, CancellationToken ct)
    {
        // Driven by the injected TimeProvider so a test can expire the budget deterministically
        // rather than by sleeping for the real timeout.
        using var timeoutCts = new CancellationTokenSource(ProbeTimeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        McpClient? client = null;
        try
        {
            // The DEFAULT workspace, deliberately: this listing feeds a mode editor, which is not
            // scoped to any one workspace, and a mode is reusable across all of them. A workspace
            // whose plugins add tools beyond this listing is covered by the wildcard row rather than
            // by probing every workspace here.
            var session = await sessionRegistry
                .GetOrCreateLiveSessionAsync(SandboxSessionRegistry.DefaultWorkspaceId, linkedCts.Token)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string> { ["X-Session-ID"] = session.SessionId };
            sessionRegistry.DefaultCredential.StampHeaders(headers);

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "sandbox",
                    Endpoint = new Uri($"{gatewayLifetime.GatewayBaseUrl}/mcp"),
                    ConnectionTimeout = ProbeTimeout,
                    AdditionalHeaders = headers,
                }
            );

            client = await McpClient.CreateAsync(transport, cancellationToken: linkedCts.Token).ConfigureAwait(false);

            // Reuse the registry's own MCP->contract projection rather than reading the raw tool list,
            // so the names and descriptions the editor shows are exactly the ones the agent would get.
            var scratch = new FunctionRegistry();
            _ = await scratch
                .AddMcpClientsAsync(
                    new Dictionary<string, McpClient> { ["sandbox"] = client },
                    "sandbox",
                    omitServerPrefix: true,
                    cancellationToken: linkedCts.Token
                )
                .ConfigureAwait(false);

            var (contracts, _) = scratch.Build();
            var tools = contracts
                .Select(c => (Name: c.Name, Description: c.Description))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation("Sandbox tool catalog listed {Count} tools from the gateway", tools.Count);

            return new SandboxToolCatalog(tools, IsLive: true, Warning: null);
        }
        // Branch on the CALLER's token, not on the exception type. The session registry wraps a
        // cancelled HTTP call in its own exception, so a type test here would read an abandoned
        // request as a broken gateway and answer it with a degraded catalog nobody asked for.
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        // Everything else is a probe failure - including this probe's OWN budget expiring, which is
        // the whole reason the fallback has to be reachable without an exception from the gateway.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not list sandbox tools from the gateway within {Timeout}; the Modes editor "
                    + "falls back to the static baseline and says so",
                ProbeTimeout
            );

            var baseline = StaticBaseline.Select(name => (Name: name, Description: (string?)null)).ToList();

            return new SandboxToolCatalog(
                baseline,
                IsLive: false,
                Warning: "The sandbox gateway could not be reached, so this list is the built-in "
                    + "baseline and may be missing tools your workspace's plugins provide. Select "
                    + "\"All workspace tools\" to include them regardless."
            );
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>The sandbox gateway's tool listing, plus whether it was obtained live.</summary>
/// <param name="Tools">Bare tool names and descriptions.</param>
/// <param name="IsLive">True when the gateway answered; false when <paramref name="Tools"/> is the static baseline.</param>
/// <param name="Warning">Set when <paramref name="IsLive"/> is false, explaining the listing may be incomplete.</param>
public sealed record SandboxToolCatalog(
    IReadOnlyList<(string Name, string? Description)> Tools,
    bool IsLive,
    string? Warning
);
