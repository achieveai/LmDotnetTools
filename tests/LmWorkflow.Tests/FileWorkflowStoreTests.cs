using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

public class FileWorkflowStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "workflow-store-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task SnapshotSurvivesNewStoreInstance_AndOpaqueIdentityCannotEscapeDirectory()
    {
        const string id = "../opaque/run";
        var snapshot = new WorkflowInstanceSnapshot
        {
            InstanceId = id,
            State = new() { ["count"] = 1 },
        };
        await new FileWorkflowStore(_directory).SaveAsync(id, snapshot);
        snapshot.State["count"] = 2;

        var restarted = new FileWorkflowStore(_directory);
        (await restarted.LoadAsync(id))!.State["count"]!.GetValue<int>().Should().Be(1);
        (await restarted.ListAsync()).Should().Equal(id);
        Directory.GetFiles(_directory).Should().ContainSingle();
        await restarted.DeleteAsync(id);
        (await restarted.LoadAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task InvalidSnapshotDoesNotReplaceAcknowledgedState()
    {
        var store = new FileWorkflowStore(_directory);
        await store.SaveAsync("run", new WorkflowInstanceSnapshot { InstanceId = "run", Step = 1 });

        await store
            .Invoking(s => s.SaveAsync("run", new WorkflowInstanceSnapshot { InstanceId = "other", Step = 2 }))
            .Should()
            .ThrowAsync<ArgumentException>();

        (await store.LoadAsync("run"))!.Step.Should().Be(1);
    }

    [Fact]
    public async Task CorruptionIsAnError_NotAnAbsentRunThatCanBeReplayed()
    {
        var store = new FileWorkflowStore(_directory);
        await store.SaveAsync("run", new WorkflowInstanceSnapshot { InstanceId = "run" });
        await File.WriteAllTextAsync(Directory.GetFiles(_directory).Single(), "corrupt");

        await store.Invoking(s => s.LoadAsync("run")).Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}
