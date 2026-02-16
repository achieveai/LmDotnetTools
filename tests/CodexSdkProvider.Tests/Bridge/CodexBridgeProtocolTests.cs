using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Bridge;

public class CodexBridgeProtocolTests
{
    [Fact]
    public void CodexBridgeRequest_SerializesWithSnakeCaseFields()
    {
        var request = new CodexBridgeRequest
        {
            Type = "run",
            RequestId = "req-1",
            Input = "hello",
        };

        var json = JsonSerializer.Serialize(request);

        json.Should().Contain("\"request_id\":\"req-1\"");
        json.Should().Contain("\"type\":\"run\"");
        json.Should().Contain("\"input\":\"hello\"");
    }

    [Fact]
    public void CodexBridgeResponse_DeserializesEventEnvelope()
    {
        const string json = """
            {
                "type":"event",
                "request_id":"req-2",
                "event":{"type":"thread.started","thread_id":"thread_123"}
            }
            """;

        var response = JsonSerializer.Deserialize<CodexBridgeResponse>(json);

        response.Should().NotBeNull();
        response!.Type.Should().Be("event");
        response.RequestId.Should().Be("req-2");
        response.Event.HasValue.Should().BeTrue();
        response.Event!.Value.GetProperty("type").GetString().Should().Be("thread.started");
    }
}
