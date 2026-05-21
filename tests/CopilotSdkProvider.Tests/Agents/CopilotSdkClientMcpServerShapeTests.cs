using System.Reflection;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

/// <summary>
/// Pins the ACP <c>session/new</c> <c>mcpServers</c> wire shape so the
/// Copilot CLI's Zod validator keeps accepting it. The wire shape is an
/// array (NOT a map) of <c>{ name, command, args, env: [{name,value}] }</c>
/// entries for stdio transport, and <c>{ name, type, url, headers }</c> for
/// http/sse transports. Reflection is used so the shape contract can be
/// exercised without exposing <c>BuildSessionNewParams</c> publicly.
/// </summary>
public sealed class CopilotSdkClientMcpServerShapeTests
{
    /// <summary>
    /// Hand-rolled options for the unit (no real transport spun up).
    /// </summary>
    private static CopilotSdkOptions NewOptions(
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers = null)
    {
        return new CopilotSdkOptions
        {
            CopilotCliPath = "copilot",
            McpServers = mcpServers ?? new Dictionary<string, McpServerConfig>(),
        };
    }

    /// <summary>
    /// Invoke the private <c>BuildSessionNewParams</c> method through reflection
    /// and return the resulting parameter dictionary so tests can assert on the
    /// exact wire keys/values.
    /// </summary>
    private static IDictionary<string, object?> InvokeBuildSessionNewParams(
        CopilotSdkClient client,
        CopilotBridgeInitOptions options)
    {
        var method = typeof(CopilotSdkClient).GetMethod(
            "BuildSessionNewParams",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(client, [options]);
        Assert.NotNull(result);
        Assert.IsAssignableFrom<IDictionary<string, object?>>(result);
        return (IDictionary<string, object?>)result!;
    }

    private static IList<object> McpServerArray(IDictionary<string, object?> parameters)
    {
        Assert.True(parameters.ContainsKey("mcpServers"));
        var raw = parameters["mcpServers"];
        Assert.NotNull(raw);
        return Materialize(raw!);
    }

    /// <summary>
    /// Materialize a non-generic <see cref="System.Collections.IEnumerable"/> to a
    /// <see cref="List{Object}"/> without using LINQ — analyzer IDE0305 fires on
    /// <c>.Cast&lt;object&gt;().ToList()</c> in tests.
    /// </summary>
    private static IList<object> Materialize(object raw)
    {
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(raw);
        var list = new List<object>();
        foreach (var item in enumerable)
        {
            list.Add(item);
        }
        return list;
    }

    private static IDictionary<string, object?> AsEntry(object entry)
    {
        Assert.IsAssignableFrom<IDictionary<string, object?>>(entry);
        return (IDictionary<string, object?>)entry;
    }

    [Fact]
    public void Empty_mcpServers_emits_empty_array()
    {
        var client = new CopilotSdkClient(NewOptions());
        var options = new CopilotBridgeInitOptions { Model = "m", WorkingDirectory = "/tmp" };

        var parameters = InvokeBuildSessionNewParams(client, options);

        Assert.Empty(McpServerArray(parameters));
    }

    [Fact]
    public void Stdio_entry_is_projected_to_acp_shape()
    {
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["github"] = McpServerConfig.CreateStdio(
                command: "npx",
                args: ["-y", "@github/mcp"],
                env: new Dictionary<string, string> { ["TOKEN"] = "abc" }),
        };
        var client = new CopilotSdkClient(NewOptions(mcp));
        var options = new CopilotBridgeInitOptions
        {
            Model = "m",
            WorkingDirectory = "/tmp",
            McpServers = mcp,
        };

        var parameters = InvokeBuildSessionNewParams(client, options);
        var list = McpServerArray(parameters);

        Assert.Single(list);
        var entry = AsEntry(list[0]);
        Assert.Equal("github", entry["name"]);
        Assert.Equal("npx", entry["command"]);

        var args = Assert.IsType<string[]>(entry["args"]);
        Assert.Equal(["-y", "@github/mcp"], args);

        // env is an array of {name, value} objects, not a plain map
        var envList = Materialize(entry["env"]!);
        Assert.Single(envList);
        var envEntry = AsEntry(envList[0]);
        Assert.Equal("TOKEN", envEntry["name"]);
        Assert.Equal("abc", envEntry["value"]);

        // No URL or explicit type field on stdio entries
        Assert.False(entry.ContainsKey("url"));
        Assert.False(entry.ContainsKey("type"));
    }

