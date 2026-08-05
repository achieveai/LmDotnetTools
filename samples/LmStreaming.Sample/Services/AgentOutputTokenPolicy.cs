using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Configuration;

namespace LmStreaming.Sample.Services;

public sealed class AgentOutputTokenPolicy
{
    private readonly AgentOutputTokenOptions _options;

    public AgentOutputTokenPolicy(AgentOutputTokenOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public GenerateReplyOptions ApplyPrimary(
        GenerateReplyOptions options,
        bool useDelegatedFallback = false
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxToken is not null)
        {
            return options;
        }

        return options with
        {
            MaxToken = useDelegatedFallback ? _options.Delegated : _options.Primary,
        };
    }

    public GenerateReplyOptions ApplyDelegated(GenerateReplyOptions? options)
    {
        var effective = options ?? new GenerateReplyOptions();
        return effective.MaxToken is null
            ? effective with { MaxToken = _options.Delegated }
            : effective;
    }

    public SubAgentTemplate ApplyDelegated(SubAgentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template with { DefaultOptions = ApplyDelegated(template.DefaultOptions) };
    }

    public SubAgentOptions ApplyDelegated(SubAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            Templates = options.Templates.ToDictionary(
                pair => pair.Key,
                pair => ApplyDelegated(pair.Value),
                StringComparer.Ordinal),
        };
    }
}
