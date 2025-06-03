using System.Text.Json;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;

namespace AchieveAi.LmDotnetTools.Example.ModelUsageExamples;

/// <summary>
/// Demonstrates the practical value of ErrorModels and PerformanceModels
/// through structured error handling, performance monitoring, and health checks
/// </summary>
public class EmbeddingServiceUsageExample
{
    private readonly ServerEmbeddings _embeddingService;

    public EmbeddingServiceUsageExample(ServerEmbeddings embeddingService)
    {
        _embeddingService = embeddingService;
    }

    /// <summary>
    /// Example 1: Structured Error Handling for Production Systems
    /// Shows how ErrorModels provide actionable error information for monitoring systems
    /// </summary>
    public async Task<string> ProcessDocumentsWithStructuredErrorHandlingAsync(List<string> documents)
    {
        var result = await _embeddingService.GenerateEmbeddingsWithMetricsAsync(documents);
        
        if (!result.Success)
        {
            // The ErrorModels provide structured information that can be:
            // 1. Logged to structured logging systems (Serilog, etc.)
            // 2. Sent to monitoring systems (Application Insights, DataDog)
            // 3. Used for automated retry logic
            // 4. Parsed by alerting systems
            
            var errorJson = JsonSerializer.Serialize(result.Error, new JsonSerializerOptions { WriteIndented = true });
            
            // Example: Automated retry decision based on structured error
            if (result.Error?.IsRetryable == true)
            {
                await Task.Delay(result.Error.RetryAfterMs ?? 1000);
                Console.WriteLine($"Retrying after {result.Error.RetryAfterMs}ms due to retryable error: {result.Error.Code}");
                // Could implement actual retry logic here
            }
            
            // Example: Different handling based on error source
            var actionRequired = result.Error?.Source switch
            {
                ErrorSource.Authentication => "Check API credentials and refresh tokens",
                ErrorSource.RateLimit => "Implement backoff strategy and reduce request rate",
                ErrorSource.Api => "Check service status and network connectivity",
                ErrorSource.Validation => "Fix input validation errors in client code",
                _ => "General error handling required"
            };

            return $"Error Processing Documents:\n{errorJson}\n\nRecommended Action: {actionRequired}";
        }

        // Success case with performance metrics
        var metrics = result.Metrics;
        var performanceJson = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        
        return $"Successfully processed {result.Data?.Count} documents.\n" +
               $"Performance Metrics:\n{performanceJson}";
    }

