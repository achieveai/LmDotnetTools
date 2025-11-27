using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface ICanGetText : IMessage
{
    string? GetText();
}

public interface ICanGetBinary : IMessage
{
    BinaryData? GetBinary();
}

public interface ICanGetToolCalls : IMessage
{
    IEnumerable<ToolCall>? GetToolCalls();
}

public interface ICanGetMessages : IMessage
{
    IEnumerable<IMessage>? GetMessages();
}

public interface ICanGetUsage : IMessage
{
    Usage? GetUsage();
}
