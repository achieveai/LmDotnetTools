namespace TodoEval.Runner;

/// <summary>
/// Renders the eval task template (<c>task.md</c>, owned by the eval asset set) by substituting its
/// per-repeat placeholders. The contract is deliberately tiny — exact, case-sensitive tokens — so the
/// task file stays a plain markdown document the mode lane can edit freely.
/// <para>
/// There are two, because the two suites vary a repeat differently: the todo-eval task swaps the
/// subject it plans (<c>{TOPIC}</c>), while a compaction-eval task keeps the work identical and swaps
/// only a code name (<c>{SEED}</c>, from the task's own <c>meta.json</c>) so the repeats are not
/// byte-identical. A template needs whichever one its suite supplies, not both.
/// </para>
/// </summary>
internal static class TaskTemplateRenderer
{
    private const string TopicPlaceholder = "{TOPIC}";
    private const string SeedPlaceholder = "{SEED}";

    /// <summary>
    /// Substitutes every <c>{TOPIC}</c> with <paramref name="topic"/> and, when
    /// <paramref name="seed"/> is supplied, every <c>{SEED}</c> with it. Throws when the template
    /// carries NEITHER placeholder: a task that varies with nothing would silently run the same
    /// conversation N times, which defeats the seed axis of the sweep.
    /// </summary>
    /// <param name="template">The task message as read from <c>task.md</c>.</param>
    /// <param name="topic">The topic for this repeat.</param>
    /// <param name="seed">The seed word for this repeat, or null when the task declares no seeds.</param>
    public static string Render(string template, string topic, string? seed = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var hasTopic = template.Contains(TopicPlaceholder, StringComparison.Ordinal);
        var hasSeed = template.Contains(SeedPlaceholder, StringComparison.Ordinal);
        if (!hasTopic && !hasSeed)
        {
            throw new InvalidOperationException(
                $"The task template contains neither the '{TopicPlaceholder}' nor the '{SeedPlaceholder}' "
                    + "placeholder; every seed would run an identical conversation. Fix the template or point "
                    + "the runner at the right task file."
            );
        }

        var rendered = template.Replace(TopicPlaceholder, topic, StringComparison.Ordinal);
        return seed is null ? rendered : rendered.Replace(SeedPlaceholder, seed, StringComparison.Ordinal);
    }
}
