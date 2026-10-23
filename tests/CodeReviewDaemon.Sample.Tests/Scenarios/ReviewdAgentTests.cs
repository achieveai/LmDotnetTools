extern alias Reviewd;

using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdAgentTests
{
    [Fact]
    public async Task Pr_conversation_is_saved_before_submission_and_retained_when_send_fails()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/repo");
        var handler = new FakeHttpMessageHandler()
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"threadId\":\"thread\"}"),
                }
            )
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal),
                _ =>
                {
                    using var connection = PrThreadBindings.Connection(database);
                    connection.Open();
                    using var query = connection.CreateCommand();
                    query.CommandText =
                        "SELECT value FROM cache WHERE key = 'reviewd/pr-thread/azure-devops/org/project/repo/17'";
                    query.ExecuteScalar().Should().Be("\"thread\"");
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var options = CliOptions.Parse([
            "agent",
            "start",
            "--workspace",
            "slot",
            "--provider",
            "test",
            "--prompt",
            "test",
            "--repo",
            repo.DisplayName,
            "--pr",
            "017",
            "--db",
            database,
        ]);
        var client = new LmStreamingS2SClient(http, null, null, null);
        try
        {
            var submit = () =>
                WorkspaceCommands.StartAgentAsync(
                    options,
                    config,
                    client,
                    new S2SWorkspace("slot", "Test", "test", []),
                    null,
                    CancellationToken.None
                );
            await submit.Should().ThrowAsync<HttpRequestException>();
            (await PrThreadBindings.GetAsync(database, repo, "17", CancellationToken.None)).Should().Be("thread");
            await submit.Should().ThrowAsync<InvalidOperationException>();
            handler.CountRequests("/messages").Should().Be(1);
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("completed", 0)]
    [InlineData("error", 1)]
    [InlineData("stopped", 1)]
    public async Task Agent_start_waits_by_default_for_recursive_descendants(string childOutcome, int expectedExit)
    {
        var polls = 0;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "api/conversations/thread/messages", "{\"inputId\":\"input\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread\"}")
            .OnJson(
                HttpMethod.Get,
                "/status?",
                "{\"status\":\"Completed\",\"runId\":\"run\",\"response\":{\"text\":\"done\"}}"
            )
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                _ =>
                {
                    polls++;
                    var state = polls == 1 ? "running" : childOutcome;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $$"""{"schemaVersion":1,"nodes":[{"agentId":"child","threadId":"child-thread","parentThreadId":"thread","depth":1,"template":"test","status":"{{state}}"}]}"""
                        ),
                    };
                }
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var options = CliOptions.Parse([
            "agent",
            "start",
            "--workspace",
            "slot",
            "--provider",
            "test",
            "--prompt",
            "test",
        ]);

        var exit = await WorkspaceCommands.StartAgentAsync(
            options,
            config,
            client,
            new S2SWorkspace("slot", "Test", "test", []),
            timeout: null,
            CancellationToken.None,
            pollDelay: _ => Task.CompletedTask
        );

        exit.Should().Be(expectedExit);
        polls
            .Should()
            .BeGreaterThanOrEqualTo(
                3,
                "a terminal root does not settle a running child, and terminal rosters must stabilize"
            );
        handler.CountRequests("/messages").Should().Be(1);
        handler
            .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal))
            .Should()
            .OnlyContain(r => r.Uri.Query.Contains("recursive=true", StringComparison.Ordinal));
    }
}
