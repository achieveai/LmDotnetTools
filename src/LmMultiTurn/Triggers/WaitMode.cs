namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// How a <c>Wait</c> delivers its result. <see cref="Block"/> parks the tool call and resolves it
/// once (the merged #107 behavior). <see cref="Notify"/> arms without parking: each fire injects a
/// <c>&lt;trigger&gt;</c> envelope as a fresh turn and the wait stays armed for more fires.
/// </summary>
public enum WaitMode
{
    Block = 0,
    Notify = 1,
}
