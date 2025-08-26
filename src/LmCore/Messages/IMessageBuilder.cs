namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface IMessageBuilder
{
    public IMessage Build();
}

public interface IMessageBuilder<T, U> : IMessageBuilder
    where T : IMessage
    where U : IMessage
{
    public string? FromAgent { get; }

    public Role Role { get; }

    public void Add(U streamingMessageUpdate);

    public new T Build();
}
