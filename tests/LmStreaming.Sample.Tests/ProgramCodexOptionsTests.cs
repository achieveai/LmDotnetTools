using System.Reflection;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;

namespace LmStreaming.Sample.Tests;

public class ProgramCodexOptionsTests
{
    [Fact]
    public void CreateCodexOptions_EnablesRpcTrace_WhenRecordingBasePathIsProvided()
    {
        var previousRpcTraceEnabled = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED");
        var previousRpcTraceFile = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", "false");
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_FILE", null);

            var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
            programType.Should().NotBeNull();
            var method = programType!.GetMethod("CreateCodexOptions", BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            var dumpBase = "/tmp/thread_20260217T120000.llm";
            var result = method!.Invoke(null, [dumpBase, "thread-1"]);
            result.Should().BeOfType<CodexSdkOptions>();

            var options = (CodexSdkOptions)result!;
            options.EnableRpcTrace.Should().BeTrue();
            options.RpcTraceFilePath.Should().Be($"{dumpBase}.codex.rpc.jsonl");
            options.CodexSessionId.Should().Be("thread_20260217T120000.llm");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", previousRpcTraceEnabled);
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_FILE", previousRpcTraceFile);
        }
    }

    [Fact]
    public void CreateCodexOptions_AssignsDefaultTraceFile_WhenEnvTraceEnabled()
    {
        var previousRpcTraceEnabled = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED");
        var previousRpcTraceFile = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", "true");
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_FILE", null);

            var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
            programType.Should().NotBeNull();
            var method = programType!.GetMethod("CreateCodexOptions", BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            var result = method!.Invoke(null, [null, "thread-2"]);
            result.Should().BeOfType<CodexSdkOptions>();

            var options = (CodexSdkOptions)result!;
            options.EnableRpcTrace.Should().BeTrue();
            options.RpcTraceFilePath.Should().NotBeNullOrWhiteSpace();
            options.RpcTraceFilePath.Should().Contain("codex-rpc-");
            options.RpcTraceFilePath.Should().EndWith(".jsonl");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", previousRpcTraceEnabled);
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_FILE", previousRpcTraceFile);
        }
    }

    [Fact]
    public void CreateCodexOptions_ReadsInternalToolSurfacingFlags()
    {
        var previousExpose = Environment.GetEnvironmentVariable("CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES");
        var previousLegacy = Environment.GetEnvironmentVariable("CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES", "false");
            Environment.SetEnvironmentVariable("CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES", "true");

            var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
            programType.Should().NotBeNull();
            var method = programType!.GetMethod("CreateCodexOptions", BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            var result = method!.Invoke(null, [null, "thread-3"]);
            result.Should().BeOfType<CodexSdkOptions>();

            var options = (CodexSdkOptions)result!;
            options.ExposeCodexInternalToolsAsToolMessages.Should().BeFalse();
            options.EmitLegacyInternalToolReasoningSummaries.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES", previousExpose);
            Environment.SetEnvironmentVariable("CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES", previousLegacy);
        }
    }

    [Fact]
    public void ResolveCodexCliPath_UsesConfiguredPath()
    {
        InvokeResolveCodexCliPath("custom-codex", isWindows: true, pathValue: "ignored").Should().Be("custom-codex");
    }

    [Fact]
    public void ResolveCodexCliPath_PrefersWindowsExeOverCmdShim()
    {
        using var tempPath = TemporaryCodexPath.Create(("codex.cmd", "@echo off\r\n"), ("codex.exe", string.Empty));

        InvokeResolveCodexCliPath(null, isWindows: true, tempPath.PathValue)
            .Should()
            .Be(Path.Combine(tempPath.DirectoryPath, "codex.exe"));
    }

    [Fact]
    public void ResolveCodexCliPath_ResolvesWindowsCmdShim()
    {
        using var tempPath = TemporaryCodexPath.Create(("codex.cmd", "@echo off\r\n"));

        InvokeResolveCodexCliPath(null, isWindows: true, tempPath.PathValue)
            .Should()
            .Be(Path.Combine(tempPath.DirectoryPath, "codex.cmd"));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "ignored")]
    public void ResolveCodexCliPath_FallsBackToCodexCommand(bool isWindows, string? pathValue)
    {
        InvokeResolveCodexCliPath(null, isWindows, pathValue).Should().Be("codex");
    }

    private static string InvokeResolveCodexCliPath(string? configuredPath, bool isWindows, string? pathValue)
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "ResolveCodexCliPath",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(bool), typeof(string)],
            modifiers: null
        );
        method.Should().NotBeNull();

        return ((string?)method!.Invoke(null, [configuredPath, isWindows, pathValue]))
            .Should()
            .NotBeNull()
            .And.Subject!;
    }

    private sealed class TemporaryCodexPath : IDisposable
    {
        private TemporaryCodexPath(string directoryPath)
        {
            DirectoryPath = directoryPath;
            PathValue = directoryPath;
        }

        public string DirectoryPath { get; }

        public string PathValue { get; }

        public static TemporaryCodexPath Create(params (string FileName, string Content)[] files)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"codex-shim-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            foreach (var (fileName, content) in files)
            {
                File.WriteAllText(Path.Combine(directory, fileName), content);
            }

            return new TemporaryCodexPath(directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
