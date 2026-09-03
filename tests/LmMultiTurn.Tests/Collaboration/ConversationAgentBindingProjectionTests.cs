using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.Collaboration;

/// <summary>
/// The durable half of restart reconciliation: what a binding capture writes, what it refuses to
/// write, and what it refuses to read back.
/// </summary>
public class ConversationAgentBindingProjectionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private const string RootA = "conv-a";
    private const string RootB = "conv-b";

    private static CollaborationNodeRecord Node(
        string collaborationId,
        string agentId,
        string name,
        string parentAgentId
    ) =>
        new()
        {
            AgentId = agentId,
            CollaborationId = collaborationId,
            Name = name,
            ParentAgentId = parentAgentId,
            AncestorAgentIds = [parentAgentId],
            Kind = AgentKind.SubAgent,
            Role = "researcher",
            Description = "finds things out",
            StructuralDepth = 1,
            DelegationDepth = 1,
            Status = AgentCollaborationStatuses.Running,
            SpawnedAt = Noon.AddMinutes(-5),
        };

    private static AgentIdentityBindingSet Binding(
        string collaborationId = RootA,
        string agentId = "agent-1",
        string name = "researcher",
        DateTimeOffset? capturedAt = null
    ) =>
        new()
        {
            CollaborationId = collaborationId,
            RootAgentId = collaborationId,
            CapturedAtUtc = capturedAt ?? Noon,
            Agents = [Node(collaborationId, agentId, name, collaborationId)],
            OpenObligations =
            [
                new OpenObligationRecord
                {
                    MessageId = "agentmsg-1",
                    FromAgentId = collaborationId,
                    ToAgentId = agentId,
                    MessageType = AgentMessageType.Question,
                    AdmittedAt = Noon.AddMinutes(-1),
                },
            ],
        };

    /// <summary>
    /// Creates the conversation's metadata row the way a pooled agent already does. The projection
    /// deliberately never creates one, so a test that expects a write to LAND must seed it first.
    /// </summary>
    private static Task SeedConversationAsync(IConversationStore store, string threadId) =>
        store.UpdateMetadataAsync(
            threadId,
            existing =>
                existing
                ?? new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = 0,
                    TenantId = "tenant-1",
                }
        );

    private static Task SetRawPropertyAsync(IConversationStore store, string threadId, string rawJson) =>
        store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                    ConversationAgentBindingProjection.PropertyKey,
                    rawJson
                );
                return existing is not null
                    ? existing with
                    {
                        Properties = properties,
                    }
                    : new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = 0,
                        TenantId = "tenant-1",
                        Properties = properties,
                    };
            }
        );

    [Fact]
    public async Task RoundTrips_ThroughInMemoryStore()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootA);

        var original = Binding();
        await ConversationAgentBindingProjection.SaveAsync(store, original);
        var loaded = await ConversationAgentBindingProjection.LoadAsync(store, RootA);

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task RoundTrips_ThroughFileStore()
    {
        // The file store re-hydrates property-bag values as JsonElement rather than handing back the
        // string that was written, so this is the leg that proves the read is store-agnostic.
        var dir = Path.Combine(Path.GetTempPath(), $"collab_binding_{Guid.NewGuid():N}");
        var store = new FileConversationStore(dir);
        await SeedConversationAsync(store, RootA);

        var original = Binding();
        await ConversationAgentBindingProjection.SaveAsync(store, original);
        var loaded = await ConversationAgentBindingProjection.LoadAsync(store, RootA);

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(original);

        // Detach-then-delete rather than recursive-delete in place, and deliberately NOT in a finally:
        // a throw from a finally REPLACES the assertion failure unwinding through it.
        DetachedStoreTeardown.Purge(dir);
    }

    [Fact]
    public async Task SaveAsync_NeverMintsAMetadataRow()
    {
        // A row minted here would carry no TenantId / OwnerUserId / Visibility, and the authorizer
        // reads a null TenantId as conversation_not_found — an unstamped row is one NOBODY can read.
        var store = new InMemoryConversationStore();

        await ConversationAgentBindingProjection.SaveAsync(store, Binding());

        (await store.LoadMetadataAsync(RootA)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_ReadsACorruptBlobAsAbsent()
    {
        var store = new InMemoryConversationStore();
        await SetRawPropertyAsync(store, RootA, "{ this is not json");

        (await ConversationAgentBindingProjection.LoadAsync(store, RootA)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwriteABlobWrittenByANewerSchema()
    {
        // Written THROUGH the record type, not as a hand-spelled literal: the point of the shared
        // SchemaVersionPropertyName constant is that the member the record writes and the member the
        // guard probes are the same one, and a literal here would pass even if they had drifted apart.
        var store = new InMemoryConversationStore();
        var fromTheFuture = Binding() with
        {
            SchemaVersion = AgentIdentityBindingSet.CurrentSchemaVersion + 1,
            RootAgentId = "written-by-a-newer-build",
        };
        await SetRawPropertyAsync(store, RootA, JsonSerializer.Serialize(fromTheFuture));

        await ConversationAgentBindingProjection.SaveAsync(store, Binding(capturedAt: Noon.AddHours(1)));

        var raw = await store.LoadMetadataAsync(RootA);
        var persisted = JsonDocument.Parse(
            raw!.Properties![ConversationAgentBindingProjection.PropertyKey].ToString()!
        );
        persisted
            .RootElement.GetProperty("root_agent_id")
            .GetString()
            .Should()
            .Be("written-by-a-newer-build", "an older build must not clobber a forward-compatible blob");
    }

    [Fact]
    public async Task SaveAsync_DropsACaptureOlderThanThePersistedOne()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootA);
        await ConversationAgentBindingProjection.SaveAsync(store, Binding(capturedAt: Noon.AddMinutes(10)));

        await ConversationAgentBindingProjection.SaveAsync(store, Binding(name: "stale", capturedAt: Noon));

        var loaded = await ConversationAgentBindingProjection.LoadAsync(store, RootA);
        loaded!.Agents.Should().ContainSingle().Which.Name.Should().Be("researcher");
        loaded.CapturedAtUtc.Should().Be(Noon.AddMinutes(10));
    }

    [Fact]
    public async Task SaveAsync_AcceptsACaptureAtTheSameInstant()
    {
        // Equal instants are accepted, not rejected: at coarse clock resolution successive captures
        // routinely share a tick, and treating those as stale would drop every write inside one tick.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootA);
        await ConversationAgentBindingProjection.SaveAsync(store, Binding());

        await ConversationAgentBindingProjection.SaveAsync(store, Binding(name: "renamed"));

        var loaded = await ConversationAgentBindingProjection.LoadAsync(store, RootA);
        loaded!.Agents.Should().ContainSingle().Which.Name.Should().Be("renamed");
    }

    [Fact]
    public async Task LoadAsync_IgnoresASetThatBelongsToADifferentRoot()
    {
        // #705 made agent ids ordinals minted per ROOT conversation, so EVERY conversation has an
        // `agent-1`. A binding document therefore only names agents once its own collaboration id is
        // read alongside it. If the row a set is read from is not the collaboration the set describes,
        // applying it would tombstone one hierarchy's agents in another hierarchy's directory — the
        // names and ids would all look plausible, which is exactly what makes it dangerous.
        var store = new InMemoryConversationStore();
        await SetRawPropertyAsync(store, RootB, JsonSerializer.Serialize(Binding(collaborationId: RootA)));

        (await ConversationAgentBindingProjection.LoadAsync(store, RootB)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_ReadsTheSetOnItsOwnRoot()
    {
        // The other half of the pair above: the guard must reject a foreign set WITHOUT rejecting the
        // set that legitimately lives there, or it would simply disable the whole feature.
        var store = new InMemoryConversationStore();
        await SetRawPropertyAsync(store, RootB, JsonSerializer.Serialize(Binding(collaborationId: RootB)));

        (await ConversationAgentBindingProjection.LoadAsync(store, RootB)).Should().NotBeNull();
    }

    [Fact]
    public async Task LoadAsync_ReadsABlobWrittenByANewerSchemaAsAbsent()
    {
        var store = new InMemoryConversationStore();
        await SetRawPropertyAsync(
            store,
            RootA,
            JsonSerializer.Serialize(
                Binding() with
                {
                    SchemaVersion = AgentIdentityBindingSet.CurrentSchemaVersion + 1,
                }
            )
        );

        (await ConversationAgentBindingProjection.LoadAsync(store, RootA)).Should().BeNull();
    }

    [Fact]
    public async Task TwoRootsEachHoldingAgentOne_KeepTheirOwnBindings()
    {
        // The store is shared; the roots are not. Each row is read back under its own collaboration,
        // and neither root's `agent-1` is the other's.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootA);
        await SeedConversationAsync(store, RootB);

        await ConversationAgentBindingProjection.SaveAsync(store, Binding(RootA, "agent-1", "researcher"));
        await ConversationAgentBindingProjection.SaveAsync(store, Binding(RootB, "agent-1", "writer"));

        var a = await ConversationAgentBindingProjection.LoadAsync(store, RootA);
        var b = await ConversationAgentBindingProjection.LoadAsync(store, RootB);

        a!.Agents.Should().ContainSingle().Which.Name.Should().Be("researcher");
        b!.Agents.Should().ContainSingle().Which.Name.Should().Be("writer");
        a.Agents[0].AgentId.Should().Be(b.Agents[0].AgentId, "both roots really do hold an agent-1");
    }
}
