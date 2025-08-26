namespace AchieveAi.LmDotnetTools.LmConfig.Http;

public enum ProviderType
{
    OpenAI,
    Anthropic,
}

/// <summary>
/// Details required to configure provider-specific authentication.
/// </summary>
public readonly record struct ProviderConfig(
    string ApiKey,
    string BaseUrl,
    ProviderType Type = ProviderType.OpenAI
);
