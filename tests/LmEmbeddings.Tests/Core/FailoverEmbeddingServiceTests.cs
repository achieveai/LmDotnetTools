using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LmEmbeddings.Tests.Core;

public class FailoverEmbeddingServiceTests : LoggingTestBase
{
    private readonly Mock<IEmbeddingService> _primaryMock = new();
    private readonly Mock<IEmbeddingService> _backupMock = new();

    private static FailoverOptions DefaultOptions()
    {
        return new()
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = true
        };
    }

    public FailoverEmbeddingServiceTests(ITestOutputHelper output)
        : base(output)
    {
        _primaryMock.Setup(p => p.EmbeddingSize).Returns(1536);
        _backupMock.Setup(b => b.EmbeddingSize).Returns(1536);
    }

    private FailoverEmbeddingService CreateService(FailoverOptions? options = null)
    {
        return new(_primaryMock.Object, _backupMock.Object, options ?? DefaultOptions(),
            LoggerFactory.CreateLogger<FailoverEmbeddingService>());
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_PrimarySucceeds_ReturnsPrimaryResult()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f, 2.0f]);

        var service = CreateService();
        var result = await service.GetEmbeddingAsync("test");

        LogData("Result", result);
        Assert.Equal(2, result.Length);
        _primaryMock.Verify(p => p.GetEmbeddingAsync("test", It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_PrimaryTimeout_FailsOverToBackup()
    {
        LogTestStart(new { TimeoutMs = 50 });
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromMilliseconds(50),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = true
        };

        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string s, CancellationToken ct) =>
            {
                await Task.Delay(2000, ct);
                return [1.0f];
            });

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([3.0f, 4.0f]);

        var service = CreateService(options);
        var result = await service.GetEmbeddingAsync("test");

        Assert.Equal(2, result.Length);
        Assert.Equal(3.0f, result[0]);
        _backupMock.Verify(b => b.GetEmbeddingAsync("test", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_PrimaryHttpError_FailsOverToBackup()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([5.0f]);

        var service = CreateService();
        var result = await service.GetEmbeddingAsync("test");

        Assert.Single(result);
        _backupMock.Verify(b => b.GetEmbeddingAsync("test", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_CooldownWindow_RoutesDirectlyToBackup()
    {
        LogTestStart();
        _primaryMock.SetupSequence(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);

        var service = CreateService();

        Trace("First call triggers failover");
        await service.GetEmbeddingAsync("test1");
        Trace("Second call within cooldown should route to backup directly");
        await service.GetEmbeddingAsync("test2");

        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_AfterCooldown_ProbesPrimary()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(50),
            FailoverOnHttpError = true
        };

        _primaryMock.SetupSequence(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"))
            .ReturnsAsync([2.0f]);

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.GetEmbeddingAsync("test1");
        Trace("Waiting for cooldown to expire");
        await Task.Delay(100);
        Trace("Probing primary after cooldown");
        var result = await service.GetEmbeddingAsync("test2");

        LogData("ProbeResult", result);
        Assert.Equal(2.0f, result[0]);
        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_BothFail_ThrowsPrimaryBackupFailoverException()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Primary Error"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backup Error"));

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<PrimaryBackupFailoverException>(
            () => service.GetEmbeddingAsync("test"));
        Assert.NotNull(ex.PrimaryException);
        Assert.NotNull(ex.BackupException);
        LogData("PrimaryException", ex.PrimaryException.Message);
        LogData("BackupException", ex.BackupException.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_CallerCancellation_DoesNotChangeState()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Trace("Calling with already-cancelled token");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetEmbeddingAsync("test", cts.Token));

        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Trace("Verifying primary is still used after caller cancellation");
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([1.0f]);
        await service.GetEmbeddingAsync("test2");
        _primaryMock.Verify(p => p.GetEmbeddingAsync("test2", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_FailoverOnHttpErrorFalse_StaysOnPrimary()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
            FailoverOnHttpError = false
        };

        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500"));

        var service = CreateService(options);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetEmbeddingAsync("test"));
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public void Constructor_NegativeRecoveryInterval_ThrowsArgumentOutOfRangeException()
    {
        LogTestStart();
        var options = new FailoverOptions { RecoveryInterval = TimeSpan.FromSeconds(-1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(options));
        Trace("Exception: {Message}", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void Constructor_MismatchedEmbeddingSize_ThrowsArgumentException()
    {
        LogTestStart();
        _backupMock.Setup(b => b.EmbeddingSize).Returns(768);

        var ex = Assert.Throws<ArgumentException>(() => CreateService());
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("1536", ex.Message);
        Assert.Contains("768", ex.Message);
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

        _primaryMock.SetupSequence(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Error"))
            .ReturnsAsync([2.0f]);

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.GetEmbeddingAsync("test1");

        Trace("Second call should route to backup (no recovery interval)");
        await service.GetEmbeddingAsync("test2");
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        Trace("Manual reset to primary");
        service.ResetToPrimary();

        Trace("Third call should go to primary");
        var result = await service.GetEmbeddingAsync("test3");
        LogData("ResultAfterReset", result);
        Assert.Equal(2.0f, result[0]);
        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_ProbeFailure_ReExtendsCooldown()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(50),
            FailoverOnHttpError = true
        };

        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.GetEmbeddingAsync("test1");

        Trace("Wait for cooldown to expire, then probe (which will also fail)");
        await Task.Delay(100);
        await service.GetEmbeddingAsync("test2");

        Trace("Primary was tried twice (initial + probe), both failed");
        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        Trace("Immediately call again - should route to backup without probing (new cooldown window)");
        await service.GetEmbeddingAsync("test3");
        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_PrimaryThrowsTimeoutException_FailsOverToBackup()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([5.0f]);

        var service = CreateService();
        var result = await service.GetEmbeddingAsync("test");

        Assert.Single(result);
        Assert.Equal(5.0f, result[0]);
        _backupMock.Verify(b => b.GetEmbeddingAsync("test", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_NullRecoveryInterval_NeverProbesAfterFailover()
    {
        LogTestStart();
        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = null,
            FailoverOnHttpError = true
        };

        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error"));

        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);

        var service = CreateService(options);

        Trace("First call triggers failover");
        await service.GetEmbeddingAsync("test1");

        Trace("Wait well beyond any reasonable probe interval");
        await Task.Delay(200);

        Trace("Should still route to backup - no automatic recovery");
        await service.GetEmbeddingAsync("test2");
        _primaryMock.Verify(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_NonFailoverException_PropagatesWithoutFailover()
    {
        LogTestStart();
        _primaryMock.Setup(p => p.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Deserialization error"));

        var service = CreateService();

        Trace("Non-failover exception should propagate directly");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetEmbeddingAsync("test"));
        _backupMock.Verify(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Trace("Primary should be marked unhealthy, so next call routes to backup");
        _backupMock.Setup(b => b.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1.0f]);
        await service.GetEmbeddingAsync("test2");
        _backupMock.Verify(b => b.GetEmbeddingAsync("test2", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GenerateEmbeddingsAsync_DelegatesToPrimary()
    {
        LogTestStart();
        var expectedResponse = new EmbeddingResponse { Embeddings = [], Model = "test" };
        _primaryMock.Setup(p => p.GenerateEmbeddingsAsync(It.IsAny<EmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var service = CreateService();
        var request = new EmbeddingRequest { Inputs = ["hello"], Model = "test" };
        var result = await service.GenerateEmbeddingsAsync(request);

        Assert.Equal(expectedResponse, result);
        _primaryMock.Verify(p => p.GenerateEmbeddingsAsync(It.IsAny<EmbeddingRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GenerateEmbeddingAsync_DelegatesToPrimary()
    {
        LogTestStart();
        var expectedResponse = new EmbeddingResponse { Embeddings = [], Model = "test" };
        _primaryMock.Setup(p => p.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var service = CreateService();
        var result = await service.GenerateEmbeddingAsync("text", "model");

        Assert.Equal(expectedResponse, result);
        _primaryMock.Verify(p => p.GenerateEmbeddingAsync("text", "model", It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetAvailableModelsAsync_DelegatesToPrimary()
    {
        LogTestStart();
        IReadOnlyList<string> expectedModels = ["model-a", "model-b"];
        _primaryMock.Setup(p => p.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        var service = CreateService();
        var result = await service.GetAvailableModelsAsync();

        Assert.Equal(expectedModels, result);
        _primaryMock.Verify(p => p.GetAvailableModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        LogTestEnd();
    }
}
