using System.Diagnostics;
using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace AchieveAi.LmDotnetTools.LmCore.Tests;

public class MockHttpHandlerBuilderBenchmarks
{
    private const int RequestsPerRun = 1000;
    private const int MeasuredRuns = 3;
    private const int MaxOverheadFactor = 10;
    private const int UncontendedCeilingMilliseconds = 100;

    [Fact(DisplayName = "Performance: 1000 requests stay near a bare handler")]
    public async Task MockHandler_ShouldStayNearABareHandler_Over1000Requests()
    {
        // A fixed wall-clock ceiling measures the machine, not the handler. This assembly
        // runs alongside every other one in the solution, and a scheduling stall once
        // stretched these same 1000 iterations to 163ms with nothing about the builder
        // changed. Timing a bare handler through the identical loop in the same window
        // keeps the comparison honest: contention lands on both measurements, so the
        // ratio still answers what the test is named for — the builder's per-request cost.
        using var bareHandler = new ConstantOkHandler();
        var bare = await FastestRunMillisecondsAsync(bareHandler);

        using var builtHandler = MockHttpHandlerBuilder.Create().RespondWithAnthropicMessage("OK").Build();
        var built = await FastestRunMillisecondsAsync(builtHandler);

        // The floor stops a near-zero baseline from producing a razor-thin budget, and it
        // is the ceiling this test used to carry, so an uncontended run is judged as before.
        var budget = Math.Max(bare * MaxOverheadFactor, UncontendedCeilingMilliseconds);

        Assert.True(
            built <= budget,
            $"{RequestsPerRun} requests took {built}ms against a bare handler's {bare}ms; budget was {budget}ms"
        );
    }

    [Fact(DisplayName = "Memory: 1000 handlers <10MB")]
    public void MockHandler_ShouldNotLeakMemory_After1000Handlers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 1000; i++)
        {
            var handler = MockHttpHandlerBuilder.Create().RespondWithAnthropicMessage("OK").Build();
            handler.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        var diff = after - before;
        Assert.True(diff < 10 * 1024 * 1024, $"Memory diff: {diff} bytes");
    }

    [Fact(DisplayName = "Concurrency: 100 threads × 100 requests")]
    public async Task MockHandler_ShouldBeThreadSafe_UnderParallelLoad()
    {
        var handler = MockHttpHandlerBuilder.Create().RespondWithAnthropicMessage("OK").Build();
        var client = new HttpClient(handler);
        var tasks = new Task[100];

        for (var t = 0; t < 100; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    // Create a new request for each iteration to avoid HttpRequestMessage reuse issues
                    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                    {
                        Content = new StringContent("{}"),
                    };
                    var response = await client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    // Dispose the request to free resources immediately
                    request.Dispose();
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact(DisplayName = "Leak: No memory leak after 1000 cycles")]
    public void MockHandler_ShouldNotLeakMemory_AfterManyCycles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 1000; i++)
        {
            var handler = MockHttpHandlerBuilder.Create().RespondWithAnthropicMessage("OK").Build();
            handler.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        var diff = after - before;
        Assert.True(diff < 5 * 1024 * 1024, $"Memory diff: {diff} bytes");
    }

    /// <summary>
    ///     Drives <paramref name="handler" /> through <see cref="RequestsPerRun" /> requests
    ///     several times and keeps the fastest run. The first run is discarded: it pays JIT and
    ///     pipeline warm-up that says nothing about steady-state cost.
    /// </summary>
    private static async Task<long> FastestRunMillisecondsAsync(HttpMessageHandler handler)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var fastest = long.MaxValue;

        for (var run = 0; run <= MeasuredRuns; run++)
        {
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < RequestsPerRun; i++)
            {
                // Create a new request for each iteration to avoid HttpRequestMessage reuse issues
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                {
                    Content = new StringContent("{}"),
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            stopwatch.Stop();

            if (run > 0)
            {
                fastest = Math.Min(fastest, stopwatch.ElapsedMilliseconds);
            }
        }

        return fastest;
    }

    /// <summary>The cheapest handler there is, as the yardstick the builder is measured against.</summary>
    private sealed class ConstantOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
    }
}
