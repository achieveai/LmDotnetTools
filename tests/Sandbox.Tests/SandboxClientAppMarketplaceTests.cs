using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private const string ArchiveUrl = "https://publisher.example/releases/selected";
    private const string ArchiveDigest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string ContentDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ArchiveRow = $$"""
        {"kind":"archive","app_id":"app-1","alias":"team","url":"{{ArchiveUrl}}",
         "sha256":"{{ArchiveDigest}}","content_sha256":"{{ContentDigest}}","plugin":"selected"}
        """;

    [Fact]
    public async Task PublisherArchive_RoundTripsTheSignedReceipt()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team/archive", ArchiveRow);
        handler.OnJson(HttpMethod.Get, $"{Path}/team/archive", ArchiveRow);

        var registered = await client.RegisterAppMarketplaceArchiveAsync(
            "team",
            ArchiveUrl,
            ArchiveDigest.ToUpperInvariant(),
            "selected"
        );
        var retrieved = await client.GetAppMarketplaceArchiveAsync("team");

        registered
            .Should()
            .Be(
                new SandboxAppMarketplaceArchive("app-1", "team", ArchiveUrl, ArchiveDigest, ContentDigest, "selected")
            );
        retrieved.Should().Be(registered);
        handler.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Put, HttpMethod.Get);
        handler
            .Requests.Should()
            .AllSatisfy(request =>
            {
                request.Uri.PathAndQuery.Should().Be($"{Path}/team/archive");
                request.SbxAppId.Should().Be("app-1");
                request.SbxAppKey.Should().Be(TestSupport.ValidSecret);
            });
        using var body = JsonDocument.Parse(handler.Requests.First().Body!);
        body.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("url", "sha256", "plugin");
        body.RootElement.GetProperty("url").GetString().Should().Be(ArchiveUrl);
        body.RootElement.GetProperty("sha256").GetString().Should().Be(ArchiveDigest);
        body.RootElement.GetProperty("plugin").GetString().Should().Be("selected");
    }

    [Theory]
    [InlineData("kind", "git")]
    [InlineData("app_id", "other-app")]
    [InlineData("alias", "other-alias")]
    [InlineData("url", "https://publisher.example/different")]
    [InlineData("url", "https://publisher.example/releases/selected?other=value")]
    [InlineData("sha256", ContentDigest)]
    [InlineData("content_sha256", "123")]
    [InlineData("plugin", "other-package")]
    [InlineData("plugin", null)]
    public async Task RegisterAppMarketplaceArchiveAsync_RejectsMismatchedReceipt(string field, string? value)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var response = JsonNode.Parse(ArchiveRow)!.AsObject();
        response[field] = value;
        handler.OnJson(HttpMethod.Put, $"{Path}/team/archive", response.ToJsonString());

        var act = () => client.RegisterAppMarketplaceArchiveAsync("team", ArchiveUrl, ArchiveDigest, "selected");
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task GetAppMarketplaceArchiveAsync_DistinguishesMissingAndUnavailable()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/gone/archive",
            Error(404, "marketplace_not_registered"),
            HttpStatusCode.NotFound
        );
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/broken/archive",
            Error(503, "archive_unavailable"),
            HttpStatusCode.ServiceUnavailable
        );

        (await client.GetAppMarketplaceArchiveAsync("gone")).Should().BeNull();
        var act = () => client.GetAppMarketplaceArchiveAsync("broken");
        (await act.Should().ThrowAsync<SandboxException>()).Which.ErrorCode.Should().Be("archive_unavailable");
    }

    [Theory]
    [InlineData("url", "https://publisher.example")]
    [InlineData("url", "https://publisher.example:443/")]
    [InlineData("url", "https://private.local/package")]
    [InlineData("url", "https://publisher.example/a/../archive")]
    [InlineData("url", "https://publisher.example./archive")]
    [InlineData("plugin", " selected")]
    [InlineData("plugin", "selected\u007f")]
    public async Task GetAppMarketplaceArchiveAsync_RejectsNoncanonicalReceipts(string field, string value)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var response = JsonNode.Parse(ArchiveRow)!.AsObject();
        response[field] = value;
        handler.OnJson(HttpMethod.Get, $"{Path}/team/archive", response.ToJsonString());

        var act = () => client.GetAppMarketplaceArchiveAsync("team");
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PublisherArchive_PreservesEscapedPublisherPathInRequestsAndReceipts()
    {
        const string escapedUrl = "https://publisher.example/releases/%73elected";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var row = ArchiveRow.Replace(ArchiveUrl, escapedUrl, StringComparison.Ordinal);
        handler.OnJson(HttpMethod.Put, $"{Path}/team/archive", row);
        handler.OnJson(HttpMethod.Get, $"{Path}/team/archive", row);

        (await client.RegisterAppMarketplaceArchiveAsync("team", escapedUrl, ArchiveDigest, "selected"))
            .Url.Should()
            .Be(escapedUrl);
        (await client.GetAppMarketplaceArchiveAsync("team"))!.Url.Should().Be(escapedUrl);
        using var body = JsonDocument.Parse(handler.Requests.First().Body!);
        body.RootElement.GetProperty("url").GetString().Should().Be(escapedUrl);
    }

    [Fact]
    public async Task PublisherArchive_InvalidSourceFields_DoNotSendRequests()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAppMarketplaceArchiveAsync("a/../b"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterAppMarketplaceArchiveAsync("team!", ArchiveUrl, ArchiveDigest, "selected")
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterAppMarketplaceArchiveAsync("team", "https://publisher.example", ArchiveDigest, "selected")
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterAppMarketplaceArchiveAsync("team", ArchiveUrl, ArchiveDigest, new string('é', 129))
        );
        handler.Requests.Should().BeEmpty();
    }

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
        body.RootElement.TryGetProperty("plugin", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("sibling")]
    [InlineData(null)]
    public async Task RegisterAppMarketplacePackageAsync_RequiresTheGatewayToEchoThePackage(string? acknowledged)
    {
        const string commit = "8ca22dba9a94f28898bbce59f2537ff4d87c747d";
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        var response = JsonNode.Parse(Row)!.AsObject();
        response["ref"] = commit;
        response["last_error"] = null;
        if (acknowledged is not null)
        {
            response["plugin"] = acknowledged;
        }
        handler.OnJson(HttpMethod.Put, $"{Path}/team/package", response.ToJsonString());

        Func<Task<SandboxAppMarketplace>> register = () =>
            client.RegisterAppMarketplacePackageAsync("team", "https://github.com/acme/plugins", commit, "selected");
        if (acknowledged == "selected")
        {
            (await register()).Plugin.Should().Be("selected");
        }
        else
        {
            (await register.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        }

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.GetProperty("ref").GetString().Should().Be(commit);
        body.RootElement.GetProperty("plugin").GetString().Should().Be("selected");
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
