using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.McpMiddleware;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
///     A tool call over the real streamable-HTTP client, against a handler that speaks just enough MCP and then
///     stalls. The wait bound on each act only turns a hang into a failure; a passing call ends long before it.
/// </summary>
public class McpClientToolCancellationTests
{
    private const string ToolName = "SearchSkills";
    private const string ToolErrorPrefix = "Error executing MCP tool";
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ToolCall_StalledBeforeResponseHeaders_EndsOnTheRunToken()
    {
        var gateway = new ScriptedGateway(StallBeforeHeadersAsync);
        await using var tool = await ConnectAsync(gateway);
        using var run = new CancellationTokenSource();

        var call = tool.Handler("{}", new ToolCallContext(), run.Token);
        await gateway.ToolCallReceived.Task.WaitAsync(HangBound);
        await run.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(HangBound));
    }

    [Fact]
    public async Task ToolCall_StalledAfterResponseHeaders_OutlivesHttpClientTimeout_AndEndsOnTheRunToken()
    {
        var gateway = new ScriptedGateway(StallInEventStreamAsync);
        await using var tool = await ConnectAsync(gateway, TimeSpan.FromSeconds(1));
        using var run = new CancellationTokenSource();

        var call = tool.Handler("{}", new ToolCallContext(), run.Token);
        await gateway.ToolCallReceived.Task.WaitAsync(HangBound);

        // HttpClient.Timeout stops counting once headers arrive, so this window is past it with the call still open.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(call.IsCompleted, "only the run's token should end a stall after the response headers");

        await run.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(HangBound));
    }

    [Fact]
    public async Task ToolCall_HttpClientTimeoutWithoutAStop_StaysAToolErrorForTheModel()
    {
        var gateway = new ScriptedGateway(StallBeforeHeadersAsync);
        await using var tool = await ConnectAsync(gateway, TimeSpan.FromSeconds(1));

        var result = await tool.Handler("{}", new ToolCallContext(), CancellationToken.None).WaitAsync(HangBound);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        Assert.StartsWith(ToolErrorPrefix, resolved.Payload.Text);
    }

    [Fact]
    public async Task ToolCall_Completed_KeepsItsTextAndImageBlocks()
    {
        var gateway = new ScriptedGateway(
            (id, _) =>
                Task.FromResult(
                    JsonResult(
                        id,
                        """{"content":[{"type":"text","text":"found"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}"""
                    )
                )
        );
        await using var tool = await ConnectAsync(gateway);
        using var run = new CancellationTokenSource();

        var result = await tool.Handler("{}", new ToolCallContext(), run.Token).WaitAsync(HangBound);

        var payload = Assert.IsType<ToolHandlerResult.Resolved>(result).Payload;
        Assert.Equal("found", payload.Text);
        var image = Assert.IsType<ImageToolResultBlock>(Assert.Single(payload.ContentBlocks!));
        Assert.Equal("iVBORw0KGgo=", image.Data);
    }

    [Fact]
    public async Task StopDuringAStalledToolCall_EndsTheRun_RecordsNoToolError_AndAllowsARestart()
    {
        var gateway = new ScriptedGateway(StallBeforeHeadersAsync);
        await using var tool = await ConnectAsync(gateway);
        var registry = new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = ToolName,
                Description = "Stalls",
                Parameters = [],
            },
            tool.Handler
        );
        using var bound = new CancellationTokenSource(HangBound);
        await using var loop = new MultiTurnAgentLoop(new CallsTheToolAgent(), registry, "mcp-stalled-stop");

        var firstRun = loop.RunAsync(bound.Token);
        await loop.WaitUntilReadyAsync(bound.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "search", Role = Role.User }], inputId: "input-1");
        await gateway.ToolCallReceived.Task.WaitAsync(bound.Token);

        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await firstRun.WaitAsync(bound.Token);

        Assert.DoesNotContain(
            loop.GetHistorySnapshot().OfType<ToolCallResultMessage>(),
            message => message.Result.Contains(ToolErrorPrefix, StringComparison.Ordinal)
        );

        var secondRun = loop.RunAsync(bound.Token);
        await loop.WaitUntilReadyAsync(bound.Token);
        Assert.True(loop.IsRunning);
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await secondRun.WaitAsync(bound.Token);
    }

    private static async Task<HttpResponseMessage> StallBeforeHeadersAsync(string id, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("An infinite delay only ends by cancellation.");
    }

    private static Task<HttpResponseMessage> StallInEventStreamAsync(string id, CancellationToken ct)
    {
        // A pipe nobody writes to: the body is open and silent, as a gateway stuck after flushing headers would be.
        var content = new StreamContent(new Pipe().Reader.AsStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static HttpResponseMessage JsonResult(string id, string resultJson) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{resultJson}}}""",
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static async Task<ConnectedTool> ConnectAsync(ScriptedGateway gateway, TimeSpan? httpTimeout = null)
    {
        var http = new HttpClient(gateway);
        if (httpTimeout is { } timeout)
        {
            http.Timeout = timeout;
        }

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Name = "gateway", Endpoint = new Uri("http://gateway.test/mcp") },
                http,
                loggerFactory: null,
                ownsHttpClient: true
            )
        );
        var provider = await McpClientFunctionProvider.CreateAsync(client, "gateway");
        return new ConnectedTool(client, Assert.Single(provider.GetFunctions()).Handler);
    }

    private sealed record ConnectedTool(McpClient Client, ToolHandler Handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class ScriptedGateway(Func<string, CancellationToken, Task<HttpResponseMessage>> onToolCall)
        : HttpMessageHandler
    {
        public TaskCompletionSource ToolCallReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (!root.TryGetProperty("id", out var idElement))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var id = idElement.GetRawText();
            switch (root.GetProperty("method").GetString())
            {
                case "initialize":
                    var version = root.GetProperty("params").GetProperty("protocolVersion").GetString();
                    return JsonResult(
                        id,
                        $$$"""{"protocolVersion":"{{{version}}}","capabilities":{"tools":{}},"serverInfo":{"name":"gateway","version":"1"}}"""
                    );
                case "tools/list":
                    return JsonResult(
                        id,
                        $$$"""{"tools":[{"name":"{{{ToolName}}}","description":"Stalls","inputSchema":{"type":"object"}}]}"""
                    );
                case "tools/call":
                    ToolCallReceived.TrySetResult();
                    return await onToolCall(id, cancellationToken);
                default:
                    throw new InvalidOperationException($"Unscripted method: {root.GetProperty("method")}");
            }
        }
    }

    /// <summary>Asks for the tool on its first generation and answers in text after that.</summary>
    private sealed class CallsTheToolAgent : IStreamingAgent
    {
        private int _generations;

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            IMessage reply =
                Interlocked.Increment(ref _generations) == 1
                    ? new ToolCallMessage
                    {
                        FunctionName = ToolName,
                        FunctionArgs = "{}",
                        ToolCallId = "call-1",
                        Role = Role.Assistant,
                    }
                    : new TextMessage { Text = "answer", Role = Role.Assistant };
            return Task.FromResult(Single(reply, cancellationToken));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The loop streams.");

        private static async IAsyncEnumerable<IMessage> Single(
            IMessage message,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
