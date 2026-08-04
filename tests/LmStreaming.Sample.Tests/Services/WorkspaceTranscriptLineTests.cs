using System.Collections.Immutable;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     Pure unit coverage for <see cref="WorkspaceTranscriptLine"/> — the workspace-transcript line
///     envelope, its <c>uid</c> derivation, the ASCII file-leaf slug, and the exact bytes a line
///     serializes to (#251, phase 1 step B). Nothing here touches a store, a sandbox or a clock: every
///     assertion is over a pure function, which is the point — <c>uid</c> is only usable as an append
///     watermark because it is deterministic.
/// </summary>
public sealed class WorkspaceTranscriptLineTests
{
    private const string ThreadId = "thread-alpha";
    private const string RunId = "run-1";

    /// <summary>
    ///     A provider message id of the shape that lands in <see cref="PersistedMessage.FromAgent"/> —
    ///     the value <c>agent</c> must NEVER be sourced from.
    /// </summary>
    private const string ProviderMessageId = "msg_01AbCdEfGhIjKlMnOpQrStUv";

    private static PersistedMessage Persisted(
        string id,
        string messageType = "TextMessage",
        string role = "Assistant",
        string? messageJson = null,
        string? generationId = "gen-1",
        int? messageOrderIdx = 0,
        string? parentRunId = null,
        string? fromAgent = ProviderMessageId) =>
        new()
        {
            Id = id,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = parentRunId,
            GenerationId = generationId,
            MessageOrderIdx = messageOrderIdx,
            Timestamp = 1_700_000_000_000,
            MessageType = messageType,
            Role = role,
            FromAgent = fromAgent,
            MessageJson = messageJson ?? """{"role":"assistant","text":"hi"}""",
        };

    /// <summary>The five message kinds the transcript has to carry with one uniform key set.</summary>
    private static IReadOnlyList<PersistedMessage> FiveMessageKinds() =>
    [
        Persisted("id-text", "TextMessage", "Assistant"),
        Persisted("id-tool-call", "ToolCallMessage", "Assistant", """{"role":"assistant","tool_calls":[]}"""),
        // A tool result carries no generation/order in some stores, and a reasoning row may arrive
        // without a parent run — the key set still has to come out identical.
        Persisted(
            "id-tool-result",
            "ToolCallResultMessage",
            "Tool",
            """{"role":"tool","result":"ok"}""",
            generationId: null,
            messageOrderIdx: null),
        Persisted("id-reasoning", "ReasoningMessage", "Assistant", """{"role":"assistant","reasoning":"…"}"""),
        Persisted("id-usage", "UsageMessage", "None", """{"role":"none","usage":{"total_tokens":7}}"""),
    ];

    private static JsonElement Parse(WorkspaceTranscriptLine line) =>
        JsonDocument.Parse(WorkspaceTranscriptLine.Serialize(line)).RootElement;

