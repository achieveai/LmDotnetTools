namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface IMessageBuilder
{
    IMessage Build();
}

public interface IMessageBuilder<T, U> : IMessageBuilder
    where T : IMessage
    where U : IMessage
{
    string? FromAgent { get; }

    Role Role { get; }

    void Add(U streamingMessageUpdate);

    new T Build();
}
