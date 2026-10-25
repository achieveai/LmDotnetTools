using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Read-only egress is a contract, not a hint: a host that predates it ignores the field and would give the
/// thread full egress, so the client deletes an unacknowledged thread and stops.
/// </summary>
public sealed class LmStreamingS2SClientReadOnlyEgressTests
{
    [Fact]
    public async Task Read_only_egress_is_sent_and_an_acknowledged_thread_is_returned()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            """{"threadId":"thread-ro","readOnlyEgressAccepted":true}"""
        );

        var thread = await ProvisionReadOnlyAsync(handler);

        thread.Should().Be("thread-ro");
        handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("\"readOnlyEgress\":true");
    }

    [Fact]
    public async Task A_host_that_does_not_acknowledge_read_only_egress_gets_the_thread_deleted_and_a_contract_error()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "api/conversations", """{"threadId":"thread-old"}""")
            .OnJson(HttpMethod.Delete, "api/conversations/thread-old", "{}");

        var provision = () => ProvisionReadOnlyAsync(handler);

        _ = await provision.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*read-only egress*");
        handler
            .Requests.Should()
            .ContainSingle(request => request.Method == HttpMethod.Delete)
            .Which.Uri.AbsolutePath.Should()
            .Be("/api/conversations/thread-old");
    }

    [Fact]
    public async Task A_host_that_acknowledges_effort_but_not_read_only_egress_gets_the_thread_deleted_and_a_contract_error()
    {
        // Mid-rollout: a host new enough to acknowledge reasoning effort, but older than read-only egress.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Post,
                "api/conversations",
                """{"threadId":"thread-old","reasoningEffortAccepted":true}"""
            )
            .OnJson(HttpMethod.Delete, "api/conversations/thread-old", "{}");

        var provision = () => ProvisionReadOnlyAsync(handler, reasoningEffort: "high");

        _ = await provision.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*read-only egress*");
        handler
            .Requests.Should()
            .ContainSingle(request => request.Method == HttpMethod.Delete)
            .Which.Uri.AbsolutePath.Should()
            .Be("/api/conversations/thread-old");
    }

    private static async Task<string> ProvisionReadOnlyAsync(
        FakeHttpMessageHandler handler,
        string? reasoningEffort = null
    )
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return await new LmStreamingS2SClient(http, null, null, null).ProvisionAsync(
            "ws-1",
            "test",
            "workspace-agent",
            null,
            null,
            reasoningEffort,
            workingDirectory: null,
            disableClientTools: true,
            readOnlyEgress: true,
            CancellationToken.None
        );
    }
}
