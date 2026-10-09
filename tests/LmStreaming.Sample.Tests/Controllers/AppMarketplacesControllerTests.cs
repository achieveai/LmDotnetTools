using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// <see cref="AppMarketplacesController"/> against a fake <see cref="IAppMarketplaceClient"/>: a gateway rejection
/// keeps its status and <c>error_code</c> (the UI shows why), an unreachable gateway is 503, and removing an
/// unregistered alias is 404.
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

    private sealed class FakeClient : IAppMarketplaceClient
    {
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

        public Task<IReadOnlyList<SandboxAppMarketplace>> ListAsync(CancellationToken ct = default) =>
            Failure is null ? Task.FromResult<IReadOnlyList<SandboxAppMarketplace>>([Row("team")]) : throw Failure;

        public Task<SandboxAppMarketplace> RegisterAsync(
            string alias,
            string url,
            string? gitRef,
            CancellationToken ct = default
        )
        {
            LastRef = gitRef;
            return Failure is null ? Task.FromResult(Row(alias)) : throw Failure;
        }

        public Task<SandboxAppMarketplace> SyncAsync(string alias, CancellationToken ct = default) =>
            Failure is null ? Task.FromResult(Row(alias)) : throw Failure;

        public Task<bool> RemoveAsync(string alias, CancellationToken ct = default) =>
            Failure is null ? Task.FromResult(RemoveResult) : throw Failure;
    }
}
