using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// #769 through the real loop and the real Responses agent: the model's encrypted reasoning is saved, goes back
/// before its tool call on the next step, and still does after the conversation is reloaded from the store.
/// </summary>
public sealed class ResponsesReasoningReplayLoopTests
{
    private const string Model = "gpt-5.6-terra";

    private sealed class ScriptedClient(params string[][] turns) : IOpenAiResponsesClient
    {
        private int _call;

        public List<ResponseCreateRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
            ResponseCreateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            foreach (var json in turns[Math.Min(_call++, turns.Length - 1)])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (json == Cut)
                {
                    // The connection drops mid-stream: the one failure the loop retries on its own.
                    throw new HttpIOException(HttpRequestError.ResponseEnded);
                }

                yield return ResponseEventParser.Parse(json);
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }

    private static readonly string[] s_reasonThenCall =
    [
        """{"type":"response.created","response":{"id":"r1"}}""",
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"thinking"}],"encrypted_content":"BLOB"}}""",
        """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"c1","name":"Echo","arguments":"{}"}}""",
        """{"type":"response.completed","response":{"id":"r1"}}""",
    ];

    private const string Cut = "CUT";

    private static string ReasoningDone(string blob) =>
        $$$"""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_{{{blob}}}","summary":[],"encrypted_content":"{{{blob}}}"}}""";

    private static readonly string[] s_answer =
    [
        """{"type":"response.created","response":{"id":"r2"}}""",
        """{"type":"response.output_text.done","output_index":0,"item_id":"m1","content_index":0,"text":"done"}""",
        """{"type":"response.completed","response":{"id":"r2"}}""",
    ];

    private static MultiTurnAgentLoop Loop(ScriptedClient client, IConversationStore store, string threadId)
    {
        var registry = new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = "Echo",
                Description = "Echo",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"))
        );
        return new MultiTurnAgentLoop(
            new OpenAiResponsesAgent("test", client, replayReasoning: true),
            registry,
            threadId,
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = Model,
                ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(
                    "Reasoning",
                    new ResponseReasoningOptions { Summary = "auto" }
                ),
            },
            store: store
        );
    }

    private static async Task RunUntilRequestsAsync(
        MultiTurnAgentLoop loop,
        ScriptedClient client,
        int requests,
        string userText
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = loop.RunAsync(cts.Token);
        var completed = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var message in loop.SubscribeAsync(cts.Token))
            {
                if (message is RunCompletedMessage)
                {
                    _ = Interlocked.Increment(ref completed);
                }
            }
        });

        _ = await loop.SendAsync([new TextMessage { Role = Role.User, Text = userText }], "input-" + requests);
        await Wait.UntilAsync(
            () => Task.FromResult(Volatile.Read(ref completed) > 0 && client.Requests.Count >= requests),
            "the run finished"
        );

        await cts.CancelAsync();
        await reader.ContinueWith(_ => { }, TaskScheduler.Default);
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Fact]
    public async Task Reasoning_replays_before_its_call_and_after_a_reload()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var client = new ScriptedClient(s_reasonThenCall, s_answer, s_answer);

        await using (var loop = Loop(client, store, threadId))
        {
            await RunUntilRequestsAsync(loop, client, 2, "first");
        }

        client
            .Requests[1]
            .Input.Select(i => i.Type)
            .Should()
            .Equal("message", "reasoning", "function_call", "function_call_output");
        client.Requests[1].Input[1].EncryptedContent.Should().Be("BLOB");
        client.Requests[0].Include.Should().Equal("reasoning.encrypted_content");

        await using (var reloaded = Loop(client, store, threadId))
        {
            await RunUntilRequestsAsync(reloaded, client, 3, "second");
        }

        client
            .Requests[2]
            .Input.Select(i => i.Type)
            .Should()
            .Equal("message", "reasoning", "function_call", "function_call_output", "message", "message");
        client.Requests[2].Input[1].EncryptedContent.Should().Be("BLOB");
    }

    /// <summary>
    /// An attempt that saved its reasoning (B1) and then lost its connection is retried, and the retry saves
    /// new reasoning (B2) with its answer. B1 belongs to no output the model ever gave, so it must not be
    /// sent again; B2 must.
    /// </summary>
    [Fact]
    public async Task Reasoning_from_an_abandoned_attempt_is_not_replayed()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var client = new ScriptedClient(
            ["""{"type":"response.created","response":{"id":"r1"}}""", ReasoningDone("B1"), Cut],
            [
                """{"type":"response.created","response":{"id":"r2"}}""",
                ReasoningDone("B2"),
                """{"type":"response.output_text.done","output_index":1,"item_id":"m1","content_index":0,"text":"done"}""",
                """{"type":"response.completed","response":{"id":"r2"}}""",
            ],
            s_answer
        );

        await using (var loop = Loop(client, store, threadId))
        {
            await RunUntilRequestsAsync(loop, client, 2, "first");
        }

        await using (var reloaded = Loop(client, store, threadId))
        {
            await RunUntilRequestsAsync(reloaded, client, 3, "second");
        }

        client
            .Requests[2]
            .Input.Where(i => i.Type == "reasoning")
            .Select(i => i.EncryptedContent)
            .Should()
            .Equal(["B2"], "only the attempt that gave an answer keeps its reasoning");
    }

    /// <summary>
    /// An attempt saves its reasoning (B1) and loses its connection; the retry answers with text and no reasoning.
    /// B1 belongs to no output the model gave, so neither the next request nor one after a reload carries it.
    /// </summary>
    [Fact]
    public async Task Reasoning_from_an_abandoned_attempt_is_not_replayed_when_the_retry_only_answers()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var client = new ScriptedClient(
            ["""{"type":"response.created","response":{"id":"r1"}}""", ReasoningDone("B1"), Cut],
            s_answer,
            s_answer,
            s_answer
        );

        await using (var loop = Loop(client, store, threadId))
        {
            await RunTurnsAsync(loop, client, ("first", 2), ("second", 3));
        }

        await using (var reloaded = Loop(client, store, threadId))
        {
            await RunUntilRequestsAsync(reloaded, client, 4, "third");
        }

        client.Requests[1].Input.Should().NotContain(i => i.Type == "reasoning", "the retry itself never sees B1");
        client.Requests[2].Input.Should().NotContain(i => i.Type == "reasoning", "the live history drops B1");
        client.Requests[3].Input.Should().NotContain(i => i.Type == "reasoning", "the reloaded history drops B1");
        client
            .Requests[3]
            .Input.Where(i => i.Type == "message" && i.Role == "assistant")
            .Should()
            .HaveCount(2, "both answers are still sent");
    }

    private static string[] SummaryBlobAnswer(string id, string text) =>
        [
            $$$"""{"type":"response.created","response":{"id":"{{{id}}}"}}""",
            $$$"""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_{{{id}}}","summary":[{"type":"summary_text","text":"thinking {{{id}}}"}],"encrypted_content":"BLOB-{{{id}}}"}}""",
            $$$"""{"type":"response.output_text.done","output_index":1,"item_id":"m_{{{id}}}","content_index":0,"text":"{{{text}}}"}""",
            $$$"""{"type":"response.completed","response":{"id":"{{{id}}}"}}""",
        ];

    /// <summary>
    /// The identities the browser merges on: across two turns, the summary and the answer streamed live carry the
    /// same generation and order as the rows saved for them, and the blob saved beside them is never streamed.
    /// In this loop the transformation middleware gives each message its own order (summary 0, blob 1, answer 2),
    /// so the blob never shares the summary's order here; that case is pinned in <c>JoinedSubscriptionTests</c>.
    /// </summary>
    [Fact]
    public async Task Live_and_saved_summary_and_answer_share_their_identity_across_two_turns()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var client = new ScriptedClient(SummaryBlobAnswer("r1", "answer one"), SummaryBlobAnswer("r2", "answer two"));
        var published = new List<IMessage>();

        await using (var loop = Loop(client, store, threadId))
        {
            await RunTurnsAsync(loop, client, published, ("first", 1), ("second", 2));
        }

        static (string Kind, string? Text, string? GenerationId, int? Order) Identity(IMessage m) =>
            m switch
            {
                ReasoningMessage r => ("reasoning", r.Reasoning, r.GenerationId, r.MessageOrderIdx),
                TextMessage t => ("text", t.Text, t.GenerationId, t.MessageOrderIdx),
                _ => ("other", null, null, null),
            };

        List<IMessage> live;
        lock (published)
        {
            live = [.. published];
        }

        live.OfType<ReasoningMessage>()
            .Where(r => r.EncryptedOrigin is not null)
            .Should()
            .BeEmpty("the blob is never streamed");
        var liveIds = live.Where(m => m is ReasoningMessage or TextMessage { Role: Role.Assistant })
            .Select(Identity)
            .ToList();

        var rows = await store.LoadMessagesAsync(threadId);
        var saved = MessagePersistenceConverter.FromPersistedMessages(rows);
        saved
            .Count(m => m is ReasoningMessage { EncryptedOrigin: not null })
            .Should()
            .Be(2, "each turn saves its blob");
        var savedIds = saved
            .Where(m => m is ReasoningMessage { EncryptedOrigin: null } or TextMessage { Role: Role.Assistant })
            .Select(Identity)
            .ToList();

        liveIds.Select(i => i.Text).Should().Equal("thinking r1", "answer one", "thinking r2", "answer two");
        savedIds.Should().Equal(liveIds, "a restored row must merge with its live copy, not sit beside it");
    }

    private static Task RunTurnsAsync(
        MultiTurnAgentLoop loop,
        ScriptedClient client,
        params (string UserText, int Requests)[] turns
    ) => RunTurnsAsync(loop, client, null, turns);

    private static async Task RunTurnsAsync(
        MultiTurnAgentLoop loop,
        ScriptedClient client,
        List<IMessage>? published,
        params (string UserText, int Requests)[] turns
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = loop.RunAsync(cts.Token);
        var completed = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var message in loop.SubscribeAsync(cts.Token))
            {
                if (published is not null)
                {
                    lock (published)
                    {
                        published.Add(message);
                    }
                }

                if (message is RunCompletedMessage)
                {
                    _ = Interlocked.Increment(ref completed);
                }
            }
        });

        for (var turn = 0; turn < turns.Length; turn++)
        {
            var (userText, requests) = turns[turn];
            var runsBefore = turn;
            _ = await loop.SendAsync([new TextMessage { Role = Role.User, Text = userText }], "input-" + requests);
            await Wait.UntilAsync(
                () => Task.FromResult(Volatile.Read(ref completed) > runsBefore && client.Requests.Count >= requests),
                "the run finished"
            );
        }

        await cts.CancelAsync();
        await reader.ContinueWith(_ => { }, TaskScheduler.Default);
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
