using System.Collections.Immutable;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Centralises the set of LLM providers the sample app exposes to clients.
/// Availability rules look only at env vars and (cached) PATH probes, so this is safe
/// to call on the request hot path. Probes execute once at construction and the result
/// is frozen for the process lifetime.
/// </summary>
public sealed class ProviderRegistry
{
    private static readonly ImmutableArray<(string Id, string DisplayName)> CatalogEntries =
        [
            ("openai", "OpenAI"),
            ("anthropic", "Anthropic"),
            ("test", "Test (Mock)"),
            ("test-anthropic", "Test (Anthropic)"),
            ("claude", "Claude (CLI)"),
            ("codex", "Codex"),
            ("copilot", "Copilot (CLI)"),
        ];

    private readonly ImmutableDictionary<string, ProviderDescriptor> _byId;

    public ProviderRegistry(IFileSystemProbe? probe = null)
    {
        probe ??= new FileSystemProbe();
        var bootMode = NormalizeId(Environment.GetEnvironmentVariable("LM_PROVIDER_MODE")) ?? "test";
        DefaultProviderId = bootMode;

        var builder = ImmutableDictionary.CreateBuilder<string, ProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, displayName) in CatalogEntries)
        {
            builder[id] = new ProviderDescriptor(id, displayName, ComputeAvailability(id, probe));
        }

        _byId = builder.ToImmutable();
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
            && _byId.TryGetValue(normalized, out var descriptor)
            && descriptor.Available;
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

        return _byId.TryGetValue(normalized, out var descriptor) ? descriptor : null;
    }

    /// <summary>
    /// Returns the full provider catalog (each entry tagged with its availability flag).
    /// Used by <c>GET /api/providers</c>; the client is responsible for hiding entries
    /// it doesn't want to render.
    /// </summary>
    public IReadOnlyList<ProviderDescriptor> ListAll()
    {
        return [.. _byId.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? NormalizeId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return providerId.Trim().ToLowerInvariant();
    }

    private static bool ComputeAvailability(string providerId, IFileSystemProbe probe)
    {
        return providerId switch
        {
            "test" or "test-anthropic" => true,
            "openai" => HasEnvVar("OPENAI_API_KEY"),
            "anthropic" => HasEnvVar("ANTHROPIC_API_KEY"),
            "claude" => HasCliPath("CLAUDE_CLI_PATH", "claude", probe),
            "codex" => true,
            "copilot" => HasCliPath("COPILOT_CLI_PATH", "copilot", probe),
            _ => false,
        };
    }

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
