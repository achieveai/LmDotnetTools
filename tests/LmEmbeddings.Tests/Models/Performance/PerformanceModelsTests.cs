using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Models.Performance;

/// <summary>
/// Tests for Performance models including serialization, validation, and data integrity
/// </summary>
public class PerformanceModelsTests
{
    private readonly ILogger<PerformanceModelsTests> _logger;

    public PerformanceModelsTests()
    {
        _logger = TestLoggerFactory.CreateLogger<PerformanceModelsTests>();
    }

    #region RequestMetrics Tests

    [Theory]
    [MemberData(nameof(RequestMetricsTestCases))]
    public void RequestMetrics_Serialization_SerializesCorrectly(
        RequestMetrics metrics,
        string[] expectedJsonProperties,
        string description
    )
    {
        Debug.WriteLine($"Testing RequestMetrics serialization: {description}");
        Debug.WriteLine(
            $"RequestId: {metrics.RequestId}, Service: {metrics.Service}, Success: {metrics.Success}"
        );

        // Act
        var json = JsonSerializer.Serialize(metrics);
        var deserializedMetrics = JsonSerializer.Deserialize<RequestMetrics>(json);

        // Assert
        Assert.NotNull(json);
        Assert.NotNull(deserializedMetrics);

        foreach (var expectedProperty in expectedJsonProperties)
        {
            Assert.Contains(expectedProperty, json);
            Debug.WriteLine($"✓ Found expected property: {expectedProperty}");
        }

        Assert.Equal(metrics.RequestId, deserializedMetrics.RequestId);
        Assert.Equal(metrics.Service, deserializedMetrics.Service);
        Assert.Equal(metrics.Success, deserializedMetrics.Success);

        Debug.WriteLine($"✓ RequestMetrics serialization successful: {json.Length} characters");
    }

    [Theory]
    [MemberData(nameof(TimingBreakdownTestCases))]
    public void TimingBreakdown_Validation_ValidatesCorrectly(
        TimingBreakdown timing,
        bool isValid,
        string description
    )
    {
        Debug.WriteLine($"Testing TimingBreakdown validation: {description}");
        Debug.WriteLine(
            $"ValidationMs: {timing.ValidationMs}, ServerProcessingMs: {timing.ServerProcessingMs}"
        );

        // Act
        var json = JsonSerializer.Serialize(timing);
        var deserializedTiming = JsonSerializer.Deserialize<TimingBreakdown>(json);

        // Assert
        Assert.NotNull(deserializedTiming);

        if (isValid)
        {
            Assert.Equal(timing.ValidationMs, deserializedTiming.ValidationMs);
            Assert.Equal(timing.ServerProcessingMs, deserializedTiming.ServerProcessingMs);
            Debug.WriteLine("✓ TimingBreakdown validation passed");
        }
        else
        {
            // For invalid cases, we still expect successful serialization but may want to validate business rules
            Assert.NotNull(deserializedTiming);
            Debug.WriteLine("✓ TimingBreakdown handled invalid case correctly");
        }
    }

    #endregion

    #region PerformanceProfile Tests

    [Theory]
    [MemberData(nameof(PerformanceProfileTestCases))]
    public void PerformanceProfile_CompleteProfile_SerializesCorrectly(
        PerformanceProfile profile,
        int expectedTrendCount,
        string description
    )
    {
        Debug.WriteLine($"Testing PerformanceProfile: {description}");
        Debug.WriteLine(
            $"Identifier: {profile.Identifier}, Type: {profile.Type}, Trends: {profile.Trends?.Count ?? 0}"
        );

        // Act
        var json = JsonSerializer.Serialize(profile);
        var deserializedProfile = JsonSerializer.Deserialize<PerformanceProfile>(json);

        // Assert
        Assert.NotNull(deserializedProfile);
        Assert.Equal(profile.Identifier, deserializedProfile.Identifier);
        Assert.Equal(profile.Type, deserializedProfile.Type);
        Assert.Equal(expectedTrendCount, deserializedProfile.Trends?.Count ?? 0);

        Debug.WriteLine($"✓ PerformanceProfile serialization successful: {json.Length} characters");
    }

