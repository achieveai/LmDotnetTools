using System.Net;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public class WorkflowPublicationGatewayTests
{
    [Theory]
    [InlineData(false, false, false, "host-run", true)]
    [InlineData(true, false, false, "host-run", false)]
    [InlineData(false, true, false, "host-run", false)]
    [InlineData(false, false, true, "host-run", false)]
    [InlineData(false, false, false, "other-run", false)]
    [InlineData(false, false, false, "host-run", false, -5)]
    [InlineData(false, false, false, "host-run", false, 0)]
    public async Task ScopeRequiresAuthoredGrantActiveSnapshotAndExactHostedRun(
        bool correction,
        bool noGrant,
        bool complete,
        string hostedRun,
        bool expected,
        int deadlineMinutes = 5
    )
    {
        var deadline = deadlineMinutes == 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddMinutes(deadlineMinutes);
        var snapshots = new InMemoryWorkflowStore();
        await snapshots.SaveAsync(
            "instance",
            new WorkflowInstanceSnapshot
            {
                InstanceId = "instance",
                IsComplete = complete,
                CurrentNodeId = "publish",
                Deadlines = deadline is { } saved
                    ? new Dictionary<string, DateTimeOffset> { ["publish:1:publish"] = saved }
                    : new Dictionary<string, DateTimeOffset>(),
                Sessions = new Dictionary<string, string> { ["parent"] = "parent-session" },
                Tasks =
                [
                    new WorkflowTaskSnapshot
                    {
                        Name = "publish:1:publish",
                        NodeId = "publish",
                        TaskId = "publish",
                        Visit = 1,
                        Status = WorkflowTaskStatus.InFlight,
                        ToolCallId = "invocation",
                    },
                ],
            }
        );
        using var http = new HttpClient(new StatusHandler(hostedRun)) { BaseAddress = new Uri("https://review-host/") };
        var scopes = new WorkflowPublicationScopes(snapshots, new LmStreamingS2SClient(http, null, null, null));
        var tools = new ReviewPublicationTools(null!, null!, "instance", null!, null!, null!, false, () => true);
        scopes.Register(
            "thread",
            new WorkflowInvocation
            {
                InstanceId = "instance",
                InvocationId = "invocation",
                UnitName = "publish:1:publish",
                SessionId = "parent-session",
                DeadlineUtc = deadline,
                IsCorrection = correction,
                Input = new System.Text.Json.Nodes.JsonObject(),
                Task = new WorkflowTask
                {
                    Id = "publish",
                    PromptTemplate = "publish",
                    Tools = noGrant ? [] : ["review_publish_summary"],
                },
            },
            tools
        );
        var result = await scopes.ResolveAsync(
            new WorkflowPublicationRequest("thread", "host-run", "tool-call", "review_publish_summary", []),
            default
        );
        if (expected)
            result.Should().BeSameAs(tools);
        else
            result.Should().BeNull();
    }

    private sealed class StatusHandler(string runId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(new { status = "InProgress", runId })
                    ),
                }
            );
    }

    [Fact]
    public void CallbackRequiresConfiguredHostCredential()
    {
        var gateway = new WorkflowPublicationGateway(
            "host-secret",
            (_, _) => Task.FromResult<ReviewPublicationTools?>(null)
        );
        gateway.Authenticate("Bearer host-secret").Should().BeTrue();
        gateway.Authenticate("Bearer other").Should().BeFalse();
        gateway.Authenticate("host-secret").Should().BeFalse();
        new WorkflowPublicationGateway("", (_, _) => Task.FromResult<ReviewPublicationTools?>(null))
            .Authenticate("Bearer ")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task UnknownOrExpiredHostedInvocationHasNoPublicationCapability()
    {
        var gateway = new WorkflowPublicationGateway(
            "host-secret",
            (_, _) => Task.FromResult<ReviewPublicationTools?>(null)
        );
        await gateway
            .Invoking(g =>
                g.InvokeAsync(
                    new WorkflowPublicationRequest(
                        "thread",
                        "run",
                        "tool",
                        "review_publish_summary",
                        new() { ["actionId"] = "one", ["body"] = "body" }
                    ),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }
}
