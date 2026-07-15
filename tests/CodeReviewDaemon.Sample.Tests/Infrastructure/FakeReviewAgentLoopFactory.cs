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

    /// <summary>The full <see cref="AgentProfile"/>s passed to <see cref="Create"/>, in call order — lets a
    /// test assert on the rendered <see cref="AgentProfile.SystemPrompt"/> the executor built (e.g. the
    /// templated workspace-layout paths), not just the profile id.</summary>
    public List<AgentProfile> CreatedProfiles { get; } = [];

    /// <summary>Thread ids passed to <see cref="Create"/>, in call order.</summary>
    public List<string> ThreadIds { get; } = [];

    /// <summary>Reasoning-effort values passed to <see cref="Create"/>, in call order (null = default).</summary>
    public List<string?> ReasoningEfforts { get; } = [];

    /// <summary>Tool contexts passed to <see cref="Create"/>, in call order (null = diff-only path).</summary>
    public List<ReviewToolContext?> ToolContexts { get; } = [];

    /// <summary>The scripted agents returned by <see cref="Create"/>, in call order, so a test can
    /// inspect the <see cref="FakeMultiTurnAgent.ReceivedInputs"/> the executor sent each one.</summary>
    public List<FakeMultiTurnAgent> CreatedAgents { get; } = [];

    /// <summary>When set, a tool-assisted <see cref="Create"/> (non-null <c>toolContext</c>) returns an agent
    /// that THROWS this exception instead of scripted text — models the model API rejecting the accumulated
    /// tool-assisted context (e.g. a context-window 400) so the executor's diff-only degrade is exercised.
    /// The diff-only path (null <c>toolContext</c>) still returns scripted text.</summary>
    public Exception? ThrowWhenToolAssisted { get; set; }

    /// <summary>When set, <see cref="ThrowWhenToolAssisted"/> fires ONLY for a tool-assisted <see cref="Create"/>
    /// whose <c>modelId</c> equals this value — models a smaller model overflowing while the escalation model
    /// (e.g. gpt-5.6-terra) succeeds. When null it fires for every tool-assisted Create regardless of model.</summary>
    public string? ThrowOnlyForModel { get; set; }

    /// <summary>Model ids passed to <see cref="Create"/>, in call order (null = the run's configured model).</summary>
    public List<string?> ModelIds { get; } = [];

    public IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null)
    {
        CreatedProfileIds.Add(profile.Id);
        CreatedProfiles.Add(profile);
        ThreadIds.Add(threadId);
        ReasoningEfforts.Add(reasoningEffort);
        ToolContexts.Add(toolContext);
        ModelIds.Add(modelId);

        if (toolContext is not null && ThrowWhenToolAssisted is not null
            && (ThrowOnlyForModel is null || string.Equals(modelId, ThrowOnlyForModel, StringComparison.Ordinal)))
        {
            var throwing = FakeMultiTurnAgent.Throwing($"run-{profile.Id}-overflow", ThrowWhenToolAssisted);
            CreatedAgents.Add(throwing);
            return throwing;
        }

        var text = TextByProfileId.TryGetValue(profile.Id, out var scripted) ? scripted : DefaultText;
        var runId = $"run-{profile.Id}";
        var agent = new FakeMultiTurnAgent(runId, new TextMessage { Text = text, Role = Role.Assistant, RunId = runId });
        CreatedAgents.Add(agent);
        return agent;
    }
}
