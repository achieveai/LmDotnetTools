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
        using var saveFinished = new ManualResetEventSlim();
        var saveCalled = 0;
        Exception? saveError = null;

        // Warms the save path, so nothing but the store's lock can make the save below wait.
        await store.SaveMetadataAsync(
            "thread-warm-up",
            new ThreadMetadata { ThreadId = "thread-warm-up", LastUpdated = 1 }
        );

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

        // A thread of its own, because the in-memory store blocks its caller on the lock. A blocked thread
        // can be observed; a save that has only been scheduled cannot.
        var saveThread = new Thread(() =>
        {
            try
            {
                Volatile.Write(ref saveCalled, 1);
                store
                    .SaveMetadataAsync(
                        "thread-1",
                        new ThreadMetadata
                        {
                            ThreadId = "thread-1",
                            LastUpdated = 2,
                            LatestRunId = "from-save",
                        }
                    )
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                saveError = ex;
            }
            finally
            {
                saveFinished.Set();
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            updateEntered.Wait(generous).Should().BeTrue("the update has to reach its read-modify-write window");
            saveThread.Start();

            SpinWait
                .SpinUntil(
                    () =>
                        saveFinished.IsSet
                        || (
                            Volatile.Read(ref saveCalled) == 1
                            && saveThread.ThreadState.HasFlag(ThreadState.WaitSleepJoin)
                        ),
                    generous
                )
                .Should()
                .BeTrue("the save has to reach the store");

            // The save is waiting now, so a store with a lock keeps it waiting until the update is released.
            saveFinished
                .Wait(250)
                .Should()
                .BeFalse("a save must wait for an update that is between its read and its write");
        }
        finally
        {
            releaseUpdate.Set();
        }

        await update.WaitAsync(generous);
        saveFinished.Wait(generous).Should().BeTrue("the save runs once the update is done");
        saveError.Should().BeNull();

        (await store.LoadMetadataAsync("thread-1"))!
            .LatestRunId.Should()
            .Be("from-save", "the save came after the update finished, so it must be what remains");
    }
}
