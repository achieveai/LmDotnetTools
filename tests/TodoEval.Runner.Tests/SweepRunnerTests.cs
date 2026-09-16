using System.Net;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Per-run failure containment (F-008): a malformed 200 body from the host must become THAT run's
/// HarnessError manifest row, never an exception that faults the whole sweep and skips the archive
/// and extraction of runs that DID finish.
/// </summary>
public class SweepRunnerTests
{
    [Fact]
    public async Task MalformedResponseBody_FailsThatRunOnly_AndTheSweepContinues()
    {
        var config = new EvalRunnerConfig
        {
            Models = ["bad", "good"],
            Topics = ["a topic"],
            Seeds = 1,
            PerRunTimeoutMinutes = 1,
        };
        using var http = new HttpClient(new ScriptedHostHandler()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var runner = new SweepRunner(
            new EvalHostClient(http),
            config,
            "ws-1",
            "mode-1",
            VariantConfig.Default,
            [
                new EvalTaskAsset
                {
                    Id = null,
                    Dir = Path.GetTempPath(),
                    Template = "Do {TOPIC}",
                    ExpectedBoard = null,
                },
            ],
            TextWriter.Null
        );
        var manifestPath = Path.Combine(Path.GetTempPath(), $"todo-eval-manifest-{Guid.NewGuid():N}.jsonl");

        try
        {
            var entries = await runner.RunSweepAsync(manifestPath, CancellationToken.None);

            entries.Should().HaveCount(2);

            var bad = entries.Single(e => e.Model == "bad");
            bad.Status.Should().Be(RunOutcomes.HarnessError, "a parse failure is a harness fault of that run alone");
            bad.Error.Should().NotBeNullOrEmpty();

            var good = entries.Single(e => e.Model == "good");
            good.Status.Should().Be(RunOutcomes.Completed);
            good.ThreadId.Should().Be("t-good");

            File.ReadAllLines(manifestPath).Should().HaveCount(2, "both runs still land in the manifest");
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// A run the HOST reports as Errored is a real outcome, not a harness fault — but its manifest
    /// row used to carry no error text at all, so a failed live sweep could not be diagnosed from
    /// the results directory. When the status payload names the error, the row and the console
    /// line both carry it.
    /// </summary>
    [Fact]
    public async Task HostReportedError_LandsInTheManifestRow_AndOnTheConsoleLine()
    {
        const string hostError = "ContextOverflowException: view_exceeds_window";
        var config = new EvalRunnerConfig
        {
            Models = ["m1"],
            Topics = ["a topic"],
            Seeds = 1,
            PerRunTimeoutMinutes = 1,
        };
        using var http = new HttpClient(new ErroredHostHandler(hostError))
        {
            BaseAddress = new Uri("http://127.0.0.1:9/"),
        };
        var console = new StringWriter();
        var runner = new SweepRunner(
            new EvalHostClient(http),
            config,
            "ws-1",
            "mode-1",
            VariantConfig.Default,
            [
                new EvalTaskAsset
                {
                    Id = null,
                    Dir = Path.GetTempPath(),
                    Template = "Do {TOPIC}",
                    ExpectedBoard = null,
                },
            ],
            console
        );
        var manifestPath = Path.Combine(Path.GetTempPath(), $"todo-eval-manifest-{Guid.NewGuid():N}.jsonl");

        try
        {
            var entries = await runner.RunSweepAsync(manifestPath, CancellationToken.None);

            var entry = entries.Should().ContainSingle().Subject;
            entry.Status.Should().Be(RunOutcomes.Errored, "the host's verdict is kept as-is");
            entry.RunId.Should().Be("r-1");
            entry.Error.Should().Be(hostError);

            RunManifestEntry
                .ReadJsonl(manifestPath)
                .Single()
                .Error.Should()
                .Be(hostError, "the row on disk carries it too");
            console.ToString().Should().Contain("Errored after").And.Contain(hostError);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    /// <summary>Scripted host whose single run errors, with the error named on the status payload.</summary>
    private sealed class ErroredHostHandler(string error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = (request.Method.Method, path) switch
            {
                ("POST", "/api/conversations") => """{"threadId":"t-1"}""",
                ("POST", "/api/conversations/t-1/messages") => """{"inputId":"i-1"}""",
                ("GET", "/api/conversations/t-1/status") => System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        threadId = "t-1",
                        runId = "r-1",
                        status = "Errored",
                        response = (object?)null,
                        error,
                    }
                ),
                _ => null,
            };
            return Task.FromResult(
                body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not scripted") }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    }
            );
        }
    }

    /// <summary>
    /// Scripted host: provisioning for model "bad" 200s with a body that is not JSON; model "good"
    /// gets a full provision/send/poll flow that completes on the first status poll.
    /// </summary>
    private sealed class ScriptedHostHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/conversations")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return body.Contains("\"providerId\":\"bad\"", StringComparison.Ordinal)
                    ? Json("this is not json {{{")
                    : Json("""{"threadId":"t-good"}""");
            }

            if (request.Method == HttpMethod.Post && path == "/api/conversations/t-good/messages")
            {
                return Json("""{"inputId":"i-1"}""");
            }

            if (request.Method == HttpMethod.Get && path == "/api/conversations/t-good/status")
            {
                return Json("""{"status":"Completed","runId":"r-1"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not scripted") };
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
