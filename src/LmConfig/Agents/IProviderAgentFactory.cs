using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
///     Factory responsible for creating appropriate agents for resolved providers.
/// </summary>
public interface IProviderAgentFactory
{
    /// <summary>
    ///     Creates an agent for the specified provider resolution.
    /// </summary>
    /// <param name="resolution">The provider resolution containing all necessary configuration.</param>
    /// <returns>A configured agent instance for the provider.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provider is not supported.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the provider configuration is invalid.</exception>
    IAgent CreateAgent(ProviderResolution resolution);

    /// <summary>
    ///     Creates a streaming agent for the specified provider resolution.
    /// </summary>
    /// <param name="resolution">The provider resolution containing all necessary configuration.</param>
    /// <returns>A configured streaming agent instance for the provider.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provider is not supported or doesn't support streaming.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the provider configuration is invalid.</exception>
    IStreamingAgent CreateStreamingAgent(ProviderResolution resolution);

    /// <summary>
    ///     Checks if the factory can create an agent for the specified provider.
    /// </summary>
    /// <param name="providerName">The name of the provider to check.</param>
    /// <returns>True if the factory can create an agent for the provider, false otherwise.</returns>
    bool CanCreateAgent(string providerName);

    /// <summary>
    ///     Checks if the factory can create a streaming agent for the specified provider.
    /// </summary>
    /// <param name="providerName">The name of the provider to check.</param>
    /// <returns>True if the factory can create a streaming agent for the provider, false otherwise.</returns>
    bool CanCreateStreamingAgent(string providerName);

    /// <summary>
    ///     Gets all supported provider names.
    /// </summary>
    /// <returns>List of all provider names that this factory can create agents for.</returns>
    IReadOnlyList<string> GetSupportedProviders();

    /// <summary>
    ///     Gets information about the capabilities of a specific provider.
    /// </summary>
    /// <param name="providerName">The name of the provider to get information for.</param>
    /// <returns>Provider capability information, or null if the provider is not supported.</returns>
    ProviderCapabilityInfo? GetProviderCapabilities(string providerName);
}

/// <summary>
///     Information about what capabilities a provider agent factory supports.
/// </summary>
public record ProviderCapabilityInfo
{
    /// <summary>
    ///     The name of the provider.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Whether the provider supports basic (non-streaming) agents.
    /// </summary>
    public bool SupportsBasicAgent { get; init; } = true;

    /// <summary>
    ///     Whether the provider supports streaming agents.
    /// </summary>
    public bool SupportsStreamingAgent { get; init; } = true;

    /// <summary>
    ///     The compatibility type of the provider (e.g., "OpenAI", "Anthropic").
    /// </summary>
    public string? CompatibilityType { get; init; }

    /// <summary>
    ///     Additional notes about the provider's capabilities or limitations.
    /// </summary>
    public string? Notes { get; init; }
}
