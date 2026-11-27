using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Private Git marketplaces (gateway PR #375, LmDotnetTools #1004): the opaque <c>connectionId</c> on registration and
/// how a repository-grant denial stays distinguishable from a rejected app credential.
/// </summary>
public sealed class SandboxClientAppMarketplacePrivateTests
{
    private const string Path = "/api/v1/apps/self/marketplaces";
    private const string AdoUrl = "https://dev.azure.com/acme/plugins/_git/team-plugins.git";

    private const string Row = """
        {"app_id":"app-1","alias":"team","url":"https://dev.azure.com/acme/plugins/_git/team-plugins.git",
         "repo_key":"plugins-0a1b","ref":"latest","resolved_sha":"8ca22dba9a94f28898bbce59f2537ff4d87c747d",
         "created_at":"2026-10-09T22:18:15.652076747+00:00","updated_at":"2026-10-09T22:19:39.428182518+00:00"}
        """;

    [Fact]
    public async Task RegisterAppMarketplaceAsync_Registration_SendsConnectionIdAndKeepsGitSuffix()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row);

        var row = await client.RegisterAppMarketplaceAsync(
            "team",
            new SandboxAppMarketplaceRegistration
            {
                Url = AdoUrl,
                Ref = "main",
                ConnectionId = "publishing-team.v1_x",
            }
        );

        row.Alias.Should().Be("team");
        var sent = handler.Requests.Single();
        sent.Uri.PathAndQuery.Should().Be($"{Path}/team?app_id=app-1");
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("url", "ref", "connectionId");
        body.RootElement.GetProperty("url").GetString().Should().Be(AdoUrl);
        body.RootElement.GetProperty("ref").GetString().Should().Be("main");
        body.RootElement.GetProperty("connectionId").GetString().Should().Be("publishing-team.v1_x");
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_PublicRequests_SendNoConnectionId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row);

        await client.RegisterAppMarketplaceAsync("team", AdoUrl, null, CancellationToken.None);
        await client.RegisterAppMarketplaceAsync("team", new SandboxAppMarketplaceRegistration { Url = AdoUrl });

        handler
            .Requests.Should()
            .HaveCount(2)
            .And.AllSatisfy(request =>
            {
                using var body = JsonDocument.Parse(request.Body!);
                body.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("url");
                body.RootElement.GetProperty("url").GetString().Should().Be(AdoUrl);
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/not-allowed")]
    [InlineData("tok=en")]
    [InlineData("é")]
    public async Task RegisterAppMarketplaceAsync_InvalidConnectionId_ThrowsWithoutSending(string connectionId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        var act = () =>
            client.RegisterAppMarketplaceAsync(
                "team",
                new SandboxAppMarketplaceRegistration { Url = AdoUrl, ConnectionId = connectionId }
            );

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_ConnectionIdLengthBoundary()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", Row);

        await client.RegisterAppMarketplaceAsync(
            "team",
            new SandboxAppMarketplaceRegistration { Url = AdoUrl, ConnectionId = new string('a', 128) }
        );
        var tooLong = () =>
            client.RegisterAppMarketplaceAsync(
                "team",
                new SandboxAppMarketplaceRegistration { Url = AdoUrl, ConnectionId = new string('a', 129) }
            );

        await tooLong.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_AccessDenied_KeepsErrorCodeAndNoBodyText()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Put,
            $"{Path}/team",
            """{"error":"token=SECRET https://private.example/x","code":403,"error_code":"marketplace_access_denied"}""",
            HttpStatusCode.Forbidden
        );

        var thrown = await RegisterPrivateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().Be("marketplace_access_denied");
        thrown.StatusCode.Should().Be(403);
        thrown.Message.Should().NotContain("SECRET").And.NotContain("private.example");
        handler.Requests.Should().ContainSingle("a denied private registration is never retried anonymously");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"x","code":403,"error_code":"app_forbidden"}""")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.Forbidden, "not json")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"x","code":401,"error_code":"marketplace_access_denied"}""")]
    public async Task RegisterAppMarketplaceAsync_OtherCredentialRejections_StayCodeless(
        HttpStatusCode status,
        string body
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Put, $"{Path}/team", body, status);

        var thrown = await RegisterPrivateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().BeNull();
        thrown.Message.Should().Be($"Sandbox gateway returned {(int)status} for registering app marketplace 'team'.");
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_OversizedForbiddenBody_StaysCodeless()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Put,
            $"{Path}/team",
            "{\"error_code\":\"marketplace_access_denied\",\"pad\":\""
                + new string('x', SandboxClient.MaxForbiddenBodyBytes)
                + "\"}",
            HttpStatusCode.Forbidden
        );

        var thrown = await RegisterPrivateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Authorization);
        thrown.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_BrokerUnavailable_IsUnavailableWithCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Put,
            $"{Path}/team",
            """{"error":"broker down","code":503,"error_code":"marketplace_broker_unavailable"}""",
            HttpStatusCode.ServiceUnavailable
        );

        var thrown = await RegisterPrivateAndCatchAsync(client);

        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
        thrown.ErrorCode.Should().Be("marketplace_broker_unavailable");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RegisterAppMarketplaceAsync_Cancelled_PropagatesCancellation()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnHang(request => request.Method == HttpMethod.Put);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () =>
            client.RegisterAppMarketplaceAsync(
                "team",
                new SandboxAppMarketplaceRegistration { Url = AdoUrl, ConnectionId = "publishing-team" },
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<SandboxException> RegisterPrivateAndCatchAsync(SandboxClient client) =>
        (
            await Record.ExceptionAsync(() =>
                client.RegisterAppMarketplaceAsync(
                    "team",
                    new SandboxAppMarketplaceRegistration { Url = AdoUrl, ConnectionId = "publishing-team" }
                )
            )
        )
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
}
