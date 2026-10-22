using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Per-app git marketplaces (gateway api/app_marketplaces.rs, ADR 0045): the routes, the row contract, and how
/// the gateway's error codes classify. Error bodies mirror the gateway's <c>{error, code, error_code}</c> shape.
/// </summary>
public sealed class SandboxClientAppMarketplaceTests
{
    private const string Path = "/api/v1/apps/self/marketplaces";

    private const string Row = """
        {"app_id":"app-1","alias":"team","url":"https://github.com/acme/plugins","repo_key":"plugins-0a1b",
         "ref":"latest","resolved_sha":"8ca22dba9a94f28898bbce59f2537ff4d87c747d",
         "created_at":"2026-10-09T22:18:15.652076747+00:00","updated_at":"2026-10-09T22:19:39.428182518+00:00",
         "last_error":"fetch timed out"}
        """;

    private static string Error(int status, string code) =>
        $$"""{"error":"free text","code":{{status}},"error_code":"{{code}}"}""";

    [Fact]
    public async Task RegisterAppMarketplaceAsync_PutsUrlAndRefWithAppId_AndParsesTheRow()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row);

        var row = await client.RegisterAppMarketplaceAsync("team", "https://github.com/acme/plugins", "v1.4");

        var sent = handler.Requests.Single();
        sent.Uri.AbsolutePath.Should().Be($"{Path}/team");
        sent.Uri.Query.Should().Be("?app_id=app-1");
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("url").GetString().Should().Be("https://github.com/acme/plugins");
        body.RootElement.GetProperty("ref").GetString().Should().Be("v1.4");
        row.Should()
            .BeEquivalentTo(
                new SandboxAppMarketplace
                {
                    AppId = "app-1",
                    Alias = "team",
                    Url = "https://github.com/acme/plugins",
                    RepoKey = "plugins-0a1b",
                    Ref = "latest",
                    ResolvedSha = "8ca22dba9a94f28898bbce59f2537ff4d87c747d",
                    CreatedAt = "2026-10-09T22:18:15.652076747+00:00",
                    UpdatedAt = "2026-10-09T22:19:39.428182518+00:00",
                    LastError = "fetch timed out",
                }
            );
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_NoRef_OmitsRefSoTheGatewayUsesLatest()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row);

        _ = await client.RegisterAppMarketplaceAsync("team", "https://github.com/acme/plugins");

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.TryGetProperty("ref", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ListAppMarketplacesAsync_ParsesEveryRow()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(HttpMethod.Get, Path, $$"""{"marketplaces":[{{Row}}]}""");

        var rows = await client.ListAppMarketplacesAsync();

        rows.Should().ContainSingle().Which.ResolvedSha.Should().Be("8ca22dba9a94f28898bbce59f2537ff4d87c747d");
        handler.Requests.Single().Uri.Query.Should().Be("?app_id=app-1");
    }

    [Fact]
    public async Task SyncAppMarketplaceAsync_PostsToTheSyncRoute()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, $"{Path}/team/sync", Row);

        var row = await client.SyncAppMarketplaceAsync("team");

        row.Alias.Should().Be("team");
    }

    [Fact]
    public async Task RemoveAppMarketplaceAsync_204_IsTrue_AndNotRegistered_IsFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Delete, $"{Path}/team", HttpStatusCode.NoContent);
        handler.OnJson(
            HttpMethod.Delete,
            $"{Path}/gone",
            Error(404, "marketplace_not_registered"),
            HttpStatusCode.NotFound
        );

        (await client.RemoveAppMarketplaceAsync("team")).Should().BeTrue();
        (await client.RemoveAppMarketplaceAsync("gone")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAppMarketplaceAsync_MarketplacesDisabled_Throws_NotFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Delete, $"{Path}/team", Error(404, "marketplaces_disabled"), HttpStatusCode.NotFound);

        var act = () => client.RemoveAppMarketplaceAsync("team");

        (await act.Should().ThrowAsync<SandboxException>())
            .Which.Should()
            .Match<SandboxException>(e =>
                e.Kind == SandboxErrorKind.Unavailable && e.ErrorCode == "marketplaces_disabled"
            );
    }

    [Theory]
    [InlineData(404, "marketplaces_disabled", SandboxErrorKind.Unavailable)]
    [InlineData(404, "marketplace_not_registered", SandboxErrorKind.NotFound)]
    [InlineData(409, "alias_taken", SandboxErrorKind.Conflict)]
    [InlineData(502, "git_failed", SandboxErrorKind.Unavailable)]
    [InlineData(500, "store_failed", SandboxErrorKind.Unavailable)]
    [InlineData(500, "registry_failed", SandboxErrorKind.Unavailable)]
    [InlineData(400, "ref_not_found", SandboxErrorKind.Protocol)]
    [InlineData(400, "host_not_allowed", SandboxErrorKind.Protocol)]
    [InlineData(400, "invalid_app_id", SandboxErrorKind.Protocol)]
    [InlineData(422, "unsafe_checkout", SandboxErrorKind.Protocol)]
    [InlineData(422, "no_default_branch", SandboxErrorKind.Protocol)]
    public async Task SyncAppMarketplaceAsync_GatewayErrorCode_ClassifiesByCode(
        int status,
        string errorCode,
        SandboxErrorKind expectedKind
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, $"{Path}/team/sync", Error(status, errorCode), (HttpStatusCode)status);

        var act = () => client.SyncAppMarketplaceAsync("team");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(expectedKind);
        thrown.StatusCode.Should().Be(status);
        thrown.ErrorCode.Should().Be(errorCode);
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_RowWithoutResolvedSha_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row.Replace("\"resolved_sha\"", "\"other\""));

        var act = () => client.RegisterAppMarketplaceAsync("team", "https://github.com/acme/plugins");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_SendsAppId_SoADevGatewayIncludesTheAppsRegistrations()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(HttpMethod.Get, "/api/v1/marketplaces/preview", """{"selected":[],"marketplaces":[]}""");

        _ = await client.PreviewMarketplacesAsync(["team"]);

        handler.Requests.Single().Uri.Query.Should().Be("?app_id=app-1&marketplaces=team");
    }
}
