namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="OAuthTokenEndpointClient"/> — the endpoint-agnostic token-endpoint
/// POST/parse helper used by the predefined-key refresh_token / client_credentials grants. Driven
/// against an in-memory <see cref="HttpMessageHandler"/> (no network). SECURITY: assertions never
/// print the form or token values.
/// </summary>
public sealed class OAuthTokenEndpointClientTests
{
    private sealed class CannedHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }
        public bool SawJsonAccept { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            SawJsonAccept = request.Headers.Accept.Any(a => a.MediaType == "application/json");
            return response;
        }
    }

    private static (OAuthTokenEndpointClient client, CannedHandler handler) NewClient(string body) =>
        NewClient(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });

    private static (OAuthTokenEndpointClient client, CannedHandler handler) NewClient(HttpResponseMessage response)
    {
        var handler = new CannedHandler(response);
        return (new OAuthTokenEndpointClient(new HttpClient(handler)), handler);
    }

    private static Dictionary<string, string> RefreshForm() => new()
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = "cid",
        ["refresh_token"] = "rt-secret",
    };

    [Fact]
    public async Task PostAsync_parses_success_token_and_posts_json_form()
    {
        var (client, handler) = NewClient("{\"access_token\":\"AT\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().Be("AT");
        result.RefreshToken.Should().Be("RT2");
        result.ExpiresIn.Should().Be(3600);
        result.Error.Should().BeNull();
        handler.LastUri.Should().Be(new Uri("https://token.example/oauth/token"));
        handler.SawJsonAccept.Should().BeTrue();
        handler.LastBody.Should().Contain("grant_type=refresh_token");
    }

    [Fact]
    public async Task PostAsync_surfaces_oauth_error()
    {
        var (client, _) = NewClient("{\"error\":\"invalid_grant\"}");

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task PostAsync_handles_empty_body()
    {
        var (client, _) = NewClient(string.Empty);

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("empty_response");
    }

    [Fact]
    public async Task PostAsync_handles_unparseable_body()
    {
        var (client, _) = NewClient("<html>gateway error</html>");

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("unparseable_response");
    }

    [Fact]
    public async Task PostAsync_rejects_token_from_non_success_status()
    {
        // A 503 (transient) with a token-shaped body must NOT be accepted as a credential.
        var (client, _) = NewClient(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"access_token\":\"should-be-ignored\"}"),
        });

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("http_503");
    }

    [Fact]
    public async Task PostAsync_handles_unexpected_json_field_types()
    {
        // Valid JSON but access_token is an object → GetString throws InvalidOperationException, which
        // must be normalized rather than escaping the documented result contract.
        var (client, _) = NewClient("{\"access_token\":{}}");

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("unparseable_response");
    }

    [Fact]
    public async Task PostAsync_surfaces_oauth_error_on_4xx()
    {
        var (client, _) = NewClient(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}"),
        });

        var result = await client.PostAsync("https://token.example/oauth/token", RefreshForm());

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("invalid_grant");
    }
}
