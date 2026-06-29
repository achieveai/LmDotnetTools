using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
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
}
