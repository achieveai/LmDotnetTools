using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// A dedicated sandbox is a contract, not a hint: a host that predates it would run the review in the workspace's
/// shared sandbox, writable everywhere. So the client checks the capability before it creates anything, throws on a
/// thread the host did not acknowledge after a best-effort delete of it (a failed delete is swallowed, so the thread
/// may outlive the error), and stops on any release the host refuses, without carrying the host's body.
/// </summary>
public sealed class LmStreamingS2SClientDedicatedSandboxTests(ITestOutputHelper output)
{
    private const string Capabilities = """{"conversationWorkingDirectory":true,"dedicatedSandbox":true}""";

    /// <summary>Text a host could put in a refusal body; it must never reach an error message or a log.</summary>
    private const string Sentinel = "UPSTREAM-BODY-SENTINEL-5d1c";

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

        Log(handler, $"thread={thread}");
        thread.Should().Be("thread-d");
        var body = handler.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        body.Should().Contain("\"dedicatedSandbox\":true").And.Contain("\"workingDirectoryRelPath\":\"PRs/nova-17\"");
    }

    [Fact]
    public async Task A_host_that_does_not_acknowledge_it_gets_a_best_effort_thread_delete_and_a_contract_error()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/capabilities", Capabilities)
            .OnJson(HttpMethod.Post, "api/conversations", """{"threadId":"thread-old"}""")
            .OnJson(HttpMethod.Delete, "api/conversations/thread-old", "{}");

        var provision = () => ProvisionDedicatedAsync(handler);

        var error = await provision
            .Should()
            .ThrowAsync<ReviewHostContractException>()
            .WithMessage("*dedicated sandbox*");
        Log(handler, error.Which.Message);
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

        var error = await provision
            .Should()
            .ThrowAsync<ReviewHostContractException>()
            .WithMessage("*dedicated sandboxes*");
        Log(handler, error.Which.Message);
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    /// <summary>
    /// Each release outcome on its own row. A move must land in the directory asked for; a release without one only
    /// needs a success. A refusal names its status and a documented code, and never the rest of the host's body.
    /// </summary>
    [Theory]
    [InlineData("moved", "PRs/nova-17", 200, """{"workingDirectoryRelPath":"PRs/nova-17"}""", null)]
    [InlineData("released", null, 200, """{"workingDirectoryRelPath":"reviews"}""", null)]
    [InlineData(
        "moved-elsewhere",
        "PRs/nova-17",
        200,
        """{"workingDirectoryRelPath":"PRs/nova-9"}""",
        "The review host did not move 'thread-d' to 'PRs/nova-17'."
    )]
    [InlineData(
        "move-unconfirmed",
        "PRs/nova-17",
        200,
        """{"detail":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host did not move 'thread-d' to 'PRs/nova-17'."
    )]
    [InlineData(
        "running",
        null,
        409,
        """{"code":"sandbox_release_while_running","error":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host refused to release the sandbox of 'thread-d' (409, sandbox_release_while_running)."
    )]
    [InlineData(
        "gateway-refused",
        "PRs/nova-17",
        503,
        """{"code":"sandbox_release_failed","detail":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host refused to release the sandbox of 'thread-d' (503, sandbox_release_failed)."
    )]
    [InlineData(
        "mode-unsupported",
        "PRs/nova-17",
        400,
        """{"code":"dedicated_sandbox_mode_unsupported","detail":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host refused to release the sandbox of 'thread-d' (400, dedicated_sandbox_mode_unsupported)."
    )]
    [InlineData(
        "unknown-thread",
        null,
        404,
        """{"error":"Conversation 'thread-d' not found.","code":"unknown_thread"}""",
        "The review host reports the thread 'thread-d' as not found (404, unknown_thread)."
    )]
    [InlineData(
        "no-route",
        "PRs/nova-17",
        404,
        "",
        "The review host has no sandbox release route (404 for 'thread-d'); it predates dedicated sandboxes."
    )]
    [InlineData(
        "404-other-code",
        null,
        404,
        """{"code":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host has no sandbox release route (404 for 'thread-d'); it predates dedicated sandboxes."
    )]
    [InlineData(
        "404-numeric-code",
        null,
        404,
        """{"code":404}""",
        "The review host has no sandbox release route (404 for 'thread-d'); it predates dedicated sandboxes."
    )]
    [InlineData(
        "unknown-code",
        null,
        400,
        """{"code":"UPSTREAM-BODY-SENTINEL-5d1c"}""",
        "The review host refused to release the sandbox of 'thread-d' (400)."
    )]
    [InlineData(
        "not-json",
        null,
        500,
        "UPSTREAM-BODY-SENTINEL-5d1c",
        "The review host refused to release the sandbox of 'thread-d' (500)."
    )]
    public async Task Release_moves_or_releases_the_thread_or_throws_a_sanitized_contract_error(
        string outcome,
        string? directory,
        int status,
        string reply,
        string? expectedError
    )
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/sandbox/release",
            reply,
            (HttpStatusCode)status
        );
        var release = () => ReleaseAsync(handler, directory);

        if (expectedError is null)
        {
            await release.Should().NotThrowAsync();
            Log(handler, $"{outcome}: released");
        }
        else
        {
            var error = (await release.Should().ThrowExactlyAsync<ReviewHostContractException>()).Which;
            Log(handler, $"{outcome}: {error.Message}");
            error.Message.Should().Be(expectedError).And.NotContain(Sentinel);
        }
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.AbsolutePath.Should().Be("/api/conversations/thread-d/sandbox/release");
        request
            .Body.Should()
            .Be(
                directory is null
                    ? """{"workingDirectoryRelPath":null}"""
                    : $$"""{"workingDirectoryRelPath":"{{directory}}"}"""
            );
    }

    /// <summary>Bounded per-case diagnostics: the requests sent and the outcome, each line cut to 300 characters.</summary>
    private void Log(FakeHttpMessageHandler handler, string result)
    {
        foreach (var request in handler.Requests)
        {
            output.WriteLine(Bounded($"{request.Method} {request.Uri.PathAndQuery} {request.Body}"));
        }
        output.WriteLine(Bounded("result: " + result));
    }

    private static string Bounded(string text) => text.Length <= 300 ? text : text[..300] + "...";

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
