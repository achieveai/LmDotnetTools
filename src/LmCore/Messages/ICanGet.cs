using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface ICanGetText : IMessage
{
    public string? GetText();
}

public interface ICanGetBinary : IMessage
{
    public BinaryData? GetBinary();
}

public interface ICanGetToolCalls : IMessage
{
    public IEnumerable<ToolCall>? GetToolCalls();
}

public interface ICanGetMessages : IMessage
{
    public IEnumerable<IMessage>? GetMessages();
}

public interface ICanGetUsage : IMessage
{
    public Usage? GetUsage();
}
