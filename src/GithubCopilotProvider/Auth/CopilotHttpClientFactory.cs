using AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

/// <summary>
///     Builds <see cref="HttpClient"/> instances that route through the GitHub Copilot API by
///     wrapping a transport handler with <see cref="CopilotHeadersHandler"/> (auth + Copilot headers).
/// </summary>
public static class CopilotHttpClientFactory
{
    /// <summary>
    ///     Creates an <see cref="HttpClient"/> whose every request is authenticated and decorated for
    ///     the Copilot API.
    /// </summary>
    /// <param name="baseAddress">Base address for the client (e.g. the Copilot host root).</param>
    /// <param name="tokenProvider">Bearer-token source.</param>
    /// <param name="session">Shared client/machine tracking ids.</param>
    /// <param name="options">Copilot header options (integration id, version, extra headers).</param>
    /// <param name="timeout">Optional timeout (defaults to <see cref="HttpClientFactory.DefaultTimeout"/>).</param>
    /// <param name="innerHandler">
    ///     Optional transport handler to wrap (for tests, Polly resilience, or IHttpClientFactory
    ///     integration). Defaults to a new <see cref="HttpClientHandler"/>.
    /// </param>
    public static HttpClient Create(
        string baseAddress,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext session,
        CopilotOptions? options = null,
        TimeSpan? timeout = null,
        HttpMessageHandler? innerHandler = null
    )
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(session);

        var handler = new CopilotHeadersHandler(
            tokenProvider,
            session,
            options,
            innerHandler ?? new HttpClientHandler()
        );

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseAddress.TrimEnd('/')),
            Timeout = timeout ?? HttpClientFactory.DefaultTimeout,
        };
    }
}
