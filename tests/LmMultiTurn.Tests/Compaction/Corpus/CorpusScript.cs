using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>
/// One scripted provider reply (#686, spec 679 §12.4). Declarative so a scenario's provider behaviour is
/// part of its fingerprint: an edited reply table changes the fingerprint, and the corpus refuses to run
/// against a manifest that no longer matches.
/// </summary>
public sealed record ScriptedReply
{
    /// <summary>text | tool | fail | interrupted | block.</summary>
    public required string Kind { get; init; }

    public string? Text { get; init; }

    public string? Tool { get; init; }

    public string? Args { get; init; }

    public string? ToolCallId { get; init; }

    /// <summary>For <c>block</c>: the gate the reply waits on before answering with <see cref="Text"/>.</summary>
    public string? Gate { get; init; }

    public static ScriptedReply Say(string text) => new() { Kind = "text", Text = text };

    public static ScriptedReply Call(string tool, string toolCallId, string args = "{}") =>
        new()
        {
            Kind = "tool",
            Tool = tool,
            ToolCallId = toolCallId,
            Args = args,
        };

    /// <summary>A non-retryable provider failure: the run ends in error.</summary>
    public static ScriptedReply Fail(string message) => new() { Kind = "fail", Text = message };

    /// <summary>The stream ends prematurely (transport interruption); the loop retries with a continuation.</summary>
    public static ScriptedReply Interrupted() => new() { Kind = "interrupted" };

    /// <summary>Blocks until <paramref name="gate"/> is released, then answers with <paramref name="text"/>.</summary>
    public static ScriptedReply Block(string gate, string text) =>
        new()
        {
            Kind = "block",
            Gate = gate,
            Text = text,
        };
}

/// <summary>A reply table indexed by provider call number, with a default for calls past its end.</summary>
public sealed record CorpusScript
{
    public IReadOnlyList<ScriptedReply> Replies { get; init; } = [];

    public ScriptedReply Default { get; init; } = ScriptedReply.Say("done");

    public ScriptedReply For(int call) => call <= Replies.Count ? Replies[call - 1] : Default;

    /// <summary><paramref name="toolCalls"/> consecutive <c>Echo</c> calls with ids <c>{idPrefix}-{i}</c>.</summary>
    public static List<ScriptedReply> Echoes(int toolCalls, string idPrefix = "tc") =>
        [.. Enumerable.Range(1, toolCalls).Select(i => ScriptedReply.Call("Echo", $"{idPrefix}-{i}"))];

    /// <summary>Calls <c>Echo</c> for <paramref name="toolCalls"/> turns, then answers with <paramref name="done"/>.</summary>
    public static CorpusScript EchoThenDone(int toolCalls, string done = "done", string idPrefix = "tc") =>
        new() { Replies = Echoes(toolCalls, idPrefix), Default = ScriptedReply.Say(done) };
}

/// <summary>Named gates a <c>block</c> reply waits on; the runner releases them from a scenario step.</summary>
internal sealed class CorpusGates
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _gates = new(StringComparer.Ordinal);

    private TaskCompletionSource<bool> Get(string name) =>
        _gates.GetOrAdd(name, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task WaitAsync(string name) => Get(name).Task;

    public void Release(string name) => Get(name).TrySetResult(true);
}

/// <summary>What one provider call looked like from the provider's side.</summary>
public sealed record ProviderCall(int Index, long RequestTokens, long CachedTokens, bool Overflowed, int Rows);

