using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// The create result's confirmed-inventory contract: what the gateway says is <i>loaded</i> is
/// reported, what it merely offers or was merely asked for is not, and silence is reported as
/// silence rather than as an empty session.
/// </summary>
public class SandboxClientInventoryTests
{
    private const string SessionAndVolumes =
        """
        "session_id":"sess-1","container_id":"container-1","volumes":{"workspace":{"container_path":"/workspace","read_only":false}}
        """;

    private static string CreateResponse(string? extra) =>
        "{" + SessionAndVolumes + (extra is null ? string.Empty : "," + extra) + "}";

    private static async Task<SandboxInfo> CreateWithAsync(string? extra)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponse(extra));
        return await client.CreateAsync(new SandboxCreateRequest("my-workspace"));
    }

    #region Silence is reported as silence

    [Fact]
    public async Task CreateAsync_GatewayOmitsInventory_ReportsUnavailableWithAReason()
    {
        var info = await CreateWithAsync(extra: null);

        info.Inventory.Should().NotBeNull("a caller must never have to null-check the inventory");
        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Unavailable);
        info.Inventory.Items.Should().BeEmpty();
        info.Inventory.UnavailableReason.Should().NotBeNullOrWhiteSpace(
            "'unavailable' without a reason cannot be told apart from a session that loaded nothing"
        );
    }

    [Fact]
    public async Task CreateAsync_GatewayOmitsStatus_LeavesItEmptyRatherThanInventingOne()
    {
        var info = await CreateWithAsync(extra: null);

        info.Status.Should().BeEmpty("a synthesized status would be indistinguishable from a reported one");
    }

    [Fact]
    public async Task CreateAsync_GatewayReportsStatus_SurfacesItVerbatim()
    {
        var info = await CreateWithAsync("""
            "status":"running"
            """);

        info.Status.Should().Be("running");
    }

    #endregion

    #region Confirmed means loaded

    [Fact]
    public async Task CreateAsync_ConfirmedInventory_SurfacesKindIdAndVersion()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"confirmed","items":[
                {"kind":"plugin","id":"development","version":"1.4.0"},
                {"kind":"skill","id":"development:implement"},
                {"kind":"agent","id":"code-reviewer:pr-review","version":"2.0.1"}
            ]}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Confirmed);
        info.Inventory.UnavailableReason.Should().BeNull("a confirmed inventory has nothing to explain");
        info.Inventory.Items.Select(i => (i.Kind, i.Id, i.Version))
            .Should()
            .Equal(
                [
                    (SandboxInventoryKinds.Plugin, "development", "1.4.0"),
                    (SandboxInventoryKinds.Skill, "development:implement", null),
                    (SandboxInventoryKinds.Agent, "code-reviewer:pr-review", "2.0.1"),
                ],
                "items keep gateway order, and a kind that carries no version reports none rather than a placeholder"
            );
    }

    [Fact]
    public async Task CreateAsync_ConfirmedButEmpty_IsAnAnswerNotASilence()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"confirmed","items":[]}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Confirmed);
        info.Inventory.Items.Should().BeEmpty();
        info.Inventory.UnavailableReason.Should().BeNull(
            "the gateway positively confirmed that nothing is loaded, which is not the same as being unable to say"
        );
    }

    #endregion

    #region Unconfirmed data is never labeled confirmed

    [Fact]
    public async Task CreateAsync_ItemsWithoutAConfirmedStatus_AreNotReported()
    {
        // The shape a gateway would produce if it echoed the create request's marketplace selection
        // — what was asked for, not what loaded.
        var info = await CreateWithAsync("""
            "inventory":{"items":[{"kind":"plugin","id":"development","version":"1.4.0"}]}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Unavailable);
        info.Inventory.Items.Should().BeEmpty("an unconfirmed list read without checking the status would pass as confirmed");
        info.Inventory.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateAsync_UnrecognizedStatus_IsUnavailableAndNamesTheStatus()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"partial","items":[{"kind":"plugin","id":"development"}]}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Unavailable);
        info.Inventory.Items.Should().BeEmpty();
        info.Inventory.UnavailableReason.Should()
            .Contain("partial", "an operator has to be able to tell an old gateway from one that answered but would not confirm");
    }

    [Fact]
    public async Task CreateAsync_ExplicitlyUnavailable_KeepsTheGatewaysOwnReason()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"unavailable","unavailable_reason":"marketplace resolution timed out"}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Unavailable);
        info.Inventory.UnavailableReason.Should().Be("marketplace resolution timed out");
    }

    #endregion

    #region Malformed entries

    [Fact]
    public async Task CreateAsync_ItemMissingKindOrId_IsDroppedRatherThanHalfIdentified()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"confirmed","items":[
                {"kind":"plugin","id":"development"},
                {"kind":"skill"},
                {"id":"orphan"},
                {"kind":"  ","id":"blank-kind"},
                {"kind":null,"id":"explicit-null-kind"}
            ]}
            """);

        // The last entry lands on the same `null` Kind as the omitted-field entry above it — an
        // explicit JSON null and an absent member are indistinguishable once the record's parameter
        // is bound — so it adds no coverage the others lack. It is spelled out because it is the
        // shape a "make the two paths consistent" edit reaches for first (the plugin-resolution
        // path THROWS on exactly this), and the payload should show that this array does not.
        // The rule: a malformed FIELD drops, only a malformed ELEMENT throws. See ToInventory.
        info.Inventory.Items.Should().ContainSingle().Which.Id.Should().Be("development");
    }

    /// <summary>
    /// The other half of that rule. A <c>null</c> ARRAY ELEMENT is not a droppable item — there is
    /// no item to inspect — and unguarded it dereferences to a <see cref="NullReferenceException"/>
    /// that escapes the SDK's exception contract on an otherwise successful 2xx response.
    /// </summary>
    [Fact]
    public async Task CreateAsync_InventoryWithNullArrayElement_ThrowsProtocolNamingTheInventory()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            CreateResponse("""
                "inventory":{"status":"confirmed","items":[{"kind":"plugin","id":"development"},null]}
                """)
        );

        var thrown = await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("my-workspace")));

        thrown.Should().BeOfType<SandboxException>("a malformed 2xx payload is a protocol defect, not an unhandled NullReferenceException");
        var sandboxException = (SandboxException)thrown;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.StatusCode.Should().Be(200);
        sandboxException.Message.Should().Contain("inventory", "the message has to say which of the response's arrays was malformed");
    }

    [Fact]
    public async Task CreateAsync_UnknownInventoryFields_AreIgnoredNotFatal()
    {
        var info = await CreateWithAsync("""
            "inventory":{"status":"confirmed","source":"marketplace","items":[
                {"kind":"plugin","id":"development","version":"1.4.0","install_path":"/opt/plugins/development","manifest":{"x":1}}
            ]}
            """);

        info.Inventory.Status.Should().Be(SandboxInventoryStatuses.Confirmed);
        info.Inventory.Items.Should().ContainSingle().Which.Id.Should().Be("development");
    }

    #endregion
}
