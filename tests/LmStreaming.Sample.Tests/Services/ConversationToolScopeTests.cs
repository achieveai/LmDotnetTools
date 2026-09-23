using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// <see cref="ConversationToolScope.DisposeAsync"/> runs from the pool's entry teardown, where every
/// owned resource is a final flush. One refusing to close must not stop the rest from being asked —
/// and must not vanish either: a failed flush is a durability loss, so it is logged with the resource's
/// type and the exception before the next resource is disposed.
/// </summary>
public sealed class ConversationToolScopeTests
{
    [Fact]
    public async Task DisposeAsync_LogsAFailedResourceAndStillDisposesTheRest()
    {
        var logger = new CapturingLogger<ConversationToolScope>();
        var scope = new ConversationToolScope(logger) { Board = new TaskManager() };
        _ = scope.Own(new ThrowingResource());
        var survivor = scope.Own(new CountingResource());

        await scope.DisposeAsync();

        survivor
            .Disposals.Should()
            .Be(1, "a resource that refuses to close must not stop the one after it from being asked");
        logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain(nameof(ThrowingResource), "the warning names the resource that failed");
        logger
            .CountAtLevelWithExceptionText(LogLevel.Warning, "flush refused")
            .Should()
            .Be(1, "the exception travels with the warning rather than being reduced to its rendered text");
    }

    [Fact]
    public async Task DisposeAsync_WithNoLogger_StillContinuesPastAFailedResource()
    {
        // The object-initializer construction every existing site uses: no logger, null logger inside.
        var scope = new ConversationToolScope { Board = new TaskManager() };
        _ = scope.Own(new ThrowingResource());
        var survivor = scope.Own(new CountingResource());

        await scope.DisposeAsync();

        survivor.Disposals.Should().Be(1);
    }

    private sealed class ThrowingResource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new IOException("flush refused");
    }

    private sealed class CountingResource : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