    [Theory]
    [MemberData(nameof(ProfileTypeTestCases))]
    public void ProfileType_EnumSerialization_SerializesAsString(
        ProfileType profileType,
        string expectedStringValue,
        string description
    )
    {
        Debug.WriteLine($"Testing ProfileType enum serialization: {description}");
        Debug.WriteLine($"ProfileType: {profileType}, Expected: {expectedStringValue}");

        // Act
        var json = JsonSerializer.Serialize(profileType);
        var deserializedType = JsonSerializer.Deserialize<ProfileType>(json);

        // Assert
        Assert.Contains(expectedStringValue, json);
        Assert.Equal(profileType, deserializedType);

        Debug.WriteLine($"✓ ProfileType serialized as: {json}");
    }

    #endregion

    #region Statistics Models Tests

    [Theory]
    [MemberData(nameof(ResponseTimeStatsTestCases))]
    public void ResponseTimeStats_StatisticalValues_ValidatesCorrectly(
        ResponseTimeStats stats,
        bool isStatisticallyValid,
        string description
    )
    {
        Debug.WriteLine($"Testing ResponseTimeStats: {description}");
        Debug.WriteLine(
            $"Average: {stats.AverageMs}ms, P95: {stats.P95Ms}ms, P99: {stats.P99Ms}ms"
        );

        // Act
        var json = JsonSerializer.Serialize(stats);
        var deserializedStats = JsonSerializer.Deserialize<ResponseTimeStats>(json);

        // Assert
        Assert.NotNull(deserializedStats);
        Assert.Equal(stats.AverageMs, deserializedStats.AverageMs);
        Assert.Equal(stats.P95Ms, deserializedStats.P95Ms);
        Assert.Equal(stats.P99Ms, deserializedStats.P99Ms);

        if (isStatisticallyValid)
        {
            Assert.True(stats.MinMs <= stats.AverageMs);
            Assert.True(stats.AverageMs <= stats.P95Ms);
            Assert.True(stats.P95Ms <= stats.P99Ms);
            Assert.True(stats.P99Ms <= stats.MaxMs);
            Debug.WriteLine("✓ Statistical relationships validated");
        }

        Debug.WriteLine($"✓ ResponseTimeStats validation completed");
    }

    #endregion

    #region Usage Models Tests

    [Theory]
    [MemberData(nameof(UsageStatisticsTestCases))]
    public void UsageStatistics_CompleteUsage_SerializesCorrectly(
        UsageStatistics usage,
        int expectedModelCount,
        string description
    )
    {
        Debug.WriteLine($"Testing UsageStatistics: {description}");
        Debug.WriteLine(
            $"Entity: {usage.Entity}, EntityType: {usage.EntityType}, Models: {usage.ModelUsage?.Count ?? 0}"
        );

        // Act
        var json = JsonSerializer.Serialize(usage);
        var deserializedUsage = JsonSerializer.Deserialize<UsageStatistics>(json);

        // Assert
        Assert.NotNull(deserializedUsage);
        Assert.Equal(usage.Entity, deserializedUsage.Entity);
        Assert.Equal(usage.EntityType, deserializedUsage.EntityType);
        Assert.Equal(expectedModelCount, deserializedUsage.ModelUsage?.Count ?? 0);

        Debug.WriteLine($"✓ UsageStatistics serialization successful: {json.Length} characters");
    }

    #endregion

    #region Quality Models Tests

    [Theory]
    [MemberData(nameof(QualityMetricsTestCases))]
    public void QualityMetrics_QualityScores_ValidatesRanges(
        QualityMetrics quality,
        bool scoresInValidRange,
        string description
    )
    {
        Debug.WriteLine($"Testing QualityMetrics: {description}");
        Debug.WriteLine(
            $"AvgQualityScore: {quality.AvgQualityScore}, UserSatisfaction: {quality.UserSatisfaction}"
        );

        // Act
        var json = JsonSerializer.Serialize(quality);
        var deserializedQuality = JsonSerializer.Deserialize<QualityMetrics>(json);

        // Assert
        Assert.NotNull(deserializedQuality);

        if (scoresInValidRange && quality.AvgQualityScore.HasValue)
        {
            Assert.True(quality.AvgQualityScore >= 0.0 && quality.AvgQualityScore <= 1.0);
            Debug.WriteLine("✓ Quality scores within valid range [0,1]");
        }

        Debug.WriteLine($"✓ QualityMetrics validation completed");
    }

