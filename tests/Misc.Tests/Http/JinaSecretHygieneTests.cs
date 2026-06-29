using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Http;

/// <summary>
///     Drives a real <see cref="JinaWebProvider" /> behind a <see cref="WebFetchTool" /> against an
///     upstream 500 whose body echoes the API key and a content sentinel. Proves the key and the raw
///     upstream body never reach the tool's output nor its logs.
/// </summary>
public class JinaSecretHygieneTests
{
    private const string ApiKey = "secret-key-1234567890";
    private const string BodySentinel = "UPSTREAM_SECRET_BODY";

    [Fact]
    public async Task WebFetch_UpstreamErrorEchoesSecrets_NeverLeaksToOutputOrLogs()
    {
        // The upstream 500 body deliberately contains both the API key and a content sentinel.
        var leakyBody =
            "{\"data\":{\"content\":\"contains " + ApiKey + " and " + BodySentinel + " inline\"}}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            leakyBody,
            HttpStatusCode.InternalServerError
        );

        var options = new WebToolsOptions { JinaApiKey = ApiKey };
        var provider = new JinaWebProvider(
            new HttpClient(handler),
            options,
            logger: null,
            retryOptions: RetryOptions.FastForTests
        );

        var capturingLogger = new CapturingLogger<WebFetchTool>();
        var tool = new WebFetchTool(provider, options, capturingLogger);

        var result = await tool.Handler(
            "{\"url\":\"https://example.com\"}",
            new ToolCallContext(),
            CancellationToken.None
        );

        // The 500 exhausts retries and surfaces as a bounded, sanitized error to the model.
        result.ResultText.Should().NotContain(ApiKey);
        result.ResultText.Should().NotContain(BodySentinel);

        LogLevel[] levels =
        [
            LogLevel.Trace,
            LogLevel.Debug,
            LogLevel.Information,
            LogLevel.Warning,
            LogLevel.Error,
            LogLevel.Critical,
        ];
        foreach (var level in levels)
        {
            capturingLogger.CountAtLevel(level, ApiKey).Should().Be(0);
            capturingLogger.CountAtLevel(level, BodySentinel).Should().Be(0);
        }
    }

    /// <summary>
    ///     Drives a real <see cref="JinaWebProvider" /> whose OWN logger is a
    ///     <see cref="CapturingLogger{T}" />. The upstream returns a non-retryable status whose body
    ///     carries the API key, the body sentinel, and a retryable token ("500 timeout"). The inherited
    ///     retry helper builds an <see cref="HttpRequestException" /> whose message embeds that raw body
    ///     and — because its substring-based retry classification matches the token — would emit the
    ///     message (and thus the body) at Warning if it were wired to the provider's logger. This pins
    ///     that the provider's logger never receives the key or the raw upstream body at any level.
    /// </summary>
    [Fact]
    public async Task Provider_UpstreamBodyWithRetryableToken_NeverReachesProviderLogger()
    {
        var leakyBody =
            "{\"data\":{\"content\":\"contains "
            + ApiKey
            + " and "
            + BodySentinel
            + " and 500 timeout inline\"}}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(leakyBody, HttpStatusCode.Forbidden);

        var options = new WebToolsOptions { JinaApiKey = ApiKey };
        var capturingLogger = new CapturingLogger<JinaWebProvider>();
        var provider = new JinaWebProvider(
            new HttpClient(handler),
            options,
            logger: capturingLogger,
            retryOptions: RetryOptions.FastForTests
        );

        // Retries are exhausted and the failure surfaces as an exception; we only assert nothing secret
        // was logged along the way.
        var act = async () =>
            await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);
        _ = await act.Should().ThrowAsync<HttpRequestException>();

        LogLevel[] levels =
        [
            LogLevel.Trace,
            LogLevel.Debug,
            LogLevel.Information,
            LogLevel.Warning,
            LogLevel.Error,
            LogLevel.Critical,
        ];
        foreach (var level in levels)
        {
            capturingLogger.CountAtLevel(level, ApiKey).Should().Be(0);
            capturingLogger.CountAtLevel(level, BodySentinel).Should().Be(0);
        }
    }

    /// <summary>
    ///     Happy-path counterpart to the error-bounding tests: a 200 response whose content legitimately
    ///     contains the API key must still be redacted to <c>***</c> in the tool's output, proving
    ///     <see cref="WebToolOutput.Sanitize" /> runs end-to-end on success, not only on error paths.
    /// </summary>
    [Fact]
    public async Task WebFetch_SuccessBodyContainsKey_RedactsBeforeReturningToModel()
    {
        var successBody = "{\"data\":{\"content\":\"the key " + ApiKey + " appears in the page\"}}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(successBody, HttpStatusCode.OK);

        var options = new WebToolsOptions { JinaApiKey = ApiKey };
        var provider = new JinaWebProvider(
            new HttpClient(handler),
            options,
            logger: null,
            retryOptions: RetryOptions.FastForTests
        );
        var tool = new WebFetchTool(provider, options);

        var result = await tool.Handler(
            "{\"url\":\"https://example.com\"}",
            new ToolCallContext(),
            CancellationToken.None
        );

        result.ResultText.Should().Contain("***");
        result.ResultText.Should().NotContain(ApiKey);
    }
}
