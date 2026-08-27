using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Reads the two eval-sweep knobs and decides whether the sweep is registered at all.
/// <para>
/// A named type rather than four lines at the composition root, because those four lines are a
/// decision with three outcomes — off, on, or a configuration the daemon must refuse to start under —
/// and only the first two were reachable from a test. <c>Program.cs</c> is a top-level program; the
/// refusal it holds is exactly the kind that never runs until a real deployment types a real typo.
/// </para>
/// </summary>
internal static class EvalSweepConfiguration
{
    /// <summary>
    /// The sweep's cadence and window, or <b>null</b> when the sweep is switched off.
    /// </summary>
    /// <param name="options">The bound daemon options.</param>
    /// <returns>
    /// Null when <see cref="CodeReviewDaemonOptions.EvalCorpusSweepIntervalMinutes"/> is zero — the
    /// default, and the only value that means "off". Cadence and enablement are one knob on purpose,
    /// so there is no "enabled with no cadence" state to get wrong.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A configured value cannot mean what it says: an interval that is negative or not a finite
    /// number, or a non-positive window alongside a configured interval. Both name the section and
    /// key, because the operator's next action is to edit that line.
    /// </exception>
    public static (TimeSpan Interval, int Window)? Resolve(CodeReviewDaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var minutes = options.EvalCorpusSweepIntervalMinutes;

        // Refused BEFORE the off test, and refused rather than treated as off. The sibling window
        // knob already got a named startup refusal for a non-positive value; this one gated on
        // "> zero", so a typo'd -5 was indistinguishable from the default 0 and the daemon came up
        // clean with the sweep silently not running — while the operator's evidence that they had
        // turned it on was the line they had just edited.
        //
        // NaN is on the same line for a sharper reason than tidiness: every comparison against it is
        // false, so it walks past both a "< 0" guard and an "== 0" test and reaches
        // TimeSpan.FromMinutes, which throws about an argument no operator ever passed, from a stack
        // holding no configuration key. The infinities do the same one step later.
        //
        // The upper bound is on this line for EXACTLY that reason and no other: finiteness is not
        // representability. TimeSpan.FromMinutes overflows above TimeSpan.MaxValue.TotalMinutes
        // (~1.54e10), so a finite-but-enormous value reached the same nameless OverflowException the
        // NaN case exists to prevent. The comparison is strict, so the largest representable cadence
        // is still a cadence — absurd, but the operator asked for it, and a guard that refuses what
        // TimeSpan would have accepted is a second bound nobody wrote down.
        if (!double.IsFinite(minutes) || minutes < 0 || minutes > TimeSpan.MaxValue.TotalMinutes)
        {
            throw new InvalidOperationException(
                $"{CodeReviewDaemonOptions.SectionName}:{nameof(CodeReviewDaemonOptions.EvalCorpusSweepIntervalMinutes)} "
                + $"is {minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which is not a cadence. "
                + "Zero — the default — switches the sweep off; anything else must be a positive, finite number of "
                + "minutes no larger than "
                + $"{TimeSpan.MaxValue.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}. "
                + "It is refused rather than read as off, because a sweep nobody asked to disable would "
                + "then never run and never say so.");
        }

        if (minutes == 0)
        {
            return null;
        }

        if (options.EvalCorpusSweepWindow <= 0)
        {
            throw new InvalidOperationException(
                $"{CodeReviewDaemonOptions.SectionName}:{nameof(CodeReviewDaemonOptions.EvalCorpusSweepWindow)} "
                + "must be positive when a sweep interval is configured; a window of zero would make every "
                + "sweep report an empty corpus while the store fills up.");
        }

        return (TimeSpan.FromMinutes(minutes), options.EvalCorpusSweepWindow);
    }
}
