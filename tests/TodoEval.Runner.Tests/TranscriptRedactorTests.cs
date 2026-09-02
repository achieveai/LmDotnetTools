using System.Text.Json;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Redaction is only worth committing if it is metric-preserving: the archived store has to score
/// EXACTLY like the raw one it replaces, or the committed archive stops being evidence for the
/// numbers in the report. That identity is the headline test here; the rest pin the individual
/// rules it rests on.
/// </summary>
public class TranscriptRedactorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"todo-eval-redact-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RedactedArchive_ScoresIdenticallyToTheRawStore()
    {
        var raw = RepoPaths.FixtureConversations("coordination-run");
        var redacted = Path.Combine(_temp, "conversations");
        TranscriptRedactor.CopyRedacted(raw, redacted);

        // Compared through the committed artifact itself - runs.jsonl - so ANY field that moved
        // under redaction fails this, not only the ones this test thought to name.
        Score(raw, "raw").Should().Be(Score(redacted, "redacted"));
    }

    [Fact]
    public void RedactedStore_NoLongerCarriesTheModelsProse()
    {
        var raw = RepoPaths.FixtureConversations("coordination-run");
        var redacted = Path.Combine(_temp, "conversations");
        TranscriptRedactor.CopyRedacted(raw, redacted);

        var before = ReadAll(raw);
        var after = ReadAll(redacted);

        before.Should().Contain("No agent named", "the fixture really does contain prose to remove");
        after.Should().Contain(Fingerprints.RedactedArgsKey, "call arguments became digests");
        after.Should().NotContain("I spawned one agent and it is working the board");
    }

    [Fact]
    public void ToolResults_SurviveVerbatim_BecauseTheBoardLedgerIsDerivedFromThem()
    {
        // #621 Part B reads the vanished-id ledger out of result TEXT. Hashing results would erase
        // the only evidence that finding is made of, so they are deliberately exempt.
        var redacted = TranscriptRedactor.RedactMessages(
            """
            [
              {
                "messageType": "ToolCallResultMessage",
                "messageJson": "{\"tool_call_id\":\"c1\",\"text\":\"Error: Task 'task-7' not found\"}"
              }
            ]
            """
        );

        Inner(redacted, "text").GetString().Should().Be("Error: Task 'task-7' not found");
    }

    [Fact]
    public void CallArguments_BecomeACanonicalDigest_SoIdenticalRetriesStayIdentical()
    {
        // The storm identity is (tool, canonical args). Hashing the CANONICAL bytes is what keeps
        // two spec-identical calls identical after redaction; hashing the raw string would not.
        TranscriptRedactor
            .ArgsHash("""{"b":2,"a":1}""")
            .Should()
            .Be(TranscriptRedactor.ArgsHash("""{ "a":1, "b":2 }"""));

        TranscriptRedactor.ArgsHash("""{"a":1}""").Should().NotBe(TranscriptRedactor.ArgsHash("""{"a":2}"""));
        TranscriptRedactor.ArgsHash("""{"a":1}""").Should().HaveLength(64);
    }

    [Fact]
    public void ProseBecomesItsClaimSignals_WhichIsWhatTheScorerActuallyReads()
    {
        var redacted = TranscriptRedactor.RedactMessages(
            """
            [
              {
                "messageType": "TextMessage",
                "messageJson": "{\"role\":\"assistant\",\"text\":\"I have completed every task on the board.\"}"
              }
            ]
            """
        );

        var text = Inner(redacted, "text");
        text.GetProperty("length").GetInt32().Should().Be(41);
        text.GetProperty("claimVerbMatch").GetBoolean().Should().BeTrue();
        text.GetProperty("claimNounMatch").GetBoolean().Should().BeTrue();
        redacted.Should().NotContain("every task on the board");
    }

    [Fact]
    public void ProseWithoutAClaim_KeepsItsSignalsFalse_SoTheFieldIsNeverVacuous()
    {
        var redacted = TranscriptRedactor.RedactMessages(
            """
            [
              {
                "messageType": "TextMessage",
                "messageJson": "{\"role\":\"assistant\",\"text\":\"Hello there.\"}"
              }
            ]
            """
        );

        var text = Inner(redacted, "text");
        text.GetProperty("claimVerbMatch").GetBoolean().Should().BeFalse();
        text.GetProperty("claimNounMatch").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void UnparseableDocument_IsEmptied_NeverCopiedThrough()
    {
        // A parse failure must fail CLOSED. Falling back to a verbatim copy would publish raw
        // transcripts into a committed archive, which is exactly what redaction exists to prevent.
        var source = Path.Combine(_temp, "src", "conversations", "thread-broken");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "messages.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(source, "metadata.json"), "also not json");

        var destination = Path.Combine(_temp, "dst");
        TranscriptRedactor.CopyRedacted(Path.Combine(_temp, "src", "conversations"), destination);

        File.ReadAllText(Path.Combine(destination, "thread-broken", "messages.json")).Should().Be("[]");
        File.ReadAllText(Path.Combine(destination, "thread-broken", "metadata.json")).Should().Be("{}");
    }

    [Fact]
    public void OperatorAuthoredMetadataProse_IsRedactedToo()
    {
        var redacted = TranscriptRedactor.RedactMetadata(
            """
            {
              "properties": {
                "sample.subAgentTask": "Complete the remaining board tasks",
                "sample.subAgentOf": "thread-1"
              }
            }
            """
        );

        redacted.Should().NotContain("remaining board tasks");
        redacted.Should().Contain("thread-1", "structural properties are not prose and must survive");

        // metrics-spec.md promises ONE form for prose everywhere: the signals OBJECT, as RedactProse
        // emits for message fields. Emitting a JSON string here instead made the two paths disagree.
        var signals = System.Text.Json.Nodes.JsonNode.Parse(redacted)!["properties"]!["sample.subAgentTask"];
        signals
            .Should()
            .BeOfType<System.Text.Json.Nodes.JsonObject>("the spec's form is an object, not a string holding one");
        signals!["length"]!.GetValue<int>().Should().Be("Complete the remaining board tasks".Length);

        // Redacting an already-redacted archive must be a no-op. It used to THROW, because the second
        // pass called GetValue<string>() on the signals object it had just written.
        TranscriptRedactor.RedactMetadata(redacted).Should().Be(redacted, "redaction is idempotent");
    }

    private string Score(string conversationsDir, string label)
    {
        var metrics = MetricsExtractor.Extract(
            conversationsDir,
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

        var path = Path.Combine(_temp, $"runs-{label}.jsonl");
        Directory.CreateDirectory(_temp);
        ResultsWriter.WriteRunsJsonl(path, metrics.Runs);
        return File.ReadAllText(path);
    }

    private static string ReadAll(string dir) =>
        string.Concat(Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));

    private static JsonElement Inner(string redactedEnvelopes, string field)
    {
        var inner = JsonDocument.Parse(redactedEnvelopes).RootElement[0].GetProperty("messageJson").GetString()!;
        return JsonDocument.Parse(inner).RootElement.GetProperty(field).Clone();
    }
}