/// <summary>
/// The corpus's mock model (D1): interprets a <see cref="CorpusScript"/>, reports deterministic usage
/// (prompt = the estimate of the request it received, cached = the token length of the longest common
/// prefix with the previous request it served - a prompt-cache simulator, D4) and refuses a request larger
/// than the window with the same 400 a real provider returns. No live model is ever called.
/// </summary>
internal sealed class ScriptedProvider(string label, CorpusScript script, CorpusGates gates, long? windowTokens)
    : IStreamingAgent
{
    private const int CompletionTokens = 20;
    private IReadOnlyList<string>? _previousKeys;

    public string Label { get; } = label;

    public List<IReadOnlyList<IMessage>> Requests { get; } = [];

    public List<ProviderCall> Calls { get; } = [];

    /// <summary>The tool names offered on each call, in call order.</summary>
    public List<IReadOnlyList<string>> FunctionNames { get; } = [];

    public int CallCount => Requests.Count;

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = messages.ToList();
        var expanded = Expand(request);
        var keys = expanded.Select(Key).ToList();
        var tokens = CompactionRuntime.EstimateTokens(expanded);
        var cached = CachedPrefixTokens(expanded, keys);
        _previousKeys = keys;
        var overflowed = windowTokens is { } window && tokens > window;

        Requests.Add(request);
        FunctionNames.Add([.. (options?.Functions ?? []).Select(f => f.Name)]);
        Calls.Add(new ProviderCall(Requests.Count, tokens, cached, overflowed, expanded.Count));

        var usage = new UsageMessage
        {
            Usage = new Usage
            {
                PromptTokens = (int)tokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = (int)tokens + CompletionTokens,
                InputTokenDetails = new InputTokenDetails { CachedTokens = (int)cached },
            },
        }.WithIds(options);

        var reply = script.For(Requests.Count);
        return Task.FromResult(Stream(reply, usage, overflowed, options, cancellationToken));
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("the corpus provider streams");

    private async IAsyncEnumerable<IMessage> Stream(
        ScriptedReply reply,
        IMessage usage,
        bool overflowed,
        GenerateReplyOptions? options,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (overflowed)
        {
            throw new HttpRequestException(
                "prompt is too long: context_length_exceeded",
                inner: null,
                HttpStatusCode.BadRequest
            );
        }

        switch (reply.Kind)
        {
            case "fail":
                throw new InvalidOperationException(reply.Text ?? "provider failed");
            case "interrupted":
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            case "block":
                await gates.WaitAsync(reply.Gate!).WaitAsync(ct);
                yield return usage;
                yield return new TextMessage { Text = reply.Text ?? "done", Role = Role.Assistant }.WithIds(options);
                yield break;
            case "tool":
                yield return usage;
                yield return new ToolCallMessage
                {
                    ToolCallId = reply.ToolCallId!,
                    FunctionName = reply.Tool!,
                    FunctionArgs = reply.Args ?? "{}",
                    Role = Role.Assistant,
                }.WithIds(options);
                yield break;
            default:
                yield return usage;
                yield return new TextMessage { Text = reply.Text ?? "done", Role = Role.Assistant }.WithIds(options);
                yield break;
        }
    }

    private long CachedPrefixTokens(IReadOnlyList<IMessage> expanded, IReadOnlyList<string> keys)
    {
        if (_previousKeys is null)
        {
            return 0;
        }

        var shared = 0;
        while (
            shared < keys.Count
            && shared < _previousKeys.Count
            && string.Equals(keys[shared], _previousKeys[shared], StringComparison.Ordinal)
        )
        {
            shared++;
        }

        return shared == 0 ? 0 : CompactionRuntime.EstimateTokens([.. expanded.Take(shared)]);
    }

    /// <summary>
    /// The provider sees tool turns joined into <see cref="ToolsCallAggregateMessage"/> and
    /// <see cref="CompositeMessage"/> by the loop's middleware; the estimator measures the split rows the
    /// history holds, so expand them first (a composite counts as 12 tokens unexpanded, whatever it holds).
    /// </summary>
    public static List<IMessage> Expand(IReadOnlyList<IMessage> request) => [.. request.SelectMany(Flatten)];

    private static IEnumerable<IMessage> Flatten(IMessage message) =>
        message switch
        {
            ToolsCallAggregateMessage agg => [agg.ToolsCallMessage, agg.ToolsCallResult],
            CompositeMessage composite => composite.Messages.SelectMany(Flatten),
            _ => [message],
        };

    private static string Key(IMessage message) =>
        message switch
        {
            TextMessage t => $"text:{t.Role}:{t.Text}",
            ToolCallMessage c => $"call:{c.ToolCallId}:{c.FunctionArgs}",
            ToolCallResultMessage r => $"result:{r.ToolCallId}:{r.Result}",
            ToolsCallMessage tc => "calls:"
                + string.Join("|", tc.ToolCalls.Select(c => $"{c.ToolCallId}:{c.FunctionArgs}")),
            ToolsCallResultMessage tr => "results:"
                + string.Join("|", tr.ToolCallResults.Select(r => $"{r.ToolCallId}:{r.Result}")),
            CompactionCheckpointMessage cp => $"checkpoint:{cp.CheckpointId}",
            ICanGetText text => $"{message.GetType().Name}:{text.GetText()}",
            _ => message.GetType().Name,
        };
}
