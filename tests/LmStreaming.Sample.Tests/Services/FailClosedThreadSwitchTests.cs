using System.Collections.Immutable;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The per-conversation security switches (<see cref="ConversationDedicatedSandbox"/> and
/// <see cref="ConversationReadOnlyEgress"/>) decode a stored boolean in either form the stores hand back, read
/// absence as off, and refuse anything else rather than read it as off. An in-memory store keeps the stored object
/// as it is, so each branch is reached on purpose.
/// </summary>
public class FailClosedThreadSwitchTests
{
    private const string ThreadId = "thread-switch";

    public static TheoryData<string, object, bool> Readable =>
        new()
        {
            { ConversationDedicatedSandbox.PropertyKey, true, true },
            { ConversationDedicatedSandbox.PropertyKey, false, false },
            { ConversationDedicatedSandbox.PropertyKey, JsonSerializer.SerializeToElement(true), true },
            { ConversationDedicatedSandbox.PropertyKey, JsonSerializer.SerializeToElement(false), false },
            { ConversationReadOnlyEgress.PropertyKey, false, false },
            { ConversationReadOnlyEgress.PropertyKey, JsonSerializer.SerializeToElement(false), false },
        };

    public static TheoryData<string, object?, string> Unreadable =>
        new()
        {
            { ConversationDedicatedSandbox.PropertyKey, "true", "String" },
            { ConversationDedicatedSandbox.PropertyKey, null, "null" },
            { ConversationDedicatedSandbox.PropertyKey, new Dictionary<string, string>(), "Dictionary" },
            { ConversationDedicatedSandbox.PropertyKey, JsonSerializer.SerializeToElement("true"), "String" },
            { ConversationDedicatedSandbox.PropertyKey, JsonSerializer.SerializeToElement((object?)null), "Null" },
            {
                ConversationDedicatedSandbox.PropertyKey,
                JsonSerializer.SerializeToElement(new { on = true }),
                "Object"
            },
            { ConversationReadOnlyEgress.PropertyKey, "true", "String" },
        };

    [Theory]
    [MemberData(nameof(Readable))]
    public async Task A_stored_boolean_reads_as_itself_in_either_form(string key, object stored, bool expected)
    {
        var store = await SeedAsync(key, stored);

        (await ReadAsync(key, store, ThreadId)).Should().Be(expected);
    }

    [Theory]
    [InlineData("dedicated")]
    [InlineData("egress")]
    public async Task An_absent_switch_or_an_unknown_thread_reads_as_off(string which)
    {
        var key =
            which == "dedicated" ? ConversationDedicatedSandbox.PropertyKey : ConversationReadOnlyEgress.PropertyKey;
        var store = await SeedAsync(key: null, stored: null);

        (await ReadAsync(key, store, ThreadId)).Should().BeFalse();
        (await ReadAsync(key, store, "thread-unknown")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task Any_other_stored_value_is_refused_naming_what_reading_it_as_off_would_do(
        string key,
        object? stored,
        string kind
    )
    {
        var store = await SeedAsync(key, stored);

        var read = () => ReadAsync(key, store, ThreadId);

        var refusal = (await read.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        refusal.Should().Contain(key).And.Contain(kind).And.Contain("refusing to build its agent");
        refusal
            .Should()
            .Contain(
                key == ConversationDedicatedSandbox.PropertyKey ? "run it in the shared sandbox" : "give it full egress"
            );
    }

    private static Task<bool> ReadAsync(string key, IConversationStore store, string threadId) =>
        key == ConversationDedicatedSandbox.PropertyKey
            ? ConversationDedicatedSandbox.ReadAsync(store, threadId)
            : ConversationReadOnlyEgress.ReadAsync(store, threadId);

    private static async Task<InMemoryConversationStore> SeedAsync(string? key, object? stored)
    {
        var properties = ImmutableDictionary<string, object>.Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, "ws-1");
        if (key is not null)
        {
            properties = properties.Add(key, stored!);
        }

        var store = new InMemoryConversationStore();
        await store.UpdateMetadataAsync(
            ThreadId,
            _ => new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = properties,
            }
        );
        return store;
    }
}
