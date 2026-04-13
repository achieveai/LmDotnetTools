using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class PromptCachingRequestTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void FromMessages_WithCachingAuto_AddsBreakpointToLastTool()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.Auto,
            BuiltInTools = [new AnthropicWebSearchTool()],
            Functions =
            [
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get weather",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "city",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        },
                    ],
                },
            ],
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools");

        Assert.Equal(2, tools.GetArrayLength());

        // First tool should NOT have cache_control
        Assert.False(
            tools[0].TryGetProperty("cache_control", out _),
            "First tool should not have cache_control"
        );

        // Last tool SHOULD have cache_control
        Assert.True(
            tools[1].TryGetProperty("cache_control", out var cacheControl),
            "Last tool should have cache_control"
        );
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
    }

    [Fact]
    public void FromMessages_WithCachingAuto_ConvertsSystemToArrayWithBreakpoint()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "You are a helpful assistant." },
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.Auto,
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // System should be an array, not a string
        var system = doc.RootElement.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal(1, system.GetArrayLength());

        var systemBlock = system[0];
        Assert.Equal("text", systemBlock.GetProperty("type").GetString());
        Assert.Equal("You are a helpful assistant.", systemBlock.GetProperty("text").GetString());
        Assert.True(
            systemBlock.TryGetProperty("cache_control", out var cacheControl),
            "System content block should have cache_control"
        );
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
    }

    [Fact]
    public void FromMessages_WithCachingAuto_AddsBreakpointToLastUserMessage()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "First question" },
            new TextMessage { Role = Role.Assistant, Text = "First answer" },
            new TextMessage { Role = Role.User, Text = "Second question" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.Auto,
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var requestMessages = doc.RootElement.GetProperty("messages");

        // Find the last user message
        JsonElement? lastUserMsg = null;
        foreach (var msg in requestMessages.EnumerateArray())
        {
            if (msg.GetProperty("role").GetString() == "user")
            {
                lastUserMsg = msg;
            }
        }

        Assert.NotNull(lastUserMsg);
        var content = lastUserMsg.Value.GetProperty("content");
        var lastContentBlock = content[content.GetArrayLength() - 1];

        Assert.True(
            lastContentBlock.TryGetProperty("cache_control", out var cacheControl),
            "Last user message's last content block should have cache_control"
        );
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
    }

    [Fact]
    public void FromMessages_WithCachingOff_NoCacheControl()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "You are a helpful assistant." },
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            // PromptCaching defaults to Off
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);

        // No cache_control should appear anywhere in the JSON
        Assert.DoesNotContain("cache_control", json);

        // System should be a plain string
        var doc = JsonDocument.Parse(json);
        var system = doc.RootElement.GetProperty("system");
        Assert.Equal(JsonValueKind.String, system.ValueKind);
    }

    [Fact]
    public void FromMessages_WithCachingAuto_NoTools_SkipsToolBreakpoint()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "You are helpful." },
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.Auto,
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // No tools property
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));

        // System should still have cache_control
        var system = doc.RootElement.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.True(system[0].TryGetProperty("cache_control", out _));

        // Last user message should have cache_control
        var msgs = doc.RootElement.GetProperty("messages");
        var lastMsg = msgs[msgs.GetArrayLength() - 1];
        Assert.Equal("user", lastMsg.GetProperty("role").GetString());
        var content = lastMsg.GetProperty("content");
        Assert.True(content[content.GetArrayLength() - 1].TryGetProperty("cache_control", out _));
    }
}
