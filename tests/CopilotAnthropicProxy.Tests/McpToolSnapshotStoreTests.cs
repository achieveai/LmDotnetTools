using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class McpToolSnapshotStoreTests
{
    [Fact]
    public void HeaderContext_IsCaseAndOrderIndependent()
    {
        var first = new HeaderDictionary
        {
            ["X-MCP-Tools"] = "web_search,issues",
            ["X-MCP-Readonly"] = "true",
        };
        var second = new HeaderDictionary
        {
            ["x-mcp-readonly"] = "true",
            ["x-mcp-tools"] = "web_search,issues",
        };

        McpToolSnapshotStore.BuildHeaderContext(first).Should().Be(McpToolSnapshotStore.BuildHeaderContext(second));
    }

    [Fact]
    public void HeaderContext_DistinguishesAmbiguousHeaderSets()
    {
        var split = new HeaderDictionary { ["X-MCP-A"] = "1", ["X-MCP-B"] = "2" };
        var combined = new HeaderDictionary { ["X-MCP-A"] = "1x-mcp-b=2" };

        McpToolSnapshotStore.BuildHeaderContext(split)
            .Should().NotBe(McpToolSnapshotStore.BuildHeaderContext(combined));
    }

    [Fact]
    public void HeaderContext_IgnoresNonMcpHeaders()
    {
        var first = new HeaderDictionary { ["X-MCP-Tools"] = "web_search" };
        var second = new HeaderDictionary { ["X-MCP-Tools"] = "web_search", ["X-Custom"] = "different" };

        McpToolSnapshotStore.BuildHeaderContext(first).Should().Be(McpToolSnapshotStore.BuildHeaderContext(second));
    }

    [Fact]
    public void Snapshot_IsIsolatedByEndpointAndSession()
    {
        var store = new McpToolSnapshotStore();
        store.Set("/mcp", "session-1", new(new HashSet<string> { "web_search" }, "context", "2025-06-18"));

        store.TryGet("/mcp", "session-1", out var snapshot).Should().BeTrue();
        snapshot.LocalToolNames.Should().ContainSingle().Which.Should().Be("web_search");
        store.TryGet("/mcp/readonly", "session-1", out _).Should().BeFalse();
        store.TryGet("/mcp", "session-2", out _).Should().BeFalse();
    }

    [Fact]
    public void Set_ReplacesSnapshotAndRemoveClearsOnlyMatch()
    {
        var store = new McpToolSnapshotStore();
        store.Set("/mcp", "session-1", new(new HashSet<string> { "web_search" }, "first", null));
        store.Set("/mcp/readonly", "session-1", new(new HashSet<string> { "web_fetch" }, "other", null));
        store.Set("/mcp", "session-1", new(new HashSet<string> { "web_fetch" }, "second", null));

        store.TryGet("/mcp", "session-1", out var snapshot).Should().BeTrue();
        snapshot.HeaderContext.Should().Be("second");
        snapshot.LocalToolNames.Should().ContainSingle().Which.Should().Be("web_fetch");

        store.Remove("/mcp", "session-1");
        store.TryGet("/mcp", "session-1", out _).Should().BeFalse();
        store.TryGet("/mcp/readonly", "session-1", out _).Should().BeTrue();
    }

    [Fact]
    public void OversizedMcpHeaderContext_IsRejected()
    {
        var headers = new HeaderDictionary { ["X-MCP-Tools"] = new string('x', 9_000) };

        McpToolSnapshotStore.TryBuildHeaderContext(headers, out _).Should().BeFalse();
    }
}
