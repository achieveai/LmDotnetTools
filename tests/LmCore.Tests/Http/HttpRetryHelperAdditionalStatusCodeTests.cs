using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Http;

/// <summary>
///     <see cref="RetryOptions.AdditionalRetryableStatusCodes" /> is the opt-in that lets a single
///     transport widen the retryable set (the Copilot Responses backend answers a transient
///     <c>404 not_found</c> for a request that succeeds on the next attempt). The opt-in must be
///     strictly additive: the global classification is unchanged for every caller that does not
///     configure it, so a 404 is still a hard failure everywhere else.
/// </summary>
public class HttpRetryHelperAdditionalStatusCodeTests
{
    private static readonly RetryOptions s_retryNotFound = RetryOptions.FastForTests with
    {
        AdditionalRetryableStatusCodes = [HttpStatusCode.NotFound],
    };

    [Fact]
    public void IsRetryableStatusCode_NotFound_IsNotRetryableByDefault()
    {
        // The global default must not change: 404 stays non-retryable for every other HTTP client.
        HttpRetryHelper.IsRetryableStatusCode(HttpStatusCode.NotFound).Should().BeFalse();
        RetryOptions.Default.IsRetryableStatusCode(HttpStatusCode.NotFound).Should().BeFalse();
    }

    [Fact]
    public void IsRetryableStatusCode_NotFound_IsRetryableWhenConfigured()
    {
        s_retryNotFound.IsRetryableStatusCode(HttpStatusCode.NotFound).Should().BeTrue();

        // The opt-in widens the set; it never narrows it.
        s_retryNotFound.IsRetryableStatusCode(HttpStatusCode.TooManyRequests).Should().BeTrue();
        s_retryNotFound.IsRetryableStatusCode(HttpStatusCode.BadGateway).Should().BeTrue();
        s_retryNotFound.IsRetryableStatusCode(HttpStatusCode.Unauthorized).Should().BeFalse();
    }

    [Fact]
    public void IsRetryableError_NotFoundStatus_FollowsTheConfiguredSet()
    {
        var exception = new HttpRequestException(
            "HTTP request failed with status NotFound. Response body: {\"error\":{\"message\":\"\",\"code\":\"not_found\"}}",
            null,
            HttpStatusCode.NotFound
        );

        HttpRetryHelper.IsRetryableError(exception).Should().BeFalse();
        HttpRetryHelper.IsRetryableError(exception, RetryOptions.Default).Should().BeFalse();
        HttpRetryHelper.IsRetryableError(exception, s_retryNotFound).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_NotFound_IsRetriedWhenConfigured()
    {
        // 404 on the first attempt, 200 on the second: the operation must succeed after one retry.
        var invocationCount = 0;
        var handler = new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var attempt = Interlocked.Increment(ref invocationCount);
                var response =
                    attempt == 1
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(
                                "{\"error\":{\"message\":\"\",\"code\":\"not_found\"}}",
                                Encoding.UTF8,
                                "application/json"
                            ),
                        }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
                        };
                return Task.FromResult(response);
            }
        );
        using var httpClient = new HttpClient(handler);

        var result = await HttpRetryHelper.ExecuteHttpWithRetryAsync(
            () => httpClient.GetAsync("http://localhost/responses"),
            response => response.Content.ReadAsStringAsync(),
            NullLogger.Instance,
            s_retryNotFound
        );

        result.Should().Be("ok");
        invocationCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_NotFound_IsNotRetriedWithDefaultOptions()
    {
        // Same 404-then-200 script, but without the opt-in: the 404 must surface on the first attempt.
        var invocationCount = 0;
        var handler = new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var attempt = Interlocked.Increment(ref invocationCount);
                var response =
                    attempt == 1
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(
                                "{\"error\":{\"message\":\"\",\"code\":\"not_found\"}}",
                                Encoding.UTF8,
                                "application/json"
                            ),
                        }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
                        };
                return Task.FromResult(response);
            }
        );
        using var httpClient = new HttpClient(handler);

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => httpClient.GetAsync("http://localhost/responses"),
                response => response.Content.ReadAsStringAsync(),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        invocationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_NotFoundException_IsRetriedWhenConfigured()
    {
        // The exception-driven overload honours the same opt-in (a transport that throws the status).
        var attempts = 0;
        var result = await HttpRetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                attempts++;
                return attempts == 1
                    ? throw new HttpRequestException("not_found", null, HttpStatusCode.NotFound)
                    : Task.FromResult("ok");
            },
            NullLogger.Instance,
            s_retryNotFound
        );

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }

    [Fact]
    public void WithAdditionalRetryableStatusCodes_MergesInsteadOfOverwritingCallerValues()
    {
        var caller = new RetryOptions
        {
            MaxRetries = 5,
            InitialDelayMs = 7,
            MaxDelayMs = 11,
            BackoffMultiplier = 1.5,
            AdditionalRetryableStatusCodes = [HttpStatusCode.Conflict],
        };

        var merged = caller.WithAdditionalRetryableStatusCodes(HttpStatusCode.NotFound);

        merged.MaxRetries.Should().Be(5);
        merged.InitialDelayMs.Should().Be(7);
        merged.MaxDelayMs.Should().Be(11);
        merged.BackoffMultiplier.Should().Be(1.5);
        merged
            .AdditionalRetryableStatusCodes.Should()
            .BeEquivalentTo([HttpStatusCode.Conflict, HttpStatusCode.NotFound]);

        // Already-present codes are a no-op (no duplicate entries, same instance is acceptable).
        merged
            .WithAdditionalRetryableStatusCodes(HttpStatusCode.NotFound)
            .AdditionalRetryableStatusCodes.Should()
            .BeEquivalentTo([HttpStatusCode.Conflict, HttpStatusCode.NotFound]);
    }

    [Fact]
    public void WithAdditionalRetryableStatusCodes_NullArray_ThrowsArgumentNullException()
    {
        // A null array is the one input that cannot merge. Without the guard it would reach the LINQ
        // filter and surface as a NullReferenceException from inside RetryOptions instead of naming the
        // caller's bad argument at the boundary.
        var act = () => RetryOptions.Default.WithAdditionalRetryableStatusCodes(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("statusCodes");
    }
}
