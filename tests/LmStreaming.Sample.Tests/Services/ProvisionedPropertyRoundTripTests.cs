using System.Collections.Immutable;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins every provisioned thread property against the store production actually uses.
/// <para>
/// <c>ThreadMetadata.Properties</c> is <c>ImmutableDictionary&lt;string, object&gt;</c>, and
/// <see cref="FileConversationStore"/> — wired unconditionally in <c>Program.cs</c> — round-trips it
/// through <c>System.Text.Json</c>. A string written at provision is read back as a <c>JsonElement</c>,
/// so a reader that tests <c>raw is string</c> returns null for every value that has actually been
/// persisted, and the property it reads is inert in the only deployment that exists.
/// </para>
/// <para>
/// Every other test of these readers uses <see cref="InMemoryConversationStore"/> or a mock, which hands
/// back the original <see cref="string"/> reference and therefore <b>cannot construct the failing state
/// at all</b>. The fixture, not the assertions, is the gap — so this file exists to make the production
/// store the fixture.
/// </para>
/// <para>
/// If you add another provisioned property, add a case here. A reader tested only against an in-memory
/// store is untested for the only deployment that exists.
/// </para>
/// </summary>
public sealed class ProvisionedPropertyRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"provisioned-props-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes the property bag the way <c>ConversationsController.Provision</c> does.</summary>
    private async Task<FileConversationStore> SeedAsync(
        string threadId,
        params (string Key, string Value)[] properties
    )
    {
        var store = new FileConversationStore(_root);

        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var builder =
                    existing?.Properties?.ToBuilder()
                    ?? ImmutableDictionary.CreateBuilder<string, object>();
                foreach (var (key, value) in properties)
                {
                    builder[key] = value;
                }

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Properties = builder.ToImmutable(),
                };
            }
        );

        return store;
    }

    [Fact]
    public async Task SystemPromptAppendix_survives_the_production_stores_json_round_trip()
    {
        const string Appendix = "REVIEW METHODOLOGY: obey the caller's output contract.";
        var threadId = $"thread-{Guid.NewGuid():N}";
        var store = await SeedAsync(threadId, (SystemPromptAugmenter.AppendixPropertyKey, Appendix));

        var read = await SystemPromptAugmenter.ReadAppendixAsync(store, threadId);

        read.Should()
            .Be(
                Appendix,
                "a value written at provision is read back as a JsonElement, and the reader must "
                    + "handle that or the appendix silently never reaches the model"
            );
    }

    [Fact]
    public async Task ComposedPrompt_carries_the_appendix_after_a_round_trip_through_the_real_store()
    {
        const string Appendix = "REVIEW METHODOLOGY: obey the caller's output contract.";
        var threadId = $"thread-{Guid.NewGuid():N}";
        var store = await SeedAsync(threadId, (SystemPromptAugmenter.AppendixPropertyKey, Appendix));

        var composed = await SystemPromptAugmenter.ComposeAsync(store, threadId, "MODE PROMPT");

        composed.Should().Contain("MODE PROMPT").And.EndWith(Appendix);
    }

    [Fact]
    public async Task Absent_properties_still_read_as_null_after_a_round_trip()
    {
        var threadId = $"thread-{Guid.NewGuid():N}";
        var store = await SeedAsync(threadId, ("sample.unrelated", "value"));

        (await SystemPromptAugmenter.ReadAppendixAsync(store, threadId)).Should().BeNull();
        (await SystemPromptAugmenter.ComposeAsync(store, threadId, "MODE PROMPT"))
            .Should()
            .Be("MODE PROMPT");
    }
}
