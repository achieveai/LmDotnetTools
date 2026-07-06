using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

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
/// <remarks>
/// GitHub Copilot models are discovered dynamically at startup (see
/// <see cref="AchieveAi.LmDotnetTools.GithubCopilotProvider.Models.CopilotModelsClient"/>) and injected
/// as catalog entries keyed by their raw model id, partitioned into <c>Copilot · Anthropic</c> /
/// <c>Copilot · OpenAI</c> groups. When no Copilot token resolves (or discovery fails) the injected
/// list is empty and no Copilot models are exposed.
/// </remarks>
public sealed class ProviderRegistry : AchieveAi.LmDotnetTools.LmAgentInfra.IProviderResolver
{
    private const string CopilotAnthropicGroup = "Copilot · Anthropic";
    private const string CopilotOpenAiGroup = "Copilot · OpenAI";

    private static readonly ImmutableArray<CatalogEntry> CatalogEntries =
        [
            new("openai", "OpenAI"),
            new("anthropic", "Anthropic"),
            new("test", "Test (Mock)"),
            new("test-anthropic", "Test (Anthropic)"),
            new("claude", "Claude (CLI)"),
            new("codex", "Codex"),
            new("copilot", "Copilot (CLI)"),
            new("claude-mock", "Claude (CLI, Mock)"),
            new("codex-mock", "Codex (Mock)"),
            new("copilot-mock", "Copilot (CLI, Mock)"),
        ];

    private readonly record struct CatalogEntry(
        string Id,
        string DisplayName,
        string? KnownLimitation = null);

    private readonly ImmutableDictionary<string, ProviderDescriptor> _byId;
    private readonly ImmutableDictionary<string, CopilotModelInfo> _copilotModelsById;
    private readonly ImmutableHashSet<string> _staticAvailability;
    private readonly Func<string, bool> _dynamicAvailability;

    public ProviderRegistry(IFileSystemProbe? probe = null, MockProviderHostLifetime? mockHost = null)
        : this(probe, mockHost is null ? () => false : () => mockHost.IsRunning)
    {
    }

    /// <summary>
    ///     Production constructor that injects the Copilot models discovered at startup. When the list is
    ///     empty (no token, or discovery failed) no Copilot models are exposed.
    /// </summary>
    public ProviderRegistry(
        IReadOnlyList<CopilotModelInfo> copilotModels,
        IFileSystemProbe? probe = null,
        MockProviderHostLifetime? mockHost = null)
        : this(probe, mockHost is null ? () => false : () => mockHost.IsRunning, copilotModels)
    {
    }

    // Test-only constructor: lets tests inject a stub IsRunning probe without standing up a
    // real Kestrel host, and inject the Copilot token gate (copilotTokenAvailable) so discovered
    // Copilot models can render available without a real gh/Copilot login. Marked internal so it
    // stays out of the public surface.
    internal ProviderRegistry(
        IFileSystemProbe? probe,
        Func<bool> mockHostIsRunning,
        IReadOnlyList<CopilotModelInfo>? copilotModels = null,
        Func<bool>? copilotTokenAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(mockHostIsRunning);

        probe ??= new FileSystemProbe();
        var bootMode = NormalizeId(Environment.GetEnvironmentVariable("LM_PROVIDER_MODE")) ?? "test";
        DefaultProviderId = bootMode;

        var builder = ImmutableDictionary.CreateBuilder<string, ProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        var staticBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var copilotBuilder = ImmutableDictionary.CreateBuilder<string, CopilotModelInfo>(StringComparer.OrdinalIgnoreCase);

        // Pre-compute the static availability of CLI gates and env-var gates once.
        // *-mock providers AND-on the runtime mock-host signal in the dynamic delegate below.
        var hasClaudeCli = HasCliPath("CLAUDE_CLI_PATH", "claude", probe);
        var hasCopilotCli = HasCliPath("COPILOT_CLI_PATH", "copilot", probe);
        var hasOpenAiKey = HasEnvVar("OPENAI_API_KEY");
        var hasAnthropicKey = HasEnvVar("ANTHROPIC_API_KEY");
        // Copilot-backed providers route through the Copilot API using the developer's existing
        // Copilot/gh login, so they are available whenever a token can be resolved. The check is
        // delegated (defaulting to the real CLI-credential probe) so tests can force it without a login.
        var hasCopilotToken = (copilotTokenAvailable ?? DefaultCopilotTokenAvailable)();

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
                _ = staticBuilder.Add(id);
            }

            builder[id] = new ProviderDescriptor(id, displayName, isStatic, entry.KnownLimitation);
        }

        // Dynamically discovered GitHub Copilot models — one entry per routable Anthropic/OpenAI
        // model, keyed by its raw model id and partitioned into the two Copilot groups. Availability
        // mirrors the Copilot token gate the former curated entries used.
        foreach (var model in copilotModels ?? [])
        {
            var id = NormalizeId(model.Id);
            if (id is null || builder.ContainsKey(id))
            {
                continue;
            }

            copilotBuilder[id] = model;
            if (hasCopilotToken)
            {
                _ = staticBuilder.Add(id);
            }

            var group = model.Vendor == CopilotModelVendor.Anthropic ? CopilotAnthropicGroup : CopilotOpenAiGroup;
            // Suffix "(Copilot)" so the model is identifiable as Copilot-backed even in a client that
            // renders a flat list without the group headers.
            var displayName = $"{model.DisplayName} (Copilot)";
            builder[id] = new ProviderDescriptor(id, displayName, hasCopilotToken, Group: group);
        }

        _byId = builder.ToImmutable();
        _copilotModelsById = copilotBuilder.ToImmutable();
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
    ///     Resolves the discovered GitHub Copilot model backing a provider id, if any. Returns
    ///     <c>false</c> for non-Copilot providers (openai/anthropic/CLI/test), letting callers keep
    ///     their existing behavior and only branch into Copilot-specific wiring (transport, raw model
    ///     id, reasoning options, web-tool fallback) when this returns <c>true</c>.
    /// </summary>
    public bool TryGetCopilotModel(string? providerId, out CopilotModelInfo model)
    {
        var normalized = NormalizeId(providerId);
        if (normalized != null && _copilotModelsById.TryGetValue(normalized, out var found))
        {
            model = found;
            return true;
        }

        model = null!;
        return false;
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
        return string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim().ToLowerInvariant();
    }

    private static bool IsMockProvider(string normalizedId)
    {
        return normalizedId.EndsWith("-mock", StringComparison.Ordinal);
    }

    // Production default for the Copilot token gate: a token resolves from the developer's existing
    // gh/Copilot CLI credentials. Overridable via the internal ctor's copilotTokenAvailable delegate
    // so tests need no real login.
    private static bool DefaultCopilotTokenAvailable()
    {
        return new CliCredentialCopilotTokenProvider().ResolveToken() is not null;
    }

    private static bool HasEnvVar(string name)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
    }

    private static bool HasCliPath(string envVar, string cliName, IFileSystemProbe probe)
    {
        var explicitPath = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(explicitPath) ? probe.FileExists(explicitPath) : probe.IsExecutableOnPath(cliName);
    }
}