    /// <summary>
    /// Example 2: Performance Monitoring and Optimization
    /// Shows how PerformanceModels enable data-driven optimization decisions
    /// </summary>
    public async Task<string> AnalyzeServicePerformanceAsync()
    {
        // Run performance analysis with different input sizes
        var testCases = new[]
        {
            "Short text",
            "Medium length text that represents typical document content with several sentences and more complexity.",
            new string('A', 1000), // Long text
            "Unicode test: 🌟 Emojis and special characters! 日本語 テスト"
        };

        var profile = await _embeddingService.AnalyzePerformanceAsync(testCases);
        var profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });

        // The PerformanceModels enable:
        // 1. Performance regression detection
        // 2. Optimization opportunity identification
        // 3. SLA monitoring and alerting
        // 4. Cost optimization analysis
        // 5. Capacity planning

        var optimizationRecommendations = GenerateOptimizationRecommendations(profile);
        
        return $"Performance Analysis Results:\n{profileJson}\n\n" +
               $"Optimization Recommendations:\n{optimizationRecommendations}";
    }

    /// <summary>
    /// Example 3: Health Monitoring for Production Services
    /// Shows how ConfigurationModels enable comprehensive health checks
    /// </summary>
    public async Task<string> PerformHealthCheckAsync()
    {
        var healthResult = await _embeddingService.GetHealthAsync();
        var healthJson = JsonSerializer.Serialize(healthResult, new JsonSerializerOptions { WriteIndented = true });

        // The ConfigurationModels (HealthCheckResult) enable:
        // 1. Automated health monitoring
        // 2. Detailed component-level diagnostics
        // 3. Integration with health check frameworks
        // 4. Dependency validation
        // 5. Configuration drift detection

        var healthSummary = healthResult.Status switch
        {
            HealthStatus.Healthy => "✅ Service is operating normally",
            HealthStatus.Degraded => "⚠️ Service is operational but with reduced performance",
            HealthStatus.Unhealthy => "❌ Service is experiencing issues and may be unavailable",
            _ => "❓ Health status is unknown"
        };

        return $"Health Check Results:\n{healthJson}\n\n" +
               $"Status: {healthSummary}\n" +
               $"Response Time: {healthResult.ResponseTimeMs:F2}ms";
    }

    /// <summary>
    /// Example 4: Structured Logging Integration
    /// Shows how the models integrate with structured logging systems
    /// </summary>
    public async Task LogStructuredMetricsAsync(List<string> texts)
    {
        var result = await _embeddingService.GenerateEmbeddingsWithMetricsAsync(texts);

        // With structured models, you can:
        // 1. Log to structured logging systems with rich context
        // 2. Create custom dashboards and alerts
        // 3. Correlate performance with business metrics
        // 4. Track usage patterns and optimization opportunities

        if (result.Success && result.Metrics != null)
        {
            // Example: Structured logging with Serilog
            Console.WriteLine("Structured Log Entry:");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Level = "Information",
                Message = "Embedding generation completed",
                RequestId = result.Metrics.RequestId,
                Service = result.Metrics.Service,
                DurationMs = result.Metrics.DurationMs,
                InputCount = result.Metrics.InputCount,
                TotalTokens = result.Metrics.TotalTokens,
                Success = result.Metrics.Success,
                TimingBreakdown = result.Metrics.TimingBreakdown
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (!result.Success && result.Error != null)
        {
            // Example: Structured error logging
            Console.WriteLine("Structured Error Log:");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Level = "Error",
                Message = "Embedding generation failed",
                Error = result.Error,
                Metrics = result.Metrics
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private string GenerateOptimizationRecommendations(PerformanceProfile profile)
    {
        var recommendations = new List<string>();

        // Response time analysis
        if (profile.ResponseTimes.AverageMs > 1000)
        {
            recommendations.Add("• Consider implementing request batching to improve efficiency");
        }

        if (profile.ResponseTimes.P99Ms > profile.ResponseTimes.AverageMs * 3)
        {
            recommendations.Add("• High tail latency detected - investigate retry logic and timeouts");
        }

        // Throughput analysis
        if (profile.Throughput.RequestsPerSecond < 10)
        {
            recommendations.Add("• Low throughput detected - consider connection pooling or parallel requests");
        }

        // Error rate analysis
        if (profile.ErrorRates.ErrorRatePercent > 5)
        {
            recommendations.Add($"• High error rate ({profile.ErrorRates.ErrorRatePercent:F1}%) - review error handling and retry logic");
        }

        if (profile.ErrorRates.AverageRetries > 1)
        {
            recommendations.Add("• High retry frequency suggests network or service issues");
        }

        return recommendations.Count > 0 
            ? string.Join("\n", recommendations)
            : "• No optimization opportunities identified - service is performing well";
    }

    /// <summary>
    /// Example 5: Configuration Validation
    /// Shows how ConfigurationModels can validate service setup
    /// </summary>
    public Task<ServiceConfiguration> ValidateAndCreateConfigurationAsync()
    {
        // The ConfigurationModels enable comprehensive service configuration
        // that can be validated, versioned, and managed centrally

        var config = new ServiceConfiguration
        {
            Id = "embedding-service-local",
            Name = "Local Nomic Embedding Service",
            Provider = "Local",
            Endpoint = new EndpointConfiguration
            {
                BaseUrl = "http://192.168.11.139:8078",
                Authentication = new AuthenticationConfiguration
                {
                    Type = "Bearer",
                    Credentials = "sk-embed-text-v1.5.f16.gguf",
                },
                TimeoutMs = 30000
            },
            DefaultModel = new ModelConfiguration
            {
                Id = "nomic-embed-text-v1.5",
                Name = "Nomic Embed Text v1.5",
                EmbeddingSize = 1536,
                MaxInputTokens = 8192
            },
            Resilience = new ResilienceConfiguration
            {
                MaxRetries = 3,
                BaseDelayMs = 1000,
                Strategy = RetryStrategy.Linear
            },
            Capabilities = new ServiceCapabilities
            {
                SupportsBatch = true,
                SupportsReranking = false,
                MaxBatchSize = 100,
                EncodingFormats = new[] { "float" }.ToImmutableList()
            }
        };

        // Configuration can be:
        // 1. Validated at startup
        // 2. Stored in configuration management systems
        // 3. Used for feature toggles and capability detection
        // 4. Monitored for configuration drift

        return Task.FromResult(config);
    }

    /// <summary>
    /// Example 6: Real-World Integration Test
    /// Tests the actual local embedding service with structured monitoring
    /// </summary>
    public async Task<string> TestRealIntegrationAsync()
    {
        Console.WriteLine("🚀 Testing real integration with local embedding service...\n");

        try
        {
            // Test different types of content
            var testDocuments = new List<string>
            {
                "The quick brown fox jumps over the lazy dog.",
                "Machine learning models are transforming natural language processing.",
                "Local embedding servers provide better privacy and control over data.",
                "Performance monitoring helps identify bottlenecks in production systems."
            };

            var result = await _embeddingService.GenerateEmbeddingsWithMetricsAsync(testDocuments);

            if (result.Success && result.Data != null)
            {
                var summary = "✅ INTEGRATION TEST SUCCESSFUL\n\n" +
                    "📊 Results Summary:\n" +
                    $"• Processed {result.Data.Count} documents\n" +
                    $"• Generated embeddings with {result.Data.FirstOrDefault()?.Count ?? 0} dimensions\n" +
                    $"• Request ID: {result.Metrics?.RequestId}\n" +
                    $"• Duration: {result.Metrics?.DurationMs:F2}ms\n" +
                    $"• Service: {result.Metrics?.Service}\n" +
                    $"• Model: {result.Metrics?.Model}\n\n" +
                    "🔍 Performance Metrics:\n" +
                    $"• Input Count: {result.Metrics?.InputCount}\n" +
                    $"• Estimated Tokens: {result.Metrics?.TotalTokens}\n" +
                    $"• Success: {result.Metrics?.Success}\n" +
                    $"• Validation Time: {result.Metrics?.TimingBreakdown?.ValidationMs:F2}ms\n" +
                    $"• Server Processing: {result.Metrics?.TimingBreakdown?.ServerProcessingMs:F2}ms\n\n" +
                    "📋 Sample Embedding Vector (first 10 dimensions):\n" +
                    string.Join(", ", result.Data.FirstOrDefault()?.Take(10)?.Select(f => f.ToString("F4")) ?? new[] { "N/A" }) + "...\n\n" +
                    "🎯 This demonstrates how ErrorModels and PerformanceModels provide:\n" +
                    "• Structured success/failure handling\n" +
                    "• Detailed performance insights\n" +
                    "• Request tracing capabilities\n" +
                    "• Automated monitoring data";

                return summary;
            }
            else
            {
                var errorDetails = JsonSerializer.Serialize(result.Error, new JsonSerializerOptions { WriteIndented = true });
                return "❌ INTEGRATION TEST FAILED\n\n" +
                    "Error Details:\n" +
                    errorDetails + "\n\n" +
                    "This demonstrates how ErrorModels provide structured error information for debugging and monitoring.";
            }
        }
        catch (Exception ex)
        {
            return "💥 INTEGRATION TEST EXCEPTION\n\n" +
                $"Exception: {ex.GetType().Name}\n" +
                $"Message: {ex.Message}\n\n" +
                "This shows how exceptions are handled and can be converted to structured EmbeddingError models.";
        }
    }
}

/// <summary>
/// Example usage with real local embedding service configuration
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("🔧 LmEmbeddings ModelUsageExamples - Local Server Integration");
        Console.WriteLine(new string('=', 70) + "\n");

        // Create embedding service with user's actual configuration
        var embeddingService = new ServerEmbeddings(
            endpoint: "http://192.168.11.139:8078", 
            model: "nomic-embed-text-v1.5", 
            embeddingSize: 1536,
            apiKey: "sk-embed-text-v1.5.f16.gguf");

        var examples = new EmbeddingServiceUsageExample(embeddingService);

        // Test real integration first
        Console.WriteLine("=== Real Integration Test ===");
        var integrationResult = await examples.TestRealIntegrationAsync();
        Console.WriteLine(integrationResult);
        Console.WriteLine("\n" + new string('=', 70) + "\n");

        // If the integration test was successful, run other examples
        if (integrationResult.Contains("SUCCESSFUL"))
        {
            // Demonstrate structured error handling
            Console.WriteLine("=== Structured Error Handling ===");
            var documents = new List<string> { "Example document", "Another document" };
            var errorResult = await examples.ProcessDocumentsWithStructuredErrorHandlingAsync(documents);
            Console.WriteLine(errorResult);
            Console.WriteLine("\n" + new string('-', 50) + "\n");

            // Demonstrate performance monitoring
            Console.WriteLine("=== Performance Monitoring ===");
            var performanceResult = await examples.AnalyzeServicePerformanceAsync();
            Console.WriteLine(performanceResult);
            Console.WriteLine("\n" + new string('-', 50) + "\n");

            // Demonstrate health checking
            Console.WriteLine("=== Health Monitoring ===");
            var healthResult = await examples.PerformHealthCheckAsync();
            Console.WriteLine(healthResult);
            Console.WriteLine("\n" + new string('-', 50) + "\n");

            // Demonstrate structured logging
            Console.WriteLine("=== Structured Logging ===");
            await examples.LogStructuredMetricsAsync(documents);
            Console.WriteLine("\n" + new string('-', 50) + "\n");

            // Demonstrate configuration management
            Console.WriteLine("=== Configuration Management ===");
            var config = await examples.ValidateAndCreateConfigurationAsync();
            var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Service Configuration:\n{configJson}");
        }
        else
        {
            Console.WriteLine("⚠️  Integration test failed. Check if the embedding server is running at:");
            Console.WriteLine("   http://192.168.11.139:8078/v1/embeddings");
            Console.WriteLine("\nThe structured error handling above demonstrates how ErrorModels");
            Console.WriteLine("provide actionable information for debugging and monitoring.");
        }

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine("✨ This example demonstrates the practical value of:");
        Console.WriteLine("   • ErrorModels: Structured error handling and debugging");
        Console.WriteLine("   • PerformanceModels: Real-time monitoring and optimization");
        Console.WriteLine("   • ConfigurationModels: Service health and configuration management");
        Console.WriteLine(new string('=', 70));
    }
} 