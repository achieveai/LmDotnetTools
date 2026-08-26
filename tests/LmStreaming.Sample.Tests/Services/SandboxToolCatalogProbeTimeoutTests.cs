using System.Diagnostics;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The labelled-baseline fallback must be reachable by TIME, not only by exception.
/// </summary>
/// <remarks>
/// A gateway that refuses a connection fails fast and always reached the fallback. One that accepts
/// the socket and then never answers did not: session creation and <c>McpClient.CreateAsync</c> both
/// waited forever, and they do so while holding the probe's single-entry lock — so one wedged
/// gateway turned <c>/api/tools</c> and the whole Modes editor from degraded-but-usable into a hang.
/// </remarks>
public sealed class SandboxToolCatalogProbeTimeoutTests
{
    /// <summary>A gateway that accepts the request and then never answers.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        /// <summary>Completes once the probe has actually reached the wire.</summary>
        public Task Entered => _entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            // Never completes on its own; only cancellation ends it.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private static SandboxToolCatalogProbe CreateProbe(StallingHandler handler)
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:39999" };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(handler)
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(
                    Path.GetTempPath(),
                    "lmstreaming-test-secrets",
                    Guid.NewGuid().ToString("N")
                ),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        return new SandboxToolCatalogProbe(registry, gateway, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task WedgedGateway_YieldsTheLabelledBaselineOnceTheProbeBudgetExpires()
    {
        var handler = new StallingHandler();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(handler);

        var pending = probe.GetAsync(time);

        // Advance only AFTER the request is on the wire: the probe's budget starts when the probe
        // does, so advancing first would expire a timer that has not been created yet and the test
        // would pass without the timeout ever being exercised.
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(SandboxToolCatalogProbe.ProbeTimeout + TimeSpan.FromSeconds(1));

        var catalog = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        catalog.IsLive.Should().BeFalse();
        catalog.Warning.Should().NotBeNullOrWhiteSpace();
        catalog.Tools.Select(t => t.Name).Should().BeEquivalentTo(SandboxToolCatalogProbe.StaticBaseline);
    }

    [Fact]
    public async Task WedgedGateway_DoesNotHangBeforeTheBudgetExpires()
    {
        // The other half of the same claim: the fallback is reached BY the timeout, not merely at
        // some point. Without this, a probe that returned the baseline immediately (say, because the
        // handler threw) would satisfy the test above while proving nothing about the bound.
        var handler = new StallingHandler();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(handler);

        var pending = probe.GetAsync(time);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        time.Advance(SandboxToolCatalogProbe.ProbeTimeout - TimeSpan.FromSeconds(1));
        var completedEarly = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(250)));

        completedEarly.Should().NotBeSameAs(pending, "the probe must still be waiting on the gateway");

        time.Advance(TimeSpan.FromSeconds(2));
        (await pending.WaitAsync(TimeSpan.FromSeconds(30))).IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_IsNotDisguisedAsAGatewayFailure()
    {
        // The fallback catches the probe's OWN timeout, so it must not also swallow a caller who
        // walked away - that would report a degraded catalog for a request nobody is waiting on.
        var handler = new StallingHandler();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(handler);
        using var caller = new CancellationTokenSource();

        var pending = probe.GetAsync(time, caller.Token);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(30))
        );
    }
}
