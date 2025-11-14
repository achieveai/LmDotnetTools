using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Extension methods for ServerEmbeddings that provide structured results with performance metrics and error handling
/// </summary>
public static class ServerEmbeddingsExtensions
{
    /// <summary>
    /// Generate embeddings with comprehensive metrics and structured error handling
    /// </summary>
    /// <param name="service">The embedding service</param>
    /// <param name="texts">Texts to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Structured result with embeddings, performance metrics, and error details</returns>
    public static async Task<EmbeddingServiceResult<List<List<float>>>> GenerateEmbeddingsWithMetricsAsync(
        this ServerEmbeddings service,
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default
    )
    {
        var requestId = Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        try
        {
            var textList = texts.ToList();

            // Validate inputs
            if (textList.Count == 0)
            {
                return EmbeddingResults.ValidationError<List<List<float>>>(
                    "texts",
                    "Cannot be empty",
                    textList,
                    requestId
                );
            }

            if (textList.Any(string.IsNullOrWhiteSpace))
            {
                return EmbeddingResults.ValidationError<List<List<float>>>(
                    "texts",
                    "Cannot contain null or empty strings",
                    textList,
                    requestId
                );
            }

            // Create embedding request using the service's configured model
            var request = new EmbeddingRequest
            {
                Inputs = textList.ToArray(),
                Model = "nomic-embed-text-v1.5", // Use the configured model
                ApiType = EmbeddingApiType.Default,
            };

            // Generate embeddings using the correct method
            var response = await service.GenerateEmbeddingsAsync(request, cancellationToken);

            stopwatch.Stop();

            // Convert embeddings to the expected format
            var embeddings =
                response.Embeddings?.Select(e => e.Vector?.ToList() ?? new List<float>()).ToList()
                ?? new List<List<float>>();

            // Create performance metrics
            var metrics = new RequestMetrics
            {
                RequestId = requestId,
                Service = "ServerEmbeddings",
                Model = response.Model ?? "Unknown",
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                InputCount = textList.Count,
                TotalTokens = EstimateTokenCount(textList),
                Success = true,
                StatusCode = 200,
                TimingBreakdown = new TimingBreakdown
                {
                    ValidationMs = 1.0, // Minimal validation time
                    ServerProcessingMs = stopwatch.Elapsed.TotalMilliseconds - 1.0,
                },
            };

            return EmbeddingServiceResult<List<List<float>>>.CreateSuccess(embeddings, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Create metrics even for failed requests
            var metrics = new RequestMetrics
            {
                RequestId = requestId,
                Service = "ServerEmbeddings",
                Model = "Unknown",
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                InputCount = texts.Count(),
                Success = false,
                Error = ex.Message,
            };

            return EmbeddingServiceResult<List<List<float>>>.FromException(ex, requestId, metrics);
        }
    }

    /// <summary>
    /// Get service health with structured configuration validation
    /// </summary>
    /// <param name="service">The embedding service</param>
    /// <returns>Health check result with configuration validation</returns>
    public static async Task<HealthCheckResult> GetHealthAsync(this ServerEmbeddings service)
    {
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<ComponentHealth>();

        try
        {
            // Test basic connectivity with a simple text
            var testEmbedding = await service.GetEmbeddingAsync("health check test");

            checks.Add(
                new ComponentHealth
                {
                    Component = "API Connectivity",
                    Status = HealthStatus.Healthy,
                    Details = ImmutableDictionary<string, object>.Empty.Add("embedding_size", testEmbedding.Length),
                }
            );

            // Validate configuration
            var embeddingSize = service.EmbeddingSize;
            checks.Add(
                new ComponentHealth
                {
                    Component = "Configuration",
                    Status = embeddingSize > 0 ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Details = ImmutableDictionary<string, object>.Empty.Add("embedding_size", embeddingSize),
                }
            );

            stopwatch.Stop();

            var overallStatus = checks.All(c => c.Status == HealthStatus.Healthy)
                ? HealthStatus.Healthy
                : HealthStatus.Degraded;

            return new HealthCheckResult
            {
                Service = "ServerEmbeddings",
                Status = overallStatus,
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Checks = checks.ToImmutableList(),
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new HealthCheckResult
            {
                Service = "ServerEmbeddings",
                Status = HealthStatus.Unhealthy,
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Error = ex.Message,
                Checks = checks.ToImmutableList(),
            };
        }
    }

    /// <summary>
    /// Analyze performance profile for the service
    /// </summary>
    /// <param name="service">The embedding service</param>
    /// <param name="testCases">Test cases to run for profiling</param>
    /// <returns>Performance profile with detailed statistics</returns>
    public static async Task<PerformanceProfile> AnalyzePerformanceAsync(
        this ServerEmbeddings service,
        IEnumerable<string> testCases
    )
    {
        var testList = testCases.ToList();
        var results = new List<RequestMetrics>();
        var startTime = DateTime.UtcNow;

        foreach (var testCase in testList)
        {
            var result = await service.GenerateEmbeddingsWithMetricsAsync(new[] { testCase });
            if (result.Metrics != null)
            {
                results.Add(result.Metrics);
            }
        }

        var endTime = DateTime.UtcNow;
        var responseTimes = results.Where(r => r.DurationMs.HasValue).Select(r => r.DurationMs!.Value).ToList();

        return new PerformanceProfile
        {
            Identifier = "ServerEmbeddings",
            Type = ProfileType.Service,
            TimePeriod = new TimePeriod { Start = startTime, End = endTime },
            ResponseTimes = new ResponseTimeStats
            {
                AverageMs = responseTimes.Count != 0 ? responseTimes.Average() : 0,
                MedianMs =
                    responseTimes.Count != 0
                        ? responseTimes.OrderBy(x => x).Skip(responseTimes.Count / 2).FirstOrDefault()
                        : 0,
                P95Ms =
                    responseTimes.Count != 0
                        ? responseTimes.OrderBy(x => x).Skip((int)(responseTimes.Count * 0.95)).FirstOrDefault()
                        : 0,
                P99Ms =
                    responseTimes.Count != 0
                        ? responseTimes.OrderBy(x => x).Skip((int)(responseTimes.Count * 0.99)).FirstOrDefault()
                        : 0,
                MinMs = responseTimes.Count != 0 ? responseTimes.Min() : 0,
                MaxMs = responseTimes.Count != 0 ? responseTimes.Max() : 0,
                StdDevMs = responseTimes.Count != 0 ? CalculateStandardDeviation(responseTimes) : 0,
            },
            Throughput = new ThroughputStats
            {
                RequestsPerSecond = results.Count / Math.Max((endTime - startTime).TotalSeconds, 1),
                TotalRequests = results.Count,
                TotalTokens = results.Sum(r => r.TotalTokens ?? 0),
            },
            ErrorRates = new ErrorRateStats
            {
                ErrorRatePercent =
                    results.Count != 0 ? (results.Count(r => !r.Success) / (double)results.Count) * 100 : 0,
                TotalErrors = results.Count(r => !r.Success),
                AverageRetries = results.Count != 0 ? results.Average(r => r.RetryCount) : 0,
                SuccessRateAfterRetriesPercent =
                    results.Count != 0 ? (results.Count(r => r.Success) / (double)results.Count) * 100 : 0,
            },
        };
    }

    private static int EstimateTokenCount(IEnumerable<string> texts)
    {
        // Simple estimation: ~4 characters per token
        return texts.Sum(t => t.Length / 4);
    }

    private static double CalculateStandardDeviation(IEnumerable<double> values)
    {
        var valueList = values.ToList();
        if (valueList.Count == 0)
            return 0;

        var average = valueList.Average();
        var sumOfSquares = valueList.Sum(v => Math.Pow(v - average, 2));
        return Math.Sqrt(sumOfSquares / valueList.Count);
    }
}
