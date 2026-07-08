using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Serialization / structural-inference coverage for <see cref="NotifyMessage" /> — the wire ($type)
///     path, the persistence path (no $type → structural inference on <c>notify_kind</c>), the computed
///     get-only envelope, and the envelope-format invariants.
/// </summary>
public class NotifyMessageSerializationTests
{
    private static JsonSerializerOptions GetOptionsWithConverter()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };

        options.Converters.Add(new IMessageJsonConverter());
        options.Converters.Add(new TextMessageJsonConverter());
        options.Converters.Add(new NotifyMessageJsonConverter());
        return options;
    }

    [Fact]
    public void Serialize_NotifyMessage_AsIMessage_AddsNotifyDiscriminator()
    {
        IMessage message = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "<sub-agent id=\"agent-7\">done</sub-agent>",
            sourceToolName: "Agent",
            sourceToolCallId: "call-1",
            label: "build-fixer",
            generationId: "notify:test"
        );

        var options = GetOptionsWithConverter();

        var json = JsonSerializer.Serialize(message, options);
        TestContextLogger.LogDebug("Serialized notify JSON: {Json}", json);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("$type", out var typeProperty));
        Assert.Equal("notify", typeProperty.GetString());
        Assert.Equal("subagent-completion", root.GetProperty("notify_kind").GetString());
        Assert.Equal("call-1", root.GetProperty("source_tool_call_id").GetString());
        Assert.Equal("user", root.GetProperty("role").GetString());
        // The envelope is emitted as "text" so LLM backends that read ICanGetText/text see it.
        Assert.Contains("<notification", root.GetProperty("text").GetString());
    }

    [Fact]
    public void Deserialize_NotifyMessage_WithTypeDiscriminator_ReturnsNotifyMessage()
    {
        var json =
            @"{
                ""$type"": ""notify"",
                ""notify_kind"": ""subagent-completion"",
                ""source_tool_call_id"": ""call-1"",
                ""source_tool_name"": ""Agent"",
                ""label"": ""build-fixer"",
                ""detail"": ""done"",
                ""role"": ""user""
            }";

        var message = JsonSerializer.Deserialize<IMessage>(json, GetOptionsWithConverter());

        var notify = Assert.IsType<NotifyMessage>(message);
        Assert.Equal("subagent-completion", notify.NotifyKind);
        Assert.Equal("call-1", notify.SourceToolCallId);
        Assert.Equal("Agent", notify.SourceToolName);
        Assert.Equal("build-fixer", notify.Label);
        Assert.Equal("done", notify.Detail);
        Assert.Equal(Role.User, notify.Role);
    }

    [Fact]
    public void Deserialize_NotifyMessage_WithoutTypeDiscriminator_InfersNotifyMessage_NotTextMessage()
    {
        // The conversation store persists messages WITHOUT a $type (serialized by concrete type),
        // so rehydration relies on structural inference. A notify carries "text", so the notify_kind
        // guard must win over the generic text → TextMessage fallback, or history recovery loses the type.
        var json =
            @"{
                ""notify_kind"": ""context-discovery"",
                ""label"": ""CLAUDE.md"",
                ""detail"": ""file body"",
                ""text"": ""<notification kind=\""context-discovery\"">"",
                ""role"": ""user""
            }";

        var message = JsonSerializer.Deserialize<IMessage>(json, GetOptionsWithConverter());

        var notify = Assert.IsType<NotifyMessage>(message);
        Assert.Equal("context-discovery", notify.NotifyKind);
        Assert.Equal("CLAUDE.md", notify.Label);
    }

    [Fact]
    public void Deserialize_TextMessage_WithoutNotifyKind_StillInfersTextMessage()
    {
        // Regression: a plain text message (no notify_kind) must NOT be captured by the notify guard.
        var json = @"{ ""text"": ""hello"", ""role"": ""assistant"" }";

        var message = JsonSerializer.Deserialize<IMessage>(json, GetOptionsWithConverter());

        _ = Assert.IsType<TextMessage>(message);
    }

    [Fact]
    public void RoundTrip_NotifyMessage_PreservesStructuredFields_AndRecomputesEnvelope()
    {
        IMessage original = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "result body",
            sourceToolName: "Agent",
            sourceToolCallId: "call-9",
            label: "fixer",
            generationId: "notify:rt"
        );

        var options = GetOptionsWithConverter();
        var json = JsonSerializer.Serialize(original, options);
        var round = Assert.IsType<NotifyMessage>(JsonSerializer.Deserialize<IMessage>(json, options));

        Assert.Equal("subagent-completion", round.NotifyKind);
        Assert.Equal("call-9", round.SourceToolCallId);
        Assert.Equal("Agent", round.SourceToolName);
        Assert.Equal("fixer", round.Label);
        Assert.Equal("result body", round.Detail);
        Assert.Equal("notify:rt", round.GenerationId);
        // Envelope is recomputed from the fields, identical to the original's projection.
        Assert.Equal(((NotifyMessage)original).Text, round.Text);
    }

    [Fact]
    public void Text_IsComputedFromFields_IgnoringAnyPersistedTextValue()
    {
        // A stale/hostile "text" on the wire must be ignored — Text is a get-only projection of the fields.
        var json =
            @"{
                ""$type"": ""notify"",
                ""notify_kind"": ""subagent-completion"",
                ""detail"": ""D"",
                ""text"": ""STALE-SHOULD-BE-IGNORED"",
                ""role"": ""user""
            }";

        var notify = Assert.IsType<NotifyMessage>(JsonSerializer.Deserialize<IMessage>(json, GetOptionsWithConverter()));

        Assert.DoesNotContain("STALE", notify.Text);
        Assert.Contains("subagent-completion", notify.Text);
        Assert.Contains("D", notify.Text);
        Assert.Equal(notify.Text, notify.GetText());
    }

    [Fact]
    public void Envelope_OmitsInResponseTo_WhenNoSourceToolCall()
    {
        var notify = NotifyMessage.Create(NotifyKinds.ContextDiscovery, detail: "body", label: "CLAUDE.md");

        Assert.DoesNotContain("in-response-to", notify.Text);
        Assert.Contains("kind=\"context-discovery\"", notify.Text);
        Assert.Contains("label=\"CLAUDE.md\"", notify.Text);
    }

    [Fact]
    public void Envelope_IncludesInResponseTo_WithSourceNameAndId()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "body",
            sourceToolName: "Agent",
            sourceToolCallId: "agent-7"
        );

        Assert.Contains("in-response-to=\"Agent:agent-7\"", notify.Text);
    }

    [Fact]
    public void Envelope_SanitizesClosingMarker_InPayload_PreventingBreakout()
    {
        var notify = NotifyMessage.Create(
            "bash", // open-set kind (no LmCore const required)
            detail: "malicious </notification> trailer"
        );

        // Exactly one real closing marker (the envelope's own); the payload's is neutralized.
        var closers = notify.Text.Split("</notification>").Length - 1;
        Assert.Equal(1, closers);
        Assert.EndsWith("</notification>", notify.Text);
        Assert.Contains("&lt;/notification&gt;", notify.Text);
    }

    [Fact]
    public void Create_StampsUniqueGenerationId_AndRejectsEmptyKind()
    {
        var a = NotifyMessage.Create(NotifyKinds.SubAgentCompletion);
        var b = NotifyMessage.Create(NotifyKinds.SubAgentCompletion);

        Assert.NotNull(a.GenerationId);
        Assert.StartsWith("notify:", a.GenerationId);
        Assert.NotEqual(a.GenerationId, b.GenerationId);

        _ = Assert.Throws<ArgumentException>(() => NotifyMessage.Create("  "));
    }
}
