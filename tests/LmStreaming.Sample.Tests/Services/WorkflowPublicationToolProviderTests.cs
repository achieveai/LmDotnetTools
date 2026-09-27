using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public sealed class WorkflowPublicationToolProviderTests
{
    [Fact]
    public async Task Sends_bound_identity_and_stable_action_with_server_side_auth()
    {
        string? body = null;
        string? authorization = null;
        using var handler = new CallbackHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            authorization = request.Headers.Authorization!.ToString();
            request.RequestUri!.AbsolutePath.Should().Be("/api/workflow/publication");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"Status\":\"Posted\",\"ReceiptId\":12,\"ProviderCommentId\":\"comment-1\"}"
                ),
            };
        });
        using var client = new HttpClient(handler);
        var provider = Create(client);
        var function = provider.GetFunctions().Single(f => f.Contract.Name == "review_publish_summary");
        var result = await function.Handler(
            "{\"actionId\":\"summary-1\",\"body\":\"Review text\"}",
            new ToolCallContext { ToolCallId = "call-1" },
            default
        );
        authorization.Should().Be("Bearer test-secret");
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        root.GetProperty("threadId").GetString().Should().Be("thread-parent");
        root.GetProperty("runId").GetString().Should().Be("run-active");
        root.GetProperty("toolCallId").GetString().Should().Be("call-1");
        root.GetProperty("args").GetProperty("actionId").GetString().Should().Be("summary-1");
        result.ResultText.Should().Contain("comment-1");
        body.Should().NotContain("test-secret");
    }

    [Theory]
    [InlineData("{\"actionId\":\"one\",\"body\":\"text\",\"runId\":\"forged\"}")]
    [InlineData("{\"body\":\"text\"}")]
    [InlineData("{\"actionId\":\"one\",\"actionId\":\"two\",\"body\":\"text\"}")]
    public async Task Invalid_or_scope_injecting_arguments_never_send(string args)
    {
        using var handler = new CallbackHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var result = await Create(client)
            .GetFunctions()
            .First()
            .Handler(args, new ToolCallContext { ToolCallId = "call" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.IsError.Should().BeTrue();
        handler.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ambiguous_callback_is_unknown_and_never_automatically_retried(bool throws)
    {
        using var handler = new CallbackHandler(_ =>
            throws
                ? throw new HttpRequestException("disconnected")
                : Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("unavailable"),
                    }
                )
        );
        using var client = new HttpClient(handler);
        var result = await Create(client)
            .GetFunctions()
            .First()
            .Handler("{\"actionId\":\"one\",\"body\":\"text\"}", new ToolCallContext { ToolCallId = "call" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.ErrorCode.Should().Be("publication_unknown");
        handler.Count.Should().Be(1);
    }

    [Fact]
    public void Registration_excludes_all_publication_tools_from_children()
    {
        using var client = new HttpClient();
        var registry = new FunctionRegistry();
        var options = Program.RegisterWorkflowPublicationTools(
            registry,
            new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() },
            Create(client)
        );
        registry
            .BuildContracts()
            .Select(c => c.Name)
            .Should()
            .BeEquivalentTo(WorkflowPublicationToolProvider.ToolNames);
        options!.NonInheritedToolNames.Should().Contain(WorkflowPublicationToolProvider.ToolNames);
        options.ChildToolProviderFactory.Should().BeNull();
    }

    [Fact]
    public void Configuration_requires_both_fixed_endpoint_and_secret()
    {
        new WorkflowPublicationOptions().IsConfigured.Should().BeFalse();
        new WorkflowPublicationOptions { CallbackUrl = "https://daemon/api/workflow/publication" }
            .IsConfigured.Should()
            .BeFalse();
        Options().IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"error\":\"publication_unknown\"}")]
    [InlineData("{\"Status\":\"Posted\",\"ReceiptId\":0}")]
    public async Task Missing_or_unknown_receipt_is_an_error(string response)
    {
        using var handler = new CallbackHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) })
        );
        using var client = new HttpClient(handler);
        var result = await Create(client)
            .GetFunctions()
            .First()
            .Handler("{\"actionId\":\"one\",\"body\":\"text\"}", new ToolCallContext { ToolCallId = "call" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.ErrorCode.Should().Be("publication_unknown");
    }

    [Theory]
    [InlineData(
        "review_publish_inline",
        "{\"actionId\":\"one\",\"body\":\"text\",\"path\":\"file.cs\",\"line\":\"3\",\"side\":\"RIGHT\"}"
    )]
    [InlineData("review_reply", "{\"actionId\":\"one\",\"body\":\"text\",\"providerThreadId\":\"thread\"}")]
    public async Task Invalid_target_shape_never_sends(string tool, string arguments)
    {
        using var handler = new CallbackHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var result = await Create(client)
            .GetFunctions()
            .Single(f => f.Contract.Name == tool)
            .Handler(arguments, new ToolCallContext { ToolCallId = "call" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.IsError.Should().BeTrue();
        handler.Count.Should().Be(0);
    }

    [Fact]
    public async Task Absent_active_run_never_sends()
    {
        using var handler = new CallbackHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var provider = new WorkflowPublicationToolProvider(client, Options(), "thread", () => null);
        var result = await provider
            .GetFunctions()
            .First()
            .Handler("{\"actionId\":\"one\",\"body\":\"text\"}", new ToolCallContext { ToolCallId = "call" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.ErrorCode.Should().Be("publication_denied");
        handler.Count.Should().Be(0);
    }

    private static WorkflowPublicationOptions Options() =>
        new() { CallbackUrl = "https://daemon/api/workflow/publication", SharedSecret = "test-secret" };

    private static WorkflowPublicationToolProvider Create(HttpClient client) =>
        new(client, Options(), "thread-parent", () => "run-active");

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Count++;
            return callback(request);
        }
    }
}
