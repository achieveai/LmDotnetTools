using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="S2SReviewAgent"/> — the <c>IMultiTurnAgent</c> adapter that drives one review
/// turn against a running LmStreaming review host over the S2S REST API. Both tests construct the agent
/// directly (the factory does not forward poll intervals) with millisecond polling so the scripted
/// InProgress→terminal sequence resolves instantly. They pin the two facts the executor depends on: the
/// single finalized review <see cref="TextMessage"/> carries <c>status.Response</c> verbatim, and
/// <see cref="S2SReviewAgent.ThreadId"/> / <see cref="S2SReviewAgent.CurrentRunId"/> surface the
/// server-minted ids (the deep-link target) even when the run ends without review text.
/// </summary>
public sealed class S2SReviewAgentTests
{
    private static S2SReviewAgent NewAgent(LmStreamingS2SClient client, string? title) =>
        new(
            client,
            workspaceId: "ws-1",
            providerId: "openai",
            modeId: "workspace-agent",
            title: title,
            logger: NullLogger<S2SReviewAgent>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            pollMaxInterval: TimeSpan.FromMilliseconds(1),
            overallTimeout: TimeSpan.FromSeconds(5));

    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    private static async Task<List<IMessage>> DriveAsync(S2SReviewAgent agent, string userText)
    {
        var collected = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = userText, Role = Role.User }]);
        await foreach (var message in agent.ExecuteRunAsync(input, CancellationToken.None))
        {
            collected.Add(message);
        }

        return collected;
    }

    [Fact]
    public async Task ExecuteRunAsync_provisions_polls_to_completion_and_yields_the_review_text_verbatim()
    {
        // Route order matters: the messages POST url (api/conversations/{id}/messages) is a superset of the
        // provision POST url (api/conversations), and the handler is first-match-wins — so the more specific
        // "/messages" POST must be registered BEFORE the "api/conversations" provision POST.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-9\"}"),
                (HttpStatusCode.OK, "{\"status\":\"Completed\",\"runId\":\"run-9\",\"response\":{\"text\":\"LGTM, ship it.\"}}"))
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-xyz\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: "Review PR #118");

        var messages = await DriveAsync(agent, "review this PR");

        var text = messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>().Subject;
        text.Text.Should().Be("LGTM, ship it.");
        text.Role.Should().Be(Role.Assistant);
        text.IsThinking.Should().BeFalse("the review text is a finalized assistant message, not a thinking trace");
        agent.ThreadId.Should().Be("thread-xyz", "ThreadId surfaces the minted conversation id — the deep-link target");
        agent.CurrentRunId.Should().Be("run-9");
    }

    [Fact]
    public async Task ExecuteRunAsync_yields_no_message_but_still_surfaces_the_ids_when_the_run_ends_errored()
    {
        // A non-Completed terminal (Errored) with no resolved response text yields nothing, but the minted
        // thread/run ids must still surface so the executor can deep-link the (failed) hosted conversation.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Errored\",\"runId\":\"run-err\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-err\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        // title null → EnsureProvisioned skips the metadata call, so no /metadata route is needed.
        var agent = NewAgent(client, title: null);

        var messages = await DriveAsync(agent, "review this PR");

        messages.Should().BeEmpty("an errored run with no response text produces no review message");
        agent.ThreadId.Should().Be("thread-err");
        agent.CurrentRunId.Should().Be("run-err");
    }
}
