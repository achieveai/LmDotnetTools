using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="EgressKeysController"/> — CRUD, host/header validation, managed-host
/// collision rejection, secret masking on the view, and edit-preserves-secret. Driven directly
/// against the controller with a temp-dir-backed registry (no HTTP pipeline, no network).
/// </summary>
public sealed class EgressKeysControllerTests
{
    private sealed class NoopStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (EgressKeysController controller, PredefinedKeyRegistry registry, DirectoryInfo dir) NewController()
    {
        var dir = Directory.CreateTempSubdirectory("egr-ctl");
        var registry = new PredefinedKeyRegistry(dir.FullName, new NoopStore(), new HttpClient(), NullLoggerFactory.Instance);
        return (new EgressKeysController(registry, NullLogger<EgressKeysController>.Instance), registry, dir);
    }

    private static EgressKeyRequest CustomReq(string host, params (string name, string value)[] headers) => new(
        Id: null, Host: host, Kind: "custom-headers",
        Headers: [.. headers.Select(h => new EgressHeaderInput(h.name, h.value))],
        HeaderName: null, TokenEndpoint: null, ClientId: null, ClientSecret: null, RefreshToken: null, Scopes: null);

    [Fact]
    public async Task Post_creates_custom_headers_entry_and_masks_secrets()
    {
        var (controller, _, dir) = NewController();
        try
        {
            var result = await controller.Upsert(CustomReq("api.example.com", ("Cookie", "sid=abc")));

            var view = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EgressKeyView>().Subject;
            view.Kind.Should().Be("custom-headers");
            view.Host.Should().Be("api.example.com");
            view.HeaderNames.Should().Equal("Cookie");
            // EgressKeyView exposes no header/secret VALUE fields at all — masking is by shape.
            typeof(EgressKeyView).GetProperties().Select(p => p.Name)
                .Should().NotContain(["Headers", "Value", "ClientSecret", "RefreshToken"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("*")]
    [InlineData("github.com")] // collides with a managed OAuth host
    public async Task Post_rejects_invalid_or_colliding_hosts(string host)
    {
        var (controller, _, dir) = NewController();
        try
        {
            (await controller.Upsert(CustomReq(host, ("X-Key", "v")))).Should().BeOfType<BadRequestObjectResult>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Post_rejects_forbidden_header_name()
    {
        var (controller, _, dir) = NewController();
        try
        {
            (await controller.Upsert(CustomReq("api.example.com", ("Host", "evil")))).Should().BeOfType<BadRequestObjectResult>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Post_client_credentials_requires_secret()
    {
        var (controller, _, dir) = NewController();
        try
        {
            var req = new EgressKeyRequest(
                Id: null, Host: "api.example.com", Kind: "client-credentials", Headers: null, HeaderName: null,
                TokenEndpoint: "https://token.example/oauth", ClientId: "cid", ClientSecret: null, RefreshToken: null, Scopes: null);

            (await controller.Upsert(req)).Should().BeOfType<BadRequestObjectResult>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Update_with_blank_secret_preserves_stored_secret()
    {
        var (controller, registry, dir) = NewController();
        try
        {
            var create = new EgressKeyRequest(
                Id: null, Host: "api.example.com", Kind: "client-credentials", Headers: null, HeaderName: null,
                TokenEndpoint: "https://token.example/oauth", ClientId: "cid", ClientSecret: "the-secret", RefreshToken: null, Scopes: null);
            var created = (await controller.Upsert(create)).Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<EgressKeyView>().Subject;

            // Edit only the host; leave the secret blank → it must be preserved.
            var edit = create with { Id = created.Id, Host = "api2.example.com", ClientSecret = null };
            var updated = (await controller.Upsert(edit)).Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<EgressKeyView>().Subject;

            updated.Host.Should().Be("api2.example.com");
            updated.HasClientSecret.Should().BeTrue();
            registry.Find(created.Id)!.ClientSecret.Should().Be("the-secret");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Update_custom_headers_with_blank_value_preserves_stored_value()
    {
        var (controller, registry, dir) = NewController();
        try
        {
            var created = (await controller.Upsert(CustomReq("api.example.com", ("X-Api-Key", "secret-abc"))))
                .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EgressKeyView>().Subject;

            // GET masks header values, so an edit re-sends the header NAME with a blank value. Changing
            // only the host must preserve the stored value, not wipe/reject it.
            var edit = new EgressKeyRequest(
                Id: created.Id, Host: "api2.example.com", Kind: "custom-headers",
                Headers: [new EgressHeaderInput("X-Api-Key", "")],
                HeaderName: null, TokenEndpoint: null, ClientId: null, ClientSecret: null, RefreshToken: null, Scopes: null);
            var updated = (await controller.Upsert(edit)).Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<EgressKeyView>().Subject;

            updated.Host.Should().Be("api2.example.com");
            registry.Find(created.Id)!.Headers.Single().Value.Should().Be("secret-abc");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Post_rejects_blank_value_for_a_new_header()
    {
        var (controller, _, dir) = NewController();
        try
        {
            // No existing entry to preserve from → a blank value on a fresh header is an error.
            (await controller.Upsert(CustomReq("api.example.com", ("X-New", "")))).Should().BeOfType<BadRequestObjectResult>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Delete_removes_entry_and_404s_when_unknown()
    {
        var (controller, _, dir) = NewController();
        try
        {
            var created = (await controller.Upsert(CustomReq("api.example.com", ("X-Key", "v")))).Should()
                .BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EgressKeyView>().Subject;

            (await controller.Delete(created.Id)).Should().BeOfType<NoContentResult>();
            (await controller.Delete("missing")).Should().BeOfType<NotFoundResult>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
