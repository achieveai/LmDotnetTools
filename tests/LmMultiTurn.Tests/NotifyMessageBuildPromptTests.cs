using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
///     The Copilot and Codex CLI loops build the model prompt from the input batch (not history) and
///     previously skipped any non-<c>TextMessage</c> with a "unsupported_message_type" warning. A
///     <see cref="NotifyMessage"/> must instead have its envelope appended so the async event reaches
///     a CLI model turn.
/// </summary>
public class NotifyMessageBuildPromptTests
{
    private static IReadOnlyList<QueuedInput> OneNotify()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion, detail: "sub-agent done", sourceToolName: "Agent", sourceToolCallId: "call-1");
        return [new QueuedInput(new UserInput([notify]), "r1", DateTimeOffset.UtcNow)];
    }

    [Fact]
    public void CopilotBuildPrompt_IncludesNotifyEnvelope_NotSkipped()
    {
        var prompt = CopilotEventTranslator.BuildPrompt(OneNotify());

        prompt.Should().Contain("<notification").And.Contain("subagent-completion");
    }

    [Fact]
    public void CodexBuildPrompt_IncludesNotifyEnvelope_NotSkipped()
    {
        var prompt = CodexEventTranslator.BuildPrompt(OneNotify());

        prompt.Should().Contain("<notification").And.Contain("subagent-completion");
    }
}
