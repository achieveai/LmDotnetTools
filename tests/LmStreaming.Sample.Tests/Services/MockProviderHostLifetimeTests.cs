using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Regression tests for the lifetime contract that the registry's <c>*-mock</c> availability
/// gate depends on: <see cref="MockProviderHostLifetime.IsRunning"/> must reflect the inner
/// Kestrel app's bound state, and a startup failure must NOT propagate (the sample app must
/// keep running even if the mock host fails to bind).
/// </summary>
public class MockProviderHostLifetimeTests
{
    [Fact]
    public async Task StartAsync_BindsLoopback_AndExposesBaseUrl()
    {
        await using var lifetime = new MockProviderHostLifetime(
            () => ScriptedSseResponder.New()
                .ForRole("any", _ => true).Turn(t => t.Text("ok"))
                .Build(),
            NullLogger<MockProviderHostLifetime>.Instance);

        await lifetime.StartAsync(CancellationToken.None);

        lifetime.IsRunning.Should().BeTrue();
        lifetime.BaseUrl.Should().NotBeNullOrEmpty();
        lifetime.BaseUrl.Should().StartWith("http://127.0.0.1:");
    }

    [Fact]
    public async Task StartAsync_SwallowsException_WhenResponderFactoryThrows()
    {
        // The IHostedService contract: a failure here would crash Host.StartAsync and take
        // the whole sample app down. The catch in StartAsync exists to keep the rest of the
        // app running when only the mock host can't bind.
        await using var lifetime = new MockProviderHostLifetime(
            () => throw new InvalidOperationException("scenario failed to load"),
            NullLogger<MockProviderHostLifetime>.Instance);

        var act = async () => await lifetime.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        lifetime.IsRunning.Should().BeFalse();
        lifetime.BaseUrl.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_NoOp_WhenAlreadyDisposed()
    {
        var responderInvoked = 0;
        var lifetime = new MockProviderHostLifetime(
            () =>
            {
                Interlocked.Increment(ref responderInvoked);
                return ScriptedSseResponder.New()
                    .ForRole("any", _ => true).Turn(t => t.Text("ok"))
                    .Build();
            },
            NullLogger<MockProviderHostLifetime>.Instance);

        await lifetime.DisposeAsync();
        await lifetime.StartAsync(CancellationToken.None);

        responderInvoked.Should().Be(0);
        lifetime.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_NoOp_WhenNeverStarted()
    {
        await using var lifetime = new MockProviderHostLifetime(
            () => ScriptedSseResponder.New()
                .ForRole("any", _ => true).Turn(t => t.Text("ok"))
                .Build(),
            NullLogger<MockProviderHostLifetime>.Instance);

        var act = async () => await lifetime.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var lifetime = new MockProviderHostLifetime(
            () => ScriptedSseResponder.New()
                .ForRole("any", _ => true).Turn(t => t.Text("ok"))
                .Build(),
            NullLogger<MockProviderHostLifetime>.Instance);

        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.DisposeAsync();
        var act = async () => await lifetime.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
