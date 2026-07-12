using System.Net;
using System.Text;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> for driving <see cref="SandboxClient"/> against
/// canned HTTP responses (no network). Routes are matched by a predicate over the outgoing
/// request (method + URI), in registration order; the first match wins. Every request is recorded
/// so a test can assert the exact URL, method, headers, and body the client produced.
/// </summary>
internal sealed class FakeGatewayHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every request that flowed through the handler, in call order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Registers a handler for requests matching <paramref name="predicate"/>.</summary>
    public FakeGatewayHandler On(Func<HttpRequestMessage, bool> predicate, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add(new Route(predicate, respond));
        return this;
    }

    /// <summary>Registers a JSON response for a method + path-suffix match.</summary>
    public FakeGatewayHandler OnJson(HttpMethod method, string pathEndsWith, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(
            req => req.Method == method && req.RequestUri is not null && req.RequestUri.AbsolutePath.EndsWith(pathEndsWith, StringComparison.Ordinal),
            _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
        );

    /// <summary>Registers a bare-status (no body) response for a method + path-suffix match.</summary>
    public FakeGatewayHandler OnStatus(HttpMethod method, string pathEndsWith, HttpStatusCode status) =>
        On(
            req => req.Method == method && req.RequestUri is not null && req.RequestUri.AbsolutePath.EndsWith(pathEndsWith, StringComparison.Ordinal),
            _ => new HttpResponseMessage(status)
        );

    /// <summary>Registers a response that hangs until the request's cancellation token fires — simulates a wedged gateway.</summary>
    public FakeGatewayHandler OnHang(Func<HttpRequestMessage, bool> predicate)
    {
        _routes.Add(new Route(predicate, Respond: null));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(
            new RecordedRequest(
                request.Method,
                request.RequestUri!,
                body,
                GetHeader(request, "X-Sbx-App-Id"),
                GetHeader(request, "X-Sbx-App-Key"),
                GetHeader(request, "X-Session-ID")
            )
        );

        foreach (var route in _routes)
        {
            if (route.Predicate(request))
            {
                if (route.Respond is null)
                {
                    // A "hang" route: wait until the caller's linked timeout/cancellation fires.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }

                return route.Respond!(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"No fake route for {request.Method} {request.RequestUri}"),
        };
    }

    private static string? GetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed record Route(Func<HttpRequestMessage, bool> Predicate, Func<HttpRequestMessage, HttpResponseMessage>? Respond);

    /// <summary>A single observed outgoing request.</summary>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        string? SbxAppId,
        string? SbxAppKey,
        string? SessionId
    );
}
