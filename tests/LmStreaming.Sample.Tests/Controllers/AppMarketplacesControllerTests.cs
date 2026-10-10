using System.Net;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// <see cref="AppMarketplacesController"/> against a fake <see cref="IAppMarketplaceClient"/>: a gateway rejection
/// keeps its status and <c>error_code</c> (the UI shows why), an unreachable gateway is 503, and removing an
/// unregistered alias is 404. Register, sync and remove need the operator secret; listing does not.
/// </summary>
public class AppMarketplacesControllerTests
{
    [Fact]
    public async Task Register_GatewayRejection_KeepsStatusAndErrorCode()
    {
        var fake = new FakeClient
        {
            Failure = new SandboxException(SandboxErrorKind.Protocol, "rejected", 400) { ErrorCode = "ref_not_found" },
        };
        var controller = new AppMarketplacesController(fake, NullLogger<AppMarketplacesController>.Instance);

        var result = await controller.Register(
            "team",
            new RegisterAppMarketplaceRequest("https://github.com/acme/plugins", "nope"),
            CancellationToken.None
        );

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeEquivalentTo(new { error = "ref_not_found" });
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    public async Task List_GatewayRejectsTheSamplesAppCredential_Is502_NotTheUsersOwnAuthStatus(int gatewayStatus)
    {
        // The browser reads a 401 on /api as its own sign-in expiring; a rejected app key is a server fault.
        var fake = new FakeClient
        {
            Failure = new SandboxException(SandboxErrorKind.Authorization, "app key rejected", gatewayStatus),
        };
        var controller = new AppMarketplacesController(fake, NullLogger<AppMarketplacesController>.Instance);

        var result = await controller.List(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task Register_BlankRef_IsForwardedAsLatest()
    {
        var fake = new FakeClient();
        var controller = new AppMarketplacesController(fake, NullLogger<AppMarketplacesController>.Instance);

        var result = await controller.Register(
            "team",
            new RegisterAppMarketplaceRequest("https://github.com/acme/plugins", "  "),
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        fake.LastRef.Should().BeNull();
    }

    [Fact]
    public async Task List_UnreachableGateway_Is503()
    {
        var fake = new FakeClient { Failure = new SandboxException(SandboxErrorKind.TransportTimeout, "unreachable") };
        var controller = new AppMarketplacesController(fake, NullLogger<AppMarketplacesController>.Instance);

        var result = await controller.List(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Remove_NotRegistered_Is404_AndRegistered_Is204()
    {
        var controller = new AppMarketplacesController(
            new FakeClient { RemoveResult = false },
            NullLogger<AppMarketplacesController>.Instance
        );
        (await controller.Remove("gone", CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();

        controller = new AppMarketplacesController(
            new FakeClient { RemoveResult = true },
            NullLogger<AppMarketplacesController>.Instance
        );
        (await controller.Remove("team", CancellationToken.None)).Should().BeOfType<NoContentResult>();
    }

    public static TheoryData<string, string> MutationRoutes =>
        new()
        {
            { "PUT", "/api/marketplaces/app/team" },
            { "POST", "/api/marketplaces/app/team/sync" },
            { "DELETE", "/api/marketplaces/app/team" },
        };

    [Theory]
    [MemberData(nameof(MutationRoutes))]
    public async Task Mutations_WithoutTheRightOperatorSecret_Are403_AndNeverReachTheGateway(string method, string path)
    {
        var fake = new FakeClient { RemoveResult = true };
        using var host = await StartAsync(fake, OperatorGateTestServer.Secret);

        foreach (var presented in new[] { null, "not-the-secret" })
        {
            // 403, not 401: the SPA reads a 401 on /api as its own sign-in expiring.
            var response = await OperatorGateTestServer.SendAsync(host, new HttpMethod(method), path, presented, Body);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        fake.Calls.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(MutationRoutes))]
    public async Task Mutations_WithTheOperatorSecret_ReachTheGateway(string method, string path)
    {
        var fake = new FakeClient { RemoveResult = true };
        using var host = await StartAsync(fake, OperatorGateTestServer.Secret);

        var response = await OperatorGateTestServer.SendAsync(
            host,
            new HttpMethod(method),
            path,
            OperatorGateTestServer.Secret,
            Body
        );

        response.IsSuccessStatusCode.Should().BeTrue();
        fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Mutations_OperatorSecretUnconfigured_Are503_ButListingStillWorks()
    {
        var fake = new FakeClient();
        using var host = await StartAsync(fake, secret: null);

        var register = await OperatorGateTestServer.SendAsync(
            host,
            HttpMethod.Put,
            "/api/marketplaces/app/team",
            "anything",
            Body
        );
        var list = await OperatorGateTestServer.SendAsync(host, HttpMethod.Get, "/api/marketplaces/app", null);

        register.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Calls.Should().Be(1, "only the listing reached the gateway");
    }

    private static readonly RegisterAppMarketplaceRequest Body = new("https://github.com/acme/plugins", null);

    private static Task<Microsoft.Extensions.Hosting.IHost> StartAsync(FakeClient fake, string? secret) =>
        OperatorGateTestServer.StartAsync(
            secret,
            services =>
            {
                _ = services.AddSingleton<IAppMarketplaceClient>(fake);
                _ = services.AddLogging();
            }
        );

    private sealed class FakeClient : IAppMarketplaceClient
    {
        private int _calls;

        /// <summary>How many calls reached the gateway client.</summary>
        public int Calls => _calls;

        public SandboxException? Failure { get; init; }

        public bool RemoveResult { get; init; }

        public string? LastRef { get; private set; }

        private static SandboxAppMarketplace Row(string alias) =>
            new()
            {
                AppId = "app",
                Alias = alias,
                Url = "https://github.com/acme/plugins",
                RepoKey = "plugins-0a1b",
                Ref = "latest",
                ResolvedSha = "8ca22dba9a94f28898bbce59f2537ff4d87c747d",
                CreatedAt = "t0",
                UpdatedAt = "t1",
            };

        public Task<IReadOnlyList<SandboxAppMarketplace>> ListAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Failure is null
                ? Task.FromResult<IReadOnlyList<SandboxAppMarketplace>>([Row("team")])
                : throw Failure;
        }

        public Task<SandboxAppMarketplace> RegisterAsync(
            string alias,
            string url,
            string? gitRef,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _calls);
            LastRef = gitRef;
            return Failure is null ? Task.FromResult(Row(alias)) : throw Failure;
        }

        public Task<SandboxAppMarketplace> SyncAsync(string alias, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Failure is null ? Task.FromResult(Row(alias)) : throw Failure;
        }

        public Task<bool> RemoveAsync(string alias, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Failure is null ? Task.FromResult(RemoveResult) : throw Failure;
        }
    }
}
