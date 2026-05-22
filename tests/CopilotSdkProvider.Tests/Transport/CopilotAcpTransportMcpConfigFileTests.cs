using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Transport;

/// <summary>
/// Verifies that <see cref="CopilotAcpTransport"/> projects
/// <c>CopilotSdkOptions.McpServers</c> (and per-session overrides) onto the Copilot
/// CLI's <c>--additional-mcp-config=@&lt;file&gt;</c> flag instead of the ACP
/// <c>session/new</c> wire shape. Drives two surfaces:
///   1. The internal <c>BuildMcpConfigFileContent</c> producer (schema + filter rules).
///   2. The end-to-end launch contract (flag appears in arguments, file exists,
///      <c>HostPathReference</c> declared, temp file cleaned up on Dispose).
/// </summary>
public sealed class CopilotAcpTransportMcpConfigFileTests
{
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public ProcessLaunchRequest? LastRequest { get; private set; }

        public string? CapturedMcpConfigFileContents { get; private set; }

        public string? CapturedMcpConfigFilePath { get; private set; }

        public Task<IProcessHandle> LaunchAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            // The temp file must exist at launch time; capture its contents
            // before the transport's StartAsync error path cleans it up.
            foreach (var arg in request.Arguments)
            {
                if (arg.StartsWith("--additional-mcp-config=@", StringComparison.Ordinal))
                {
                    var path = arg["--additional-mcp-config=@".Length..];
                    CapturedMcpConfigFilePath = path;
                    if (File.Exists(path))
                    {
                        CapturedMcpConfigFileContents = File.ReadAllText(path);
                    }
                    break;
                }
            }

