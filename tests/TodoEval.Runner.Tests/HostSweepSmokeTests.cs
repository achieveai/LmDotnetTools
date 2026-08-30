using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Opt-in end-to-end smoke: publishes LmStreaming.Sample into a temp dir, launches it as a real
/// child process on its own port, creates the eval mode and workspace over HTTP, runs a 1-seed
/// sweep against the keyless <c>test</c> provider, and asserts the archived reports exist. Gated
/// behind <c>TODOEVAL_SMOKE=1</c> because it publishes and boots the whole host (minutes, not
/// milliseconds); set <c>TODOEVAL_HOST_PUBLISH_DIR</c> to reuse already-published binaries.
/// </summary>
public class HostSweepSmokeTests
{
    [SkippableFact]
    public async Task FullSweep_AgainstIsolatedHost_ProducesManifestAndReports()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("TODOEVAL_SMOKE") != "1",
            "Set TODOEVAL_SMOKE=1 to run the host-launch smoke (publishes and boots LmStreaming.Sample)."
        );

        var scratch = Path.Combine(Path.GetTempPath(), $"todo-eval-smoke-{Guid.NewGuid():N}");
        var evalDir = Path.Combine(scratch, "eval");
        var resultsDir = Path.Combine(scratch, "results");
        Directory.CreateDirectory(evalDir);
        try
        {
            File.WriteAllText(
                Path.Combine(evalDir, "mode.json"),
                """
                {
                  "name": "todo-eval",
                  "description": "smoke-test copy of the eval mode",
                  "systemPrompt": "You are the todo-eval smoke agent."
                }
                """
            );
            File.WriteAllText(
                Path.Combine(evalDir, "task.md"),
                "Say one short sentence about {TOPIC}. Do not use any tools."
            );

            var config = new EvalRunnerConfig
            {
                EvalDir = evalDir,
                ResultsDir = resultsDir,
                Models = ["test"],
                Topics = ["a smoke test"],
                Seeds = 1,
                PerRunTimeoutMinutes = 5,
                Host = new HostConfig
                {
                    PublishDir = Environment.GetEnvironmentVariable("TODOEVAL_HOST_PUBLISH_DIR"),
                    ShutdownGraceSeconds = 3,
                },
            };

            var exitCode = await EvalProgram.RunSweepAsync(config, TextWriter.Null, CancellationToken.None);

            exitCode.Should().Be(0);
            var sweepDir = Directory.EnumerateDirectories(resultsDir).Should().ContainSingle().Subject;
            var manifest = RunManifestEntry.ReadJsonl(Path.Combine(sweepDir, "runs-manifest.jsonl"));
            var entry = manifest.Should().ContainSingle().Subject;
            entry.Model.Should().Be("test");
            entry.Status.Should().Be(RunOutcomes.Completed);
            entry.ThreadId.Should().NotBeNullOrEmpty();

            File.Exists(Path.Combine(sweepDir, "runs.jsonl")).Should().BeTrue();
            File.Exists(Path.Combine(sweepDir, "summary.md")).Should().BeTrue();
            Directory
                .Exists(Path.Combine(sweepDir, "conversations", entry.ThreadId!))
                .Should()
                .BeTrue("the sweep archives the isolated store next to its reports");
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the dir is under %TEMP%.
            }
        }
    }
}
