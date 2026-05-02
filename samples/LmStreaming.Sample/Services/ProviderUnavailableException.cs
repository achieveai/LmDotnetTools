namespace LmStreaming.Sample.Services;

/// <summary>
/// Thrown when an agent factory is asked to create an agent for a provider that
/// is not currently available (missing API key, MCP server failed to start, etc.).
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string providerId, string reason)
        : base($"Provider '{providerId}' is unavailable: {reason}")
    {
        ProviderId = providerId;
        Reason = reason;
    }

    public ProviderUnavailableException(string providerId, string reason, Exception inner)
        : base($"Provider '{providerId}' is unavailable: {reason}", inner)
    {
        ProviderId = providerId;
        Reason = reason;
    }

    public string ProviderId { get; }

    public string Reason { get; }
}
