namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="PredefinedKeyProvider"/> — the per-entry egress credential provider.
/// Covers the three kinds (custom-headers verbatim, refresh-token / client-credentials mint + cache +
/// persist), credential-rejection invalidation + recovery via <c>UpdateEntry</c>. Token-endpoint calls
/// hit an in-memory handler (no network). SECRET: no token/secret values are logged by the assertions.
/// </summary>
public sealed class PredefinedKeyProviderTests
{
    private sealed class MemoryTokenStore : IOAuthTokenStore
    {
        private readonly Dictionary<string, OAuthTokenRecord> _map = new(StringComparer.Ordinal);

        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(_map.TryGetValue(provider, out var r) ? r : null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default)
        {
            _map[record.Provider] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string provider, CancellationToken ct = default)
        {
            _ = _map.Remove(provider);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptHandler(Func<string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(respond()) };
        }
    }

    private static (PredefinedKeyProvider provider, MemoryTokenStore store, ScriptHandler handler) NewProvider(
        PredefinedKeyEntry entry, Func<string> respond)
    {
        var store = new MemoryTokenStore();
        var handler = new ScriptHandler(respond);
        var endpoint = new OAuthTokenEndpointClient(new HttpClient(handler));
        return (new PredefinedKeyProvider(entry, store, endpoint, NullLogger.Instance), store, handler);
    }

    private static PredefinedKeyEntry CustomEntry(params (string name, string value)[] headers) => new()
    {
        Id = "c1",
        Host = "api.example.com",
        Kind = PredefinedKeyKind.CustomHeaders,
        Headers = [.. headers.Select(h => new PredefinedHeader(h.name, h.value))],
    };

    private static PredefinedKeyEntry RefreshEntry() => new()
    {
        Id = "r1",
        Host = "api.example.com",
        Kind = PredefinedKeyKind.RefreshToken,
        HeaderName = "Authorization",
        TokenEndpoint = "https://token.example/oauth/token",
        ClientId = "cid",
        ClientSecret = "csec",
        RefreshToken = "rt0",
        Scopes = ["read"],
    };

    private static PredefinedKeyEntry ClientCredentialsEntry() => new()
    {
        Id = "cc1",
        Host = "api.example.com",
        Kind = PredefinedKeyKind.ClientCredentials,
        HeaderName = "Authorization",
        TokenEndpoint = "https://token.example/oauth/token",
        ClientId = "cid",
        ClientSecret = "csec",
        Scopes = ["read"],
    };

    [Fact]
    public async Task Custom_headers_returns_list_verbatim_without_hitting_endpoint()
    {
        var (provider, _, handler) = NewProvider(CustomEntry(("Cookie", "sid=abc"), ("X-API-Key", "k")), () => "{}");

        var token = await provider.GetAccessTokenAsync();
        var headers = provider.BuildHeaders(token);

        headers.Select(h => (h.Key, h.Value)).Should().Equal(("Cookie", "sid=abc"), ("X-API-Key", "k"));
        provider.IncludeExpiry.Should().BeFalse();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Custom_headers_with_no_headers_throws()
    {
        var entry = new PredefinedKeyEntry { Id = "c", Host = "api.example.com", Kind = PredefinedKeyKind.CustomHeaders, Headers = [] };
        var (provider, _, _) = NewProvider(entry, () => "{}");

        await FluentActions.Awaiting(() => provider.GetAccessTokenAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Refresh_token_mints_bearer_persists_and_caches()
    {
        var (provider, store, handler) = NewProvider(RefreshEntry(), () => "{\"access_token\":\"AT\",\"expires_in\":3600}");

        var token = await provider.GetAccessTokenAsync();

        token.Value.Should().Be("AT");
        provider.IncludeExpiry.Should().BeTrue();
        provider.BuildHeaders(token).Select(h => (h.Key, h.Value)).Should().Equal(("Authorization", "Bearer AT"));
        handler.Bodies[0].Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=rt0");
        (await store.GetAsync("predefined-r1"))!.AccessToken.Should().Be("AT");

        // A second call is served from cache (token still fresh) — no second POST.
        _ = await provider.GetAccessTokenAsync();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Client_credentials_mints_with_client_credentials_grant()
    {
        var (provider, _, handler) = NewProvider(ClientCredentialsEntry(), () => "{\"access_token\":\"CC\",\"expires_in\":60}");

        var token = await provider.GetAccessTokenAsync();

        token.Value.Should().Be("CC");
        handler.Bodies[0].Should().Contain("grant_type=client_credentials").And.Contain("client_secret=csec");
    }

    [Fact]
    public async Task Credential_rejection_marks_invalid_until_updated()
    {
        var fail = true;
        var (provider, _, handler) = NewProvider(
            RefreshEntry(),
            () => fail ? "{\"error\":\"invalid_grant\"}" : "{\"access_token\":\"AT2\",\"expires_in\":60}");

        // First mint is rejected → throws + marks invalid.
        await FluentActions.Awaiting(() => provider.GetAccessTokenAsync()).Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(1);

        // While invalid it short-circuits — no further endpoint calls.
        await FluentActions.Awaiting(() => provider.GetAccessTokenAsync()).Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(1);

        // Updating the entry (user re-enters the credential) clears the invalid flag → mint retried.
        fail = false;
        await provider.UpdateEntry(RefreshEntry(), credentialChanged: true);
        var token = await provider.GetAccessTokenAsync();
        token.Value.Should().Be("AT2");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Update_preserving_credential_keeps_the_cached_token()
    {
        var (provider, _, handler) = NewProvider(RefreshEntry(), () => "{\"access_token\":\"AT\",\"expires_in\":3600}");
        _ = await provider.GetAccessTokenAsync();

        // A host-only edit (credentialChanged: false) keeps the still-valid cached token → no re-mint.
        // (The credential-change re-mint path — where the registry removes the persisted token — is
        // covered by PredefinedKeyRegistryTests.Update_changing_credential_invalidates_the_persisted_token.)
        await provider.UpdateEntry(RefreshEntry() with { Host = "api2.example.com" }, credentialChanged: false);

        _ = await provider.GetAccessTokenAsync();
        handler.Calls.Should().Be(1);
    }
}
