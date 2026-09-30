using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// A dual-layer preset is one provider id to the user: it must load from configuration like any
/// other host setting, show up in the catalog as one entry, and be available only when both of the
/// models behind it are.
/// </summary>
[Collection("EnvironmentVariables")]
public class DualLayerModelPresetsTests
{
    private static readonly CopilotModelInfo Astra = new(
        "gpt-6-astra",
        "GPT-6 Astra",
        CopilotModelVendor.OpenAI,
        CopilotModelTransport.Responses
    );
    private static readonly CopilotModelInfo Luna = new(
        "gpt-6-luna",
        "GPT-6 Luna",
        CopilotModelVendor.OpenAI,
        CopilotModelTransport.Responses
    );

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Load_ReadsEachPair_NormalizesIds_AndDefaultsTheDisplayName()
    {
        var presets = DualLayerModelPresets.Load(
            Config(
                ("DualLayerModels:Astuna:Planner", " GPT-6-Astra "),
                ("DualLayerModels:Astuna:Executor", "gpt-6-luna"),
                ("DualLayerModels:Astuna:DisplayName", "Astuna"),
                ("DualLayerModels:opuna:Planner", "claude-opus-5.5"),
                ("DualLayerModels:opuna:Executor", "gpt-6-luna")
            ),
            NullLogger.Instance
        );

        presets
            .Should()
            .BeEquivalentTo([
                new DualLayerModelPreset("astuna", "gpt-6-astra", "gpt-6-luna", "Astuna"),
                new DualLayerModelPreset(
                    "opuna",
                    "claude-opus-5.5",
                    "gpt-6-luna",
                    "opuna (claude-opus-5.5 + gpt-6-luna)"
                ),
            ]);
    }

    [Fact]
    public void Load_SkipsCommentKeys_AndDropsAPairMissingAModel()
    {
        var presets = DualLayerModelPresets.Load(
            Config(
                ("DualLayerModels:_comment", "prose"),
                ("DualLayerModels:_fauna:Planner", "claude-fable-5.1"),
                ("DualLayerModels:_fauna:Executor", "gpt-6-luna"),
                ("DualLayerModels:half:Planner", "gpt-6-astra"),
                ("DualLayerModels:astuna:Planner", "gpt-6-astra"),
                ("DualLayerModels:astuna:Executor", "gpt-6-luna")
            ),
            NullLogger.Instance
        );

        presets.Select(p => p.Id).Should().Equal("astuna");
    }

    [Fact]
    public void Registry_ListsAPreset_AsOneEntryInTheDualLayerGroup()
    {
        using var envMode = EnvScope.Set("LM_PROVIDER_MODE", "test");
        var registry = new ProviderRegistry(
            new FakeFileSystemProbe(),
            () => false,
            [Astra, Luna],
            copilotTokenAvailable: () => true,
            dualLayerPresets: [new DualLayerModelPreset("astuna", "gpt-6-astra", "gpt-6-luna", "Astuna")]
        );

        var entry = registry.ListAll().Should().ContainSingle(p => p.Id == "astuna").Subject;
        entry.Group.Should().Be("Dual layer");
        entry.DisplayName.Should().Be("Astuna");
        entry.Available.Should().BeTrue();
        registry.IsKnown("ASTUNA").Should().BeTrue();

        registry.TryGetDualLayerPreset("Astuna", out var preset).Should().BeTrue();
        preset.PlannerId.Should().Be("gpt-6-astra");
        preset.ExecutorId.Should().Be("gpt-6-luna");
        // The members stay single models: the two-loop wiring must branch on the pair only.
        registry.TryGetDualLayerPreset("gpt-6-astra", out _).Should().BeFalse();
        registry.TryGetCopilotModel("astuna", out _).Should().BeFalse();
    }

    [Fact]
    public void Registry_PresetIsAvailable_OnlyWhileBothMembersAre()
    {
        using var envMode = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var envCli = EnvScope.Set("CLAUDE_CLI_PATH", null);
        var mockHostRunning = false;
        // Planner is a Copilot model gated on the token; executor is a mock gated on the live host.
        var registry = new ProviderRegistry(
            new FakeFileSystemProbe(executablesOnPath: ["claude"]),
            () => mockHostRunning,
            [Astra],
            copilotTokenAvailable: () => true,
            dualLayerPresets: [new DualLayerModelPreset("astmock", "gpt-6-astra", "claude-mock", "Astra + mock")]
        );

        registry.IsAvailable("astmock").Should().BeFalse("the mock executor's host is down");
        registry.Get("astmock")!.Available.Should().BeFalse();

        mockHostRunning = true;
        registry.IsAvailable("astmock").Should().BeTrue();
        registry.ListAll().Single(p => p.Id == "astmock").Available.Should().BeTrue();

        var noToken = new ProviderRegistry(
            new FakeFileSystemProbe(executablesOnPath: ["claude"]),
            () => true,
            [Astra],
            copilotTokenAvailable: () => false,
            dualLayerPresets: [new DualLayerModelPreset("astmock", "gpt-6-astra", "claude-mock", "Astra + mock")]
        );
        noToken.IsAvailable("astmock").Should().BeFalse("the planner has no Copilot token");
        noToken.IsKnown("astmock").Should().BeTrue();
    }

    [Fact]
    public void Registry_DropsAPreset_ThatNamesAnUnknownModel_OrCollidesWithAProviderId()
    {
        using var envMode = EnvScope.Set("LM_PROVIDER_MODE", "test");
        var registry = new ProviderRegistry(
            new FakeFileSystemProbe(),
            () => false,
            [Astra, Luna],
            copilotTokenAvailable: () => true,
            dualLayerPresets:
            [
                new DualLayerModelPreset("fauna", "claude-fable-5.1", "gpt-6-luna", "Fauna"),
                new DualLayerModelPreset("test", "gpt-6-astra", "gpt-6-luna", "Shadows a real provider"),
                new DualLayerModelPreset("astuna", "gpt-6-astra", "gpt-6-luna", "Astuna"),
            ]
        );

        registry.IsKnown("fauna").Should().BeFalse();
        registry.TryGetDualLayerPreset("test", out _).Should().BeFalse();
        registry.Get("test")!.DisplayName.Should().Be("Test (Mock)", "the real provider keeps its entry");
        registry.TryGetDualLayerPreset("astuna", out _).Should().BeTrue();
    }

    /// <summary>Same scope as ProviderRegistryTests': restores the variable so tests do not leak into each other.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        private EnvScope(string name, string? previous)
        {
            _name = name;
            _previous = previous;
        }

        public static EnvScope Set(string name, string? value)
        {
            var previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return new EnvScope(name, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
