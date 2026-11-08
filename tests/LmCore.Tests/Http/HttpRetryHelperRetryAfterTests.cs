using System.Net;
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

    /// <summary>What one retried call did: how many requests went out, and the retries it reported.</summary>
    private sealed class CallRecord
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public List<HttpRetryNotice> Notices { get; } = [];

        public void CountRequest() => _ = Interlocked.Increment(ref _requests);
    }

    /// <summary>A response body that never arrives until its read is cancelled.</summary>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.Infinite);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        ) => Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private const string Url = "http://provider.test/v1/messages";

    [Fact]
    public async Task RateLimited_WithRetryAfterSeven_WaitsSevenSecondsThenRetries()
    {
        var time = new TimerSignallingTimeProvider();
        using var client = new HttpClient(MockHttpHandlerBuilder.Create().RetryScenarioWithRetryAfter(1, "7").Build());
        var record = new CallRecord();

        var call = Start(() => client.GetAsync(Url), RetryOptions.Default with { TimeProvider = time }, record);

        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(6.9));
        record.Requests.Should().Be(1, "the retry must not go out before the server's 7 seconds are up");
        call.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(0.1));
        var body = await call.WaitAsync(TimeSpan.FromSeconds(5));

        body.Should().Contain("success");
        record.Requests.Should().Be(2);
        record
            .Notices.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new HttpRetryNotice(1, 3, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7), true));
    }

    [Fact]
    public async Task RateLimited_WithRetryAfterOverTheCap_FailsAtOnceWithoutRetrying()
    {
        using var client = new HttpClient(
            MockHttpHandlerBuilder.Create().RetryScenarioWithRetryAfter(1, "3600").Build()
        );
        var record = new CallRecord();

        var thrown = await FluentActions
            .Awaiting(() =>
                Start(
                    () => client.GetAsync(Url),
                    RetryOptions.Default with
                    {
                        TimeProvider = new FakeTimeProvider(),
                    },
                    record
                )
            )
            .Should()
            .ThrowAsync<HttpRequestException>();

        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        thrown.Which.Message.Should().Contain("exceeds the 60s cap");
        record.Requests.Should().Be(1, "a wait longer than the cap is not retried early, it is not retried at all");
        record.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task RateLimited_OverTheCap_FailsWithoutWaitingForAStalledErrorBody()
    {
        // The refusal promises a fast failure. A server that sends the 429 headers and then stalls the
        // body must not turn that into a hang: the call's own token is live, so nothing else would end it.
        using var live = new CancellationTokenSource();
        var record = new CallRecord();

        var call = Start(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StalledContent(),
                };
                _ = response.Headers.TryAddWithoutValidation("Retry-After", "3600");
                return Task.FromResult(response);
            },
            RetryOptions.Default with
            {
                TimeProvider = new FakeTimeProvider(),
            },
            record,
            live.Token
        );

        var thrown = await FluentActions
            .Awaiting(() => call.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<HttpRequestException>();

        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        record.Requests.Should().Be(1);
    }

    [Fact]
    public async Task RateLimited_AMillisecondWaitBeyondTheCap_IsRefused_NotShortenedUnderIt()
    {
        // 3,000,000,000 ms is about 34.7 days: over a 30-day cap. Shortening it to int.MaxValue ms
        // (about 24.9 days) before the comparison would wrongly accept it and wait.
        var record = new CallRecord();

        await FluentActions
            .Awaiting(() =>
                Start(
                        () => Task.FromResult(RateLimited(("retry-after-ms", "3000000000"))),
                        RetryOptions.Default with
                        {
                            TimeProvider = new FakeTimeProvider(),
                            MaxRetryAfter = TimeSpan.FromDays(30),
                        },
                        record
                    )
                    .WaitAsync(TimeSpan.FromSeconds(5)) // an accepted wait would never end on the fake clock
            )
            .Should()
            .ThrowAsync<HttpRequestException>()
            .WithMessage("*exceeds the 2592000s cap*");

        record.Requests.Should().Be(1);
        record.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task RateLimited_AWaitLongerThanATimerCanWait_IsRefused_EvenWithNoCap()
    {
        // 5,000,000 s is about 58 days. Task.Delay rejects anything past about 49.7 days, so with the cap
        // lifted the honored wait would throw ArgumentOutOfRangeException instead of failing cleanly.
        var record = new CallRecord();

        var thrown = await FluentActions
            .Awaiting(() =>
                Start(
                    () => Task.FromResult(RateLimited(("Retry-After", "5000000"))),
                    RetryOptions.Default with
                    {
                        TimeProvider = new FakeTimeProvider(),
                        MaxRetryAfter = TimeSpan.MaxValue,
                    },
                    record
                )
            )
            .Should()
            .ThrowAsync<HttpRequestException>();

        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        record.Requests.Should().Be(1);
    }

    [Fact]
    public async Task RateLimited_WithAnHttpDate_WaitsUntilThatDateOnTheClock()
    {
        var time = new TimerSignallingTimeProvider();
        time.SetUtcNow(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var client = new HttpClient(
            MockHttpHandlerBuilder
                .Create()
                .RetryScenarioWithRetryAfter(1, "Sat, 07 Nov 2026 12:00:10 GMT", HttpStatusCode.ServiceUnavailable)
                .Build()
        );
        var record = new CallRecord();

        var call = Start(() => client.GetAsync(Url), RetryOptions.Default with { TimeProvider = time }, record);

        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(10));
        _ = await call.WaitAsync(TimeSpan.FromSeconds(5));

        record.Notices.Should().ContainSingle().Which.Delay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ServerError_WithNoRetryAfter_KeepsTheBackoffDelay()
    {
        using var client = new HttpClient(
            MockHttpHandlerBuilder.Create().RetryScenario(1, HttpStatusCode.BadGateway).Build()
        );
        var record = new CallRecord();

        _ = await Start(() => client.GetAsync(Url), RetryOptions.FastForTests, record);

        record.Notices.Should().ContainSingle();
        record.Notices[0].Delay.Should().Be(RetryOptions.FastForTests.CalculateDelay(1));
        record.Notices[0].FromRetryAfter.Should().BeFalse();
    }

    [Fact]
    public async Task TheLargestRetryBudget_IsReportedWithoutOverflowing()
    {
        // MaxRetries + 1 overflows at int.MaxValue; the notice must report the saturated budget.
        using var client = new HttpClient(
            MockHttpHandlerBuilder.Create().RetryScenario(1, HttpStatusCode.ServiceUnavailable).Build()
        );
        var record = new CallRecord();

        _ = await Start(
            () => client.GetAsync(Url),
            RetryOptions.FastForTests with
            {
                MaxRetries = int.MaxValue,
            },
            record
        );

        record.Notices.Should().ContainSingle().Which.MaxAttempts.Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData("retry-after-ms", "1500", 1500)]
    [InlineData("retry-after-ms", "-5", 0)]
    [InlineData("Retry-After", "0", 0)]
    [InlineData("Retry-After", "12", 12000)]
    [InlineData("Retry-After", "-5", 0)] // the typed .NET parser drops a negative value as unreadable
    public void Read_ParsesEachHeaderForm(string header, string value, int expectedMs)
    {
        using var response = RateLimited((header, value));

        RetryAfterHeader.Read(response, new FakeTimeProvider()).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void Read_AStandardWaitPastTheIntRange_IsReadAtFullLength()
    {
        // The typed .NET parser drops this value, so without the raw read it would escape the cap check.
        using var response = RateLimited(("Retry-After", "3000000000"));

        RetryAfterHeader.Read(response, new FakeTimeProvider()).Should().Be(TimeSpan.FromSeconds(3000000000));
    }

    [Fact]
    public void Read_PrefersTheMillisecondHeader_WhenBothAreSent()
    {
        using var response = RateLimited(("retry-after-ms", "1500"), ("Retry-After", "7"));

        RetryAfterHeader.Read(response, new FakeTimeProvider()).Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Theory]
    [InlineData("Sat, 07 Nov 2026 11:59:00 GMT", 0)] // a date already past
    [InlineData("soon", null)] // unreadable
    [InlineData("99999999999999999999", null)] // too large for a 64-bit integer
    public void Read_ADateInThePast_IsZero_AndAnUnreadableValueIsNull(string value, int? expectedMs)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var response = RateLimited(("Retry-After", value));

        RetryAfterHeader.Read(response, time).Should().Be(expectedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
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

    /// <summary>
    ///     Starts one retried call with <paramref name="record" /> listening. The scope covers only the
    ///     start; the call's own async flow keeps the listener for its retries.
    /// </summary>
    private static Task<string> Start(
        Func<Task<HttpResponseMessage>> send,
        RetryOptions options,
        CallRecord record,
        CancellationToken cancellationToken = default
    )
    {
        using (HttpRetryScope.Begin(record.Notices.Add))
        {
            return HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () =>
                {
                    record.CountRequest();
                    return send();
                },
                response => response.Content.ReadAsStringAsync(),
                NullLogger.Instance,
                options,
                cancellationToken
            );
        }
    }

    private static HttpResponseMessage RateLimited(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        foreach (var (name, value) in headers)
        {
            _ = response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static async IAsyncEnumerable<int> RetriesWhileFetching(HttpRetryNotice notice)
    {
        await Task.Yield();
        HttpRetryScope.Notify(notice);
        yield return 1;
    }
}
