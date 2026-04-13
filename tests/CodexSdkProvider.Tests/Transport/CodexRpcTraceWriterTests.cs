using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Transport;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Transport;

public class CodexRpcTraceWriterTests
{
    [Fact]
    public async Task WriteAsync_RedactsSensitiveFields_AndIncludesCorrelationFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using var writer = new CodexRpcTraceWriter(path, "session-1");

        await writer.WriteAsync(
            "outbound",
            """{"jsonrpc":"2.0","id":1,"method":"thread/start","params":{"apiKey":"secret","threadId":"t1","turnId":"u1"}}""",
            CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().ContainSingle();

        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        var root = envelopeDoc.RootElement;
        root.GetProperty("codex_session_id").GetString().Should().Be("session-1");
        root.GetProperty("direction").GetString().Should().Be("outbound");
        root.GetProperty("method").GetString().Should().Be("thread/start");
        root.GetProperty("thread_id").GetString().Should().Be("t1");
        root.GetProperty("turn_id").GetString().Should().Be("u1");
        root.GetProperty("payload_sha256").GetString().Should().NotBeNullOrWhiteSpace();

        var payloadJson = root.GetProperty("payload").GetString();
        payloadJson.Should().NotBeNullOrWhiteSpace();
        using var payloadDoc = JsonDocument.Parse(payloadJson!);
        payloadDoc.RootElement.GetProperty("params").GetProperty("apiKey").GetString().Should().Be("***REDACTED***");
    }

    [Fact]
    public async Task WriteAsync_ParsesInboundNotificationShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-rpc-trace-{Guid.NewGuid():N}.jsonl");
        await using var writer = new CodexRpcTraceWriter(path, "session-2");

        await writer.WriteAsync(
            "inbound",
            """{"jsonrpc":"2.0","method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}}""",
            CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().ContainSingle();

        using var envelopeDoc = JsonDocument.Parse(lines[0]);
        var root = envelopeDoc.RootElement;
        root.GetProperty("message_kind").GetString().Should().Be("notification");
        root.GetProperty("method").GetString().Should().Be("turn/completed");
        root.GetProperty("thread_id").GetString().Should().Be("thread-1");
    }
}
