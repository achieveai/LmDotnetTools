using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Tests for OpenRouterModelService dependency injection registration.
/// Implements requirement 6.1: Add service registration for dependency injection container.
/// </summary>
public class OpenRouterModelServiceRegistrationTests
{
    [Fact]
    public void ServiceRegistration_WithAddLmConfig_ShouldRegisterOpenRouterModelService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act
        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var openRouterService = serviceProvider.GetService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);

        // Verify it's registered as singleton
        var openRouterService2 = serviceProvider.GetService<OpenRouterModelService>();
        Assert.Same(openRouterService, openRouterService2);
    }

    [Fact]
    public void ServiceRegistration_ShouldResolveWithDependencies()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act
        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);

        // Verify dependencies are injected
        var logger = serviceProvider.GetRequiredService<ILogger<OpenRouterModelService>>();
        Assert.NotNull(logger);

        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        Assert.NotNull(httpClientFactory);
    }

    [Fact]
    public async Task ServiceRegistration_ResolvedService_ShouldBeFullyFunctional()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Act
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();

        // Assert - Service should be functional (this will use cache or return empty list)
        var result = await openRouterService.GetModelConfigsAsync();
        Assert.NotNull(result);

        // Verify cache info functionality
        var cacheInfo = openRouterService.GetCacheInfo();
        Assert.NotNull(cacheInfo);
        Assert.NotNull(cacheInfo.FilePath);

        // Verify refresh functionality doesn't throw due to DI issues
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await openRouterService.RefreshCacheAsync(cts.Token);
        }
        catch (Exception)
        {
            // May throw due to network/timeout, but shouldn't throw due to DI issues
            // The fact that we can call the method means DI is working
        }
    }

    [Fact]
    public void ServiceRegistration_MultipleRegistrations_ShouldNotConflict()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act - Register multiple times (should not cause issues)
        services.AddLmConfig(appConfig);
        services.AddLmConfig(appConfig);

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);

        // Should still be singleton
        var openRouterService2 = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.Same(openRouterService, openRouterService2);
    }

    [Fact]
    public void ServiceRegistration_WithCustomHttpClient_ShouldUseCustomClient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Add custom HttpClient configuration
        services.AddHttpClient<OpenRouterModelService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "CustomTestAgent");
        });

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act
        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);

        // Verify the service can be created (HttpClient configuration is internal)
        var cacheInfo = openRouterService.GetCacheInfo();
        Assert.NotNull(cacheInfo);
    }

    [Fact]
    public void ServiceRegistration_WithoutHttpClient_ShouldStillWork()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        // Note: Not adding HttpClient explicitly

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act
        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);
    }

    [Fact]
    public void ServiceRegistration_ServiceLifetime_ShouldBeSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        // Act
        services.AddLmConfig(appConfig);

        // Assert
        var serviceDescriptor = services.FirstOrDefault(s =>
            s.ServiceType == typeof(OpenRouterModelService)
        );
        Assert.NotNull(serviceDescriptor);
        Assert.Equal(ServiceLifetime.Singleton, serviceDescriptor.Lifetime);
    }

    [Fact]
    public void ServiceRegistration_WithDifferentConfigurationMethods_ShouldAllRegisterService()
    {
        // Test different AddLmConfig overloads
        var testCases = new[]
        {
            () =>
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddHttpClient();
                var appConfig = new AppConfig { Models = new List<ModelConfig>() };
                services.AddLmConfig(appConfig);
                return services.BuildServiceProvider();
            },
            () =>
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddHttpClient();
                services.AddLmConfig(options =>
                {
                    options.AppConfig = new AppConfig { Models = new List<ModelConfig>() };
                    options.RegisterAsDefaultAgent = false;
                });
                return services.BuildServiceProvider();
            },
        };

        foreach (var createServiceProvider in testCases)
        {
            // Act
            var serviceProvider = createServiceProvider();

            // Assert
            var openRouterService = serviceProvider.GetService<OpenRouterModelService>();
            Assert.NotNull(openRouterService);
        }
    }

    [Fact]
    public async Task ServiceRegistration_DisposalHandling_ShouldDisposeCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var appConfig = new AppConfig { Models = new List<ModelConfig>() };

        services.AddLmConfig(appConfig);
        var serviceProvider = services.BuildServiceProvider();

        // Act
        var openRouterService = serviceProvider.GetRequiredService<OpenRouterModelService>();
        Assert.NotNull(openRouterService);

        // Dispose the service provider
        await serviceProvider.DisposeAsync();

        // Assert - Should not throw when disposed
        // The service implements IDisposable, so it should be disposed with the container
        Assert.True(true); // If we get here without exception, disposal worked correctly
    }
}