    [Fact]
    public void Http_entry_is_projected_with_url_and_headers()
    {
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["remote"] = McpServerConfig.CreateHttp(
                url: "https://example.com/mcp",
                headers: new Dictionary<string, string> { ["X-Auth"] = "bearer" }),
        };
        var client = new CopilotSdkClient(NewOptions(mcp));
        var options = new CopilotBridgeInitOptions
        {
            Model = "m",
            WorkingDirectory = "/tmp",
            McpServers = mcp,
        };

        var parameters = InvokeBuildSessionNewParams(client, options);
        var list = McpServerArray(parameters);

        Assert.Single(list);
        var entry = AsEntry(list[0]);
        Assert.Equal("remote", entry["name"]);
        Assert.Equal("http", entry["type"]);
        Assert.Equal("https://example.com/mcp", entry["url"]);

        var headers = Materialize(entry["headers"]!);
        Assert.Single(headers);
        var headerEntry = AsEntry(headers[0]);
        Assert.Equal("X-Auth", headerEntry["name"]);
        Assert.Equal("bearer", headerEntry["value"]);
    }

    [Fact]
    public void Bridge_options_McpServers_override_sdk_options()
    {
        // Per-call dictionary on CopilotBridgeInitOptions wins over the
        // CopilotSdkClient's ctor-supplied McpServers (mirrors how Model,
        // WorkingDirectory, etc. resolve). This guards the override path
        // used by CopilotAgentLoop in OnBeforeRunAsync.
        var ctorDict = new Dictionary<string, McpServerConfig>
        {
            ["ctor"] = McpServerConfig.CreateStdio("cmd-a", ["x"]),
        };
        var perCallDict = new Dictionary<string, McpServerConfig>
        {
            ["override"] = McpServerConfig.CreateStdio("cmd-b", ["y"]),
        };
        var client = new CopilotSdkClient(NewOptions(ctorDict));
        var options = new CopilotBridgeInitOptions
        {
            Model = "m",
            WorkingDirectory = "/tmp",
            McpServers = perCallDict,
        };

        // Need to also run options through the private resolver since
        // BuildSessionNewParams takes the resolved options. Re-invoke the
        // same logic by calling ResolveEffectiveOptions reflectively.
        var resolveMethod = typeof(CopilotSdkClient).GetMethod(
            "ResolveEffectiveOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);
        var resolved = (CopilotBridgeInitOptions)resolveMethod!.Invoke(client, [options])!;

        var parameters = InvokeBuildSessionNewParams(client, resolved);
        var list = McpServerArray(parameters);
        Assert.Single(list);
        var entry = AsEntry(list[0]);
        Assert.Equal("override", entry["name"]);
        Assert.Equal("cmd-b", entry["command"]);
    }

    [Fact]
    public void Stdio_entry_without_command_is_skipped()
    {
        // Guards against shipping a malformed entry to Copilot — the Zod
        // validator would reject the whole session/new request.
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["broken"] = new McpServerConfig { Type = "stdio", Command = null },
            ["good"] = McpServerConfig.CreateStdio("cmd", ["ok"]),
        };
        var client = new CopilotSdkClient(NewOptions(mcp));
        var options = new CopilotBridgeInitOptions
        {
            Model = "m",
            WorkingDirectory = "/tmp",
            McpServers = mcp,
        };

        var parameters = InvokeBuildSessionNewParams(client, options);
        var list = McpServerArray(parameters);
        Assert.Single(list);
        var entry = AsEntry(list[0]);
        Assert.Equal("good", entry["name"]);
    }
}
