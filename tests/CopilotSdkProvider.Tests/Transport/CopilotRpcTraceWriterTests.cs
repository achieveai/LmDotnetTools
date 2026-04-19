using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Transport;

public class CopilotRpcTraceWriterTests
{
    [Fact]
    public async Task WriteAsync_RedactsSensitiveFields_AndIncludesCorrelationFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new CopilotRpcTraceWriter(path, "session-1"))
        {
            await writer.WriteAsync(
                "outbound",
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"apiKey":"secret","token":"t","authorization":"a","access_token":"at","refresh_token":"rt","sessionId":"s-1"}}""",
                CancellationToken.None);
        }

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().ContainSingle();

        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        var root = envelopeDoc.RootElement;
        root.GetProperty("copilot_session_id").GetString().Should().Be("session-1");
        root.GetProperty("direction").GetString().Should().Be("outbound");
        root.GetProperty("method").GetString().Should().Be("initialize");
        root.GetProperty("message_kind").GetString().Should().Be("request");
        root.GetProperty("rpc_id").GetString().Should().Be("1");
        root.GetProperty("session_id").GetString().Should().Be("s-1");
        root.GetProperty("payload_sha256").GetString().Should().NotBeNullOrWhiteSpace();

        var payloadJson = root.GetProperty("payload").GetString();
        payloadJson.Should().NotBeNullOrWhiteSpace();
        using var payloadDoc = JsonDocument.Parse(payloadJson!);
        var parameters = payloadDoc.RootElement.GetProperty("params");
        parameters.GetProperty("apiKey").GetString().Should().Be("***REDACTED***");
        parameters.GetProperty("token").GetString().Should().Be("***REDACTED***");
        parameters.GetProperty("authorization").GetString().Should().Be("***REDACTED***");
        parameters.GetProperty("access_token").GetString().Should().Be("***REDACTED***");
        parameters.GetProperty("refresh_token").GetString().Should().Be("***REDACTED***");
    }

    [Fact]
    public async Task WriteAsync_ParsesInboundNotificationShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new CopilotRpcTraceWriter(path, "session-2"))
        {
            await writer.WriteAsync(
                "inbound",
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess-1","update":{"sessionUpdate":"agent_message_chunk"}}}""",
                CancellationToken.None);
        }

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().ContainSingle();

        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        var root = envelopeDoc.RootElement;
        root.GetProperty("message_kind").GetString().Should().Be("notification");
        root.GetProperty("method").GetString().Should().Be("session/update");
        root.GetProperty("session_id").GetString().Should().Be("sess-1");
    }

    [Fact]
    public async Task WriteAsync_ResponseWithResult_ClassifiedAsResponse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new CopilotRpcTraceWriter(path, "session-3"))
        {
            await writer.WriteAsync(
                "inbound",
                """{"jsonrpc":"2.0","id":42,"result":{"ok":true}}""",
                CancellationToken.None);
        }

        var lines = await File.ReadAllLinesAsync(path);
        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        var root = envelopeDoc.RootElement;
        root.GetProperty("message_kind").GetString().Should().Be("response");
        root.GetProperty("rpc_id").GetString().Should().Be("42");
    }

    [Fact]
    public async Task WriteAsync_InvalidJson_FallsBackToUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new CopilotRpcTraceWriter(path, "session-4"))
        {
            await writer.WriteAsync("outbound", "not valid json", CancellationToken.None);
        }

        var lines = await File.ReadAllLinesAsync(path);
        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        envelopeDoc.RootElement.GetProperty("message_kind").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task WriteAsync_EmptyLine_Ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new CopilotRpcTraceWriter(path, "session-5"))
        {
            await writer.WriteAsync("outbound", "   ", CancellationToken.None);
        }

        (await File.ReadAllLinesAsync(path)).Should().BeEmpty();
    }
}
