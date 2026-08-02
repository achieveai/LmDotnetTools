using System.Reflection;
using System.Text.Json.Nodes;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
///     Proves that <c>agentmessage.persisted.json</c> — the fixture the Vue client's reload tests render
///     from — is byte-for-byte what this server actually produces for an <see cref="AgentMessage"/>.
/// </summary>
/// <remarks>
///     <para>
///         The fixture was hand-written, and a hand-written fixture is a guess about the wire. The client
///         suite that consumes it can be fully green while the real endpoint emits a different shape, so
///         the reload path it claims to protect stays broken behind a passing test. That is not a
///         hypothetical: the persisted envelope is produced by three layers that each impose their own
///         naming (<see cref="MessagePersistenceConverter"/> serializes the message snake_case,
///         <see cref="AgentMessage"/> overrides individual names back to camelCase, and MVC serializes
///         the outer row camelCase), and nothing but this test makes the fixture answer to them.
///     </para>
///     <para>
///         The fixture is therefore treated as an expected value, not as input: the message is built here
///         from literals and pushed through the same converter and projection the
///         <c>GET /api/conversations/{id}/messages</c> route uses, and the result must equal the file.
///         Only <c>id</c> and <c>timestamp</c> are exempt, because the converter mints them fresh
///         (a random GUID and "now"), and <c>_note</c>, which is documentation for the next reader.
///     </para>
/// </remarks>
public class AgentMessageFixtureParityTests
{
    /// <summary>Non-deterministic fields, plus the fixture's own annotation.</summary>
    private static readonly string[] NotComparable = ["_note", "id", "timestamp"];

    private const string ThreadId = "thread-root";
    private const string RunId = "run-1";
    private const string GenerationId = "agentmsg:2f1e9c7c4b1e4a7f9c2d5b8e6a3f0d11";
    private const int MessageOrderIdx = 3;

    /// <summary>
    ///     How MVC serializes a controller's return value: <see cref="JsonSerializerDefaults.Web"/> is
    ///     what <c>AddControllers()</c> installs, and the sample overrides none of it.
    /// </summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    /// <summary>The message the fixture describes, rebuilt from the same literals.</summary>
    private static AgentMessage TheFixturesMessage() =>
        AgentMessage.Create(
            messageId: "am-1",
            agentMessageType: AgentMessageType.Question,
            fromAgentId: "agent-2",
            fromName: "reviewer",
            body: "Which repo should I review first?",
            generationId: GenerationId
        ) with
        {
            RunId = RunId,
            MessageOrderIdx = MessageOrderIdx,
        };

    /// <summary>
    ///     Runs a message through exactly what the messages route runs it through: persistence
    ///     conversion, then the shared transcript projection, then MVC's serializer.
    /// </summary>
    private static JsonObject AsServedByTheApi(IMessage message)
    {
        var persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, RunId);
        var served = TranscriptProjection.Normalize([persisted], excludeReasoning: false).Single();
        return JsonSerializer.SerializeToNode(served, ApiJson)!.AsObject();
    }

    [Fact]
    public void ThePersistedAgentMessage_IsExactlyTheShapeTheClientFixtureClaims()
    {
        var actual = DropUncomparable(AsServedByTheApi(TheFixturesMessage()));
        var expected = DropUncomparable(LoadFixture());

        // messageJson is a string holding JSON, so comparing it as a string would fail on nothing
        // worse than property order. It is lifted out and compared as a document; the envelope around
        // it is then compared on its own.
        var actualMessage = TakeMessageJson(actual);
        var expectedMessage = TakeMessageJson(expected);

        JsonNode.DeepEquals(actualMessage, expectedMessage).Should().BeTrue(
            "the fixture's messageJson must be the serialized AgentMessage this server emits, but it "
                + "is\n{0}\nand the server emits\n{1}",
            expectedMessage.ToJsonString(),
            actualMessage.ToJsonString());

        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            "the fixture's persisted envelope must be what the messages route returns, but it is\n{0}"
                + "\nand the route returns\n{1}",
            expected.ToJsonString(),
            actual.ToJsonString());
    }

    [Fact]
    public void TheCheckedInFixture_ReadsBackAsTheSameAgentMessage()
    {
        // Forward parity alone would still pass if the server wrote a shape it could not itself read
        // back — and reading back is precisely what a reloaded conversation does before the client ever
        // sees it. So the file's own bytes go through the reverse converter and out again: what returns
        // must be an AgentMessage carrying every field the pill renders, and re-serializing it must
        // reproduce the file, which is what makes this lossless rather than merely parseable.
        var fixture = LoadFixture();
        var persisted = new PersistedMessage
        {
            Id = fixture["id"]!.GetValue<string>(),
            ThreadId = ThreadId,
            RunId = RunId,
            Timestamp = fixture["timestamp"]!.GetValue<long>(),
            MessageType = fixture["messageType"]!.GetValue<string>(),
            Role = fixture["role"]!.GetValue<string>(),
            MessageJson = fixture["messageJson"]!.GetValue<string>(),
        };

        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted);

        var agent = restored.Should().BeOfType<AgentMessage>(
            "the persisted discriminator must resolve to the agent type, not a text fallback").Subject;
        agent.MessageId.Should().Be("am-1");
        agent.AgentMessageType.Should().Be(AgentMessageType.Question);
        agent.FromAgentId.Should().Be("agent-2");
        agent.FromName.Should().Be("reviewer");
        agent.Body.Should().Be("Which repo should I review first?");
        agent.Role.Should().Be(Role.User, "the role the client must not branch on first");

        // Whole-record equality is deliberately not used: deserialization stashes the `$type`
        // discriminator in the shadow Metadata dictionary, which the synthesized record equality
        // compares by reference, so no two round-tripped messages ever compare equal.
        var reserialized = JsonNode.Parse(
            MessagePersistenceConverter.ToPersistedMessage(agent, ThreadId, RunId).MessageJson)!;
        JsonNode.DeepEquals(reserialized, JsonNode.Parse(persisted.MessageJson)).Should().BeTrue(
            "reading the fixture and writing it back must not change it, but it became\n{0}",
            reserialized.ToJsonString());
    }

    /// <summary>Removes and returns <c>messageJson</c>, parsed, leaving the envelope behind.</summary>
    private static JsonNode TakeMessageJson(JsonObject row)
    {
        var json = row["messageJson"]!.GetValue<string>();
        _ = row.Remove("messageJson");
        return JsonNode.Parse(json)!;
    }

    /// <summary>Strips the fields no test can pin, so what remains is genuinely comparable.</summary>
    private static JsonObject DropUncomparable(JsonObject row)
    {
        foreach (var field in NotComparable)
        {
            _ = row.Remove(field);
        }

        return row;
    }

    /// <summary>The checked-in fixture, exactly as the client imports it.</summary>
    private static JsonObject LoadFixture() =>
        JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();

    private static string FixturePath() =>
        Path.Combine(
            RepositoryRoot(),
            "samples",
            "LmStreaming.Sample",
            "ClientApp",
            "src",
            "__tests__",
            "fixtures",
            "synthetic",
            "agentmessage.persisted.json");

    /// <summary>
    ///     Walks up from the test assembly to the solution file. The fixture lives in the client's tree,
    ///     which is not copied to the output directory — comparing against the file the client actually
    ///     imports is the entire point, so a copy would defeat it.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        while (dir != null && !File.Exists(Path.Combine(dir, "LmDotnetTools.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
