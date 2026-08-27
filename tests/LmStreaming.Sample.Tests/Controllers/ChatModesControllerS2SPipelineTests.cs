using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Real MVC-pipeline coverage for the inbound S2S guard on <see cref="ChatModesController"/> (#519).
/// </summary>
/// <remarks>
/// <para>
/// A <c>TestServer</c> hosting only the chat-mode routes, so requests flow through real routing and
/// real filter discovery. A direct call to a controller method would never run an
/// <c>IAsyncActionFilter</c> at all, and so could not fail if <c>[InboundS2SAuth]</c> were deleted —
/// which is the single edit these tests exist to catch.
/// </para>
/// <para>
/// Every refusal below is paired with a positive control at the same route, because a suite that only
/// proves "the forged caller is refused" passes just as well against a controller that refuses
/// everyone. <see cref="EveryRoute_IsReachableForTheSpa_SoTheRefusalsBelowAreNotVacuous"/> and
/// <see cref="EveryRoute_IsReachable_ForACorrectlySignedS2SCaller"/> are those controls, and they run
/// the same six routes.
/// </para>
/// <para>
/// All six routes are covered rather than a representative one. Four of them —
/// <c>Get</c>, <c>Update</c>, <c>Delete</c>, <c>Copy</c> — accept the same <c>{modeId}</c>, and a
/// guard applied to some verbs on an id and not others is the usual shape of this defect.
/// </para>
/// </remarks>
public sealed class ChatModesControllerS2SPipelineTests
{
    private const string Secret = "s3cr3t-chat-modes-pipeline-value";
    private const string CollectionRoute = "/api/chat-modes";
    private const string ModeId = "mode-under-test";
    private const string ItemRoute = $"{CollectionRoute}/{ModeId}";
    private const string CopiesRoute = $"{ItemRoute}/copies";

    private const string ValidCreateBody =
        """{"name":"Probe","systemPrompt":"you are a probe"}""";

    private const string ValidCopyBody = """{"newName":"Probe copy"}""";

    /// <summary>Unbalanced brace: the JSON reader fails before any property is bound.</summary>
    private const string MalformedBody = """{"name":"Probe",""";

    private readonly RecordingChatModeStore _store = new();

    /// <summary>
    /// The six routes the controller publishes, each with the status an allowed caller receives.
    /// Kept as one list so a new route cannot be added without either appearing in every test here
    /// or visibly not appearing.
    /// </summary>
    private static IEnumerable<(string Name, Func<HttpRequestMessage> Build, HttpStatusCode Allowed)> AllRoutes()
    {
        yield return ("List", () => new HttpRequestMessage(HttpMethod.Get, CollectionRoute), HttpStatusCode.OK);
        yield return ("Get", () => new HttpRequestMessage(HttpMethod.Get, ItemRoute), HttpStatusCode.OK);
        yield return ("Create", () => Json(HttpMethod.Post, CollectionRoute, ValidCreateBody), HttpStatusCode.Created);
        yield return ("Update", () => Json(HttpMethod.Put, ItemRoute, ValidCreateBody), HttpStatusCode.OK);
        yield return ("Delete", () => new HttpRequestMessage(HttpMethod.Delete, ItemRoute), HttpStatusCode.NoContent);
        yield return ("Copy", () => Json(HttpMethod.Post, CopiesRoute, ValidCopyBody), HttpStatusCode.Created);
    }

    /// <summary>The three routes that take a body, so only these can be probed with a malformed one.</summary>
    private static IEnumerable<(string Name, Func<string, HttpRequestMessage> Build)> BodyRoutes()
    {
        yield return ("Create", body => Json(HttpMethod.Post, CollectionRoute, body));
        yield return ("Update", body => Json(HttpMethod.Put, ItemRoute, body));
        yield return ("Copy", body => Json(HttpMethod.Post, CopiesRoute, body));
    }

    private static HttpRequestMessage Json(HttpMethod method, string route, string body) =>
        new(method, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private async Task<IHost> StartHostAsync(string? configuredSecret)
    {
        var configData = new Dictionary<string, string?>();
        if (configuredSecret != null)
        {
            configData[InboundS2SAuthAttribute.SecretConfigKey] = configuredSecret;
        }

        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                _ = webBuilder
                    .UseTestServer()
                    .ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configData))
                    .ConfigureServices(services =>
                    {
                        _ = services.AddSingleton<IChatModeStore>(_store);
                        _ = services
                            .AddControllers()
                            .AddApplicationPart(typeof(ChatModesController).Assembly);
                    })
                    .Configure(app =>
                    {
                        _ = app.UseRouting();
                        _ = app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
            })
            .StartAsync();
    }

    /// <summary>
    /// Sends <paramref name="request"/> after applying <paramref name="markers"/>, and returns the
    /// status together with the body, so a caller can assert the refusal's shape and not only its code.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        Action<HttpRequestMessage>? markers = null)
    {
        markers?.Invoke(request);
        using (request)
        {
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    private static void ForgedNoSecret(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "some-other-app");

    private static void ForgedWrongSecret(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, "totally-wrong");
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "some-other-app");
    }

    private static void CorrectlySigned(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, Secret);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "code-review-daemon");
    }

    [Fact]
    public async Task EveryRoute_IsReachableForTheSpa_SoTheRefusalsBelowAreNotVacuous()
    {
        // The SPA calls these routes with plain fetch and no S2S markers. Enabling the secret must
        // not turn every Modes-editor operation into a 401 — and if it did, every refusal test in
        // this file would pass for the wrong reason.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = AllRoutes().Should().HaveCount(6);

        foreach (var (name, build, allowed) in AllRoutes())
        {
            var (status, _) = await SendAsync(client, build());
            _ = status.Should().Be(allowed, "{0} is the SPA's own route and carries no S2S markers", name);
        }

        _ = _store.Mutations.Should().Contain(["Create", "Update", "Delete", "Copy"]);
    }

    [Fact]
    public async Task EveryRoute_Is401_ForAnS2SCallerWithNoSecret_AndNothingIsMutated()
    {
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = AllRoutes().Should().HaveCount(6);

        foreach (var (name, build, _) in AllRoutes())
        {
            var (status, body) = await SendAsync(client, build(), ForgedNoSecret);

            _ = status.Should().Be(HttpStatusCode.Unauthorized, "{0} must refuse an unsigned service caller", name);
            _ = body.Should().Contain("s2s_auth_failed", "{0} must refuse for the S2S reason, not incidentally", name);
            _ = body.Should().NotContain(Secret);
        }

        // A refusal that still reached the store would be a log entry, not a guard.
        _ = _store.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryRoute_Is401_ForAnS2SCallerWithTheWrongSecret_AndNothingIsMutated()
    {
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = AllRoutes().Should().HaveCount(6);

        foreach (var (name, build, _) in AllRoutes())
        {
            var (status, body) = await SendAsync(client, build(), ForgedWrongSecret);

            _ = status.Should().Be(HttpStatusCode.Unauthorized, "{0} must refuse a mismatched secret", name);
            _ = body.Should().Contain("s2s_auth_failed");
            _ = body.Should().NotContain(Secret);
            _ = body.Should().NotContain("totally-wrong");
        }

        _ = _store.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryRoute_IsReachable_ForACorrectlySignedS2SCaller()
    {
        // The daemon's own path. Without this, the refusals above would be satisfied by a guard that
        // simply rejects every request carrying an app-id marker.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = AllRoutes().Should().HaveCount(6);

        foreach (var (name, build, allowed) in AllRoutes())
        {
            var (status, _) = await SendAsync(client, build(), CorrectlySigned);
            _ = status.Should().Be(allowed, "{0} must stay open to a correctly signed service caller", name);
        }

        _ = _store.Mutations.Should().Contain(["Create", "Update", "Delete", "Copy"]);
    }

    [Fact]
    public async Task AMalformedBody_FromAForgedS2SCaller_IsRefused401_NotProbed400()
    {
        // [ApiController] installs model-state validation at Order = -2000. While the guard sat at
        // the default Order = 0, this returned 400: the forged caller learned the route exists and
        // what its schema is, without ever holding the secret.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = BodyRoutes().Should().HaveCount(3);

        foreach (var (name, build) in BodyRoutes())
        {
            var (status, body) = await SendAsync(client, build(MalformedBody), ForgedNoSecret);

            _ = status.Should().Be(
                HttpStatusCode.Unauthorized,
                "{0} must answer the guard before model validation answers on its behalf",
                name);
            _ = body.Should().Contain("s2s_auth_failed");
        }

        _ = _store.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task AMalformedBody_FromASignedS2SCaller_StillGetsIts400()
    {
        // Without this, ordering the guard to -2100 and breaking validation outright would look
        // identical to the test above.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        _ = BodyRoutes().Should().HaveCount(3);

        foreach (var (name, build) in BodyRoutes())
        {
            var (status, _) = await SendAsync(client, build(MalformedBody), CorrectlySigned);

            _ = status.Should().Be(
                HttpStatusCode.BadRequest,
                "{0} must still validate the body once the caller is admitted",
                name);
        }

        _ = _store.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryRoute_IsReachable_WhenNoSecretIsConfigured()
    {
        // Keyless dev path: with no secret the guard is off, matching every sibling controller.
        using var host = await StartHostAsync(configuredSecret: null);
        using var client = host.GetTestClient();

        _ = AllRoutes().Should().HaveCount(6);

        foreach (var (name, build, allowed) in AllRoutes())
        {
            var (status, _) = await SendAsync(client, build(), ForgedNoSecret);
            _ = status.Should().Be(allowed, "{0} must stay reachable when the guard is unconfigured", name);
        }
    }

    /// <summary>
    /// Always succeeds, and records which mutating methods were actually reached. A store that threw
    /// or returned null would make a refusal indistinguishable from a broken route.
    /// </summary>
    private sealed class RecordingChatModeStore : IChatModeStore
    {
        public List<string> Mutations { get; } = [];

        private static ChatMode Mode(string id = ModeId) =>
            new()
            {
                Id = id,
                Name = "Probe",
                SystemPrompt = "you are a probe",
            };

        public Task<IReadOnlyList<ChatMode>> GetAllModesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatMode>>([Mode()]);

        public Task<ChatMode?> GetModeAsync(string modeId, CancellationToken ct = default) =>
            Task.FromResult<ChatMode?>(Mode(modeId));

        public Task<ChatMode> CreateModeAsync(ChatModeCreateUpdate mode, CancellationToken ct = default)
        {
            Mutations.Add("Create");
            return Task.FromResult(Mode("created"));
        }

        public Task<ChatMode> UpdateModeAsync(
            string modeId,
            ChatModeCreateUpdate mode,
            CancellationToken ct = default)
        {
            Mutations.Add("Update");
            return Task.FromResult(Mode(modeId));
        }

        public Task DeleteModeAsync(string modeId, CancellationToken ct = default)
        {
            Mutations.Add("Delete");
            return Task.CompletedTask;
        }

        public Task<ChatMode> CopyModeAsync(string modeId, string newName, CancellationToken ct = default)
        {
            Mutations.Add("Copy");
            return Task.FromResult(Mode("copied"));
        }
    }
}
