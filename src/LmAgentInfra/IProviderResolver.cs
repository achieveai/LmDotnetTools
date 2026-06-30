namespace AchieveAi.LmDotnetTools.LmAgentInfra;

/// <summary>
/// Provider-neutral seam the shared <see cref="Agents.MultiTurnAgentPool"/> uses to validate
/// and fall back across LLM providers, without depending on any host app's concrete registry.
/// Hosts (LmStreaming.Sample's ProviderRegistry, the daemon's provider list) implement this.
/// </summary>
public interface IProviderResolver
{
    /// <summary>Provider id used when a thread has no explicit provider selection.</summary>
    string DefaultProviderId { get; }

    /// <summary>True if the provider is currently usable (key present, host up, etc.).</summary>
    bool IsAvailable(string providerId);

    /// <summary>True if the provider id is recognized at all (regardless of availability).</summary>
    bool IsKnown(string providerId);
}
