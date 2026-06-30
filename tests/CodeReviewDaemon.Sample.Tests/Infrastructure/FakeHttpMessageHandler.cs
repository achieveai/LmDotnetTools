using System.Net;
using System.Text;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> for driving the real GitHub/ADO providers and publishers
/// against canned HTTP responses (no network). Routes are matched by a predicate over the outgoing
/// request (method + URI), in registration order; the first match wins. Every request is recorded so a
/// test can assert the exact URL, method, headers, and body the provider produced.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every request that flowed through the handler, in call order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Registers a handler for requests matching <paramref name="predicate"/>.</summary>
    public FakeHttpMessageHandler On(
        Func<HttpRequestMessage, bool> predicate,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add(new Route(predicate, respond));
        return this;
    }

    /// <summary>Registers a JSON response for a method + URL-substring match.</summary>
    public FakeHttpMessageHandler OnJson(HttpMethod method, string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return On(
            req => req.Method == method
                && req.RequestUri is not null
                && req.RequestUri.ToString().Contains(urlContains, StringComparison.Ordinal),
            _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization is { } auth ? $"{auth.Scheme} {auth.Parameter}" : null,
            request.Headers.UserAgent.ToString(),
            body));

        foreach (var route in _routes)
        {
            if (route.Predicate(request))
            {
                return route.Respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"No fake route for {request.Method} {request.RequestUri}"),
        };
    }

    private sealed record Route(
        Func<HttpRequestMessage, bool> Predicate,
        Func<HttpRequestMessage, HttpResponseMessage> Respond);

    /// <summary>A single observed outgoing request.</summary>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string UserAgent,
        string? Body);
}
