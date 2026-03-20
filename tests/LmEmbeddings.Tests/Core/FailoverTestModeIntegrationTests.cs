using System.Net;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmEmbeddings.Tests.Core;

public class FailoverTestModeIntegrationTests : LoggingTestBase
{
    public FailoverTestModeIntegrationTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task EmbeddingFailover_With4xx_SwitchesToBackup()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(1),
            RecoveryInterval = TimeSpan.FromMilliseconds(200),
            FailoverOnHttpError = true
        };

        using var primaryClient = TestModeHttpClientFactory.CreateEmbeddingTestClient(
            loggerFactory: LoggerFactory,
            statusSequence: [HttpStatusCode.BadRequest],
            embeddingSize: 8);
        using var backupClient = TestModeHttpClientFactory.CreateEmbeddingTestClient(
            loggerFactory: LoggerFactory,
            embeddingSize: 8);

        using var primary = new ServerEmbeddings(
            endpoint: "http://test-mode",
            model: "test-model",
            embeddingSize: 8,
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<ServerEmbeddings>(),
            httpClient: primaryClient);

        using var backup = new ServerEmbeddings(
            endpoint: "http://test-mode",
            model: "test-model",
            embeddingSize: 8,
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<ServerEmbeddings>(),
            httpClient: backupClient);

        using var service = new FailoverEmbeddingService(
            primary,
            backup,
            options,
            LoggerFactory.CreateLogger<FailoverEmbeddingService>());
        var vector = await service.GetEmbeddingAsync("hello");

        LogData("VectorLength", vector.Length);
        Assert.Equal(8, vector.Length);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task EmbeddingFailover_WithPrimaryTimeout_SwitchesToBackup()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromMilliseconds(50),
            RecoveryInterval = TimeSpan.FromMilliseconds(200),
            FailoverOnHttpError = true
        };

        using var primaryClient = TestModeHttpClientFactory.CreateEmbeddingTestClient(
            loggerFactory: LoggerFactory,
            delay: TimeSpan.FromSeconds(2),
            embeddingSize: 8);
        using var backupClient = TestModeHttpClientFactory.CreateEmbeddingTestClient(
            loggerFactory: LoggerFactory,
            embeddingSize: 8);

        using var primary = new ServerEmbeddings(
            endpoint: "http://test-mode",
            model: "test-model",
            embeddingSize: 8,
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<ServerEmbeddings>(),
            httpClient: primaryClient);

        using var backup = new ServerEmbeddings(
            endpoint: "http://test-mode",
            model: "test-model",
            embeddingSize: 8,
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<ServerEmbeddings>(),
            httpClient: backupClient);

        using var service = new FailoverEmbeddingService(
            primary,
            backup,
            options,
            LoggerFactory.CreateLogger<FailoverEmbeddingService>());
        var vector = await service.GetEmbeddingAsync("hello");

        LogData("VectorLength", vector.Length);
        Assert.Equal(8, vector.Length);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankFailover_With4xx_SwitchesToBackup()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(1),
            RecoveryInterval = TimeSpan.FromMilliseconds(200),
            FailoverOnHttpError = true
        };

        using var primaryClient = TestModeHttpClientFactory.CreateRerankTestClient(
            loggerFactory: LoggerFactory,
            statusSequence: [HttpStatusCode.BadRequest]);
        using var backupClient = TestModeHttpClientFactory.CreateRerankTestClient(
            loggerFactory: LoggerFactory);

        using var primary = new RerankingService(
            endpoint: "http://test-mode",
            model: "test-rerank-model",
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<RerankingService>(),
            httpClient: primaryClient);

        using var backup = new RerankingService(
            endpoint: "http://test-mode",
            model: "test-rerank-model",
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<RerankingService>(),
            httpClient: backupClient);

        using var service = new FailoverRerankService(
            primary,
            backup,
            options,
            LoggerFactory.CreateLogger<FailoverRerankService>());
        var result = await service.RerankAsync(
            new RerankRequest
            {
                Model = "test-rerank-model",
                Query = "query",
                Documents = ["doc1", "doc2"]
            });

        LogData("ResultCount", result.Results.Count);
        Assert.Single(result.Results);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankFailover_WithPrimaryTimeout_SwitchesToBackup()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromMilliseconds(50),
            RecoveryInterval = TimeSpan.FromMilliseconds(200),
            FailoverOnHttpError = true
        };

        using var primaryClient = TestModeHttpClientFactory.CreateRerankTestClient(
            loggerFactory: LoggerFactory,
            delay: TimeSpan.FromSeconds(2));
        using var backupClient = TestModeHttpClientFactory.CreateRerankTestClient(
            loggerFactory: LoggerFactory);

        using var primary = new RerankingService(
            endpoint: "http://test-mode",
            model: "test-rerank-model",
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<RerankingService>(),
            httpClient: primaryClient);

        using var backup = new RerankingService(
            endpoint: "http://test-mode",
            model: "test-rerank-model",
            apiKey: "test-key",
            logger: LoggerFactory.CreateLogger<RerankingService>(),
            httpClient: backupClient);

        using var service = new FailoverRerankService(
            primary,
            backup,
            options,
            LoggerFactory.CreateLogger<FailoverRerankService>());
        var result = await service.RerankAsync(
            new RerankRequest
            {
                Model = "test-rerank-model",
                Query = "query",
                Documents = ["doc1", "doc2"]
            });

        LogData("ResultCount", result.Results.Count);
        Assert.Single(result.Results);
        LogTestEnd();
    }
}
