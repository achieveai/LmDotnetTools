using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class CodexMcpServerLifetimeTests
{
    [Fact]
    public async Task EnsureStartedAsync_StartsOnce_UnderConcurrency()
    {
        var startCount = 0;
        var startGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var lifetime = new CodexMcpServerLifetime(
            async () =>
            {
                Interlocked.Increment(ref startCount);
                return await startGate.Task;
            },
            NullLogger<CodexMcpServerLifetime>.Instance);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => lifetime.EnsureStartedAsync())
            .ToArray();

        startGate.SetResult("http://localhost:1234/mcp");

        var results = await Task.WhenAll(tasks);

        startCount.Should().Be(1);
        results.Should().AllBe("http://localhost:1234/mcp");
    }

    [Fact]
    public async Task EnsureStartedAsync_CachesFailure()
    {
        var startCount = 0;
        await using var lifetime = new CodexMcpServerLifetime(
            () =>
            {
                Interlocked.Increment(ref startCount);
                return Task.FromException<string>(new InvalidOperationException("boom"));
            },
            NullLogger<CodexMcpServerLifetime>.Instance);

        var first = await Record.ExceptionAsync(() => lifetime.EnsureStartedAsync());
        var second = await Record.ExceptionAsync(() => lifetime.EnsureStartedAsync());

        first.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
        second.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
        startCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureStartedAsync_ReturnsSameEndpointAcrossCalls()
    {
        const string endpoint = "http://localhost:9999/mcp";
        await using var lifetime = new CodexMcpServerLifetime(
            () => Task.FromResult(endpoint),
            NullLogger<CodexMcpServerLifetime>.Instance);

        var a = await lifetime.EnsureStartedAsync();
        var b = await lifetime.EnsureStartedAsync();

        a.Should().Be(endpoint);
        b.Should().Be(endpoint);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_WhenStartFailed()
    {
        var lifetime = new CodexMcpServerLifetime(
            () => Task.FromException<string>(new InvalidOperationException("boom")),
            NullLogger<CodexMcpServerLifetime>.Instance);

        _ = await Record.ExceptionAsync(() => lifetime.EnsureStartedAsync());

        var dispose = async () => await lifetime.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotInvokeStartup_WhenNeverStarted()
    {
        var startCount = 0;
        var lifetime = new CodexMcpServerLifetime(
            () =>
            {
                Interlocked.Increment(ref startCount);
                return Task.FromResult("http://localhost:1234/mcp");
            },
            NullLogger<CodexMcpServerLifetime>.Instance);

        await lifetime.DisposeAsync();

        startCount.Should().Be(0);
    }
}
