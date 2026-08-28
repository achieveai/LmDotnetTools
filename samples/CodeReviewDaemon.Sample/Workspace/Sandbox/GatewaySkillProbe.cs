using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// What a gateway marketplace preview says about Revobot's review prerequisites: the
/// <c>code-reviewer:pr-review</c> skill (the prompt's hard dependency — <c>daemon-prompts.yaml</c> tells the
/// agent that skill IS how it reviews) and the <c>code-reviewer:*</c> sub-agents the review dispatches.
/// </summary>
/// <param name="HasReviewSkill">True when the required review skill is present in an allowed marketplace.</param>
/// <param name="ReviewerAgentCount">How many <c>code-reviewer</c> sub-agents the catalog exposes.</param>
/// <param name="MarketplaceErrors">
/// Per-marketplace load errors the gateway reported (<c>SandboxMarketplaceEntry.Error</c>). A marketplace that
/// failed to load contributes no plugins, so this is the usual reason the two checks above come back empty —
/// carrying it through turns "nothing found" into an actionable message.
/// </param>
internal sealed record GatewaySkillSupport(
    bool HasReviewSkill,
    int ReviewerAgentCount,
    IReadOnlyList<string> MarketplaceErrors
)
{
    /// <summary>A review needs BOTH halves: the skill that defines the review procedure and at least one
    /// sub-agent to run the deep passes with.</summary>
    public bool IsSupported => HasReviewSkill && ReviewerAgentCount > 0;

    /// <summary>Operator-readable summary of what the probe did and did not find. Names only plugin/skill
    /// identifiers and gateway-reported errors — never a credential.</summary>
    public string Describe() =>
        $"skill '{GatewaySkillProbe.RequiredPlugin}:{GatewaySkillProbe.RequiredSkill}'="
        + (HasReviewSkill ? "present" : "MISSING")
        + $", {GatewaySkillProbe.RequiredPlugin} sub-agents={ReviewerAgentCount}"
        + (MarketplaceErrors.Count > 0 ? $", marketplace errors=[{string.Join("; ", MarketplaceErrors)}]" : "");
}

/// <summary>
/// Asks the gateway — WITHOUT provisioning a sandbox session — whether the marketplaces the daemon is
/// configured against actually surface Revobot's review prerequisites.
/// </summary>
/// <remarks>
/// This exists because the S2S path has no daemon-side session to inspect: the review runs inside a
/// conversation the review host owns, so the in-process <c>RequireSkillSupport</c> fail-fast (which keys off
/// the daemon's own session discovery) cannot see it. The gateway's
/// <see cref="SandboxClient.PreviewMarketplacesAsync"/> is a session-free read of the same catalog the host's
/// session would be built from, which makes it a genuine pre-flight rather than a post-hoc assertion.
/// </remarks>
internal interface IGatewaySkillProbe
{
    /// <summary>
    /// Previews <paramref name="marketplaces"/> (empty ⇒ the gateway's own default set) and reports what the
    /// catalog exposes. Throws on a gateway/transport failure — the caller decides whether an unreachable
    /// gateway is fatal, so this never silently reports "unsupported" for a reason that is not the catalog.
    /// </summary>
    Task<GatewaySkillSupport> ProbeAsync(IReadOnlyList<string> marketplaces, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGatewaySkillProbe"/> over the typed <see cref="SandboxClient"/> SDK, mirroring
/// <see cref="SandboxSessionAdapter"/>'s client construction (same credential, same
/// <c>allowInsecureDevelopmentTransport</c> for the local/dev gateway) but bound to no session.
/// </summary>
internal sealed class GatewaySkillProbe : IGatewaySkillProbe, IAsyncDisposable
{
    /// <summary>The marketplace plugin that contributes Revobot's review skill + review sub-agents.</summary>
    internal const string RequiredPlugin = "code-reviewer";

    /// <summary>The skill <c>daemon-prompts.yaml</c> makes mandatory ("that skill IS how you review").</summary>
    internal const string RequiredSkill = "pr-review";

    /// <summary>A catalog browse is a single small GET; it must not inherit a command-sized deadline.</summary>
    private static readonly TimeSpan S_probeTimeout = TimeSpan.FromSeconds(30);

    private readonly string _gatewayBaseUrl;
    private readonly SandboxCredential _credential;
    private readonly ILogger<GatewaySkillProbe> _logger;
    private readonly HttpMessageHandler? _testTransport;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SandboxClient? _client;
    private HttpClient? _borrowedHttpClient;
    private bool _disposed;

    public GatewaySkillProbe(string gatewayBaseUrl, SandboxCredential credential, ILogger<GatewaySkillProbe> logger)
        : this(gatewayBaseUrl, credential, logger, testTransport: null) { }

    /// <summary>Test seam: drives the SDK over a scripted gateway transport instead of an owned socket, so the
    /// probe's catalog interpretation is exercised against the real SDK wire protocol with no live gateway.</summary>
    internal GatewaySkillProbe(
        string gatewayBaseUrl,
        SandboxCredential credential,
        ILogger<GatewaySkillProbe> logger,
        HttpMessageHandler? testTransport
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayBaseUrl);
        ArgumentNullException.ThrowIfNull(logger);
        _gatewayBaseUrl = gatewayBaseUrl;
        _credential = credential;
        _logger = logger;
        _testTransport = testTransport;
    }

    public async Task<GatewaySkillSupport> ProbeAsync(
        IReadOnlyList<string> marketplaces,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(marketplaces);

        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await client
            .PreviewMarketplacesAsync(marketplaces.Count > 0 ? marketplaces : null, cancellationToken)
            .ConfigureAwait(false);

        var errors = new List<string>();
        var hasSkill = false;
        var agentCount = 0;

        foreach (var entry in catalog.Marketplaces)
        {
            if (!string.IsNullOrWhiteSpace(entry.Error))
            {
                errors.Add($"{entry.Alias}: {entry.Error}");
            }

            foreach (var plugin in entry.Plugins)
            {
                if (!string.Equals(plugin.Name, RequiredPlugin, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                agentCount += plugin.Agents.Count;
                hasSkill |= plugin.Skills.Any(s =>
                    string.Equals(s.Name, RequiredSkill, StringComparison.OrdinalIgnoreCase)
                );
            }
        }

        var support = new GatewaySkillSupport(hasSkill, agentCount, errors);
        _logger.LogInformation(
            "Gateway marketplace preview for [{Marketplaces}]: {Support}",
            marketplaces.Count > 0 ? string.Join(",", marketplaces) : "(gateway default)",
            support.Describe()
        );
        return support;
    }

    /// <summary>Lazily builds the owned SDK client (thread-safe); construction does no gateway I/O.</summary>
    private async Task<SandboxClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
        {
            return _client;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _client ??= BuildClient();
        }
        finally
        {
            _ = _connectGate.Release();
        }

        return _client;
    }

    private SandboxClient BuildClient()
    {
        var options = new SandboxClientOptions(
            new Uri(_gatewayBaseUrl, UriKind.Absolute),
            _credential.AppId,
            _credential.AppKey,
            executionTimeout: S_probeTimeout,
            transportTimeout: S_probeTimeout,
            allowInsecureDevelopmentTransport: true
        );

        if (_testTransport is null)
        {
            return new SandboxClient(options);
        }

        // The SDK never disposes a borrowed HttpClient, so this one is ours to release in DisposeAsync.
        _borrowedHttpClient = new HttpClient(_testTransport, disposeHandler: false);
        return new SandboxClient(options, _borrowedHttpClient);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client?.Dispose();
        _borrowedHttpClient?.Dispose();
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
