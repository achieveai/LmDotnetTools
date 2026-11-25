using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public sealed class SandboxClientArchiveRuntimeTests
{
    private const string PreparePath = "/api/v1/apps/self/plugins/prepare";
    private const string SandboxPath = "/api/v1/sandboxes";
    private const string Version = "4e8bf427b30fa46c167cb6d563d56b7634c670213e6fe7f1855168b2806c15cec";
    private const string PublisherDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ContentDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PublisherUrl = "https://publisher.example/releases/package";
    private const string ArchiveRow = $$"""
        {"kind":"archive","app_id":"app-1","alias":"archive-team","url":"{{PublisherUrl}}",
         "sha256":"{{PublisherDigest}}","content_sha256":"{{ContentDigest}}","plugin":"selected"}
        """;

    private static readonly SandboxAppMarketplaceArchive Archive = new(
        "app-1",
        "archive-team",
        PublisherUrl,
        PublisherDigest,
        ContentDigest,
        "selected"
    );

    private static readonly SandboxPluginRef[] Roots = [new("archive-team", "selected")];

    private const string Prepared = $$"""
        {"status":"ready","sourceVersion":"{{Version}}","resolvedArchiveReceipts":[{{ArchiveRow}}],
         "sourceReceipt":{"recipe":{"effective":[{"marketplace":"archive-team","plugin":"selected"}]}
         }
        }
        """;

    private const string Created = $$"""
        {"session_id":"archive-session","sourceVersion":"{{Version}}","resolvedArchiveReceipts":[{{ArchiveRow}}]}
        """;

    [Fact]
    public async Task PublisherArchive_PrepareCreateAndGetPreserveExactReceipt()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        gateway.OnJson(HttpMethod.Post, PreparePath, Prepared);
        gateway.OnJson(HttpMethod.Post, SandboxPath, Created);
        gateway.OnJson(HttpMethod.Get, $"{SandboxPath}/archive-session", Created);

        var preparation = await client.PrepareMarketplaceArchiveSelectionAsync(["archive-team"], Roots, [Archive]);
        var created = await client.CreateAsync(CreateRequest(preparation));
        var retrieved = await client.GetAsync(created.SessionId);

        preparation.SourceVersion.Should().Be(Version);
        preparation
            .EffectivePlugins.Select(plugin => (plugin.Marketplace, plugin.Plugin))
            .Should()
            .Equal(("archive-team", "selected"));
        preparation.Archives.Should().Equal(Archive);
        created.ResolvedArchiveReceipts.Should().Equal(Archive);
        retrieved.ResolvedArchiveReceipts.Should().Equal(Archive);
        retrieved.SourceVersion.Should().Be(Version);
        var sent = gateway.Requests.ToArray();
        sent.Should().HaveCount(3);
        var prepareBody = JsonNode.Parse(sent[0].Body!)!;
        var createBody = JsonNode.Parse(sent[1].Body!)!;
        JsonNode.DeepEquals(prepareBody["archiveReceipts"], JsonNode.Parse($"[{ArchiveRow}]")).Should().BeTrue();
        JsonNode.DeepEquals(createBody["archiveReceipts"], prepareBody["archiveReceipts"]).Should().BeTrue();
        createBody["expectedSourceVersion"]!.GetValue<string>().Should().Be(Version);
    }

    [Theory]
    [InlineData("unversioned", "resolvedArchiveReceipts")]
    [InlineData("ready", "resolvedArchiveReceipts")]
    [InlineData("ready", "sourceVersion")]
    public async Task PrepareMarketplaceArchiveSelectionAsync_RequiresExactReadyReceipt(string status, string omitted)
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        var reply = JsonNode.Parse(Prepared)!.AsObject();
        reply["status"] = status;
        reply.Remove(omitted);
        gateway.OnJson(HttpMethod.Post, PreparePath, reply.ToJsonString());

        var act = () => client.PrepareMarketplaceArchiveSelectionAsync(["archive-team"], Roots, [Archive]);
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PublisherArchive_CreateRejectsDifferentReceiptAndDeletesUnexpectedSession()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        gateway.OnJson(HttpMethod.Post, PreparePath, Prepared);
        gateway.OnJson(
            HttpMethod.Post,
            SandboxPath,
            Created.Replace(ContentDigest, PublisherDigest, StringComparison.Ordinal)
        );
        gateway.OnStatus(HttpMethod.Delete, $"{SandboxPath}/archive-session", HttpStatusCode.NoContent);

        var preparation = await client.PrepareMarketplaceArchiveSelectionAsync(["archive-team"], Roots, [Archive]);
        var act = () => client.CreateAsync(CreateRequest(preparation));

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        gateway
            .Requests.Select(request => request.Method)
            .Should()
            .Equal(HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete);
    }

    [Fact]
    public async Task PublisherArchive_CreateRejectsAnUnsolicitedMountAndDeletesUnexpectedSession()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        gateway.OnJson(HttpMethod.Post, SandboxPath, Created);
        gateway.OnStatus(HttpMethod.Delete, $"{SandboxPath}/archive-session", HttpStatusCode.NoContent);

        var act = () => client.CreateAsync(new SandboxCreateRequest("workspace"));
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        gateway.Requests.Select(request => request.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
    }

    [Fact]
    public async Task PublisherArchive_PrepareRejectsUnscopedRequestBeforeSending()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PrepareMarketplaceArchiveSelectionAsync(["other"], Roots, [Archive])
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PrepareMarketplaceArchiveSelectionAsync(["archive-team"], [new("archive-team", "other")], [Archive])
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PrepareMarketplaceArchiveSelectionAsync(["archive-team"], Roots, [Archive, Archive])
        );
        gateway.Requests.Should().BeEmpty();
    }

    private static SandboxCreateRequest CreateRequest(SandboxArchivePreparation preparation) =>
        new("workspace", ["archive-team"], pluginSelection: Roots) { ArchivePreparation = preparation };
}
