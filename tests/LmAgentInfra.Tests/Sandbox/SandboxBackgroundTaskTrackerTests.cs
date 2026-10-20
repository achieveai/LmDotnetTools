using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// A primary agent's background task is remembered against the conversation whose tool call started it, so
/// the gateway's <c>task_completed</c> (which names no agent for a primary) can be told to that conversation.
/// </summary>
public class SandboxBackgroundTaskTrackerTests
{
    private const string BackgroundArgs = """{"command":"sleep 30","run_in_background":true}""";

    [Fact]
    public async Task Wrap_PrimaryStartsABackgroundTask_RecordsItsConversation_AndPassesTheResultThrough()
    {
        await using var registry = CreateRegistry();
        var handler = SandboxBackgroundTaskTracker.Wrap(
            Returning("Task started with ID: 12"),
            registry,
            "sess-1",
            "thread-a"
        );

        var result = await handler(BackgroundArgs, new ToolCallContext(), CancellationToken.None);

        result
            .Should()
            .BeOfType<ToolHandlerResult.Resolved>()
            .Which.Payload.Text.Should()
            .Be("Task started with ID: 12");
        registry.TryTakeBackgroundTaskOwner("sess-1", "12", out var owner).Should().BeTrue();
        owner.Should().Be("thread-a");
        registry.TryTakeBackgroundTaskOwner("sess-1", "12", out _).Should().BeFalse("an ended task is taken once");
    }

    [Fact]
    public async Task Wrap_SubAgentStartsABackgroundTask_RecordsNothing()
    {
        // A sub-agent's gateway id already carries its conversation's tag, so it is routed without a record.
        await using var registry = CreateRegistry();
        var handler = SandboxBackgroundTaskTracker.Wrap(
            Returning("Task started with ID: 3"),
            registry,
            "sess-1",
            "thread-a"
        );

        using (AgentActorScope.Begin("worker"))
        {
            _ = await handler(BackgroundArgs, new ToolCallContext(), CancellationToken.None);
        }

        registry.TryTakeBackgroundTaskOwner("sess-1", "3", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"command":"echo 'Task started with ID: 7'"}""")]
    [InlineData("""{"command":"echo 'Task started with ID: 7'","run_in_background":false}""")]
    [InlineData("""{"command":"echo 'Task started with ID: 7'","run_in_background":"true"}""")]
    [InlineData("not json")]
    public async Task Wrap_ForegroundOutputThatPrintsTheStartText_RecordsNothing(string argsJson)
    {
        // A foreground command's stdout comes back raw, so it can print exactly what a background start returns.
        await using var registry = CreateRegistry();
        var handler = SandboxBackgroundTaskTracker.Wrap(
            Returning("Task started with ID: 7"),
            registry,
            "sess-1",
            "thread-b"
        );

        _ = await handler(argsJson, new ToolCallContext(), CancellationToken.None);

        registry.TryTakeBackgroundTaskOwner("sess-1", "7", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Wrap_ALaterResultForTheSameTaskId_DoesNotTakeOverItsOwner()
    {
        // Owner A records task 7; foreground output in B (same session, same text) leaves 7 owned by A.
        await using var registry = CreateRegistry();
        _ = await SandboxBackgroundTaskTracker.Wrap(
            Returning("Task started with ID: 7"),
            registry,
            "sess-1",
            "thread-a"
        )(BackgroundArgs, new ToolCallContext(), CancellationToken.None);

        _ = await SandboxBackgroundTaskTracker.Wrap(
            Returning("Task started with ID: 7"),
            registry,
            "sess-1",
            "thread-b"
        )("""{"command":"echo 'Task started with ID: 7'"}""", new ToolCallContext(), CancellationToken.None);
        registry.RecordBackgroundTaskOwner("sess-1", "7", "thread-c");

        registry.TryTakeBackgroundTaskOwner("sess-1", "7", out var owner).Should().BeTrue();
        owner.Should().Be("thread-a", "the conversation that started a task keeps it");
    }

    [Theory]
    [InlineData("Task started with ID: 7", true, "7")]
    [InlineData("Task started with ID:  42 \n", true, "42")]
    [InlineData("hello\nTask started with ID: 7", false, "")]
    [InlineData("Task started with ID: abc", false, "")]
    [InlineData("Task started with ID: ", false, "")]
    [InlineData(null, false, "")]
    public void TryReadTaskId_AcceptsOnlyTheGatewaysBackgroundResult(string? text, bool expected, string expectedId)
    {
        SandboxBackgroundTaskTracker.TryReadTaskId(text, out var taskId).Should().Be(expected);
        taskId.Should().Be(expectedId);
    }

    private static ToolHandler Returning(string text) =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(text));

    private static SandboxSessionRegistry CreateRegistry()
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new Ok())
        );
        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new Ok()),
            new AuthOptions { Webhook = { UseSandboxWebhook = true } },
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
