using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Serialization coverage for <see cref="CompactionCheckpointMessage" /> (#680; spec 679 §3.1): the
///     <c>$type</c> path, the computed envelope, structural inference when <c>$type</c> is absent, and a
///     lossless round trip of every manifest section including the lead's <c>current_instruction</c>.
/// </summary>
public class CompactionCheckpointMessageSerializationTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new IMessageJsonConverter());
        return options;
    }

    private static CompactionCheckpointMessage Sample() =>
        new()
        {
            CheckpointId = "cp-abc-1",
            Boundary = new CheckpointBoundary { Seq = 41, MessageId = "row-41" },
            SupersedesCheckpointId = "cp-abc-0",
            Trigger = CompactionTrigger.Reactive,
            Manifest = new ContextManifest
            {
                CurrentInstruction = [new QuotedItem { Seq = 40, Quote = "now fix the build" }],
                Instructions =
                [
                    new QuotedItem { Seq = 1, Quote = "never push" },
                    new QuotedItem { Seq = 7, Quote = "use short sentences" },
                ],
                Goals = ["green CI", "no pre-existing failures"],
                Decisions = [new QuotedItem { Seq = 12, Quote = "approved: delete the flag" }],
                Tasks =
                [
                    new TaskRef
                    {
                        Id = "t-1",
                        Title = "fix flaky test",
                        Status = "in_progress",
                    },
                    new TaskRef { Title = "extracted", Status = "open" },
                ],
                Artifacts =
                [
                    new ArtifactRef
                    {
                        Path = "src/a.cs",
                        Hash = "sha256:abc",
                        OriginSeq = 20,
                    },
                ],
                Agents =
                [
                    new AgentRef
                    {
                        AgentId = "agent-1",
                        Template = "researcher",
                        Task = "find prior art",
                        Status = "completed",
                        Outcome = "done",
                        ThreadId = "subagent-x-agent-1",
                    },
                ],
                Index =
                [
                    new IndexEntry
                    {
                        FromSeq = 1,
                        ToSeq = 20,
                        RunId = "run-1",
                        Headline = "setup",
                    },
                    new IndexEntry
                    {
                        FromSeq = 21,
                        ToSeq = 41,
                        RunId = "run-2",
                        Headline = "the fix",
                    },
                ],
                Recovery = new RecoveryStateAtCut(),
            },
            Narrative = "Setup, then the fix.",
            Stats = new CheckpointStats
            {
                RowsCovered = 41,
                EstimatedTokensBefore = 90_000,
                EstimatedTokensAfter = 12_000,
                SummaryUsageAttemptId = "att-9",
                SummaryLatencyMs = 1234,
            },
            CreatedAtUtc = new DateTimeOffset(2026, 9, 2, 9, 30, 0, TimeSpan.Zero),
            FromAgent = "agent-1",
            GenerationId = "gen-77",
            ThreadId = "thread-1",
            RunId = "run-2",
        };

    [Fact]
    public void Serialize_AsIMessage_WritesTheDiscriminator_TheUserRole_AndTheEnvelope()
    {
        IMessage message = Sample();

        var json = JsonSerializer.Serialize(message, Options());
        TestContextLogger.LogDebug("Serialized checkpoint JSON: {Json}", json);

        var root = JsonDocument.Parse(json).RootElement;
        // The SAMPLE is schema 2, so it carries the schema 2 discriminator. A row's $type names the
        // manifest shape, not just the type, which is what lets an older binary skip it (§8.3).
        Assert.Equal(CompactionCheckpointMessage.TypeDiscriminatorV2, root.GetProperty("$type").GetString());
        Assert.Equal("user", root.GetProperty("role").GetString());
        Assert.Equal("cp-abc-1", root.GetProperty("checkpoint_id").GetString());
        Assert.Equal("Reactive", root.GetProperty("trigger").GetString());
        Assert.Equal(41, root.GetProperty("boundary").GetProperty("seq").GetInt64());
        Assert.Equal(
            "now fix the build",
            root.GetProperty("manifest").GetProperty("current_instruction")[0].GetProperty("quote").GetString()
        );
        var text = root.GetProperty("text").GetString()!;
        Assert.Contains("<context-checkpoint version=\"2\" id=\"cp-abc-1\" covers_seq=\"1-41\"", text);
        Assert.Contains("## Current instruction (verbatim, seq 40)\n- [seq 40] now fix the build", text);
        Assert.Contains("## What happened\nSetup, then the fix.", text);
        Assert.Contains("seq 21-41 (run-2): the fix", text);
        Assert.EndsWith("</context-checkpoint>", text);
    }

    [Fact]
    public void Deserialize_WithDiscriminator_RoundTripsEverySection()
    {
        var original = Sample();
        var json = JsonSerializer.Serialize<IMessage>(original, Options());

        var restored = JsonSerializer.Deserialize<IMessage>(json, Options());

        var typed = Assert.IsType<CompactionCheckpointMessage>(restored);
        Assert.Equal(original.CheckpointId, typed.CheckpointId);
        Assert.Equal(original.SchemaVersion, typed.SchemaVersion);
        Assert.Equal(original.Boundary, typed.Boundary);
        Assert.Equal(original.SupersedesCheckpointId, typed.SupersedesCheckpointId);
        Assert.Equal(original.Trigger, typed.Trigger);
        Assert.Equal(original.Narrative, typed.Narrative);
        Assert.Equal(original.Stats, typed.Stats);
        Assert.Equal(original.CreatedAtUtc, typed.CreatedAtUtc);
        Assert.Equal(original.FromAgent, typed.FromAgent);
        Assert.Equal(original.GenerationId, typed.GenerationId);
        Assert.Equal(original.ThreadId, typed.ThreadId);
        Assert.Equal(original.RunId, typed.RunId);
        Assert.Equal(Role.User, typed.Role);

        Assert.Equal(original.Manifest.CurrentInstruction, typed.Manifest.CurrentInstruction);
        Assert.Equal(original.Manifest.Instructions, typed.Manifest.Instructions);
        Assert.Equal(original.Manifest.Goals, typed.Manifest.Goals);
        Assert.Equal(original.Manifest.Decisions, typed.Manifest.Decisions);
        Assert.Equal(original.Manifest.Tasks, typed.Manifest.Tasks);
        Assert.Equal(original.Manifest.Artifacts, typed.Manifest.Artifacts);
        Assert.Equal(original.Manifest.Agents, typed.Manifest.Agents);
        Assert.Equal(original.Manifest.Index, typed.Manifest.Index);
        Assert.Equal(original.Manifest.Recovery, typed.Manifest.Recovery);
        Assert.Equal(original.Text, typed.Text);
    }

    [Fact]
    public void Deserialize_WithoutDiscriminator_InfersTheCheckpoint_NotATextMessage()
    {
        // The row carries "text" (its rendered envelope), so without a structural guard on checkpoint_id a
        // $type-less row would rehydrate as a plain TextMessage and lose its manifest.
        var json = """
            {
              "checkpoint_id": "cp-1",
              "boundary": { "seq": 3, "message_id": "m3" },
              "trigger": "Manual",
              "manifest": { "goals": ["g"] },
              "narrative": "n",
              "text": "<context-checkpoint>…</context-checkpoint>",
              "role": "user"
            }
            """;

        var restored = JsonSerializer.Deserialize<IMessage>(json, Options());

        var typed = Assert.IsType<CompactionCheckpointMessage>(restored);
        Assert.Equal("cp-1", typed.CheckpointId);
        Assert.Equal(CompactionTrigger.Manual, typed.Trigger);
        Assert.Equal(["g"], typed.Manifest.Goals);
        Assert.Empty(typed.Manifest.CurrentInstruction);
        Assert.Contains("covers_seq=\"1-3\"", typed.Text);
    }

    [Fact]
    public void Deserialize_ManifestMissingSections_ReadsThemAsEmpty()
    {
        var json =
            """{"$type":"compaction_checkpoint","checkpoint_id":"cp-1","boundary":{"seq":1,"message_id":"m"},"trigger":"Shadow","manifest":{},"narrative":""}""";

        var typed = Assert.IsType<CompactionCheckpointMessage>(JsonSerializer.Deserialize<IMessage>(json, Options()));

        Assert.Empty(typed.Manifest.Instructions);
        Assert.Empty(typed.Manifest.Index);
        Assert.True(typed.Manifest.Recovery.IsClean);
        Assert.Equal(new CheckpointStats(), typed.Stats);
    }

    [Fact]
    public void EachSchemaVersion_IsWrittenWithItsOwnDiscriminator()
    {
        // The pair that makes the contract work: version 1 keeps the $type it has always had, so rows
        // already on disk stay readable, while version 2 takes a new one, so a binary that predates
        // version 2 does not recognise it.
        var v1 = JsonSerializer.Serialize<IMessage>(Sample() with { SchemaVersion = 1 }, Options());
        var v2 = JsonSerializer.Serialize<IMessage>(Sample() with { SchemaVersion = 2 }, Options());

        Assert.Equal(
            CompactionCheckpointMessage.TypeDiscriminator,
            JsonDocument.Parse(v1).RootElement.GetProperty("$type").GetString()
        );
        Assert.Equal(
            CompactionCheckpointMessage.TypeDiscriminatorV2,
            JsonDocument.Parse(v2).RootElement.GetProperty("$type").GetString()
        );
        Assert.NotEqual(CompactionCheckpointMessage.TypeDiscriminator, CompactionCheckpointMessage.TypeDiscriminatorV2);
    }

    [Fact]
    public void ThisBuildReadsEveryCheckpointDiscriminatorItHasEverWritten()
    {
        // The other half of the closure. Refusing to read version 1 would orphan every row written
        // before the bump, which is a worse failure than the one the bump prevents.
        foreach (var version in new[] { 1, 2 })
        {
            var json = JsonSerializer.Serialize<IMessage>(Sample() with { SchemaVersion = version }, Options());

            var typed = Assert.IsType<CompactionCheckpointMessage>(
                JsonSerializer.Deserialize<IMessage>(json, Options())
            );

            Assert.Equal(version, typed.SchemaVersion);
            Assert.Equal("cp-abc-1", typed.CheckpointId);
        }
    }

    [Fact]
    public void ADiscriminatorThisBuildDoesNotKnow_RaisesTheUnknownTypeError_RatherThanBeingInferred()
    {
        // This is the exact path an older binary takes on a row from a newer one. It matters that the
        // error is the UNKNOWN-TYPE error and not a generic parse failure: a resilient loader skips
        // the record on it, which is what leaves that binary on whole canonical history. It also must
        // not fall through to structural inference -- "checkpoint_id is present, so it is a
        // checkpoint" would adopt the row and drop the sections, defeating the discriminator.
        var json = JsonSerializer
            .Serialize<IMessage>(Sample(), Options())
            .Replace(
                $"\"$type\":\"{CompactionCheckpointMessage.TypeDiscriminatorV2}\"",
                "\"$type\":\"compaction_checkpoint@99\"",
                StringComparison.Ordinal
            );
        Assert.Contains("compaction_checkpoint@99", json);
        Assert.Contains("checkpoint_id", json);

        _ = Assert.Throws<UnknownMessageTypeDiscriminatorException>(() =>
            JsonSerializer.Deserialize<IMessage>(json, Options())
        );
    }

    [Fact]
    public void BumpingTheSchemaWithoutADiscriminator_Fails_RatherThanReusingAnOlderOne()
    {
        // The forcing function. Silently reusing version 2's $type for a version 3 row is the bug this
        // whole change replaces, so the writer refuses instead of falling back.
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CompactionCheckpointMessage.DiscriminatorFor(3));
    }
}
