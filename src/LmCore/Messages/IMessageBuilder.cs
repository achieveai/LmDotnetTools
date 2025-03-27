namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface IMessageBuilder<T, U> where T : IMessage where U: IMessage
{
    public string? FromAgent { get; }

    public Role Role { get; }

    public void Add(U streamingMessageUpdate);

    public T Build();
}