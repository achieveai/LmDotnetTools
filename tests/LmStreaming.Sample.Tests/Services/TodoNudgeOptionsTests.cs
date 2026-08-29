using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The config contract for #583 PR 6: everything but the assignment notice ships OFF, and a
///     missing/empty/malformed section reads as feature-off — never a throw. The binder is not used
///     precisely because these tests must hold for garbage input.
/// </summary>
public class TodoNudgeOptionsTests
{
    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] pairs)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build();
    }

    [Fact]
    public void Defaults_OnlyTheAssignmentNoticeIsOn()
    {
        var options = new TodoNudgeOptions();

        options.AssignmentNoticeEnabled.Should().BeTrue("N1 is the lead's dispatch flow and cannot loop");
        options.RunEndNudgeEnabled.Should().BeFalse();
        options.IdleTurnsNudgeEnabled.Should().BeFalse();
        options.BreakdownNudgeEnabled.Should().BeFalse();
        options.NudgeRootConversation.Should().BeFalse("auto-poking the user's main chat needs an explicit opt-in");
        options.AnyStallNudgeEnabled.Should().BeFalse("the event pump must not exist under shipped defaults");
        options.AnyNudgeEnabled.Should().BeTrue();
    }

    [Fact]
    public void NullConfiguration_ReadsAsDefaults()
    {
        TodoNudgeOptions.FromConfiguration(null).Should().Be(new TodoNudgeOptions());
    }

    [Fact]
    public void MissingSection_ReadsAsDefaults()
    {
        var configuration = BuildConfiguration(("Unrelated:Key", "value"));

        TodoNudgeOptions.FromConfiguration(configuration).Should().Be(new TodoNudgeOptions());
    }

    [Fact]
    public void EmptySection_ReadsAsDefaults()
    {
        // The empty-JSON-array binder trap: `"TodoNudges": []` produces a section that EXISTS but
        // holds nothing. Existence must not change a single default.
        var configuration = BuildConfiguration(("TodoNudges", null));

        TodoNudgeOptions.FromConfiguration(configuration).Should().Be(new TodoNudgeOptions());
    }

    [Fact]
    public void MalformedValues_ReadAsDefaults_NotAsThrows()
    {
        var configuration = BuildConfiguration(
            ("TodoNudges:AssignmentNoticeEnabled", "yes please"),
            ("TodoNudges:RunEndNudgeEnabled", "1"),
            ("TodoNudges:IdleTurnThreshold", "banana"),
            ("TodoNudges:BreakdownAfterMinutes", "-5")
        );

        var options = TodoNudgeOptions.FromConfiguration(configuration);

        options.Should().Be(new TodoNudgeOptions(), "malformed values must fall back, not disable N1 or go negative");
    }

    [Fact]
    public void ExplicitValues_AreHonoured()
    {
        var configuration = BuildConfiguration(
            ("TodoNudges:AssignmentNoticeEnabled", "false"),
            ("TodoNudges:RunEndNudgeEnabled", "true"),
            ("TodoNudges:IdleTurnsNudgeEnabled", "true"),
            ("TodoNudges:BreakdownNudgeEnabled", "true"),
            ("TodoNudges:NudgeRootConversation", "true"),
            ("TodoNudges:IdleTurnThreshold", "3"),
            ("TodoNudges:BreakdownAfterMinutes", "45")
        );

        var options = TodoNudgeOptions.FromConfiguration(configuration);

        options.AssignmentNoticeEnabled.Should().BeFalse();
        options.RunEndNudgeEnabled.Should().BeTrue();
        options.IdleTurnsNudgeEnabled.Should().BeTrue();
        options.BreakdownNudgeEnabled.Should().BeTrue();
        options.NudgeRootConversation.Should().BeTrue();
        options.IdleTurnThreshold.Should().Be(3);
        options.BreakdownAfterMinutes.Should().Be(45);
        options.AnyStallNudgeEnabled.Should().BeTrue();
        options.AnyNudgeEnabled.Should().BeTrue();
    }

    [Fact]
    public void EverythingOff_MeansTheServiceNeverNeedsToExist()
    {
        var options = new TodoNudgeOptions
        {
            AssignmentNoticeEnabled = false,
            RunEndNudgeEnabled = false,
            IdleTurnsNudgeEnabled = false,
            BreakdownNudgeEnabled = false,
        };

        options.AnyNudgeEnabled.Should().BeFalse();
        options.AnyStallNudgeEnabled.Should().BeFalse();
    }
}
