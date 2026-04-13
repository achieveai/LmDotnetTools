using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LmEmbeddings.Tests.Core;

public class FailoverRerankServiceTests : LoggingTestBase
{
    private readonly Mock<IRerankService> _primaryMock = new();
    private readonly Mock<IRerankService> _backupMock = new();

    private static FailoverOptions DefaultOptions()
    {
        return new()
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = true
        };
    }

    public FailoverRerankServiceTests(ITestOutputHelper output)
        : base(output) { }

    private FailoverRerankService CreateService(FailoverOptions? options = null)
    {
        return new(_primaryMock.Object, _backupMock.Object, options ?? DefaultOptions(),
            LoggerFactory.CreateLogger<FailoverRerankService>());
    }

    private static RerankRequest CreateRequest()
    {
        return new() { Model = "test", Query = "q", Documents = [] };
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_PrimarySucceeds_ReturnsPrimaryResult()
    {
        LogTestStart();
        var expectedResponse = new RerankResponse { Results = [] };
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var service = CreateService();
        var result = await service.RerankAsync(CreateRequest());

        LogData("Result", result);
        Assert.Equal(expectedResponse, result);
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_PrimaryTimeout_FailsOverToBackup()
    {
        LogTestStart(new { TimeoutMs = 50 });
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromMilliseconds(50),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = true
        };

        var backupResponse = new RerankResponse { Results = [] };

        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (RerankRequest r, CancellationToken ct) =>
            {
                await Task.Delay(2000, ct);
                return new RerankResponse { Results = [] };
            });

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupResponse);

        var service = CreateService(options);
        var result = await service.RerankAsync(CreateRequest());

        Assert.Equal(backupResponse, result);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_PrimaryHttpError_FailsOverToBackup()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        var backupResponse = new RerankResponse { Results = [] };
        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupResponse);

        var service = CreateService();
        var result = await service.RerankAsync(CreateRequest());

        Assert.Equal(backupResponse, result);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_CooldownWindow_RoutesDirectlyToBackup()
    {
        LogTestStart();
        _primaryMock.SetupSequence(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });

        var service = CreateService();

        Trace("First call triggers failover");
        await service.RerankAsync(CreateRequest());
        Trace("Second call within cooldown should route to backup directly");
        await service.RerankAsync(CreateRequest());

        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_AfterCooldown_ProbesPrimary()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(50),
            FailoverOnHttpError = true
        };

        var primaryResponse = new RerankResponse { Results = [new RerankResult { Index = 0, RelevanceScore = 0.99 }] };
        _primaryMock.SetupSequence(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"))
            .ReturnsAsync(primaryResponse);

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.RerankAsync(CreateRequest());
        Trace("Waiting for cooldown to expire");
        await Task.Delay(100);
        Trace("Probing primary after cooldown");
        var result = await service.RerankAsync(CreateRequest());

        LogData("ProbeResult", result.Results.Count);
        Assert.Single(result.Results);
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_BothFail_ThrowsPrimaryBackupFailoverException()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Primary Error"));

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backup Error"));

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<PrimaryBackupFailoverException>(
            () => service.RerankAsync(CreateRequest()));
        Assert.NotNull(ex.PrimaryException);
        Assert.NotNull(ex.BackupException);
        LogData("PrimaryException", ex.PrimaryException.Message);
        LogData("BackupException", ex.BackupException.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_CallerCancellation_DoesNotChangeState()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Trace("Calling with already-cancelled token");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RerankAsync(CreateRequest(), cts.Token));

        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        Trace("Verifying primary is still used after caller cancellation");
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });
        await service.RerankAsync(CreateRequest());
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_FailoverOnHttpErrorFalse_StaysOnPrimary()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = false
        };

        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500"));

        var service = CreateService(options);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync(CreateRequest()));
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void Constructor_ZeroTimeout_ThrowsArgumentOutOfRangeException()
    {
        LogTestStart();
        var options = new FailoverOptions { PrimaryRequestTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options));
        Trace("Exception: {Message}", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void Constructor_ZeroRecoveryInterval_ThrowsArgumentOutOfRangeException()
    {
        LogTestStart();
        var options = new FailoverOptions { RecoveryInterval = TimeSpan.Zero };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("RecoveryInterval", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task ResetToPrimary_AfterFailover_RestoresPrimary()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = null
        };

        _primaryMock.SetupSequence(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"))
            .ReturnsAsync(new RerankResponse { Results = [] });

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.RerankAsync(CreateRequest());

        Trace("Second call should route to backup (no recovery interval)");
        await service.RerankAsync(CreateRequest());
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        Trace("Manual reset to primary");
        service.ResetToPrimary();

        Trace("Third call should go to primary");
        await service.RerankAsync(CreateRequest());
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_ProbeFailure_ReExtendsCooldown()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(50),
            FailoverOnHttpError = true
        };

        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.RerankAsync(CreateRequest());

        Trace("Wait for cooldown to expire, then probe (which will also fail)");
        await Task.Delay(100);
        await service.RerankAsync(CreateRequest());

        Trace("Primary was tried twice (initial + probe), both failed");
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        Trace("Immediately call again - should route to backup without probing (new cooldown window)");
        await service.RerankAsync(CreateRequest());
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_PrimaryThrowsTimeoutException_FailsOverToBackup()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out"));

        var backupResponse = new RerankResponse { Results = [] };
        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupResponse);

        var service = CreateService();
        var result = await service.RerankAsync(CreateRequest());

        Assert.Equal(backupResponse, result);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_NullRecoveryInterval_NeverProbesAfterFailover()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = null,
            FailoverOnHttpError = true
        };

        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.RerankAsync(CreateRequest());

        Trace("Wait well beyond any reasonable probe interval");
        await Task.Delay(200);

        Trace("Should still route to backup - no automatic recovery");
        await service.RerankAsync(CreateRequest());
        _primaryMock.Verify(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_NonFailoverException_PropagatesWithoutFailover()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Deserialization error"));

        var service = CreateService();

        Trace("Non-failover exception should propagate directly");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RerankAsync(CreateRequest()));
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        Trace("Primary should be marked unhealthy, so next call routes to backup");
        _backupMock.Setup(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResponse { Results = [] });
        await service.RerankAsync(CreateRequest());
        _backupMock.Verify(b => b.RerankAsync(It.IsAny<RerankRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_Overload_DelegatesToPrimary()
    {
        LogTestStart();
        var expectedResponse = new RerankResponse { Results = [] };
        _primaryMock.Setup(p => p.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var service = CreateService();
        var result = await service.RerankAsync("query", ["doc1"], "model", 5);

        Assert.Equal(expectedResponse, result);
        _primaryMock.Verify(p => p.RerankAsync("query", It.IsAny<IReadOnlyList<string>>(), "model", 5, It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetAvailableModelsAsync_DelegatesToPrimary()
    {
        LogTestStart();
        IReadOnlyList<string> expectedModels = ["rerank-a", "rerank-b"];
        _primaryMock.Setup(p => p.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        var service = CreateService();
        var result = await service.GetAvailableModelsAsync();

        Assert.Equal(expectedModels, result);
        _primaryMock.Verify(p => p.GetAvailableModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }
}
