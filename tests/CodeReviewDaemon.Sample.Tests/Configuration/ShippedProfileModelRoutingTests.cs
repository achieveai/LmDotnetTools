using CodeReviewDaemon.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Configuration;

public sealed class ShippedProfileModelRoutingTests
{
    public static TheoryData<string> ShippedProfiles =>
        [
            "appsettings.json",
            "appsettings.achieveai.json",
            "appsettings.astra.json",
            "appsettings.mcqdb.json",
            "appsettings.nova.json",
            "appsettings.s2s.json",
        ];

    [Theory]
    [MemberData(nameof(ShippedProfiles))]
    public void Every_shipped_profile_uses_the_stage_model_matrix(string profileName)
    {
        var profilePath = Path.Combine(AppContext.BaseDirectory, "ShippedProfiles", profileName);
        var configuration = new ConfigurationBuilder().AddJsonFile(profilePath, optional: false).Build();

        var options = CodeReviewDaemonOptions.Bind(configuration.GetSection(CodeReviewDaemonOptions.SectionName));

        options.EffectiveReviewModelId.Should().Be("gpt-5.6-terra");
        options.EffectiveSynthesisModelId.Should().Be("gpt-5.6-sol");
        options.EffectiveJudgeModelId.Should().Be("gpt-5.6-sol");
        options.EffectiveKnowledgeModelId.Should().Be("gpt-5.6-terra");
        options.SubAgentModelId.Trim().Should().Be("gpt-5.6-sol");
        options.OverflowEscalationModelId.Trim().Should().Be("gpt-5.6-terra");
        options.LmStreamingProviderId.Trim().Should().Be("gpt-5.6-terra");
    }
}