    #endregion

    #region Common Types Tests

    [Theory]
    [MemberData(nameof(TimePeriodTestCases))]
    public void TimePeriod_DurationCalculation_CalculatesCorrectly(
        TimePeriod period,
        double expectedDurationSeconds,
        string description
    )
    {
        Debug.WriteLine($"Testing TimePeriod: {description}");
        Debug.WriteLine(
            $"Start: {period.Start}, End: {period.End}, Expected Duration: {expectedDurationSeconds}s"
        );

        // Act
        var calculatedDuration = period.DurationSeconds;
        var json = JsonSerializer.Serialize(period);
        var deserializedPeriod = JsonSerializer.Deserialize<TimePeriod>(json);

        // Assert
        Assert.Equal(expectedDurationSeconds, calculatedDuration, 1); // 1 second tolerance
        Assert.NotNull(deserializedPeriod);
        Assert.Equal(period.Start, deserializedPeriod.Start);
        Assert.Equal(period.End, deserializedPeriod.End);

        Debug.WriteLine($"✓ Duration calculated: {calculatedDuration}s");
    }

    [Theory]
    [MemberData(nameof(TrendDirectionTestCases))]
    public void TrendDirection_EnumSerialization_SerializesAsString(
        TrendDirection trend,
        string expectedStringValue,
        string description
    )
    {
        Debug.WriteLine($"Testing TrendDirection enum: {description}");
        Debug.WriteLine($"TrendDirection: {trend}, Expected: {expectedStringValue}");

        // Act
        var json = JsonSerializer.Serialize(trend);
        var deserializedTrend = JsonSerializer.Deserialize<TrendDirection>(json);

        // Assert
        Assert.Contains(expectedStringValue, json);
        Assert.Equal(trend, deserializedTrend);

        Debug.WriteLine($"✓ TrendDirection serialized as: {json}");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> RequestMetricsTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new RequestMetrics
                {
                    RequestId = "req-123",
                    Service = "OpenAI",
                    Model = "text-embedding-3-small",
                    StartTime = DateTime.UtcNow,
                    EndTime = DateTime.UtcNow.AddMilliseconds(500),
                    DurationMs = 500,
                    InputCount = 3,
                    Success = true,
                },
                new[] { "request_id", "service", "model", "success" },
                "Basic successful request metrics",
            },
            new object[]
            {
                new RequestMetrics
                {
                    RequestId = "req-456",
                    Service = "Jina",
                    Model = "jina-embeddings-v2-base-en",
                    StartTime = DateTime.UtcNow,
                    InputCount = 1,
                    Success = false,
                    Error = "Rate limit exceeded",
                    RetryCount = 3,
                },
                new[] { "request_id", "service", "error", "retry_count" },
                "Failed request with retries",
            },
        };

    public static IEnumerable<object[]> TimingBreakdownTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new TimingBreakdown
                {
                    ValidationMs = 10,
                    RequestPreparationMs = 5,
                    ServerProcessingMs = 200,
                    ResponseProcessingMs = 15,
                },
                true,
                "Valid timing breakdown",
            },
            new object[]
            {
                new TimingBreakdown { ValidationMs = null, ServerProcessingMs = 150 },
                true,
                "Partial timing breakdown",
            },
        };

    public static IEnumerable<object[]> PerformanceProfileTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new PerformanceProfile
                {
                    Identifier = "openai-service",
                    Type = ProfileType.Service,
                    TimePeriod = new TimePeriod
                    {
                        Start = DateTime.UtcNow.AddDays(-1),
                        End = DateTime.UtcNow,
                    },
                    ResponseTimes = new ResponseTimeStats
                    {
                        AverageMs = 250,
                        P95Ms = 500,
                        P99Ms = 800,
                        MinMs = 100,
                        MaxMs = 1000,
                        MedianMs = 200,
                        StdDevMs = 150,
                    },
                    Throughput = new ThroughputStats
                    {
                        RequestsPerSecond = 10,
                        TotalRequests = 864000,
                        PeakRps = 25,
                    },
                    ErrorRates = new ErrorRateStats
                    {
                        ErrorRatePercent = 2.5,
                        TotalErrors = 21600,
                        AverageRetries = 1.2,
                        SuccessRateAfterRetriesPercent = 98.5,
                    },
                    Trends = ImmutableList.Create(
                        new PerformanceTrend
                        {
                            Timestamp = DateTime.UtcNow,
                            Metric = "response_time",
                            Value = 250,
                            Trend = TrendDirection.Improving,
                        }
                    ),
                },
                1,
                "Complete service performance profile",
            },
        };

    public static IEnumerable<object[]> ProfileTypeTestCases =>
        new List<object[]>
        {
            new object[] { ProfileType.Service, "Service", "Service profile type" },
            new object[] { ProfileType.Model, "Model", "Model profile type" },
            new object[] { ProfileType.Endpoint, "Endpoint", "Endpoint profile type" },
            new object[] { ProfileType.User, "User", "User profile type" },
            new object[] { ProfileType.Feature, "Feature", "Feature profile type" },
        };

    public static IEnumerable<object[]> ResponseTimeStatsTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new ResponseTimeStats
                {
                    AverageMs = 250,
                    MedianMs = 200,
                    P95Ms = 500,
                    P99Ms = 800,
                    MinMs = 100,
                    MaxMs = 1000,
                    StdDevMs = 150,
                },
                true,
                "Valid statistical distribution",
            },
            new object[]
            {
                new ResponseTimeStats
                {
                    AverageMs = 1000,
                    MedianMs = 50,
                    P95Ms = 100,
                    P99Ms = 150,
                    MinMs = 10,
                    MaxMs = 200,
                    StdDevMs = 25,
                },
                false,
                "Invalid statistical distribution (average > P99)",
            },
        };

    public static IEnumerable<object[]> UsageStatisticsTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new UsageStatistics
                {
                    Entity = "user-123",
                    EntityType = "user",
                    TimePeriod = new TimePeriod
                    {
                        Start = DateTime.UtcNow.AddDays(-30),
                        End = DateTime.UtcNow,
                    },
                    RequestVolume = new VolumeStats
                    {
                        Total = 1000,
                        AvgPerDay = 33.3,
                        PeakPerDay = 150,
                    },
                    ModelUsage = ImmutableDictionary
                        .Create<string, VolumeStats>()
                        .Add(
                            "text-embedding-3-small",
                            new VolumeStats
                            {
                                Total = 600,
                                AvgPerDay = 20,
                                PeakPerDay = 100,
                            }
                        )
                        .Add(
                            "text-embedding-3-large",
                            new VolumeStats
                            {
                                Total = 400,
                                AvgPerDay = 13.3,
                                PeakPerDay = 50,
                            }
                        ),
                },
                2,
                "User usage with multiple models",
            },
        };

    public static IEnumerable<object[]> QualityMetricsTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new QualityMetrics
                {
                    AvgQualityScore = 0.85,
                    QualityThresholdMetPercent = 92.5,
                    UserSatisfaction = 0.78,
                },
                true,
                "Valid quality scores",
            },
            new object[]
            {
                new QualityMetrics
                {
                    AvgQualityScore = 1.5, // Invalid: > 1.0
                    UserSatisfaction = -0.1, // Invalid: < 0.0
                },
                false,
                "Invalid quality scores",
            },
        };

    public static IEnumerable<object[]> TimePeriodTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new TimePeriod
                {
                    Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    End = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                    Description = "One hour period",
                },
                3600,
                "One hour duration",
            },
            new object[]
            {
                new TimePeriod
                {
                    Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    End = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Description = "One day period",
                },
                86400,
                "One day duration",
            },
        };

    public static IEnumerable<object[]> TrendDirectionTestCases =>
        new List<object[]>
        {
            new object[] { TrendDirection.Improving, "Improving", "Improving trend" },
            new object[] { TrendDirection.Stable, "Stable", "Stable trend" },
            new object[] { TrendDirection.Degrading, "Degrading", "Degrading trend" },
            new object[] { TrendDirection.Unknown, "Unknown", "Unknown trend" },
        };

    #endregion
}
