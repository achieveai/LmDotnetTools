using System.Text;
using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class DaemonProfileConfigurationTests
{
    [Fact]
    public void Mcqdb_listener_and_auth_webhook_use_the_same_origin()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateProfile("appsettings.mcqdb.json")));
        var root = document.RootElement;

        var listener = new Uri(root.GetProperty("Urls").GetString()!);
        var webhook = new Uri(
            root.GetProperty("Auth").GetProperty("Webhook").GetProperty("PublicBaseUrl").GetString()!
        );

        listener
            .GetLeftPart(UriPartial.Authority)
            .Should()
            .Be(
                webhook.GetLeftPart(UriPartial.Authority),
                "sandbox auth callbacks must return to the same daemon that minted each per-session secret"
            );
        listener.Port.Should().Be(5082, "the GitHub daemon owns 5081 and cannot validate MCQdb session secrets");
    }

    [Theory]
    [InlineData("appsettings.achieveai.json")]
    [InlineData("appsettings.mcqdb.json")]
    public void The_shipped_review_profiles_default_the_primary_review_conversation_to_sol(string profileFileName)
    {
        // The Luna-only-child-routing fast PR: the primary review conversation (ReviewModelId, the
        // orchestrator's model, and its S2S fallback provider id LmStreamingProviderId) must start on
        // gpt-5.6-sol in both shipped tenant profiles. Bound through CodeReviewDaemonOptions rather than
        // read as raw JSON so a misspelled key (which binds nothing and silently keeps the class default)
        // fails this test instead of passing it.
        var options = BindDaemonOptions(profileFileName);

        options.ReviewModelId.Should().Be("gpt-5.6-sol", "the primary orchestrator loop must run on Sol");
        options
            .LmStreamingProviderId.Should()
            .Be(
                "gpt-5.6-sol",
                "S2SReviewAgentLoopFactory warns at boot when ReviewModelId and LmStreamingProviderId "
                    + "disagree, since both mean 'the review model' — they must stay in lockstep"
            );
    }

    [Theory]
    [InlineData("appsettings.achieveai.json")]
    [InlineData("appsettings.mcqdb.json")]
    public void The_shipped_review_profiles_leave_child_model_selection_to_the_mode_policy(string profileFileName)
    {
        var options = BindDaemonOptions(profileFileName);

        options.SubAgentModelId.Should().BeEmpty("the code-review mode now routes each canonical subagent_type");
        options.ReviewReasoningEffort.Should().Be("xhigh");
        options.ToolAssistedReasoningEffort.Should().Be("xhigh");
    }

    /// <summary>
    /// Binds a shipped profile's <c>CodeReviewDaemon</c> section through the same
    /// <see cref="ConfigurationBuilder"/> + typed <see cref="CodeReviewDaemonOptions"/> path the daemon
    /// itself uses, rather than reading the raw JSON — a renamed/misspelled key binds nothing and silently
    /// keeps the class default, which a raw-JSON read would not catch.
    /// </summary>
    private static CodeReviewDaemonOptions BindDaemonOptions(string profileFileName)
    {
        var options = new ConfigurationBuilder()
            .AddJsonFile(LocateProfile(profileFileName))
            .Build()
            .GetSection(CodeReviewDaemonOptions.SectionName)
            .Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull($"{profileFileName} must bind a {nameof(CodeReviewDaemonOptions)} section");
        return options!;
    }

    [Fact]
    public void The_achieveai_profile_enables_polling_of_the_ClaudePlugins_repo()
    {
        // #653: the cross-owner PrPollTargetBuilder test proves the builder can turn a
        // "gautam-achieveai/ClaudePlugins" entry into a target — it never reads this file, so it stays green
        // even if the entry here is missing or renamed. This is the assertion that actually pins the shipped
        // config: without it, deleting the entry from appsettings.achieveai.json breaks nothing in the suite.
        using var document = JsonDocument.Parse(File.ReadAllText(LocateProfile("appsettings.achieveai.json")));
        var enabledRepos = document
            .RootElement.GetProperty("CodeReviewDaemon")
            .GetProperty("EnabledRepos")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        enabledRepos
            .Should()
            .Contain(
                "gautam-achieveai/ClaudePlugins",
                "the achieveai daemon must monitor pull requests on gautam-achieveai/ClaudePlugins per issue #653"
            );
        enabledRepos
            .Should()
            .Contain(
                ["achieveai/LmDotnetTools", "achieveai/SandboxedOstoolsMcpServer"],
                "issue #653 adds an entry rather than replacing the existing ones"
            );
    }

    [Fact]
    public void The_shipped_settings_state_the_stranded_run_knobs_at_their_defaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateProfile("appsettings.json")));
        var daemon = document.RootElement.GetProperty("CodeReviewDaemon");

        // #317's mechanism (StrandedRunReconciler, delivered by #274) has always been configurable and has
        // never been visible: no shipped settings file named these keys, so every deploy ran on class
        // defaults an operator had to read the source to discover. Stating them changes no behaviour and
        // makes the resume policy an operator can actually tune.
        daemon
            .GetProperty("StrandedRunGraceHours")
            .GetDouble()
            .Should()
            .Be(new CodeReviewDaemonOptions().StrandedRunGraceHours);
        daemon
            .GetProperty("StrandedRunScanLimit")
            .GetInt32()
            .Should()
            .Be(new CodeReviewDaemonOptions().StrandedRunScanLimit);
        daemon
            .GetProperty("StrandedRunMaxResumesPerSweep")
            .GetInt32()
            .Should()
            .Be(new CodeReviewDaemonOptions().StrandedRunMaxResumesPerSweep);
        daemon
            .GetProperty("StrandedRunRetryPendingGraceMinutes")
            .GetDouble()
            .Should()
            .Be(new CodeReviewDaemonOptions().StrandedRunRetryPendingGraceMinutes);
    }

    [Fact]
    public void The_stranded_run_keys_in_the_shipped_settings_actually_bind()
    {
        // Asserting the bound value equals the shipped one would pass over a MISSPELLED key: a key that binds
        // nothing leaves exactly the class default the file states. So each value is rewritten to a distinct
        // one first — a key whose spelling the binder does not read cannot carry the rewrite through.
        var json = File.ReadAllText(LocateProfile("appsettings.json"))
            .Replace("\"StrandedRunGraceHours\": 6", "\"StrandedRunGraceHours\": 11", StringComparison.Ordinal)
            .Replace("\"StrandedRunScanLimit\": 50", "\"StrandedRunScanLimit\": 77", StringComparison.Ordinal)
            .Replace(
                "\"StrandedRunMaxResumesPerSweep\": 2",
                "\"StrandedRunMaxResumesPerSweep\": 9",
                StringComparison.Ordinal
            )
            // The rewrite matches "\"Key\": value", not the bare key name, because the same name also appears
            // in the neighbouring "_comment_StrandedRunRetryPendingGraceMinutes" documentation key — a
            // name-only replace would rewrite the comment key and leave the real one untouched, which is the
            // very failure this test exists to catch, inverted.
            .Replace(
                "\"StrandedRunRetryPendingGraceMinutes\": 45",
                "\"StrandedRunRetryPendingGraceMinutes\": 23",
                StringComparison.Ordinal
            );
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var options = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build()
            .GetSection(CodeReviewDaemonOptions.SectionName)
            .Get<CodeReviewDaemonOptions>();

        options.Should().NotBeNull();
        options!.StrandedRunGraceHours.Should().Be(11);
        options.StrandedRunScanLimit.Should().Be(77);
        options.StrandedRunMaxResumesPerSweep.Should().Be(9);
        options.StrandedRunRetryPendingGraceMinutes.Should().Be(23);
    }

    /// <summary>
    /// Walks up from the test binary until it finds the daemon's profile, rather than counting a fixed
    /// number of <c>..</c> segments out of <see cref="AppContext.BaseDirectory"/>. The depth is not a
    /// constant: this repo's own convention redirects <c>BaseOutputPath</c> to <c>.logs/tb/bin/</c> to
    /// dodge locked DLLs, which adds two levels and made the fixed walk resolve to a path under the TEST
    /// project. That surfaced as a DirectoryNotFoundException, which reads like a missing profile — a
    /// config regression — when the profile is present and only the arithmetic was wrong.
    /// </summary>
    private static string LocateProfile(string fileName)
    {
        var relative = Path.Combine("samples", "CodeReviewDaemon.Sample", fileName);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {relative} in any ancestor of {AppContext.BaseDirectory}.",
            relative
        );
    }
}
