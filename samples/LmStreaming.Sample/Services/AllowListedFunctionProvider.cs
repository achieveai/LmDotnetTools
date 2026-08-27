using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Wraps a function provider so only the tools a mode actually selected reach the model.
/// </summary>
/// <remarks>
///     <para>
///         Providers such as <c>WorkflowToolProvider</c> hand over a whole family at once, but the
///         Modes editor offers a checkbox per tool. Without this decorator the two disagree: ticking
///         one authoring tool would grant all seven, so the runtime surface would be a superset of
///         what the user chose. That mismatch is invisible in the editor, which is what makes it
///         worth a type rather than an inline <c>Where</c>.
///     </para>
///     <para>
///         Filtering can only ever REMOVE. A name in the allow-list that the wrapped provider does
///         not emit is simply absent — an allow-list cannot conjure a tool, and in particular cannot
///         widen a sub-agent surface whose shape was decided elsewhere.
///     </para>
/// </remarks>
public sealed class AllowListedFunctionProvider : IFunctionProvider
{
    private readonly IFunctionProvider _inner;
    private readonly IReadOnlySet<string> _allowed;

    /// <summary>Creates a filtered view of <paramref name="inner" />.</summary>
    /// <param name="inner">The provider whose tools are being narrowed.</param>
    /// <param name="allowed">The bare tool names to keep.</param>
    public AllowListedFunctionProvider(IFunctionProvider inner, IReadOnlySet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(allowed);
        _inner = inner;
        _allowed = allowed;
    }

    /// <summary>
    ///     Returns <paramref name="inner" /> unchanged when <paramref name="allowed" /> is null, so a
    ///     caller can wire "null means everything" without branching at every call site.
    /// </summary>
    public static IFunctionProvider Wrap(IFunctionProvider inner, IReadOnlySet<string>? allowed) =>
        allowed is null ? inner : new AllowListedFunctionProvider(inner, allowed);

    /// <inheritdoc />
    public string ProviderName => _inner.ProviderName;

    /// <inheritdoc />
    public int Priority => _inner.Priority;

    /// <inheritdoc />
    /// <remarks>
    ///     Deferred, not snapshotted: the wrapped provider may vary its output over the conversation's
    ///     life (the sub-agent provider withdraws its spawn tool at the delegation limit), and caching
    ///     the first answer here would freeze that.
    /// </remarks>
    public IEnumerable<FunctionDescriptor> GetFunctions() =>
        _inner.GetFunctions().Where(d => _allowed.Contains(d.Contract.Name));
}
