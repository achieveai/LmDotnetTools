using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using ModelContextProtocol.Client;

namespace LmStreaming.Sample.Services;

internal sealed record CopilotWebSearchRegistrationResult(
    bool Registered,
    IAsyncDisposable? Resource,
    string Status
);

internal static class CopilotWebSearchRegistration
{
    internal const string ToolName = "web_search";
    private const string ClientName = "copilot-web-search";
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(10);

    public static CopilotWebSearchRegistrationResult TryRegister(
        FunctionRegistry registry,
        IReadOnlyList<string>? enabledTools,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext session,
        CopilotOptions options,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? innerHandler = null
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (enabledTools is not null && !enabledTools.Contains(ToolName))
        {
            return new(false, null, "Copilot web_search disabled by mode");
        }

        var logger = loggerFactory.CreateLogger("LmStreaming.Sample.CopilotWebSearchRegistration");
        HttpClientTransport? transport = null;
        McpClient? client = null;
        try
        {
            var httpClient = CopilotHttpClientFactory.Create(
                options.BaseUrl,
                tokenProvider,
                session,
                options,
                innerHandler: innerHandler
            );
            transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = ClientName,
                    Endpoint = new Uri(new Uri(options.BaseUrl), "/mcp/readonly"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = InitializationTimeout,
                    AdditionalHeaders = new Dictionary<string, string> { ["X-MCP-Tools"] = ToolName },
                },
                httpClient,
                loggerFactory,
                ownsHttpClient: true
            );

            using var cts = new CancellationTokenSource(InitializationTimeout);
            client = McpClient.CreateAsync(transport, cancellationToken: cts.Token).GetAwaiter().GetResult();
            var provider = McpClientFunctionProvider
                .CreateAsync(
                    new Dictionary<string, McpClient> { [ClientName] = client },
                    ClientName,
                    loggerFactory.CreateLogger<McpClientFunctionProvider>(),
                    cts.Token,
                    omitServerPrefix: true
                )
                .GetAwaiter()
                .GetResult();
            var functions = provider.GetFunctions().ToList();
            if (
                functions.Count != 1
                || !string.Equals(functions[0].Contract.Name, ToolName, StringComparison.Ordinal)
            )
            {
                Dispose(client, transport);
                return new(false, null, "Copilot web_search unavailable");
            }

            _ = registry.AddProvider(provider);
            return new(true, new OwnedMcpResource(client, transport), "Copilot web_search registered");
        }
        catch (Exception ex)
        {
            Dispose(client, transport);
            logger.LogWarning(ex, "Copilot hosted web_search is unavailable; using configured fallback");
            return new(false, null, "Copilot web_search unavailable");
        }
    }

    private static void Dispose(McpClient? client, HttpClientTransport? transport)
    {
        if (client is not null)
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (transport is not null)
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class OwnedMcpResource(McpClient client, HttpClientTransport transport) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
