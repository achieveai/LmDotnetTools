using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Http;

/// <summary>
/// Builds HttpClient instances using the handler pipeline abstraction.
/// </summary>
public static class HttpClientFactory
{
    public static HttpClient Create(
        ProviderConfig? provider = null,
        IHttpHandlerBuilder? pipeline = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        ILogger? logger = null
    )
    {
        var inner = new HttpClientHandler();
        var top = pipeline?.Build(inner, logger) ?? inner;

        var client = new HttpClient(top) { Timeout = timeout ?? TimeSpan.FromMinutes(5) };

        if (provider is not null && !string.IsNullOrWhiteSpace(provider.Value.BaseUrl))
        {
            client.BaseAddress = new Uri(provider.Value.BaseUrl.TrimEnd('/'));
        }

        if (headers is not null)
        {
            foreach (var h in headers)
            {
                client.DefaultRequestHeaders.Add(h.Key, h.Value);
            }
        }

        if (provider is not null)
        {
            AddAuth(client, provider.Value);
        }

        return client;
    }

    private static void AddAuth(HttpClient client, ProviderConfig provider)
    {
        switch (provider.Type)
        {
            case ProviderType.OpenAI:
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {provider.ApiKey}");
                break;
            case ProviderType.Anthropic:
                client.DefaultRequestHeaders.Add("x-api-key", provider.ApiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
