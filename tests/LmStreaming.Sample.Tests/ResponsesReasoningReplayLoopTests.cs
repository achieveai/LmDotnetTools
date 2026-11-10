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
}
