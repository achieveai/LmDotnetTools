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

    /// <summary>
    /// Registers a SEQUENCE of responses for a method + URL-substring match: successive matching requests
    /// get <paramref name="responses"/>[0], [1], … repeating the last once exhausted. Lets a test drive a
    /// transient-then-success retry (e.g. <c>429</c> then <c>200</c>) against a single URL.
    /// </summary>
    public FakeHttpMessageHandler OnSequence(
        HttpMethod method,
        string urlContains,
        params (HttpStatusCode Status, string Json)[] responses)
    {
        if (responses.Length == 0)
        {
            throw new ArgumentException("At least one response is required.", nameof(responses));
        }

        var index = 0;
        return On(
            req => req.Method == method
                && req.RequestUri is not null
                && req.RequestUri.ToString().Contains(urlContains, StringComparison.Ordinal),
            _ =>
            {
                var (status, json) = responses[Math.Min(index, responses.Length - 1)];
                index++;
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            });
    }

    /// <summary>Number of requests observed whose URL contains <paramref name="urlContains"/>.</summary>
    public int CountRequests(string urlContains) =>
        Requests.Count(r => r.Uri.ToString().Contains(urlContains, StringComparison.Ordinal));

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
            body,
            GetHeader(request, "X-Sbx-App-Id"),
            GetHeader(request, "X-Sbx-App-Key")));

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

    /// <summary>Reads a single request header value, or <c>null</c> when absent — used to capture the
    /// sandbox gateway's per-app auth headers (ADR 0029) alongside the existing bearer/basic Authorization
    /// capture above.</summary>
    private static string? GetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed record Route(
        Func<HttpRequestMessage, bool> Predicate,
        Func<HttpRequestMessage, HttpResponseMessage> Respond);

    /// <summary>A single observed outgoing request.</summary>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string UserAgent,
        string? Body,
        string? SbxAppId = null,
        string? SbxAppKey = null);
}
