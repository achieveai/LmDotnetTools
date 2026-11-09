using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// #745: a plain save must not land between the read and the write of an atomic update, or the update
/// writes the row it read back over the save and the save is lost with no error.
/// </summary>
public class MetadataSaveDuringUpdateTests
{
    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Save_DuringAnUpdatesReadModifyWrite_IsNotLost(string kind)
    {
        await using var harness = new ConversationStoreHarness();
        var store = harness.Open(kind);
        using var updateEntered = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();

        var update = Task.Run(() =>
            store.UpdateMetadataAsync(
                "thread-1",
                existing =>
                {
                    updateEntered.Set();
                    releaseUpdate.Wait();
                    return (existing ?? new ThreadMetadata { ThreadId = "thread-1", LastUpdated = 1 }) with
                    {
                        LatestRunId = "from-update",
                    };
                }
            )
        );
        updateEntered.Wait();

        var save = Task.Run(() =>
            store.SaveMetadataAsync(
                "thread-1",
                new ThreadMetadata
                {
                    ThreadId = "thread-1",
                    LastUpdated = 2,
                    LatestRunId = "from-save",
                }
            )
        );

        // The bounded wait only matters if the save wrongly finishes while the update holds its window.
        (await Task.WhenAny(save, Task.Delay(150)))
            .Should()
            .NotBeSameAs(save, "a save must wait for an update that is between its read and its write");

        releaseUpdate.Set();
        await update;
        await save;

        (await store.LoadMetadataAsync("thread-1"))!
            .LatestRunId.Should()
            .Be("from-save", "the save came after the update finished, so it must be what remains");
    }
}