    private static IReadOnlyList<string> KeysOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name)];

    // ---- AC 13: every emitted line has a non-empty uid -------------------------------------------

    [Fact]
    public void EveryEmittedLine_HasNonEmptyUid()
    {
        var lines = WorkspaceTranscriptLine
            .ChainMessages(FiveMessageKinds())
            .Append(WorkspaceTranscriptLine.ForState(ThreadId, "title", "Fix the login bug", DateTimeOffset.UnixEpoch))
            .ToList();

        lines.Should().HaveCount(6);
        foreach (var line in lines)
        {
            line.Uid.Should().NotBeNullOrWhiteSpace();
            line.Uid.Should().HaveLength(WorkspaceTranscriptLine.UidLength);
            line.Uid.Should().MatchRegex("^[a-z2-7]+$", "uid is base32 lowercase, RFC 4648 alphabet");
            Parse(line).GetProperty("uid").GetString().Should().Be(line.Uid);
        }
    }

    [Fact]
    public void Uid_IsUnique_AcrossDistinctMessages()
    {
        var lines = WorkspaceTranscriptLine.ChainMessages(FiveMessageKinds());

        lines.Select(l => l.Uid).Distinct(StringComparer.Ordinal).Should().HaveCount(lines.Count);
    }

    // ---- AC 14: the same message yields an identical uid every time ------------------------------

    [Fact]
    public void Uid_IsIdentical_AcrossThreeSeparateDerivations()
    {
        // Stands in for flushes N, N+1, N+2: the derivation is over PersistedMessage.Id alone, so a
        // re-read of the same stored row must land on the same uid or the append watermark is useless.
        var message = Persisted("id-text");

        var first = WorkspaceTranscriptLine.ForMessage(message).Uid;
        var second = WorkspaceTranscriptLine.ForMessage(message with { }, parentUid: "somethingelse").Uid;
        var third = WorkspaceTranscriptLine.ForMessage(Persisted("id-text"), agent: "reviewer").Uid;

        second.Should().Be(first);
        third.Should().Be(first);
        WorkspaceTranscriptLine.DeriveUid("id-text").Should().Be(first);
    }

    [Fact]
    public void DeriveUid_IsSensitiveToTheWholeSeed()
    {
        WorkspaceTranscriptLine.DeriveUid("id-text").Should().NotBe(WorkspaceTranscriptLine.DeriveUid("id-texu"));
    }

    // ---- AC 15: role classifies all five Role members, including None ----------------------------

    [Theory]
    [InlineData(Role.None)]
    [InlineData(Role.User)]
    [InlineData(Role.Assistant)]
    [InlineData(Role.System)]
    [InlineData(Role.Tool)]
    public void Role_ClassifiesEveryRoleMember_IncludingNone(Role role)
    {
        // The envelope's role is PascalCase; the same role inside message_json is lowercase. Both
        // casings must land on the same canonical member — None included, since it is a real member
        // and not an error code.
        var pascal = WorkspaceTranscriptLine.ForMessage(Persisted("id", role: role.ToString()));
        var lower = WorkspaceTranscriptLine.ForMessage(Persisted("id", role: role.ToString().ToLowerInvariant()));

        pascal.Role.Should().Be(role.ToString());
        lower.Role.Should().Be(role.ToString());
        Parse(pascal).GetProperty("role").GetString().Should().Be(role.ToString());
    }

    [Fact]
    public void Role_FallsBackToNone_ForAnUnrecognizedOrAbsentValue()
    {
        WorkspaceTranscriptLine.NormalizeRole(null).Should().Be(nameof(Role.None));
        WorkspaceTranscriptLine.NormalizeRole("").Should().Be(nameof(Role.None));
        WorkspaceTranscriptLine.NormalizeRole("developer").Should().Be(nameof(Role.None));
    }

    [Fact]
    public void Role_CoversEveryDeclaredRoleMember()
    {
        // Guard: if a sixth Role member is ever added, this fails and forces the classification above
        // to be extended rather than silently collapsing the new member to None.
        Enum.GetValues<Role>().Should().HaveCount(5);
    }

    // ---- AC 18: uniform key set across five message kinds ----------------------------------------

    [Fact]
    public void MessageLines_ShareOneUniformKeySet_AcrossFiveMessageKinds()
    {
        var lines = WorkspaceTranscriptLine.ChainMessages(FiveMessageKinds());
        var expected = new[]
        {
            "schema_version",
            "type",
            "uid",
            "parent_uid",
            "agent",
            "thread_id",
            "run_id",
            "parent_run_id",
            "generation_id",
            "message_order_idx",
            "timestamp",
            "message_type",
            "role",
            "id",
            "message_json",
        };

        foreach (var line in lines)
        {
            // Key ORDER as well as key SET: absent values are written as JSON null, never omitted, so a
            // columnar reader sees one stable schema no matter which message kind a line carries.
            KeysOf(Parse(line)).Should().Equal(expected, $"kind {line.MessageType} must not change the key set");
        }
    }

    [Fact]
    public void AbsentMessageFields_AreWrittenAsExplicitNulls_NotOmitted()
    {
        var line = WorkspaceTranscriptLine.ForMessage(
            Persisted("id-tool-result", generationId: null, messageOrderIdx: null));
        var json = Parse(line);

        json.GetProperty("parent_uid").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("agent").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("parent_run_id").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("generation_id").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("message_order_idx").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void SchemaVersion_IsStampedOnEveryLine()
    {
        Parse(WorkspaceTranscriptLine.ForMessage(Persisted("id")))
            .GetProperty("schema_version").GetInt32().Should().Be(WorkspaceTranscriptLine.CurrentSchemaVersion);
        Parse(WorkspaceTranscriptLine.ForState(ThreadId, "mode", "chat", DateTimeOffset.UnixEpoch))
            .GetProperty("schema_version").GetInt32().Should().Be(WorkspaceTranscriptLine.CurrentSchemaVersion);
    }

    // ---- AC 20: message_json is an opaque STRING, and stays recoverable --------------------------

    [Fact]
    public void MessageJson_IsSerializedAsAString_NotAnInlinedObject()
    {
        const string Payload =
            """{"role":"assistant","message_id":"m-42","in_response_to":"m-41","text":"answer"}""";

        var json = Parse(WorkspaceTranscriptLine.ForMessage(Persisted("id-text", messageJson: Payload)));
        var messageJson = json.GetProperty("message_json");

        messageJson.ValueKind.Should().Be(
            JsonValueKind.String,
            "message_json is opaque — an object-or-string union is exactly what a tolerant JSONL reader drops");
        messageJson.GetString().Should().Be(Payload);

        // AC 20 proper: message_id / in_response_to remain recoverable by parsing that string.
        var inner = JsonDocument.Parse(messageJson.GetString()!).RootElement;
        inner.GetProperty("message_id").GetString().Should().Be("m-42");
        inner.GetProperty("in_response_to").GetString().Should().Be("m-41");
    }

    [Fact]
    public void SerializedLine_IsExactlyOneLine_EvenWhenMessageJsonContainsNewlines()
    {
        var payload = "{\"role\":\"assistant\",\"text\":\"line one\nline two\"}";

        var serialized = WorkspaceTranscriptLine.Serialize(
            WorkspaceTranscriptLine.ForMessage(Persisted("id-text", messageJson: payload)));

        serialized.Should().NotContain("\n", "a JSONL record must occupy exactly one physical line");
        JsonDocument.Parse(serialized).RootElement.GetProperty("message_json").GetString().Should().Be(payload);
    }

    // ---- AC 21: agent is the display name, never a provider message id ---------------------------

    [Fact]
    public void Agent_IsTheProvenanceDisplayName_NeverTheProviderMessageId()
    {
        // The display name is read off SubAgentProvenance.NameKey — the only place a human-authored
        // sub-agent name is durably stamped. PersistedMessage.FromAgent holds a provider message id.
        var metadata = new ThreadMetadata
        {
            ThreadId = SubAgentProvenance.ThreadIdPrefix + "child-1",
            LastUpdated = 1_700_000_000_000,
            Properties = ImmutableDictionary<string, object>
                .Empty.Add(SubAgentProvenance.ParentThreadIdKey, ThreadId)
                .Add(SubAgentProvenance.NameKey, "reviewer"),
        };
        var displayName = SubAgentProvenance.TryProject(metadata)!.Name;
        displayName.Should().Be("reviewer");

        var line = WorkspaceTranscriptLine.ForMessage(Persisted("id-text"), agent: displayName);

        line.Agent.Should().Be("reviewer");
        WorkspaceTranscriptLine.Serialize(line).Should().NotContain(
            ProviderMessageId,
            "FromAgent carries a provider message id and must never reach the transcript envelope");
    }

    [Fact]
    public void Agent_IsNull_OnAMainFileLine()
    {
        WorkspaceTranscriptLine.ForMessage(Persisted("id-text")).Agent.Should().BeNull();
    }

    // ---- AC 9: the ASCII slug and the file leaves -------------------------------------------------

    [Theory]
    [InlineData("Fix the login bug", "fix-the-login-bug")]
    [InlineData("a/b/c", "a-b-c")]
    [InlineData("a\\b\\c", "a-b-c")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("/leading/slash", "leading-slash")]
    [InlineData("..", "")]
    [InlineData("/", "")]
    [InlineData("  ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    // Unicode is DROPPED, not passed through — this is the whole reason none of the repo's six
    // char.IsLetterOrDigit-based slug helpers could be reused here.
    [InlineData("修复登录错误", "")]
    [InlineData("café résumé", "caf-r-sum")]
    [InlineData("Ünïcödé", "n-c-d")]
    [InlineData("Trailing---hyphens---", "trailing-hyphens")]
    [InlineData("multi   space", "multi-space")]
    public void Slug_ProducesAsciiOnlyFlatSegments(string? title, string expected)
    {
        var slug = WorkspaceTranscriptLine.Slug(title);

        slug.Should().Be(expected);
        slug.Should().MatchRegex("^[a-z0-9-]*$");
        slug.Should().NotStartWith("-").And.NotEndWith("-");
    }

    [Theory]
    [InlineData("a/b/c")]
    [InlineData("a\\b\\c")]
    [InlineData("../../etc/passwd")]
    [InlineData("/leading/slash")]
    [InlineData("..")]
    [InlineData("修复登录错误")]
    [InlineData("café")]
    [InlineData("")]
    [InlineData(null)]
    public void MainFileLeaf_IsAlwaysAFlatLegalNonEmptyAsciiName(string? title)
    {
        var leaf = WorkspaceTranscriptLine.MainFileLeaf(title, WorkspaceTranscriptLine.ShortId(ThreadId));

        leaf.Should().NotBeNullOrEmpty();
        leaf.Should().MatchRegex("^[a-z0-9-]+$");
        leaf.Should().NotContain("/").And.NotContain("\\").And.NotContain("..");
    }

    [Fact]
    public void MainFileLeaf_FallsBackToTheShortThreadId_WhenTheTitleSlugsToNothing()
    {
        var shortId = WorkspaceTranscriptLine.ShortId(ThreadId);

        WorkspaceTranscriptLine.MainFileLeaf("修复登录错误", shortId).Should().Be(shortId);
        WorkspaceTranscriptLine.MainFileLeaf("Fix the login bug", shortId).Should().Be($"fix-the-login-bug-{shortId}");
    }

    [Fact]
    public void MainFileLeaf_TruncatesAnUnboundedTitle()
    {
        var leaf = WorkspaceTranscriptLine.MainFileLeaf(new string('x', 500), "abcd");

        leaf.Length.Should().BeLessThan(120, "a path component has to stay under the filesystem's 255-byte cap");
    }

    [Fact]
    public void AgentFileLeaf_PinsToAgentPrefix_WhenTheNameIsNullOrSlugsToNothing()
    {
        var shortAgentId = WorkspaceTranscriptLine.ShortId("agent-7");

        WorkspaceTranscriptLine.AgentFileLeaf(null, shortAgentId)
            .Should().Be($"{WorkspaceTranscriptLine.UnnamedAgentLeafPrefix}-{shortAgentId}");
        WorkspaceTranscriptLine.AgentFileLeaf("修复", shortAgentId)
            .Should().Be($"{WorkspaceTranscriptLine.UnnamedAgentLeafPrefix}-{shortAgentId}");
        WorkspaceTranscriptLine.AgentFileLeaf("Reviewer", shortAgentId).Should().Be($"reviewer-{shortAgentId}");
    }

    [Fact]
    public void AgentFileLeaf_KeepsTwoAgentsWithTheSameDisplayNameApart()
    {
        var first = WorkspaceTranscriptLine.AgentFileLeaf("reviewer", WorkspaceTranscriptLine.ShortId("agent-a"));
        var second = WorkspaceTranscriptLine.AgentFileLeaf("reviewer", WorkspaceTranscriptLine.ShortId("agent-b"));

        first.Should().NotBe(second);
    }

    [Fact]
    public void ShortId_IsDeterministicAndAsciiLegal()
    {
        var shortId = WorkspaceTranscriptLine.ShortId(SubAgentProvenance.ThreadIdPrefix + "child-1");

        shortId.Should().HaveLength(WorkspaceTranscriptLine.ShortIdLength);
        shortId.Should().MatchRegex("^[a-z2-7]+$");
        shortId.Should().Be(WorkspaceTranscriptLine.ShortId(SubAgentProvenance.ThreadIdPrefix + "child-1"));
        // Ids in this sample share the "subagent-" prefix, so a prefix slice would collide where a
        // hash does not.
        shortId.Should().NotBe(WorkspaceTranscriptLine.ShortId(SubAgentProvenance.ThreadIdPrefix + "child-2"));
    }

    // ---- AC 22: state lines are constructible and distinguishable by type ------------------------

    [Theory]
    [InlineData("title", "Fix the login bug")]
    [InlineData("mode", "chat")]
    [InlineData("provider", "claude-opus-5")]
    public void StateLine_IsConstructible_AndDistinguishableFromAMessageLineByType(string key, string value)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        var state = WorkspaceTranscriptLine.ForState(ThreadId, key, value, at, parentUid: "aaaaaaaa");
        var json = Parse(state);

        state.Type.Should().Be(WorkspaceTranscriptLine.StateLineType);
        json.GetProperty("type").GetString().Should().Be("state");
        json.GetProperty("key").GetString().Should().Be(key);
        json.GetProperty("value").GetString().Should().Be(value);
        json.GetProperty("parent_uid").GetString().Should().Be("aaaaaaaa");
        json.GetProperty("thread_id").GetString().Should().Be(ThreadId);

        // A state line carries none of PersistedMessage's seven required members — that is exactly why
        // the message-specific envelope fields are nullable.
        KeysOf(json).Should().Equal("schema_version", "type", "uid", "parent_uid", "thread_id", "timestamp", "key", "value");
    }

    [Fact]
    public void StateAndMessageLines_AreTellableApartByTypeAlone()
    {
        var lines = new[]
        {
            WorkspaceTranscriptLine.ForMessage(Persisted("id-text")),
            WorkspaceTranscriptLine.ForState(ThreadId, "title", "t", DateTimeOffset.UnixEpoch),
        };

        lines.Count(l => l.Type == WorkspaceTranscriptLine.MessageLineType).Should().Be(1);
        lines.Count(l => l.Type == WorkspaceTranscriptLine.StateLineType).Should().Be(1);
    }

    [Fact]
    public void StateLine_ForTheSameKeyAtADifferentInstant_GetsADistinctUid()
    {
        var first = WorkspaceTranscriptLine.ForState(ThreadId, "title", "one", DateTimeOffset.UnixEpoch);
        var again = WorkspaceTranscriptLine.ForState(ThreadId, "title", "one", DateTimeOffset.UnixEpoch);
        var later = WorkspaceTranscriptLine.ForState(
            ThreadId, "title", "one", DateTimeOffset.UnixEpoch.AddSeconds(1));

        again.Uid.Should().Be(first.Uid, "the derivation is pure");
        later.Uid.Should().NotBe(first.Uid);
    }

    // ---- AC 16: parent_uid chains over a sequence -------------------------------------------------

    [Fact]
    public void ChainMessages_ChainsEachLineToItsPredecessorInStoreOrder()
    {
        var lines = WorkspaceTranscriptLine.ChainMessages(FiveMessageKinds());

        lines[0].ParentUid.Should().BeNull("the first line of the main file has no predecessor");
        for (var i = 1; i < lines.Count; i++)
        {
            lines[i].ParentUid.Should().Be(lines[i - 1].Uid);
        }

        // The chain has to resolve: walking parent_uid backwards from the tail visits every line once.
        var byUid = lines.ToDictionary(l => l.Uid, StringComparer.Ordinal);
        var walked = 0;
        var cursor = lines[^1];
        while (true)
        {
            walked++;
            if (cursor.ParentUid is null)
            {
                break;
            }

            byUid.Should().ContainKey(cursor.ParentUid);
            cursor = byUid[cursor.ParentUid];
        }

        walked.Should().Be(lines.Count);
    }

    [Fact]
    public void ChainMessages_AnchorsASubAgentFileToTheMainFile()
    {
        var main = WorkspaceTranscriptLine.ChainMessages(FiveMessageKinds());
        var anchor = main[2].Uid;

        var agentLines = WorkspaceTranscriptLine.ChainMessages(
            [Persisted("sub-1"), Persisted("sub-2")],
            agent: "reviewer",
            rootParentUid: anchor);

        // AC 16 as reworded: the chain resolves within the conversation's FILE SET — an _agents/ root
        // line points into the main file, not into its own file.
        agentLines[0].ParentUid.Should().Be(anchor);
        agentLines[1].ParentUid.Should().Be(agentLines[0].Uid);
        agentLines.Should().OnlyContain(l => l.Agent == "reviewer");
    }

    [Fact]
    public void ChainMessages_OnAnEmptySequence_ReturnsNoLines()
    {
        WorkspaceTranscriptLine.ChainMessages([]).Should().BeEmpty();
    }

    // ---- envelope field mapping -------------------------------------------------------------------

    [Fact]
    public void MessageLine_CarriesTheStoreFieldsVerbatim()
    {
        var message = Persisted("id-text", parentRunId: "run-0", messageOrderIdx: 3);

        var json = Parse(WorkspaceTranscriptLine.ForMessage(message, parentUid: "bbbbbbbb", agent: "reviewer"));

        json.GetProperty("type").GetString().Should().Be("message");
        json.GetProperty("agent").GetString().Should().Be("reviewer");
        json.GetProperty("thread_id").GetString().Should().Be(ThreadId);
        json.GetProperty("run_id").GetString().Should().Be(RunId);
        json.GetProperty("parent_run_id").GetString().Should().Be("run-0");
        json.GetProperty("generation_id").GetString().Should().Be("gen-1");
        json.GetProperty("message_order_idx").GetInt32().Should().Be(3);
        json.GetProperty("message_type").GetString().Should().Be("TextMessage");
        json.GetProperty("id").GetString().Should().Be("id-text");
        json.GetProperty("timestamp").GetString().Should().Be("2023-11-14T22:13:20.0000000Z");
    }

    [Fact]
    public void FormatTimestamp_IsIso8601Utc()
    {
        WorkspaceTranscriptLine.FormatTimestamp(0).Should().Be("1970-01-01T00:00:00.0000000Z");
    }
}
