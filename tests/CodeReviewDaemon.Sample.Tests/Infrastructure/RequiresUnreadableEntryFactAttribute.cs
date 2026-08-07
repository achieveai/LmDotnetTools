namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs an entry it is denied the attributes of. See
/// <see cref="UnreadableEntry"/> for what stops a machine from producing one, and
/// <see cref="WindowsOnlyFactAttribute"/> for why a body that cannot build its input is skipped out loud
/// rather than left to pass on a setup that silently degraded.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresUnreadableEntryFactAttribute : FactAttribute
{
    public RequiresUnreadableEntryFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!UnreadableEntry.Supported)
        {
            Skip = $"Needs a deny ACE the running process cannot read past: {because}";
        }
    }
}