            throw new ProcessLauncherException("recording launcher: never spawns");
        }
    }

    private static string? InvokeBuildMcpConfigFileContent(
        CopilotAcpTransport transport,
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers)
    {
        var method = typeof(CopilotAcpTransport).GetMethod(
            "BuildMcpConfigFileContent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method.Invoke(transport, [mcpServers]);
    }

    private static async Task RunStartAsync(CopilotAcpTransport transport, string workingDir, IReadOnlyDictionary<string, McpServerConfig>? mcp)
    {
        var startMethod = typeof(CopilotAcpTransport).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "StartAsync" && m.GetParameters().Length == 7);

        var task = (Task)startMethod.Invoke(transport,
        [
            workingDir,
            "ghp_test",
            "https://example.test",
            mcp,
            (Func<string, JsonElement?, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(default(JsonElement))),
            (Action<string, JsonElement?>)((_, _) => { }),
            CancellationToken.None,
        ])!;
        await task;
    }

    [Fact]
    public void Empty_mcpServers_produces_no_config_file_content()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var content = InvokeBuildMcpConfigFileContent(transport, null);
        Assert.Null(content);

        var empty = InvokeBuildMcpConfigFileContent(transport, new Dictionary<string, McpServerConfig>());
        Assert.Null(empty);
    }

    [Fact]
    public void Single_stdio_server_uses_config_file_schema_with_object_env()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["github"] = McpServerConfig.CreateStdio(
                command: "npx",
                args: ["-y", "@github/mcp"],
                env: new Dictionary<string, string> { ["TOKEN"] = "abc", ["EXTRA"] = "1" }),
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.NotNull(content);

        using var doc = JsonDocument.Parse(content!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal(JsonValueKind.Object, servers.ValueKind);
        var entry = servers.GetProperty("github");
        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.Equal("npx", entry.GetProperty("command").GetString());

        var args = entry.GetProperty("args");
        Assert.Equal(JsonValueKind.Array, args.ValueKind);
        Assert.Equal(2, args.GetArrayLength());
        Assert.Equal("-y", args[0].GetString());
        Assert.Equal("@github/mcp", args[1].GetString());

        // env must be a plain object map, NOT the ACP {name,value} array shape.
        var env = entry.GetProperty("env");
        Assert.Equal(JsonValueKind.Object, env.ValueKind);
        Assert.Equal("abc", env.GetProperty("TOKEN").GetString());
        Assert.Equal("1", env.GetProperty("EXTRA").GetString());
    }

    [Fact]
    public void Http_server_uses_config_file_schema_with_object_headers()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["remote"] = McpServerConfig.CreateHttp(
                url: "https://example.com/mcp",
                headers: new Dictionary<string, string> { ["X-Auth"] = "bearer" }),
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.NotNull(content);

        using var doc = JsonDocument.Parse(content!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("remote");
        Assert.Equal("http", entry.GetProperty("type").GetString());
        Assert.Equal("https://example.com/mcp", entry.GetProperty("url").GetString());
        Assert.False(entry.TryGetProperty("command", out _));

        var headers = entry.GetProperty("headers");
        Assert.Equal(JsonValueKind.Object, headers.ValueKind);
        Assert.Equal("bearer", headers.GetProperty("X-Auth").GetString());
    }

    [Fact]
    public void Mixed_server_types_appear_in_one_file()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["stdio-srv"] = McpServerConfig.CreateStdio("npx", ["-y", "thing"]),
            ["http-srv"] = McpServerConfig.CreateHttp("https://h.example/mcp"),
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.NotNull(content);

        using var doc = JsonDocument.Parse(content!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("stdio", servers.GetProperty("stdio-srv").GetProperty("type").GetString());
        Assert.Equal("http", servers.GetProperty("http-srv").GetProperty("type").GetString());
    }

    [Fact]
    public void Stdio_server_without_command_is_skipped()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["broken"] = new McpServerConfig { Type = "stdio", Command = null },
            ["good"] = McpServerConfig.CreateStdio("cmd", ["ok"]),
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.NotNull(content);

        using var doc = JsonDocument.Parse(content!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.False(servers.TryGetProperty("broken", out _));
        Assert.True(servers.TryGetProperty("good", out _));
    }

    [Fact]
    public void Only_invalid_entries_yields_no_file_content()
    {
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["broken"] = new McpServerConfig { Type = "stdio", Command = null },
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.Null(content);
    }

    [Fact]
    public void Args_default_to_empty_array_when_omitted()
    {
        // Some commands (e.g. `npx some-mcp`) carry no extra args; ensure
        // `args` is emitted as `[]` rather than missing so the CLI schema
        // sees the expected key.
        var transport = new CopilotAcpTransport(new CopilotSdkOptions());
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["noargs"] = new McpServerConfig { Type = "stdio", Command = "cmd", Args = null },
        };

        var content = InvokeBuildMcpConfigFileContent(transport, mcp);
        Assert.NotNull(content);

        using var doc = JsonDocument.Parse(content!);
        var args = doc.RootElement.GetProperty("mcpServers").GetProperty("noargs").GetProperty("args");
        Assert.Equal(JsonValueKind.Array, args.ValueKind);
        Assert.Equal(0, args.GetArrayLength());
    }

    [Fact]
    public async Task StartAsync_without_mcp_servers_omits_flag_and_creates_no_file()
    {
        var recorder = new RecordingLauncher();
        var options = new CopilotSdkOptions
        {
            CopilotCliPath = "copilot-cli-mock",
            ProcessLauncher = recorder,
        };

        await using var transport = new CopilotAcpTransport(options);

        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);
        try
        {
            var act = async () => await RunStartAsync(transport, workingDir, mcp: null);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }

        recorder.LastRequest.Should().NotBeNull();
        recorder.LastRequest!.Arguments.Should().Equal("--acp", "--stdio");
        recorder.CapturedMcpConfigFilePath.Should().BeNull();
        recorder.LastRequest.HostPaths.Should().NotContain(p => p.Kind == HostPathKind.McpConfigFile);
    }

    [Fact]
    public async Task StartAsync_with_stdio_server_injects_flag_writes_file_and_declares_host_path()
    {
        var recorder = new RecordingLauncher();
        var options = new CopilotSdkOptions
        {
            CopilotCliPath = "copilot-cli-mock",
            ProcessLauncher = recorder,
        };
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["github"] = McpServerConfig.CreateStdio("npx", ["-y", "@github/mcp"]),
        };

        await using var transport = new CopilotAcpTransport(options);

        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);
        try
        {
            var act = async () => await RunStartAsync(transport, workingDir, mcp);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }

        recorder.LastRequest.Should().NotBeNull();
        recorder.LastRequest!.Arguments.Should().StartWith(["--acp", "--stdio"]);
        var flagArg = recorder.LastRequest.Arguments.Single(a => a.StartsWith("--additional-mcp-config=@", StringComparison.Ordinal));
        flagArg.Should().StartWith("--additional-mcp-config=@");

        recorder.CapturedMcpConfigFilePath.Should().NotBeNull();
        recorder.CapturedMcpConfigFileContents.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(recorder.CapturedMcpConfigFileContents!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("github");
        entry.GetProperty("command").GetString().Should().Be("npx");

        recorder.LastRequest.HostPaths.Should().Contain(p =>
            p.Kind == HostPathKind.McpConfigFile && p.Path == recorder.CapturedMcpConfigFilePath);
    }

    [Fact]
    public async Task StartAsync_cleans_up_temp_file_after_launch_failure()
    {
        var recorder = new RecordingLauncher();
        var options = new CopilotSdkOptions
        {
            CopilotCliPath = "copilot-cli-mock",
            ProcessLauncher = recorder,
        };
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["github"] = McpServerConfig.CreateStdio("npx", ["-y", "@github/mcp"]),
        };

        var transport = new CopilotAcpTransport(options);

        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);
        try
        {
            var act = async () => await RunStartAsync(transport, workingDir, mcp);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
            await transport.DisposeAsync();
        }

        // The recording launcher captured the path BEFORE the failure-path
        // cleanup ran, so the file existed at launch time. The transport must
        // delete it before returning control to the caller.
        recorder.CapturedMcpConfigFilePath.Should().NotBeNull();
        File.Exists(recorder.CapturedMcpConfigFilePath!).Should().BeFalse();
    }
}
