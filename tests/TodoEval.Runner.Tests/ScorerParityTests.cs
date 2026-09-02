using System.Diagnostics;
using System.Text.Json;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The dual-scorer contract. metrics-spec.md is the contract and <c>score.ps1</c> is the reference
/// oracle; the Runner is the twin that actually produces the committed numbers. Two independent
/// implementations of one spec drift, and a drift is indistinguishable from a real change in the
/// model's behaviour unless something compares them - this does, over the committed coordination
/// fixture, on the fields #670 adds.
/// </summary>
public class ScorerParityTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"todo-eval-parity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public void TheTwoScorers_AgreeOnTheCoordinationFixture()
    {
        var oracle = RunOracle();
        var run = RunTwin();

        // Counts and families.
        Number(oracle, "totalToolCalls").Should().Be(run.TotalToolCalls);
        Number(oracle, "taskToolCalls").Should().Be(run.TaskToolCalls);
        Number(oracle, "taskToolErrors").Should().Be(run.TaskToolErrors);
        Number(oracle, "coordinationToolCalls").Should().Be(run.CoordinationToolCalls);
        Number(oracle, "coordinationToolErrors").Should().Be(run.CoordinationToolErrors);
        Number(oracle, "unpairedToolCalls").Should().Be(run.UnpairedToolCalls);
        oracle.GetProperty("schema").GetString().Should().Be(run.Schema);

        // Every per-tool row, including the zero rows: a family or a code that moved in one scorer
        // and not the other fails here on the exact tool name.
        foreach (var tool in ToolFamilies.RowOrder)
        {
            var row = oracle.GetProperty("perTool").GetProperty(tool);
            var mine = run.PerTool[tool];
            row.GetProperty("calls").GetInt32().Should().Be(mine.Calls, "calls for {0}", tool);
            row.GetProperty("errors").GetInt32().Should().Be(mine.Errors, "errors for {0}", tool);
            row.GetProperty("family").GetString().Should().Be(mine.Family, "family for {0}", tool);
            CountMapOf(row.GetProperty("errorCodes")).Should().Equal(mine.ErrorCodes, "error codes for {0}", tool);
        }

        CountMapOf(oracle.GetProperty("errorCodes")).Should().Equal(run.ErrorCodes);
        CountMapOf(oracle.GetProperty("waitOutcomes")).Should().Equal(run.WaitOutcomes);

        // Storms, including the argument digest - the digest is the whole reason the two scorers
        // can agree on a redacted archive at all.
        var storms = oracle.GetProperty("retryStorms");
        Number(oracle, "retryStormCount").Should().Be(run.RetryStormCount);
        storms.GetArrayLength().Should().Be(run.RetryStorms.Count);
        for (var i = 0; i < run.RetryStorms.Count; i++)
        {
            storms[i].GetProperty("tool").GetString().Should().Be(run.RetryStorms[i].Tool);
            storms[i].GetProperty("count").GetInt32().Should().Be(run.RetryStorms[i].Count);
            storms[i].GetProperty("args").GetString().Should().Be(run.RetryStorms[i].Args);
        }

        // Usage.
        var usage = oracle.GetProperty("usage");
        usage.GetProperty("duplicateAttemptIds").GetInt32().Should().Be(run.Usage.DuplicateAttemptIds);
        usage.GetProperty("totals").GetProperty("totalTokens").GetInt64().Should().Be(run.Usage.Totals.TotalTokens);
        usage.GetProperty("totals").GetProperty("records").GetInt32().Should().Be(run.Usage.Totals.Records);
        usage.GetProperty("attributedTurnTokens").GetInt64().Should().Be(run.Usage.AttributedTurnTokens);
        usage.GetProperty("unattributedTurnTokens").GetInt64().Should().Be(run.Usage.UnattributedTurnTokens);

        Number(oracle, "turns").Should().Be(run.Turns);
        Number(oracle, "primaryTurns").Should().Be(run.PrimaryTurns);
        oracle.GetProperty("validity").GetProperty("valid").GetBoolean().Should().Be(run.Validity.Valid);
    }

    [SkippableFact]
    public void TheTwoScorers_ComputeIdenticalFingerprints()
    {
        // The comparison gate #677 will use. If the C# and PowerShell recipes disagree, an archived
        // baseline scored by one can never be compared with a sweep scored by the other - and the
        // mismatch would look like a corpus change rather than a scorer bug.
        var oracle = RunOracle().GetProperty("fingerprints");
        var mine = FingerprintSet.Compute(RepoPaths.EvalDir);

        oracle.GetProperty("taskCorpusHash").GetString().Should().Be(mine.TaskCorpusHash);
        oracle.GetProperty("specHash").GetString().Should().Be(mine.SpecHash);
        oracle.GetProperty("evaluatorHash").GetString().Should().Be(mine.EvaluatorHash);
        oracle.GetProperty("specVersion").GetString().Should().Be(mine.SpecVersion);
    }

    private static int Number(JsonElement root, string name) => root.GetProperty(name).GetInt32();

    private static Dictionary<string, int> CountMapOf(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);

    private static RunMetrics RunTwin()
    {
        var metrics = MetricsExtractor.Extract(
            RepoPaths.FixtureConversations("coordination-run"),
            [
                new RunManifestEntry
                {
                    RunKey = "fixture/seed0",
                    Model = "fixture-model",
                    SeedIndex = 0,
                    Topic = "coordination",
                    Status = RunOutcomes.Completed,
                    ThreadId = "thread-fixture-coord",
                },
            ],
            expectedBoard: null
        );

        return metrics.Runs.Single();
    }

    private JsonElement RunOracle()
    {
        // Locally this is skipped rather than failed: the oracle is a cross-check, and a machine without
        // PowerShell 7 must still be able to run the Runner's own suite. On CI it must FAIL instead.
        // These are the only tests that compare the two scorers against each other, so a silent skip
        // there would let both drift together undetected - and two scorers wrong in the same way is
        // precisely what parity testing cannot otherwise catch.
        if (!HasPwsh())
        {
            Assert.True(
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                "pwsh is not on PATH on a CI leg, so the reference-oracle parity check silently did not run."
            );
            Skip.If(true, "pwsh is not on PATH, so the reference oracle cannot be run.");
        }

        Directory.CreateDirectory(_temp);
        var outFile = Path.Combine(_temp, "oracle.json");
        var fixture = RepoPaths.Fixture("coordination-run");

        var process =
            Process.Start(
                new ProcessStartInfo("pwsh")
                {
                    ArgumentList =
                    {
                        "-NoProfile",
                        "-File",
                        RepoPaths.ScoreScript,
                        "-ConversationsDir",
                        Path.Combine(fixture, "conversations"),
                        "-BoardSnapshot",
                        Path.Combine(fixture, "board.json"),
                        "-OutFile",
                        outFile,
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            ) ?? throw new InvalidOperationException("pwsh did not start.");

        // BOTH pipes are drained concurrently. The score object is several KB and the pipe buffer
        // is 4 KB, so reading stderr to the end first deadlocks: the oracle blocks writing stdout
        // while this side blocks waiting for a stderr EOF that can never arrive.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(milliseconds: 120_000);
        var stderr = exited ? stderrTask.GetAwaiter().GetResult() : "(the oracle did not exit)";
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        exited.Should().BeTrue("the oracle must finish scoring a 16-call fixture well inside 2 minutes");
        stdoutTask.GetAwaiter().GetResult();
        process.ExitCode.Should().Be(0, "the oracle must score the fixture cleanly. stderr: {0}", stderr);

        // -OutFile writes utf8NoBOM, but read defensively: a BOM would make the parse fail with a
        // message that says nothing about the real problem.
        return JsonDocument.Parse(File.ReadAllText(outFile).TrimStart('﻿')).RootElement.Clone();
    }

    private static bool HasPwsh()
    {
        // Probed once: launching a shell per test would dominate the suite's runtime.
        _hasPwsh ??= ProbePwsh();
        return _hasPwsh.Value;
    }

    private static bool? _hasPwsh;

    private static bool ProbePwsh()
    {
        try
        {
            using var probe = Process.Start(
                new ProcessStartInfo("pwsh")
                {
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "exit 0" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );
            if (probe is null)
            {
                return false;
            }

            probe.StandardOutput.ReadToEndAsync();
            probe.StandardError.ReadToEndAsync();
            return probe.WaitForExit(milliseconds: 60_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
