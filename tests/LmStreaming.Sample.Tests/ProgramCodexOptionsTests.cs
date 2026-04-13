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
            var method = programType!.GetMethod(
                "CreateCodexOptions",
                BindingFlags.NonPublic | BindingFlags.Static);
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
            var method = programType!.GetMethod(
                "CreateCodexOptions",
                BindingFlags.NonPublic | BindingFlags.Static);
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
            var method = programType!.GetMethod(
                "CreateCodexOptions",
                BindingFlags.NonPublic | BindingFlags.Static);
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
}
