using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public sealed class SandboxClientPluginPreparationTests
{
    private const string Path = "/api/v1/apps/self/plugins/prepare";
    private const string OperationId = "2276472b-0b6e-4db9-8c50-0cb8c93b07aa";

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_PostsExplicitRecipeAndReturnsExpandedPlugins()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            """
            {"status":"ready","recipe":{"effective":[]},
             "sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"},
                                                      {"marketplace":"shared","plugin":"dependency"}]}}}
            """
        );

        var effective = await client.PrepareMarketplacePluginSelectionAsync(
            ["team", "shared"],
            [new("team", "chosen")]
        );

        effective
            .Select(plugin => (plugin.Marketplace, plugin.Plugin))
            .Should()
            .Equal(("team", "chosen"), ("shared", "dependency"));
        var sent = handler.Requests.Single();
        sent.Uri.AbsolutePath.Should().Be(Path);
        sent.Uri.Query.Should().Be("?app_id=app-1");
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
        sent.SessionId.Should().BeNull();
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("marketplaces")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should()
            .Equal("team", "shared");
        var requested = body.RootElement.GetProperty("pluginSelection").EnumerateArray().Single();
        requested.GetProperty("marketplace").GetString().Should().Be("team");
        requested.GetProperty("plugin").GetString().Should().Be("chosen");
        body.RootElement.TryGetProperty("plugins", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_PollsItsOwnOperationAndAcceptsAnUnversionedReceipt()
    {
        var (client, handler, gateway) = TestSupport.CreateBorrowedClientWithBaseAddress(
            new Uri("https://unrelated.example/")
        );
        var preparing = $$"""{"status":"preparing","operationId":"{{OperationId}}"}""";
        handler.On(
            request => request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == Path,
            _ => new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Headers = { Location = new Uri("https://unrelated.example/dont-follow") },
                Content = new StringContent(preparing, Encoding.UTF8, "application/json"),
            }
        );
        var pollCount = 0;
        handler.On(
            request => request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"{Path}/{OperationId}",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ++pollCount == 1
                        ? preparing
                        : """
                        {"status":"unversioned","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}}
                        """,
                    Encoding.UTF8,
                    "application/json"
                ),
            }
        );

        var effective = await client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

        effective.Select(plugin => (plugin.Marketplace, plugin.Plugin)).Should().Equal(("team", "chosen"));
        handler
            .Requests.Select(request => request.Method)
            .Should()
            .Equal(HttpMethod.Post, HttpMethod.Get, HttpMethod.Get);
        handler
            .Requests.Should()
            .OnlyContain(request =>
                request.Uri.Authority == gateway.Authority
                && request.Uri.Query == "?app_id=app-1"
                && request.SbxAppId == "app-1"
                && request.SbxAppKey == TestSupport.ValidSecret
            );
    }

    [Theory]
    [InlineData("""{"status":"ready","recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}""")]
    [InlineData("""{"status":"unversioned","sourceReceipt":{"recipe":{"effective":null}}}""")]
    [InlineData("""{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team"}]}}}""")]
    [InlineData(
        """{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"other"}]}}}"""
    )]
    [InlineData(
        """{"status":"ready","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"},{"marketplace":"team","plugin":"chosen"}]}}}"""
    )]
    [InlineData(
        """{"status":"failed","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}}"""
    )]
    [InlineData(
        """{"status":"unknown","sourceReceipt":{"recipe":{"effective":[{"marketplace":"team","plugin":"chosen"}]}}}"""
    )]
    public async Task PrepareMarketplacePluginSelectionAsync_RejectsInvalidTerminalReplies(string reply)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, Path, reply);

        Func<Task> prepare = () => client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

        (await prepare.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_RejectsMalformedOperationIdWithoutPolling()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            """{"status":"preparing","operationId":"../different-route"}""",
            HttpStatusCode.Accepted
        );

        Func<Task> prepare = () => client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

        (await prepare.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_StopsWhenTheGatewayChangesThePendingId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            Path,
            $$"""{"status":"preparing","operationId":"{{OperationId}}"}""",
            HttpStatusCode.Accepted
        );
        handler.OnJson(
            HttpMethod.Get,
            $"{Path}/{OperationId}",
            """{"status":"preparing","operationId":"aaf81083-fdb8-4bd0-b33f-ca04ef220e2b"}"""
        );

        Func<Task> prepare = () => client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")]);

        (await prepare.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task PrepareMarketplacePluginSelectionAsync_PreservesCallerCancellationBeforeDispatch()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Func<Task> prepare = () =>
            client.PrepareMarketplacePluginSelectionAsync(["team"], [new("team", "chosen")], cancelled.Token);

        await prepare.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }
}
