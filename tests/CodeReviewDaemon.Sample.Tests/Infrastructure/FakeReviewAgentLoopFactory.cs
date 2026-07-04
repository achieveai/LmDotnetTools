using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IReviewAgentLoopFactory"/>. Returns a scripted <see cref="FakeMultiTurnAgent"/>
/// per call so the executor's agent logic is verifiable without a live provider loop. The assistant
/// text is chosen per <see cref="AgentProfile.Id"/> (<see cref="TextByProfileId"/>), falling back to
/// <see cref="DefaultText"/>, which lets one test script a JSON judge verdict while the reviewer returns
/// prose. Every created profile id is recorded in <see cref="CreatedProfileIds"/>.
/// </summary>
internal sealed class FakeReviewAgentLoopFactory : IReviewAgentLoopFactory
{
    /// <summary>Assistant text to return for a given <see cref="AgentProfile.Id"/>.</summary>
    public Dictionary<string, string> TextByProfileId { get; } = new(StringComparer.Ordinal);

    /// <summary>Assistant text returned when no per-profile override is set.</summary>
    public string DefaultText { get; set; } = "## Review\nMust: null check missing in Foo.cs:10.";

    /// <summary>Profile ids passed to <see cref="Create"/>, in call order.</summary>
    public List<string> CreatedProfileIds { get; } = [];

    /// <summary>Thread ids passed to <see cref="Create"/>, in call order.</summary>
    public List<string> ThreadIds { get; } = [];

    /// <summary>Reasoning-effort values passed to <see cref="Create"/>, in call order (null = default).</summary>
    public List<string?> ReasoningEfforts { get; } = [];

    public IMultiTurnAgent Create(AgentProfile profile, string? modelId, string threadId, string? reasoningEffort = null)
    {
        CreatedProfileIds.Add(profile.Id);
        ThreadIds.Add(threadId);
        ReasoningEfforts.Add(reasoningEffort);

        var text = TextByProfileId.TryGetValue(profile.Id, out var scripted) ? scripted : DefaultText;
        var runId = $"run-{profile.Id}";
        return new FakeMultiTurnAgent(runId, new TextMessage { Text = text, Role = Role.Assistant, RunId = runId });
    }
}
