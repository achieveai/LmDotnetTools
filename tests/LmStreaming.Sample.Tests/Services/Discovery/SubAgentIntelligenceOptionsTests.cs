using System.Text;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

public sealed class SubAgentIntelligenceOptionsTests
{
    [Fact]
    public void ConfigurationBinding_PreservesTierCandidateOrder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SubAgentIntelligence:Tiers:3:0"] = "model-a",
                    ["SubAgentIntelligence:Tiers:3:1"] = "model-b",
                    ["SubAgentIntelligence:Tiers:3:2"] = "model-c",
                }
            )
            .Build();

        var options = SubAgentIntelligenceOptions.Load(
            configuration,
            new CapturingLogger<SubAgentIntelligenceOptions>()
        );

        options.Tiers[3].Should().Equal("model-a", "model-b", "model-c");
    }

    [Fact]
    public void Load_LogsAndSkipsMalformedAndOutOfRangeKeysWithoutDroppingValidMappings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SubAgentIntelligence:Tiers:not-an-integer:0"] = "bad-model",
                    ["SubAgentIntelligence:Tiers:7:0"] = "out-of-range-model",
                    ["SubAgentIntelligence:Tiers:4:0"] = "valid-model",
                }
            )
            .Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().ContainSingle().Which.Key.Should().Be(4);
        options.Tiers[4].Should().Equal("valid-model");
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(2);
    }

    [Fact]
    public void Load_LogsAndSkipsDuplicateNormalizedTierKeyWhileKeepingTheFirst()
    {
        // "3" and "03" both normalize to integer tier 3; the second is a duplicate and must be
        // logged and skipped, leaving a single tier-3 mapping.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SubAgentIntelligence:Tiers:3:0"] = "first-model",
                    ["SubAgentIntelligence:Tiers:03:0"] = "duplicate-model",
                }
            )
            .Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().ContainSingle().Which.Key.Should().Be(3);
        options.Tiers[3].Should().Equal("first-model");
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void ConfigurationBinding_EnumeratesAnEmptyTierArrayAsAChildKeyEvenThoughItDoesNotExist()
    {
        // The premise the whole empty-ladder defect rests on, EXECUTED rather than inferred — and it is
        // subtler than "an empty array binds to a present key". The JSON provider records an empty array at
        // its own path with a NULL value, so:
        //   * GetChildren() on Tiers DOES yield "3" — which is what Load iterates, which is why a stub of
        //     seven empty arrays produced Tiers.Count == 7 and silenced the "Tiers is empty" diagnostic in
        //     SubAgentModelResolver.Resolve (zero occurrences in every host log, while its downstream
        //     "no routable candidate" sibling fired dozens of times), yet
        //   * GetSection("3").Exists() is FALSE, because the section has neither a value nor children.
        // Both halves matter: the first is why the tier counted as configured, the second is why an
        // Exists()-based guard would not have caught it. Get<string[]>() on that section binds to NULL, and
        // Load's own `?? []` is what turns it into the empty candidate list the new guard rejects.
        var json = """{ "SubAgentIntelligence": { "Tiers": { "3": [], "4": ["a-model"] } } }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var tierSection = configuration.GetSection("SubAgentIntelligence").GetSection("Tiers");

        // The empty tier is enumerated exactly like the populated one.
        tierSection.GetChildren().Select(child => child.Key).Should().Equal("3", "4");
        tierSection
            .GetSection("3")
            .Exists()
            .Should()
            .BeFalse("an empty array has neither a value nor children, so Exists() cannot see it");
        tierSection
            .GetSection("3")
            .Get<string[]>()
            .Should()
            .BeNull("binding yields null, which Load's `?? []` turns into the empty candidate list");
        tierSection.GetSection("4").Get<string[]>().Should().Equal("a-model");
    }

    [Fact]
    public void Load_ReportsEveryEmptyTierAsMisconfiguredSoTheEmptyLadderDiagnosticCanFire()
    {
        // The shipped stub, verbatim: seven keys, every one an empty array. Load must report each as
        // misconfigured and drop it, leaving an EMPTY tier map — which is what lets the resolver's
        // "Tiers is empty" warning name the actual cause instead of the per-tier symptom.
        var json = """
            {
              "SubAgentIntelligence": {
                "Tiers": { "0": [], "1": [], "2": [], "3": [], "4": [], "5": [], "6": [] }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().BeEmpty("a tier that can resolve nothing is not a configured tier");
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(7);
        logger.Entries.Should().Contain(entry => entry.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_KeepsPopulatedTiersWhileDroppingEmptyAndBlankOnlyOnes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SubAgentIntelligence:Tiers:1:0"] = "   ",
                    ["SubAgentIntelligence:Tiers:5:0"] = "real-model",
                }
            )
            .Build();
        var logger = new CapturingLogger<SubAgentIntelligenceOptions>();

        var options = SubAgentIntelligenceOptions.Load(configuration, logger);

        options.Tiers.Should().ContainSingle().Which.Key.Should().Be(5);
        options.Tiers[5].Should().Equal("real-model");
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Appsettings_TierLadderIsPopulatedForEverySupportedTier()
    {
        // The stub of empty arrays did not merely fail to configure the ladder — it also emptied
        // SubAgentModelResolver's model allow-list and the Agent tool's advertised id menu (see
        // SubAgentModelResolverTests.Appsettings_*). Every tier is pinned to the model in use today, so the
        // ladder is populated without changing which model anything runs on.
        using var document = JsonDocument.Parse(File.ReadAllText(AppsettingsPath));

        var tiers = document.RootElement.GetProperty("SubAgentIntelligence").GetProperty("Tiers");

        tiers.ValueKind.Should().Be(JsonValueKind.Object);
        tiers.EnumerateObject().Select(tier => tier.Name).Should().Equal("0", "1", "2", "3", "4", "5", "6");
        tiers
            .EnumerateObject()
            .Should()
            .OnlyContain(tier => tier.Value.ValueKind == JsonValueKind.Array && tier.Value.GetArrayLength() > 0);
    }

    /// <summary>
    ///     The authoritative checked-in host <c>appsettings.json</c>, copied to the test output by MSBuild.
    ///     The explicit content item keeps this contract test independent of the output directory depth,
    ///     including isolated <c>--artifacts-path</c> builds.
    /// </summary>
    internal static string AppsettingsPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "TestData", "LmStreaming.Sample", "appsettings.json");
}
