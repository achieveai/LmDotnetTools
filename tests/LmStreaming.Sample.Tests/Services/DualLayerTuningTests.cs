using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// An eval variant is a host started with these settings. A variant that silently ran the built-in
/// prompts would be measured as something it was not, so a named file must be used or stop startup.
/// </summary>
public sealed class DualLayerTuningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dual-layer-tuning-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void NoSection_KeepsTheBuiltInTextAndGivesTheExecutorNoReferenceCopy()
    {
        var tuning = DualLayerTuning.Load(Config(), _root, NullLogger.Instance);

        tuning.Should().Be(new DualLayerTuning(null, null, null, ShareReferenceContext: false));
        DualLayerPrompts
            .ComposeExecutorSystemPrompt("host", tuning.PairInstructions, tuning.ExecutorInstructions)
            .Should()
            .Be(DualLayerPrompts.ComposeExecutorSystemPrompt("host"));
    }

    [Fact]
    public void ARelativeFile_ResolvesFromTheContentRoot_AndReplacesThatTextOnly()
    {
        File.WriteAllText(Path.Combine(_root, "planner.md"), "## Plan in briefs");

        var tuning = DualLayerTuning.Load(
            Config(("DualLayer:PlannerInstructionsFile", "planner.md"), ("DualLayer:ShareReferenceContext", "true")),
            _root,
            NullLogger.Instance
        );

        var prompt = DualLayerPrompts.ComposePlannerSystemPrompt(
            "host",
            tuning.PairInstructions,
            tuning.PlannerInstructions
        );
        prompt.Should().Contain("## Plan in briefs").And.Contain(DualLayerPrompts.PairInstructions);
        prompt.Should().NotContain(DualLayerPrompts.PlannerInstructions);
        tuning.ShareReferenceContext.Should().BeTrue();
    }

    [Theory]
    [InlineData("missing.md", "cannot be read")]
    [InlineData("empty.md", "is empty")]
    public void AFileThatCannotBeUsed_StopsStartup(string file, string reason)
    {
        File.WriteAllText(Path.Combine(_root, "empty.md"), "  \n");

        var act = () =>
            DualLayerTuning.Load(Config(("DualLayer:ExecutorInstructionsFile", file)), _root, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*ExecutorInstructionsFile*{reason}*");
    }
}
