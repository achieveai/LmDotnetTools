using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

public record struct MiddlewareContext(IEnumerable<IMessage> Messages, GenerateReplyOptions? Options = null);
