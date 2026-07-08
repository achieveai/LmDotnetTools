using System.Net;
using System.Text;

namespace ConversationDaemon.Sample.Tests;

/// <summary>
/// A self-contained scripted <see cref="HttpMessageHandler"/> for driving <see cref="DaemonRestClient"/>
/// and <see cref="ConversationScript"/> against canned responses (no network). Routes match on the
/// request method plus a URL substring, in registration order (first match wins). A route can return a
/// fixed JSON body, a SEQUENCE of bodies (successive matches advance through them, repeating the last
/// once exhausted), or throw a chosen exception to simulate a transport failure such as
/// connection-refused. Everything lives in this test project so it takes no dependency on other test
/// projects' infrastructure.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every request observed, in call order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Registers a fixed JSON <paramref name="response"/> for a method + URL-substring match.</summary>
    public FakeHttpMessageHandler OnJson(
        HttpMethod method,
        string urlContains,
        string response,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return OnJsonSequence(method, urlContains, status, response);
    }

    /// <summary>
    /// Registers a SEQUENCE of JSON responses for a method + URL-substring match: the first matching
    /// request gets <paramref name="responses"/>[0], the next [1], … and once exhausted the last body
    /// repeats. Lets a test drive a poll whose observed state changes across successive reads.
    /// </summary>
    public FakeHttpMessageHandler OnJsonSequence(
        HttpMethod method,
        string urlContains,
        params string[] responses)
    {
        return OnJsonSequence(method, urlContains, HttpStatusCode.OK, responses);
    }

    /// <summary>Sequence overload that pins the HTTP status returned for every matching request.</summary>
    public FakeHttpMessageHandler OnJsonSequence(
        HttpMethod method,
        string urlContains,
        HttpStatusCode status,
        params string[] responses)
    {
        if (responses.Length == 0)
        {
            throw new ArgumentException("At least one response is required.", nameof(responses));
        }

        _routes.Add(
            new Route(
                req => Matches(req, method, urlContains),
                (_, matchIndex) =>
                    new HttpResponseMessage(status)
                    {
                        Content = new StringContent(
                            responses[Math.Min(matchIndex, responses.Length - 1)],
                            Encoding.UTF8,
                            "application/json"),
                    }));
        return this;
    }

    /// <summary>Registers a route that THROWS <paramref name="exceptionFactory"/>() for any request.</summary>
    public FakeHttpMessageHandler OnAnyThrow(Func<Exception> exceptionFactory)
    {
        _routes.Add(new Route(_ => true, (_, _) => throw exceptionFactory()));
        return this;
    }

    /// <summary>Number of requests observed whose URL contains <paramref name="urlContains"/>.</summary>
    public int CountRequests(string urlContains)
    {
        return Requests.Count(r =>
            r.Uri.ToString().Contains(urlContains, StringComparison.Ordinal));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body =
            request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

        foreach (var route in _routes)
        {
            if (route.Predicate(request))
            {
                return route.Respond(request, route.NextIndex());
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"No fake route for {request.Method} {request.RequestUri}"),
        };
    }

    private static bool Matches(HttpRequestMessage request, HttpMethod method, string urlContains)
    {
        return request.Method == method
            && request.RequestUri is not null
            && request.RequestUri.ToString().Contains(urlContains, StringComparison.Ordinal);
    }

    private sealed class Route
    {
        private int _matchCount;

        public Route(
            Func<HttpRequestMessage, bool> predicate,
            Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        {
            Predicate = predicate;
            Respond = respond;
        }

        public Func<HttpRequestMessage, bool> Predicate { get; }

        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond { get; }

        public int NextIndex()
        {
            return _matchCount++;
        }
    }

    /// <summary>A single observed outgoing request (a snapshot, since the request is disposed after send).</summary>
    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);
}
