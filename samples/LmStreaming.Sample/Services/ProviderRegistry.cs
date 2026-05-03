using System.Collections.Immutable;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Centralises the set of LLM providers the sample app exposes to clients.
/// Availability rules look only at env vars and (cached) PATH probes, so this is safe
/// to call on the request hot path. Probes execute once at construction and the result
/// is frozen for the process lifetime.
/// </summary>
/// <remarks>
/// The <c>*-mock</c> providers depend on a runtime signal — whether the in-process
/// <see cref="MockProviderHostLifetime"/> bound successfully — so their availability is
/// resolved through a delegate captured at construction time and re-evaluated on each
/// <see cref="IsAvailable"/> / <see cref="ListAll"/> call.
/// </remarks>
public sealed class ProviderRegistry
{
    private static readonly ImmutableArray<CatalogEntry> CatalogEntries =
        [
            new("openai", "OpenAI"),
            new("anthropic", "Anthropic"),
            new("test", "Test (Mock)"),
            new("test-anthropic", "Test (Anthropic)"),
            new("claude", "Claude (CLI)"),
            new("codex", "Codex"),
            new("copilot", "Copilot (CLI)"),
            // Mock entries with known follow-up limitations are tagged here so the UI can
            // surface a caveat next to them. Wire-format gaps are tracked in dedicated issues
            // (see body) — fixing those here lets the gate flip to null and the banner disappears.
            new(
                "claude-mock",
                "Claude (CLI, Mock)",
                "Known limitation: the Claude CLI's /v1/messages handshake against the mock host "
                + "currently completes silently with no rendered content. Tracked in #29."),
            new(
                "codex-mock",
                "Codex (Mock)",
                "Known limitation: Codex CLI defaults to /v1/responses which the mock host does "
                + "not yet implement, so the run hangs. Tracked in #28."),
            new("copilot-mock", "Copilot (CLI, Mock)"),
        ];

    private readonly record struct CatalogEntry(
        string Id,
        string DisplayName,
        string? KnownLimitation = null);

    private readonly ImmutableDictionary<string, ProviderDescriptor> _byId;
    private readonly ImmutableHashSet<string> _staticAvailability;
    private readonly Func<string, bool> _dynamicAvailability;

    public ProviderRegistry(IFileSystemProbe? probe = null, MockProviderHostLifetime? mockHost = null)
        : this(probe, mockHost is null ? () => false : () => mockHost.IsRunning)
    {
    }

    // Test-only constructor: lets tests inject a stub IsRunning probe without standing up a
    // real Kestrel host. Marked internal so it stays out of the public surface.
    internal ProviderRegistry(IFileSystemProbe? probe, Func<bool> mockHostIsRunning)
    {
        ArgumentNullException.ThrowIfNull(mockHostIsRunning);

        probe ??= new FileSystemProbe();
        var bootMode = NormalizeId(Environment.GetEnvironmentVariable("LM_PROVIDER_MODE")) ?? "test";
        DefaultProviderId = bootMode;

        var builder = ImmutableDictionary.CreateBuilder<string, ProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        var staticBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-compute the static availability of CLI gates and env-var gates once.
        // *-mock providers AND-on the runtime mock-host signal in the dynamic delegate below.
        var hasClaudeCli = HasCliPath("CLAUDE_CLI_PATH", "claude", probe);
        var hasCopilotCli = HasCliPath("COPILOT_CLI_PATH", "copilot", probe);
        var hasOpenAiKey = HasEnvVar("OPENAI_API_KEY");
        var hasAnthropicKey = HasEnvVar("ANTHROPIC_API_KEY");

        foreach (var entry in CatalogEntries)
        {
            var id = entry.Id;
            var displayName = entry.DisplayName;
            var isStatic = id switch
            {
                "test" or "test-anthropic" or "codex" => true,
                "openai" => hasOpenAiKey,
                "anthropic" => hasAnthropicKey,
                "claude" => hasClaudeCli,
                "copilot" => hasCopilotCli,
                // *-mock providers need both their CLI prerequisite and the running mock host;
                // codex-mock has no CLI prerequisite (codex availability is unconditional).
                "claude-mock" => hasClaudeCli,
                "codex-mock" => true,
                "copilot-mock" => hasCopilotCli,
                _ => false,
            };

            if (isStatic)
            {
                staticBuilder.Add(id);
            }

            builder[id] = new ProviderDescriptor(id, displayName, isStatic, entry.KnownLimitation);
        }

        _byId = builder.ToImmutable();
        _staticAvailability = staticBuilder.ToImmutable();
        _dynamicAvailability = id => _staticAvailability.Contains(id)
            && (!IsMockProvider(id) || mockHostIsRunning());
    }

    /// <summary>
    /// The provider id resolved from <c>LM_PROVIDER_MODE</c> at construction time.
    /// Used as the fallback for legacy threads with no persisted provider.
    /// </summary>
    public string DefaultProviderId { get; }

    public bool IsAvailable(string providerId)
    {
        var normalized = NormalizeId(providerId);
        return normalized != null
            && _byId.ContainsKey(normalized)
            && _dynamicAvailability(normalized);
    }

    public bool IsKnown(string providerId)
    {
        var normalized = NormalizeId(providerId);
        return normalized != null && _byId.ContainsKey(normalized);
    }

    public ProviderDescriptor? Get(string providerId)
    {
        var normalized = NormalizeId(providerId);
        if (normalized == null)
        {
            return null;
        }

        if (!_byId.TryGetValue(normalized, out var descriptor))
        {
            return null;
        }

        // Re-evaluate availability so callers see the live mock-host status. The static
        // descriptor stored in the dictionary is only used as a name/display source.
        return descriptor with { Available = _dynamicAvailability(normalized) };
    }

    /// <summary>
    /// Returns the full provider catalog (each entry tagged with its availability flag).
    /// Used by <c>GET /api/providers</c>; the client is responsible for hiding entries
    /// it doesn't want to render.
    /// </summary>
    public IReadOnlyList<ProviderDescriptor> ListAll()
    {
        return [.. _byId.Values
            .Select(d => d with { Available = _dynamicAvailability(d.Id) })
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? NormalizeId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return providerId.Trim().ToLowerInvariant();
    }

    private static bool IsMockProvider(string normalizedId) =>
        normalizedId.EndsWith("-mock", StringComparison.Ordinal);

    private static bool HasEnvVar(string name)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
    }

    private static bool HasCliPath(string envVar, string cliName, IFileSystemProbe probe)
    {
        var explicitPath = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return probe.FileExists(explicitPath);
        }

        return probe.IsExecutableOnPath(cliName);
    }
}
