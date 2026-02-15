using System.Diagnostics;
using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace AchieveAi.LmDotnetTools.LmCore.Tests;

public class MockHttpHandlerBuilderBenchmarks
{
    [Fact(DisplayName = "Performance: 1000 simple requests <100ms")]
    public async Task MockHandler_ShouldProcess1000Requests_Under100ms()
    {
        var handler = MockHttpHandlerBuilder.Create().RespondWithAnthropicMessage("OK").Build();
        var client = new HttpClient(handler);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 1000; i++)
        {
            // Create a new request for each iteration to avoid HttpRequestMessage reuse issues
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{}"),
            };
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Took {stopwatch.ElapsedMilliseconds}ms");
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
}
