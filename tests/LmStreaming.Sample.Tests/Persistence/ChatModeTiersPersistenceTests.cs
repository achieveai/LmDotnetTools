namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// The update rule for a mode's skill <c>tiers</c>, driven with REST JSON bodies: the UI's PUT never sends
/// <c>tiers</c>, so every UI save relies on omission keeping them.
/// </summary>
public sealed class ChatModeTiersPersistenceTests : IDisposable
{
    private static readonly JsonSerializerOptions Rest = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "lmstreaming-chatmodes-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Fails if an update without "tiers" un-tiers the mode (Tiers = mode.Tiers), if {} collapses to null, if a
    // copy drops the tiers, or if an explicit null no longer clears them.
    [Fact]
    public async Task Update_OmittedTiersKeeps_NullClears_EmptyObjectStaysOn_CopyCarries()
    {
        _ = Directory.CreateDirectory(_dir);
        var store = new FileChatModeStore(_dir);
        var created = await store.CreateModeAsync(
            Body(
                """
                {
                  "name": "Tiered",
                  "systemPrompt": "primary",
                  "tiers": { "loaded": ["skill:alpha:a"], "subAgent": { "loaded": ["skill:beta:c"] } }
                }
                """
            )
        );
        var tiers = created.Tiers;
        tiers.Should().NotBeNull("guard: the create must store the tiers");

        var omitted = await store.UpdateModeAsync(
            created.Id,
            Body("""{ "name": "Tiered", "systemPrompt": "edited in the UI" }""")
        );
        omitted.Tiers.Should().BeEquivalentTo(tiers, "an update that omits tiers keeps them");
        (await store.GetModeAsync(created.Id))!.Tiers.Should().BeEquivalentTo(tiers);

        var copy = await store.CopyModeAsync(created.Id, "Tiered copy");
        copy.Tiers.Should().BeEquivalentTo(tiers);

        var empty = await store.UpdateModeAsync(
            created.Id,
            Body("""{ "name": "Tiered", "systemPrompt": "primary", "tiers": {} }""")
        );
        empty.Tiers.Should().NotBeNull("an empty tiers object keeps the mode tiered, with nothing loaded");
        empty.Tiers!.Loaded.Should().BeNull();

        var cleared = await store.UpdateModeAsync(
            created.Id,
            Body("""{ "name": "Tiered", "systemPrompt": "primary", "tiers": null }""")
        );
        cleared.Tiers.Should().BeNull("an explicit null clears the tiers");
    }

    private static ChatModeCreateUpdate Body(string json) =>
        JsonSerializer.Deserialize<ChatModeCreateUpdate>(json, Rest)
        ?? throw new InvalidOperationException("The body did not deserialize.");
}
