namespace TodoEval.Runner;

/// <summary>
/// Renders the eval task template (<c>task.md</c>, owned by the eval asset set) by substituting the
/// <c>{TOPIC}</c> placeholder. The placeholder contract is deliberately tiny — one token, exact,
/// case-sensitive — so the task file stays a plain markdown document the mode lane can edit freely.
/// </summary>
internal static class TaskTemplateRenderer
{
    private const string TopicPlaceholder = "{TOPIC}";

    /// <summary>
    /// Substitutes every occurrence of <c>{TOPIC}</c> with <paramref name="topic"/>. Throws when the
    /// template contains no placeholder: a task that ignores its topic would silently run the same
    /// conversation N times, which defeats the seed axis of the sweep.
    /// </summary>
    public static string Render(string template, string topic)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        if (!template.Contains(TopicPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The task template does not contain the '{TopicPlaceholder}' placeholder; every seed would run "
                    + "an identical conversation. Fix the template or point the runner at the right task file."
            );
        }

        return template.Replace(TopicPlaceholder, topic, StringComparison.Ordinal);
    }
}
