using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class PromptCachingRequestTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    private static List<JsonElement> CacheControls(JsonElement element)
    {
        var found = new List<JsonElement>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in e.EnumerateObject())
                {
                    if (property.NameEquals("cache_control"))
                    {
                        found.Add(property.Value);
                    }

                    Walk(property.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(element);
        return found;
    }

    private static JsonElement SerializedRequest(PromptCachingMode caching)
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = caching,
            BuiltInTools = [new AnthropicWebSearchTool()],
        };
        var request = AnthropicRequest.FromMessages(
            [new TextMessage { Role = Role.System, Text = "Be brief" }, .. messages],
            options
        );
        return JsonDocument.Parse(JsonSerializer.Serialize(request, _jsonOptions)).RootElement;
    }

    [Fact]
    public void FromMessages_WithOneHour_MarksToolsSystemAndLastUser_AllForOneHour()
    {
        var markers = CacheControls(SerializedRequest(PromptCachingMode.AutoOneHour));

        Assert.Equal(3, markers.Count);
        Assert.All(markers, marker => Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", marker.GetRawText()));
    }

    [Fact]
    public void FromMessages_WithOneHour_MarksTheLastFunctionToolForOneHour()
    {
        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.AutoOneHour,
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

        var request = AnthropicRequest.FromMessages([new TextMessage { Role = Role.User, Text = "Hello" }], options);
        var tools = JsonDocument
            .Parse(JsonSerializer.Serialize(request, _jsonOptions))
            .RootElement.GetProperty("tools");

        var last = tools[tools.GetArrayLength() - 1];
        Assert.Equal("get_weather", last.GetProperty("name").GetString());
        Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", last.GetProperty("cache_control").GetRawText());
    }

    [Fact]
    public void Apply_WithOneHour_MarksTheLastBlockOfAnExistingSystemArrayForOneHour()
    {
        // A request whose system prompt is already an array takes the arm that marks its last block, not the
        // string-to-array conversion FromMessages exercises.
        var request = new AnthropicRequest
        {
            SystemContent =
            [
                new AnthropicSystemContent { Type = "text", Text = "First" },
                new AnthropicSystemContent { Type = "text", Text = "Last" },
            ],
        };

        var marked = AchieveAi.LmDotnetTools.AnthropicProvider.Caching.PromptCachingStrategy.Apply(
            request,
            oneHour: true
        );

        Assert.Null(marked.SystemContent![0].CacheControl);
        Assert.Equal("1h", marked.SystemContent[1].CacheControl!.Ttl);
    }

    [Fact]
    public void FromMessages_WithCachingAuto_SendsNoTtl()
    {
        var markers = CacheControls(SerializedRequest(PromptCachingMode.Auto));

        Assert.Equal(3, markers.Count);
        Assert.All(markers, marker => Assert.Equal("""{"type":"ephemeral"}""", marker.GetRawText()));
    }

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
        Assert.False(tools[0].TryGetProperty("cache_control", out _), "First tool should not have cache_control");

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
