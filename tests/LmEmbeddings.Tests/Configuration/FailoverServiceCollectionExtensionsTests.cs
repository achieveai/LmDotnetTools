using AchieveAi.LmDotnetTools.LmEmbeddings.Configuration;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LmEmbeddings.Tests.Configuration;

public class FailoverServiceCollectionExtensionsTests : LoggingTestBase
{
    public FailoverServiceCollectionExtensionsTests(ITestOutputHelper output)
        : base(output) { }

    private static IConfigurationSection BuildSection(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return config.GetSection("Failover");
    }

    private static Dictionary<string, string?> ValidEmbeddingConfig() => new()
    {
        ["Failover:Primary:Endpoint"] = "http://primary",
        ["Failover:Primary:Model"] = "model-a",
        ["Failover:Primary:ApiKey"] = "key-a",
        ["Failover:Primary:EmbeddingSize"] = "8",
        ["Failover:Backup:Endpoint"] = "http://backup",
        ["Failover:Backup:Model"] = "model-b",
        ["Failover:Backup:ApiKey"] = "key-b",
        ["Failover:Backup:EmbeddingSize"] = "8",
        ["Failover:PrimaryRequestTimeoutSeconds"] = "5",
        ["Failover:FailoverOnHttpError"] = "true",
        ["Failover:RecoveryIntervalSeconds"] = "120"
    };

    private static Dictionary<string, string?> ValidRerankConfig() => new()
    {
        ["Failover:Primary:Endpoint"] = "http://primary",
        ["Failover:Primary:Model"] = "rerank-a",
        ["Failover:Primary:ApiKey"] = "key-a",
        ["Failover:Backup:Endpoint"] = "http://backup",
        ["Failover:Backup:Model"] = "rerank-b",
        ["Failover:Backup:ApiKey"] = "key-b",
        ["Failover:PrimaryRequestTimeoutSeconds"] = "5",
        ["Failover:FailoverOnHttpError"] = "true",
        ["Failover:RecoveryIntervalSeconds"] = "120"
    };

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_MissingPrimaryEndpoint_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:Primary:Endpoint"] = "";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("Primary:Endpoint", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_MissingPrimaryModel_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:Primary:Model"] = "";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("Primary:Model", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_MissingBackupApiKey_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:Backup:ApiKey"] = "";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("Backup:ApiKey", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_MismatchedEmbeddingSize_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:Backup:EmbeddingSize"] = "768";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("embedding sizes must match", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_ZeroTimeout_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:PrimaryRequestTimeoutSeconds"] = "0";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("PrimaryRequestTimeoutSeconds", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_NegativeRecoveryInterval_Throws()
    {
        LogTestStart();
        var values = ValidEmbeddingConfig();
        values["Failover:RecoveryIntervalSeconds"] = "-5";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverEmbeddings(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("RecoveryIntervalSeconds", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverReranking_ZeroRecoveryInterval_Throws()
    {
        LogTestStart();
        var values = ValidRerankConfig();
        values["Failover:RecoveryIntervalSeconds"] = "0";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverReranking(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("RecoveryIntervalSeconds", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverEmbeddings_WithInstances_RegistersService()
    {
        LogTestStart();
        var primaryMock = new Mock<IEmbeddingService>();
        primaryMock.Setup(p => p.EmbeddingSize).Returns(8);
        var backupMock = new Mock<IEmbeddingService>();
        backupMock.Setup(b => b.EmbeddingSize).Returns(8);

        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromSeconds(60)
        };

        var services = new ServiceCollection();
        services.AddFailoverEmbeddings(primaryMock.Object, backupMock.Object, options);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetService<IEmbeddingService>();

        Assert.NotNull(resolved);
        Assert.IsType<AchieveAi.LmDotnetTools.LmEmbeddings.Core.FailoverEmbeddingService>(resolved);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverReranking_MissingPrimaryEndpoint_Throws()
    {
        LogTestStart();
        var values = ValidRerankConfig();
        values["Failover:Primary:Endpoint"] = "";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverReranking(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("Primary:Endpoint", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverReranking_MissingBackupModel_Throws()
    {
        LogTestStart();
        var values = ValidRerankConfig();
        values["Failover:Backup:Model"] = "";
        var section = BuildSection(values);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFailoverReranking(section));
        Trace("Exception: {Message}", ex.Message);
        Assert.Contains("Backup:Model", ex.Message);
        LogTestEnd();
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public void AddFailoverReranking_WithInstances_RegistersService()
    {
        LogTestStart();
        var primaryMock = new Mock<IRerankService>();
        var backupMock = new Mock<IRerankService>();

        var options = new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromSeconds(60)
        };

        var services = new ServiceCollection();
        services.AddFailoverReranking(primaryMock.Object, backupMock.Object, options);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetService<IRerankService>();

        Assert.NotNull(resolved);
        Assert.IsType<AchieveAi.LmDotnetTools.LmEmbeddings.Core.FailoverRerankService>(resolved);
        LogTestEnd();
    }
}
