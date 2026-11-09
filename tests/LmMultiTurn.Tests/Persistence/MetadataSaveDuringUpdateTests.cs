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
        var generous = TimeSpan.FromSeconds(10);
        await using var harness = new ConversationStoreHarness();
        var store = harness.Open(kind);
        using var updateEntered = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();
        var saveAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? save = null;

        var update = Task.Run(() =>
            store.UpdateMetadataAsync(
                "thread-1",
                existing =>
                {
                    updateEntered.Set();
                    _ = releaseUpdate.Wait(generous);
                    return (existing ?? new ThreadMetadata { ThreadId = "thread-1", LastUpdated = 1 }) with
                    {
                        LatestRunId = "from-update",
                    };
                }
            )
        );

        try
        {
            updateEntered.Wait(generous).Should().BeTrue("the update has to reach its read-modify-write window");

            save = Task.Run(() =>
            {
                saveAttempted.SetResult();
                return store.SaveMetadataAsync(
                    "thread-1",
                    new ThreadMetadata
                    {
                        ThreadId = "thread-1",
                        LastUpdated = 2,
                        LatestRunId = "from-save",
                    }
                );
            });
            await saveAttempted.Task.WaitAsync(generous);

            // The window starts once the save is really under way. A save that skips the lock either ends
            // here, or lands inside the update and is overwritten, which the last assertion catches.
            (await Task.WhenAny(save, Task.Delay(250)))
                .Should()
                .NotBeSameAs(save, "a save must wait for an update that is between its read and its write");
        }
        finally
        {
            releaseUpdate.Set();
        }

        await update.WaitAsync(generous);
        await save.WaitAsync(generous);

        (await store.LoadMetadataAsync("thread-1"))!
            .LatestRunId.Should()
            .Be("from-save", "the save came after the update finished, so it must be what remains");
    }
}
