using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Serialization;

/// <summary>
///     <see cref="ShadowPropertiesJsonConverter{T}"/> writes every <c>[JsonPropertyName]</c> property
///     itself, so it — not <see cref="JsonSerializer"/> — has to honour
///     <see cref="JsonIgnoreAttribute.Condition"/>. Before #706 it only skipped nulls, which turned
///     every <c>WhenWritingDefault</c> bool/int/enum into a <c>false</c>/<c>0</c> on the wire.
/// </summary>
public sealed class ToolCallResultMessageSerializationTests
{
    private static readonly JsonSerializerOptions Production = JsonSerializerOptionsFactory.CreateForProduction();

    [Fact]
    public void Non_truncated_result_serialized_as_concrete_type_omits_is_truncated()
    {
        var message = new ToolCallResultMessage { ToolCallId = "call_1", Result = "ok" };

        var json = JsonSerializer.Serialize(message);

        json.Should().NotContain("is_truncated");
        json.Should().Contain("\"tool_call_id\":\"call_1\"");
    }

    [Fact]
    public void Non_truncated_result_serialized_as_IMessage_omits_is_truncated()
    {
        IMessage message = new ToolCallResultMessage { ToolCallId = "call_1", Result = "ok" };

        var json = JsonSerializer.Serialize(message, Production);

        json.Should().NotContain("is_truncated");
        json.Should().Contain("\"tool_call_id\":\"call_1\"");
    }

    [Fact]
    public void Truncated_result_emits_is_truncated_true_and_deserializes_back()
    {
        IMessage message = new ToolCallResultMessage
        {
            ToolCallId = "call_1",
            Result = "prefix" + ToolResultLimits.TruncationMarkerPrefix + "6 of 6,000 bytes]",
            IsTruncated = true,
        };

        var json = JsonSerializer.Serialize(message, Production);
        json.Should().Contain("\"is_truncated\":true");

        var restored = JsonSerializer.Deserialize<IMessage>(json, Production);
        restored.Should().BeOfType<ToolCallResultMessage>().Which.IsTruncated.Should().BeTrue();

        JsonSerializer
            .Deserialize<ToolCallResultMessage>(JsonSerializer.Serialize((ToolCallResultMessage)message))!
            .IsTruncated.Should()
            .BeTrue();
    }

    [Fact]
    public void Other_WhenWritingDefault_properties_follow_the_same_rule()
    {
        // ToolCallIdx is the other WhenWritingDefault value type that goes through this converter:
        // the first call of a batch (idx 0) used to serialize "toolCallIdx":0, now it is omitted and
        // reads back as 0 either way; a non-default index is still written.
        var first = new ToolCallMessage
        {
            ToolCallId = "call_1",
            FunctionName = "f",
            ToolCallIdx = 0,
        };
        var third = new ToolCallMessage
        {
            ToolCallId = "call_3",
            FunctionName = "f",
            ToolCallIdx = 2,
        };

        var firstJson = JsonSerializer.Serialize(first);
        var thirdJson = JsonSerializer.Serialize(third);

        firstJson.Should().NotContain("toolCallIdx");
        thirdJson.Should().Contain("\"toolCallIdx\":2");
        JsonSerializer.Deserialize<ToolCallMessage>(firstJson)!.ToolCallIdx.Should().Be(0);
        JsonSerializer.Deserialize<ToolCallMessage>(thirdJson)!.ToolCallIdx.Should().Be(2);
    }
}
