using System.Reflection;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

/// <summary>
/// Pins the ACP <c>session/new</c> <c>mcpServers</c> wire shape. The Copilot CLI's
/// <c>session/new</c> Zod schema validates <c>mcpServers</c> as a required array and
/// silently rejects stdio entries placed there — external MCP servers are routed
/// through <c>--additional-mcp-config=@&lt;file&gt;</c> (see
/// <c>CopilotAcpTransportLaunchContractTests</c> + <c>CopilotAcpTransportMcpConfigFileTests</c>)
/// instead. <c>session/new</c> must therefore always carry an empty array.
/// </summary>
public sealed class CopilotSdkClientMcpServerShapeTests
{
    private static CopilotSdkOptions NewOptions(
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers = null)
    {
        return new CopilotSdkOptions
        {
            CopilotCliPath = "copilot",
            McpServers = mcpServers ?? new Dictionary<string, McpServerConfig>(),
        };
    }

    private static IDictionary<string, object?> InvokeBuildSessionNewParams(
        CopilotSdkClient client,
        CopilotBridgeInitOptions options)
    {
        var method = typeof(CopilotSdkClient).GetMethod(
            "BuildSessionNewParams",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(client, [options]);
        Assert.NotNull(result);
        Assert.IsAssignableFrom<IDictionary<string, object?>>(result);
        return (IDictionary<string, object?>)result;
    }

    private static IList<object> McpServerArray(IDictionary<string, object?> parameters)
    {
        Assert.True(parameters.ContainsKey("mcpServers"));
        var raw = parameters["mcpServers"];
        Assert.NotNull(raw);
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(raw);
        var list = new List<object>();
        foreach (var item in enumerable)
        {
            list.Add(item);
        }
        return list;
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
    public void Stdio_entries_are_NOT_projected_into_session_new()
    {
        // External MCP servers flow through --additional-mcp-config=@<file>, not
        // through session/new. The wire array must remain empty regardless of the
        // configured McpServers dictionary so the Copilot CLI Zod validator does
        // not see (and reject) a stdio entry on the ACP request.
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

        Assert.Empty(McpServerArray(parameters));
    }

    [Fact]
    public void Http_entries_are_NOT_projected_into_session_new()
    {
        // Same as stdio: even though the ACP schema historically accepted http
        // entries, the consolidated routing (single source of truth) is the
        // --additional-mcp-config file.
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

        Assert.Empty(McpServerArray(parameters));
    }

    [Fact]
    public void Bridge_options_McpServers_override_sdk_options_via_resolver()
    {
        // Per-call dictionary on CopilotBridgeInitOptions wins over the
        // CopilotSdkClient's ctor-supplied McpServers (mirrors how Model,
        // WorkingDirectory, etc. resolve). This guards the override path used by
        // CopilotAgentLoop in OnBeforeRunAsync — even though McpServers no longer
        // appears in session/new, the resolver still needs to surface the
        // per-call value so the transport's --additional-mcp-config file picks
        // up the override.
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

        var resolveMethod = typeof(CopilotSdkClient).GetMethod(
            "ResolveEffectiveOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);
        var resolved = (CopilotBridgeInitOptions)resolveMethod.Invoke(client, [options])!;

        Assert.NotNull(resolved.McpServers);
        Assert.Single(resolved.McpServers);
        Assert.True(resolved.McpServers.ContainsKey("override"));
        Assert.Equal("cmd-b", resolved.McpServers["override"].Command);
    }
}
