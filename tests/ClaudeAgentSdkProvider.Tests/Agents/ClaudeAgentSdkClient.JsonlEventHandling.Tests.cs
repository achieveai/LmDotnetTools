using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkClientJsonlEventHandlingTests
{
    [Fact]
    public void ConvertNonTerminalJsonlEventToMessages_IgnoresStreamEventAndPreservesAssistantMessages()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var events = new JsonlEventBase[]
        {
            new StreamEvent
            {
                Event = JsonDocument.Parse("""{"type":"content_block_delta"}""").RootElement,
                SessionId = "session-1",
                Uuid = "stream-1",
            },
            new AssistantMessageEvent
            {
                Uuid = "assistant-1",
                SessionId = "session-1",
                Message = new AssistantMessage
                {
                    Id = "msg-1",
                    Model = "claude-test",
                    Role = "assistant",
                    Content = [new ContentBlock { Type = "text", Text = "assistant survived stream event" }],
                },
            },
        };

        var messages = events
            .SelectMany(e => client.ConvertNonTerminalJsonlEventToMessages(e, emitSystemInit: false))
            .ToList();

        var text = Assert.Single(messages.OfType<TextMessage>());
        Assert.Equal("assistant survived stream event", text.Text);
    }

    [Fact]
    public void ConvertNonTerminalJsonlEventToMessages_AllowedRateLimit_LogsInformation_AndEmitsNoMessages()
    {
        var logger = new CapturingLogger<ClaudeAgentSdkClient>();
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions(), logger);

        var rateLimit = new RateLimitEvent
        {
            SessionId = "session-1",
            RateLimitInfo = new RateLimitInfo
            {
                Status = "allowed",
                RateLimitType = "five_hour",
                ResetsAt = 1777410000,
                IsUsingOverage = false,
            },
        };

        var messages = client.ConvertNonTerminalJsonlEventToMessages(rateLimit, emitSystemInit: false);

        Assert.Empty(messages);
        Assert.Equal(1, logger.CountAtLevel(LogLevel.Information, "Rate-limit OK"));
        Assert.Equal(0, logger.CountAtLevel(LogLevel.Warning, "Rate-limit NOT allowed"));
        // Regression: rate_limit_event must NOT fall through to the "Unhandled" warning.
        Assert.Equal(0, logger.WarningCount("Unhandled JSONL event type"));
    }

    [Theory]
    [InlineData("throttled")]
    [InlineData("denied")]
    [InlineData("warning")]
    public void ConvertNonTerminalJsonlEventToMessages_NonAllowedRateLimit_LogsWarning(string status)
    {
        var logger = new CapturingLogger<ClaudeAgentSdkClient>();
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions(), logger);

        var rateLimit = new RateLimitEvent
        {
            SessionId = "session-1",
            RateLimitInfo = new RateLimitInfo
            {
                Status = status,
                RateLimitType = "five_hour",
                ResetsAt = 1777410000,
                OverageStatus = "rejected",
                OverageDisabledReason = "org_level_disabled",
            },
        };

        var messages = client.ConvertNonTerminalJsonlEventToMessages(rateLimit, emitSystemInit: false);

        Assert.Empty(messages);
        Assert.Equal(1, logger.CountAtLevel(LogLevel.Warning, "Rate-limit NOT allowed"));
        Assert.Equal(0, logger.CountAtLevel(LogLevel.Information, "Rate-limit OK"));
        Assert.Equal(0, logger.WarningCount("Unhandled JSONL event type"));
    }

    [Fact]
    public void ConvertNonTerminalJsonlEventToMessages_RateLimitWithNoInfo_LogsDebug()
    {
        var logger = new CapturingLogger<ClaudeAgentSdkClient>();
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions(), logger);

        var rateLimit = new RateLimitEvent { SessionId = "session-1", RateLimitInfo = null };

        var messages = client.ConvertNonTerminalJsonlEventToMessages(rateLimit, emitSystemInit: false);

        Assert.Empty(messages);
        Assert.Equal(1, logger.CountAtLevel(LogLevel.Debug, "no rate_limit_info payload"));
        Assert.Equal(0, logger.WarningCount("Unhandled JSONL event type"));
    }

    [Fact]
    public void ConvertNonTerminalJsonlEventToMessages_RateLimitEvent_ParsedFromJsonlLine_IsHandled()
    {
        // Guards the end-to-end path: a real rate_limit_event JSONL line must parse to a
        // RateLimitEvent (registered discriminator) and be handled, not warned as "Unhandled".
        var logger = new CapturingLogger<ClaudeAgentSdkClient>();
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions(), logger);

        const string line = """
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1777410000,"rateLimitType":"five_hour","isUsingOverage":false},"uuid":"u1","session_id":"session-1"}
            """;
        var parsed = JsonSerializer.Deserialize<JsonlEventBase>(line);

        var rateLimit = Assert.IsType<RateLimitEvent>(parsed);
        var messages = client.ConvertNonTerminalJsonlEventToMessages(rateLimit, emitSystemInit: false);

        Assert.Empty(messages);
        Assert.Equal(1, logger.CountAtLevel(LogLevel.Information, "Rate-limit OK"));
        Assert.Equal(0, logger.WarningCount("Unhandled JSONL event type"));
    }
}
