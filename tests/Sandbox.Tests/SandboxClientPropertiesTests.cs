using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Sandbox property bags and partitions (gateway ADR 0042, <c>Docs/tools-and-api.md</c> "Sandbox properties and
/// partitions"): the create field and echo, <c>GET/PATCH …/properties</c>, the immutable-partition refusal, and
/// the partitioned backup addressing.
/// </summary>
public sealed class SandboxClientPropertiesTests
{
    [Fact]
    public async Task CreateAsync_Properties_AreSentAndTheEchoedBagAndPartitionAreSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"s-1","properties":{"plan":"pro","tenant_id":"acme","user_id":"u7"},
             "partition":["tenant_id","user_id"]}
            """,
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                "proj",
                properties: new Dictionary<string, string>
                {
                    ["tenant_id"] = "acme",
                    ["user_id"] = "u7",
                    ["plan"] = "pro",
                }
            )
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.GetProperty("properties").GetProperty("tenant_id").GetString().Should().Be("acme");
        info.Properties.Should()
            .Equal(
                new Dictionary<string, string>
                {
                    ["plan"] = "pro",
                    ["tenant_id"] = "acme",
                    ["user_id"] = "u7",
                }
            );
        info.Partition.Should().Equal("tenant_id", "user_id");
    }

    [Fact]
    public async Task CreateAsync_NoProperties_OmitsTheFieldAndAnOlderGatewayReportsNoBag()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"s-1"}""", HttpStatusCode.Created);

        var info = await client.CreateAsync(new SandboxCreateRequest("proj"));

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.TryGetProperty("properties", out _).Should().BeFalse();
        info.Properties.Should()
            .BeNull("a gateway without properties sends no bag, which is not the same as an empty one");
        info.Partition.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePropertiesAsync_SendsAFlatBodyWithNullForUnset_AndReturnsTheResultingBag()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/properties",
            """{"properties":{"plan":"pro","tenant_id":"acme"},"partition":["tenant_id"]}"""
        );

        var result = await client.UpdatePropertiesAsync(
            "s-1",
            new Dictionary<string, string?> { ["plan"] = "pro", ["region"] = null }
        );

        var body = JsonDocument.Parse(handler.Requests.Single().Body!).RootElement;
        body.GetProperty("plan").GetString().Should().Be("pro");
        body.GetProperty("region").ValueKind.Should().Be(JsonValueKind.Null, "null is what unsets a key");
        result.Properties.Should().Equal(new Dictionary<string, string> { ["plan"] = "pro", ["tenant_id"] = "acme" });
        result.Partition.Should().Equal("tenant_id");
    }

    [Fact]
    public async Task GetPropertiesAsync_UnpartitionedApp_ReportsAnEmptyPartition()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/properties", """{"properties":{},"partition":[]}""");

        var result = await client.GetPropertiesAsync("s-1");

        result.Properties.Should().BeEmpty();
        result.Partition.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePropertiesAsync_PartitionKeyChange_IsConflictCarryingTheKeys()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/properties",
            """{"error":"partition properties are immutable","code":409,"error_code":"partition_property_immutable","keys":["tenant_id"]}""",
            HttpStatusCode.Conflict
        );

        var act = () =>
            client.UpdatePropertiesAsync("s-1", new Dictionary<string, string?> { ["tenant_id"] = "other" });

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Conflict);
        thrown.ErrorCode.Should().Be("partition_property_immutable");
        thrown.InvalidKeys.Should().Equal("tenant_id");
    }

    [Fact]
    public async Task GetPropertiesAsync_ResponseWithoutABag_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/properties", """{"partition":[]}""");

        var act = () => client.GetPropertiesAsync("s-1");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task BackupCalls_WithPartitionProperties_AddressThePartitionedWorkspace()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(HttpMethod.Get, "/api/v1/backup/snapshots", """{"snapshots":[],"workspace":"proj"}""");
        handler.OnJson(HttpMethod.Post, "/api/v1/backup/restore", """{"workspace":"proj","restored":null}""");
        handler.OnJson(
            HttpMethod.Delete,
            "/api/v1/backup/workspace",
            """{"workspace":"proj","snapshots_deleted":0,"sandboxes_deleted":[]}"""
        );
        var partition = new Dictionary<string, string> { ["user_id"] = "u 7", ["tenant_id"] = "acme" };

        _ = await client.ListBackupSnapshotsAsync("proj", partition);
        _ = await client.RestoreBackupSnapshotAsync("proj", "snap-1", partition);
        _ = await client.PurgeBackupWorkspaceAsync("proj", partition);

        var list = handler.Requests[0].Uri.Query;
        list.Should().EndWith("&properties.tenant_id=acme&properties.user_id=u%207", "keys go out in ordinal order");
        using var restore = JsonDocument.Parse(handler.Requests[1].Body!);
        restore.RootElement.GetProperty("properties").GetProperty("user_id").GetString().Should().Be("u 7");
        handler.Requests[2].Uri.Query.Should().Contain("properties.tenant_id=acme");
    }
}
