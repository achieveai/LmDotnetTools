using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// A dedicated sandbox is a contract, not a hint: a host that predates it would run the review in the workspace's
/// shared sandbox, writable everywhere. So the client checks the capability before it creates anything, deletes a
/// thread the host did not acknowledge, and stops on any release the host refuses.
/// </summary>
public sealed class LmStreamingS2SClientDedicatedSandboxTests
{
    private const string Capabilities = """{"conversationWorkingDirectory":true,"dedicatedSandbox":true}""";

    [Fact]
    public async Task A_dedicated_thread_is_requested_jailed_to_its_directory_and_returned_once_acknowledged()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/capabilities", Capabilities)
            .OnJson(
                HttpMethod.Post,
                "api/conversations",
                """{"threadId":"thread-d","dedicatedSandboxAccepted":true}"""
            );

        var thread = await ProvisionDedicatedAsync(handler);

        thread.Should().Be("thread-d");
        var body = handler.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        body.Should().Contain("\"dedicatedSandbox\":true").And.Contain("\"workingDirectoryRelPath\":\"PRs/nova-17\"");
    }

    [Fact]
    public async Task A_host_that_does_not_acknowledge_it_gets_the_thread_deleted_and_a_contract_error()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/capabilities", Capabilities)
            .OnJson(HttpMethod.Post, "api/conversations", """{"threadId":"thread-old"}""")
            .OnJson(HttpMethod.Delete, "api/conversations/thread-old", "{}");

        var provision = () => ProvisionDedicatedAsync(handler);

        _ = await provision.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*dedicated sandbox*");
        handler
            .Requests.Should()
            .ContainSingle(request => request.Method == HttpMethod.Delete)
            .Which.Uri.AbsolutePath.Should()
            .Be("/api/conversations/thread-old");
    }

    [Fact]
    public async Task A_host_that_does_not_advertise_it_is_refused_before_any_thread_is_created()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/capabilities",
            """{"conversationWorkingDirectory":true}"""
        );

        var provision = () => ProvisionDedicatedAsync(handler);

        _ = await provision.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*dedicated sandboxes*");
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Release_sends_the_new_directory_and_fails_unless_the_host_moved_the_thread_there()
    {
        var moved = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/sandbox/release",
            """{"workingDirectoryRelPath":"PRs/nova-17"}"""
        );
        await ReleaseAsync(moved, "PRs/nova-17");
        moved.Requests.Single().Uri.AbsolutePath.Should().Be("/api/conversations/thread-d/sandbox/release");
        moved.Requests.Single().Body.Should().Contain("\"workingDirectoryRelPath\":\"PRs/nova-17\"");

        var elsewhere = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/sandbox/release",
            """{"workingDirectoryRelPath":"PRs/nova-9"}"""
        );
        var notMoved = () => ReleaseAsync(elsewhere, "PRs/nova-17");
        _ = await notMoved.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*did not move*");

        var running = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/sandbox/release",
            """{"error":"sandbox_release_while_running"}""",
            HttpStatusCode.Conflict
        );
        var refused = () => ReleaseAsync(running, null);
        _ = await refused.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*409*");
    }

    private static async Task<string> ProvisionDedicatedAsync(FakeHttpMessageHandler handler)
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return await new LmStreamingS2SClient(http, null, null, null).ProvisionAsync(
            "ws-1",
            "test",
            "workspace-agent",
            null,
            null,
            null,
            workingDirectory: "PRs/nova-17",
            disableClientTools: true,
            readOnlyEgress: false,
            dedicatedSandbox: true,
            CancellationToken.None
        );
    }

    private static async Task ReleaseAsync(FakeHttpMessageHandler handler, string? workingDirectory)
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await new LmStreamingS2SClient(http, null, null, null).ReleaseSandboxAsync(
            "thread-d",
            workingDirectory,
            CancellationToken.None
        );
    }
}
