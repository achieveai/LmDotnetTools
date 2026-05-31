using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmCore.Auth;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     <see cref="DelegatingHandler"/> that authenticates and decorates outgoing requests for the
///     GitHub Copilot API. On every request it resolves a bearer token from an
///     <see cref="ICopilotTokenProvider"/>, sets <c>Authorization</c>, and adds the Copilot
///     integration/tracking headers the backend expects.
/// </summary>
/// <remarks>
///     Headers already present on a request are never overwritten, so a provider client that sets
///     its own protocol header (for example the Anthropic client's <c>anthropic-version</c>) keeps
///     control. A fresh <c>x-interaction-id</c> is generated per request; the machine and session
///     ids come from the shared <see cref="CopilotSessionContext"/> and stay stable.
/// </remarks>
public sealed class CopilotHeadersHandler : DelegatingHandler
{
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly CopilotSessionContext _session;
    private readonly CopilotOptions _options;

    /// <summary>
    ///     Creates the handler. When <paramref name="innerHandler"/> is null a default
    ///     <see cref="HttpClientHandler"/> is used, so the handler can drive an
    ///     <see cref="HttpClient"/> on its own.
    /// </summary>
    public CopilotHeadersHandler(
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext session,
        CopilotOptions? options = null,
        HttpMessageHandler? innerHandler = null
    )
        : base(innerHandler ?? new HttpClientHandler())
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new CopilotOptions();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var header in CopilotRequestHeaders.Build(_options, _session, Guid.NewGuid().ToString()))
        {
            AddIfMissing(request, header.Key, header.Value);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void AddIfMissing(HttpRequestMessage request, string name, string value)
    {
        if (!request.Headers.Contains(name))
        {
            _ = request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
