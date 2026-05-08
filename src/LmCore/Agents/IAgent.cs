using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Agents;

public interface IAgent
{
    Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    );
}
