using System.Net;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Http;

/// <summary>
///     The provider retry loop honors the server's <c>Retry-After</c> (#930 P1 item 1): it waits what the
///     server asked, up to <see cref="RetryOptions.MaxRetryAfter" />, and fails at once past that cap.
///     Each retry is reported to the ambient <see cref="HttpRetryScope" /> listener. Every wait runs on a
///     fake clock, so a 7-second Retry-After costs no real time.
/// </summary>
public class HttpRetryHelperRetryAfterTests
{
    private const string Url = "http://provider.test/v1/messages";

    [Fact]
    public async Task RateLimited_WithRetryAfterSeven_WaitsSevenSecondsThenRetries()
    {
        var time = new TimerSignallingTimeProvider();
        var handler = MockHttpHandlerBuilder.Create().RetryScenarioWithRetryAfter(1, "7").Build();
        using var client = new HttpClient(handler);
        var requests = 0;
        var notices = new List<HttpRetryNotice>();

        Task<string> call;
        using (HttpRetryScope.Begin(notices.Add))
        {
            call = HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () =>
                {
                    _ = Interlocked.Increment(ref requests);
                    return client.GetAsync(Url);
                },
                response => response.Content.ReadAsStringAsync(),
                NullLogger.Instance,
                RetryOptions.Default with
                {
                    TimeProvider = time,
                }
            );
        }

        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(6.9));
        requests.Should().Be(1, "the retry must not go out before the server's 7 seconds are up");
        call.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(0.1));
        var body = await call.WaitAsync(TimeSpan.FromSeconds(5));

        body.Should().Contain("success");
        requests.Should().Be(2);
        notices
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new HttpRetryNotice(1, 3, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7), true));
    }

    [Fact]
    public async Task RateLimited_WithRetryAfterOverTheCap_FailsAtOnceWithoutRetrying()
    {
        var handler = MockHttpHandlerBuilder.Create().RetryScenarioWithRetryAfter(1, "3600").Build();
        using var client = new HttpClient(handler);
        var requests = 0;
        var notices = new List<HttpRetryNotice>();

        Func<Task> act;
        using (HttpRetryScope.Begin(notices.Add))
        {
            act = () =>
                HttpRetryHelper.ExecuteHttpWithRetryAsync(
                    () =>
                    {
                        _ = Interlocked.Increment(ref requests);
                        return client.GetAsync(Url);
                    },
                    response => response.Content.ReadAsStringAsync(),
                    NullLogger.Instance,
                    RetryOptions.Default with
                    {
                        TimeProvider = new FakeTimeProvider(),
                    }
                );

            var thrown = await act.Should().ThrowAsync<HttpRequestException>();
            _ = thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            _ = thrown.Which.Message.Should().Contain("exceeds the 60s cap");
        }

        requests.Should().Be(1, "a wait longer than the cap is not retried early, it is not retried at all");
        notices.Should().BeEmpty();
    }

    [Fact]
    public async Task RateLimited_WithAnHttpDate_WaitsUntilThatDateOnTheClock()
    {
        var time = new TimerSignallingTimeProvider();
        time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var handler = MockHttpHandlerBuilder
            .Create()
            .RetryScenarioWithRetryAfter(1, "Sat, 07 Nov 2026 12:00:10 GMT", HttpStatusCode.ServiceUnavailable)
            .Build();
        using var client = new HttpClient(handler);
        var notices = new List<HttpRetryNotice>();

        Task<string> call;
        using (HttpRetryScope.Begin(notices.Add))
        {
            call = HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => client.GetAsync(Url),
                response => response.Content.ReadAsStringAsync(),
                NullLogger.Instance,
                RetryOptions.Default with
                {
                    TimeProvider = time,
                }
            );
        }

        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(10));
        _ = await call.WaitAsync(TimeSpan.FromSeconds(5));

        notices.Should().ContainSingle().Which.Delay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ServerError_WithNoRetryAfter_KeepsTheBackoffDelay()
    {
        var handler = MockHttpHandlerBuilder.Create().RetryScenario(1, HttpStatusCode.BadGateway).Build();
        using var client = new HttpClient(handler);
        var notices = new List<HttpRetryNotice>();

        using (HttpRetryScope.Begin(notices.Add))
        {
            _ = await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => client.GetAsync(Url),
                response => response.Content.ReadAsStringAsync(),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );
        }

        notices.Should().ContainSingle();
        notices[0].Delay.Should().Be(RetryOptions.FastForTests.CalculateDelay(1));
        notices[0].FromRetryAfter.Should().BeFalse();
    }

    [Theory]
    [InlineData("retry-after-ms", "1500", 1500)]
    [InlineData("retry-after-ms", "-5", 0)]
    [InlineData("Retry-After", "0", 0)]
    [InlineData("Retry-After", "12", 12000)]
    public void Read_ParsesEachHeaderForm(string header, string value, int expectedMs)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        _ = response.Headers.TryAddWithoutValidation(header, value);

        RetryAfterHeader.Read(response, new FakeTimeProvider()).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void Read_ADateInThePast_IsZero_AndAnUnreadableValueIsNull()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        _ = past.Headers.TryAddWithoutValidation("Retry-After", "Sat, 07 Nov 2026 11:59:00 GMT");
        using var garbage = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        _ = garbage.Headers.TryAddWithoutValidation("Retry-After", "soon");

        RetryAfterHeader.Read(past, time).Should().Be(TimeSpan.Zero);
        RetryAfterHeader.Read(garbage, time).Should().BeNull();
    }

    [Fact]
    public async Task Observe_ReportsRetriesWhileFetching_NotWhileTheConsumerHandlesAnItem()
    {
        var notices = new List<HttpRetryNotice>();
        var fetchNotice = new HttpRetryNotice(1, 3, HttpStatusCode.TooManyRequests, TimeSpan.Zero, false);
        var consumerNotice = fetchNotice with { Attempt = 2 };

        await foreach (var _ in HttpRetryScope.Observe(RetriesWhileFetching(fetchNotice), notices.Add))
        {
            // The consumer's own HTTP work (a tool calling a web API) is not the source's retry.
            HttpRetryScope.Notify(consumerNotice);
        }

        notices.Should().Equal(fetchNotice);
    }

    private static async IAsyncEnumerable<int> RetriesWhileFetching(
        HttpRetryNotice notice,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await Task.Yield();
        HttpRetryScope.Notify(notice);
        yield return 1;
    }

    /// <summary>
    ///     A fake clock that says when the retry loop has started its wait, so the test advances time
    ///     only once the wait exists. Advancing before it would move a clock no timer is watching yet.
    /// </summary>
    private sealed class TimerSignallingTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _ = _timerCreated.TrySetResult();
            return timer;
        }
    }
}
