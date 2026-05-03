using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class LegacyHandlerAdapterTests
{
    [Fact]
    public async Task ToLegacyHandler_Resolved_ReturnsPayloadText()
    {
        ToolHandler handler = (_, _, _) => Task.FromResult<ToolHandlerResult>(
            ToolHandlerResult.FromText("hello"));

        var legacy = LegacyHandlerAdapter.ToLegacyHandler(handler, toolKey: "echo");

        var text = await legacy("{}");
        text.Should().Be("hello");
    }

    [Fact]
    public async Task ToLegacyHandler_Deferred_ThrowsNotSupportedException()
    {
        // The legacy shape (Func<string, Task<string>>) has no resolution channel — deferral
        // is the responsibility of MultiTurnAgentLoop, which uses ToolHandler directly.
        // Adapters that bridge into the legacy shape (e.g., MCP server) must surface
        // misuse loudly. This is the only guardrail.
        ToolHandler handler = (_, _, _) => Task.FromResult<ToolHandlerResult>(
            new ToolHandlerResult.Deferred());

        var legacy = LegacyHandlerAdapter.ToLegacyHandler(handler, toolKey: "wait_for_human");

        var act = async () => await legacy("{}");
        var ex = await act.Should().ThrowAsync<NotSupportedException>();
        ex.WithMessage("*wait_for_human*deferred*MultiTurnAgentLoop*");
    }

    [Fact]
    public async Task WrapToLegacyHandlers_Deferred_ThrowsAtInvocationNotConstruction()
    {
        // Deferred handlers can be REGISTERED in a legacy-shape map without crashing — the
        // NotSupportedException only fires at invocation time, when a tool is actually called.
        var source = new Dictionary<string, ToolHandler>
        {
            ["safe"] = (_, _, _) => Task.FromResult<ToolHandlerResult>(
                ToolHandlerResult.FromText("ok")),
            ["bad"] = (_, _, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Deferred()),
        };

        var wrapped = LegacyHandlerAdapter.WrapToLegacyHandlers(source);

        wrapped.Should().HaveCount(2);
        (await wrapped["safe"]("{}")).Should().Be("ok");

        var act = async () => await wrapped["bad"]("{}");
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*bad*");
    }

    [Fact]
    public async Task ToNewHandler_FromString_ProducesResolvedTextPayload()
    {
        Func<string, Task<string>> legacy = _ => Task.FromResult("legacy-result");

        var handler = LegacyHandlerAdapter.ToNewHandler(legacy);

        var result = await handler("{}", new ToolCallContext(), CancellationToken.None);
        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        ((ToolHandlerResult.Resolved)result).Payload.Text.Should().Be("legacy-result");
    }

    [Fact]
    public async Task ToNewHandler_FromToolCallResult_ProjectsPayloadFields()
    {
        // The legacy ToolCallResult shape carries text + content blocks + error fields. The
        // adapter must project all four into ToolHandlerResultPayload; framework-controlled
        // fields (tool_call_id, IsDeferred, timestamps) on the legacy result are dropped —
        // the builder downstream is the only thing that should set them.
        var blocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "rich" },
        };
        Func<string, Task<ToolCallResult>> legacy = _ => Task.FromResult(
            new ToolCallResult(
                toolCallId: "should-be-dropped",
                result: "the-text",
                contentBlocks: blocks)
            {
                IsError = true,
                ErrorCode = "E_LEGACY",
            });

        var handler = LegacyHandlerAdapter.ToNewHandler(legacy);

        var result = await handler("{}", new ToolCallContext(), CancellationToken.None);
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.Text.Should().Be("the-text");
        resolved.Payload.ContentBlocks.Should().BeSameAs(blocks);
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("E_LEGACY");
    }
}
