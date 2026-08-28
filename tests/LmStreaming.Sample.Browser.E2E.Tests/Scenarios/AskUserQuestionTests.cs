using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Issue #246 — cross-boundary coverage for the browser-hosted <c>AskUserQuestion</c> client tool.
/// Drives the REAL <c>AskUserQuestionToolProvider</c> (registered unconditionally by
/// <c>MultiTurnAgentLoop</c>) end to end: the model calls the tool, the run PARKS
/// (<c>ToolHandlerResult.Deferred</c>), <c>QuestionRich.vue</c> renders the interactive form, the
/// browser submits an answer over the live <c>client_tool_result</c> WebSocket frame, and the parked
/// run resumes with the model's next scripted turn — proving the whole client ↔ server ↔ client
/// round-trip, not just isolated unit behavior on either side.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class AskUserQuestionTests
{
    private readonly PlaywrightFixture _fixture;

    public AskUserQuestionTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Builds the standard single-question, two-option scripted plan: turn 1 asks the question (parks
    /// the run), turn 2 only fires once the deferred call resolves and echoes the answer back — its
    /// presence in the final assistant text is proof the resume actually happened, not a static replay.
    /// </summary>
    private static ScriptedSseResponder BuildSingleQuestionResponder(string followUpText)
    {
        return ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.ToolCall(
                    "AskUserQuestion",
                    new
                    {
                        context = "Need to know your favorite color before continuing.",
                        questions = new object[]
                        {
                            new
                            {
                                prompt = "Pick a color",
                                options = new object[]
                                {
                                    new { label = "Red", value = "red" },
                                    new { label = "Blue", value = "blue" },
                                },
                            },
                        },
                    }
                )
            )
            .Turn(t => t.Text(followUpText))
            .Build();
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Answering_a_single_select_question_resolves_and_resumes_the_run(string providerMode)
    {
        var responder = BuildSingleQuestionResponder("Great, blue it is.");

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("what should I pick?");

        // The run parks after the tool call — the send/stop control returns to idle even though the
        // conversation is NOT finished (a deferred call is not a completed run from the client's view).
        await page.WaitForStreamIdleAsync();

        var pill = page.ToolCallPillByName("AskUserQuestion");
        await pill.WaitForAsync();

        // Rich content (QuestionRich) only renders once the pill is expanded.
        await pill.ClickAsync();
        await page.QuestionForm().WaitForAsync();

        await page.QuestionOption("blue").ClickAsync();
        await page.QuestionSubmitButton().ClickAsync();

        // The resolved, read-only view only appears once the server's ToolCallResultMessage
        // (is_deferred: false) round-trips back over the WebSocket.
        await page.QuestionResolved().WaitForAsync(new() { Timeout = 20_000 });
        (await page.QuestionResolved().InnerTextAsync()).Should().Contain("Blue");

        // The park truly resumed the SAME multi-turn run: the next scripted turn streamed in. Wait on
        // the text itself (not stream-idle first) — there is a real gap between the answer's ack and
        // the resumed run re-raising the stop button, during which the stream briefly reads as idle.
        await page.AssistantText().WaitForTextContainsAsync("Great, blue it is", timeoutMs: 20_000);
        await page.WaitForStreamIdleAsync();

        responder
            .RemainingTurns["parent"]
            .Should()
            .Be(0, "the full scripted plan (ask -> answer -> follow-up) ran to completion");

        await session.SaveSuccessScreenshotAsync(
            $"AskUserQuestion.Answering_a_single_select_question_resolves_and_resumes_the_run_{providerMode}"
        );
    }

    [Fact]
    public async Task Skip_resolves_the_question_as_skipped_and_resumes_the_run()
    {
        const string ProviderMode = "test-anthropic";
        var responder = BuildSingleQuestionResponder("No worries, skipping that then.");

        await using var session = await _fixture.OpenAsync(ProviderMode, responder.HandlerFor(ProviderMode));
        var page = session.Page;

        await page.SendMessageAsync("what should I pick?");
        await page.WaitForStreamIdleAsync();

        var pill = page.ToolCallPillByName("AskUserQuestion");
        await pill.WaitForAsync();
        await pill.ClickAsync();
        await page.QuestionForm().WaitForAsync();

        // Skip on a single-question batch submits immediately (it's also the last question) — no
        // separate Submit click needed.
        await page.QuestionSkipButton().ClickAsync();

        await page.QuestionResolved().WaitForAsync(new() { Timeout = 20_000 });
        (await page.QuestionResolved().InnerTextAsync()).Should().Contain("Skipped");

        await page.AssistantText().WaitForTextContainsAsync("skipping that then", timeoutMs: 20_000);
        await page.WaitForStreamIdleAsync();

        responder.RemainingTurns["parent"].Should().Be(0);

        await session.SaveSuccessScreenshotAsync(
            "AskUserQuestion.Skip_resolves_the_question_as_skipped_and_resumes_the_run"
        );
    }

    /// <summary>
    /// A page reload while the question is still pending must rehydrate the SAME interactive form
    /// (from the persisted <c>is_deferred: true</c> placeholder) — not a resolved view, not a lost
    /// question. Answering post-reload proves the reconnected subscribe-only WebSocket
    /// (<c>ensureClientToolSubmitConnection</c>) still round-trips the submission correctly.
    /// </summary>
    [Fact]
    public async Task Pending_question_survives_reload_and_can_still_be_answered()
    {
        const string ProviderMode = "test-anthropic";
        var responder = BuildSingleQuestionResponder("Thanks, blue noted.");

        await using var session = await _fixture.OpenAsync(ProviderMode, responder.HandlerFor(ProviderMode));
        var page = session.Page;

        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("what should I pick?");
        await page.WaitForStreamIdleAsync();

        await page.ConversationItems().WaitForCountAtLeastAsync(1);
        var threadId = await page.ConversationItems().First.GetAttributeAsync("data-thread-id");
        threadId.Should().NotBeNullOrEmpty();

        var pillBeforeReload = page.ToolCallPillByName("AskUserQuestion");
        await pillBeforeReload.WaitForAsync();
        await pillBeforeReload.ClickAsync();
        await page.QuestionForm().WaitForAsync();

        // Reload via the SAME deep link (?threadId=), mirroring how a real returning user would land
        // back on this conversation. onMounted re-selects it, loading persisted history from the REST
        // API — this is a fresh Vue app instance, so pill-expanded state does not survive; only the
        // deferred/resolved distinction, sourced from the persisted message, does.
        var deepLink = $"{session.Factory.ServerAddress.TrimEnd('/')}/?threadId={threadId}";
        await page.GotoAsync(deepLink);
        await page.Textarea().WaitForAsync();

        var pillAfterReload = page.ToolCallPillByName("AskUserQuestion");
        await pillAfterReload.WaitForAsync();
        await pillAfterReload.ClickAsync();

        // Still pending, NOT resolved — proves the deferred placeholder rehydrates correctly.
        await page.QuestionForm().WaitForAsync();
        (await page.QuestionResolved().CountAsync()).Should().Be(0, "the question must still be pending after reload");

        await page.QuestionOption("blue").ClickAsync();
        await page.QuestionSubmitButton().ClickAsync();

        await page.QuestionResolved().WaitForAsync(new() { Timeout = 20_000 });
        (await page.QuestionResolved().InnerTextAsync()).Should().Contain("Blue");

        await page.AssistantText().WaitForTextContainsAsync("blue noted", timeoutMs: 20_000);
        await page.WaitForStreamIdleAsync();

        responder.RemainingTurns["parent"].Should().Be(0);

        await session.SaveSuccessScreenshotAsync(
            "AskUserQuestion.Pending_question_survives_reload_and_can_still_be_answered"
        );
    }
}
