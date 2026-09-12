using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

public sealed class WorkflowPublicationRoutingTests
{
    [Theory]
    [InlineData("code-review-daemon", "http://localhost:5080/api/workflow/publication", "primary-secret")]
    [InlineData("codereview-daemon-mcqdb", "http://localhost:5082/api/workflow/publication", "mcqdb-secret")]
    public async Task Frozen_caller_selects_only_its_configured_callback(
        string appId,
        string expectedUrl,
        string expectedSecret
    )
    {
        var configuration = Configured();
        configuration.Validate();
        using var handler = new CallbackHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be(expectedUrl);
            request.Headers.Authorization!.Parameter.Should().Be(expectedSecret);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"Status\":\"Posted\",\"ReceiptId\":1}") };
        });
        using var client = new HttpClient(handler);
        var provider = new WorkflowPublicationToolProvider(
            client,
            configuration.Resolve(appId)!,
            "parent",
            () => "run"
        );
        var result = await provider
            .GetFunctions()
            .First()
            .Handler("{\"actionId\":\"a\",\"body\":\"review\"}", new ToolCallContext { ToolCallId = "tool" }, default);
        Assert.IsType<ToolHandlerResult.Resolved>(result).Payload.IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Configured_map_refuses_unknown_callers_even_with_legacy_fallback(string? appId) =>
        Configured().Resolve(appId).Should().BeNull();

    [Fact]
    public void Explicit_single_callback_remains_compatible()
    {
        var options = new WorkflowPublicationOptions
        {
            CallbackUrl = "https://daemon/publication",
            SharedSecret = "secret",
        };
        options.Validate();
        options.Resolve(null).Should().BeSameAs(options);
    }

    [Fact]
    public void Invalid_mapped_endpoint_refuses_startup()
    {
        var options = new WorkflowPublicationOptions
        {
            Callbacks = new() { ["app"] = new() { CallbackUrl = "https://daemon/publication" } },
        };
        options.Invoking(value => value.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Deployment_configuration_binds_callback_endpoints()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WorkflowPublication:Callbacks:codereview-daemon-mcqdb:CallbackUrl"] =
                        "http://localhost:5082/api/workflow/publication",
                    ["WorkflowPublication:Callbacks:codereview-daemon-mcqdb:SharedSecret"] = "private-secret",
                }
            )
            .Build();
        var options = configuration.GetSection("WorkflowPublication").Get<WorkflowPublicationOptions>()!;
        options.Validate();
        options
            .Resolve("codereview-daemon-mcqdb")!
            .CallbackUrl.Should()
            .Be("http://localhost:5082/api/workflow/publication");
        options.Resolve("code-review-daemon").Should().BeNull();
    }

    private static WorkflowPublicationOptions Configured() =>
        new()
        {
            CallbackUrl = "http://fallback/publication",
            SharedSecret = "must-not-use",
            Callbacks = new(StringComparer.Ordinal)
            {
                ["code-review-daemon"] = new()
                {
                    CallbackUrl = "http://localhost:5080/api/workflow/publication",
                    SharedSecret = "primary-secret",
                },
                ["codereview-daemon-mcqdb"] = new()
                {
                    CallbackUrl = "http://localhost:5082/api/workflow/publication",
                    SharedSecret = "mcqdb-secret",
                },
            },
        };

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
