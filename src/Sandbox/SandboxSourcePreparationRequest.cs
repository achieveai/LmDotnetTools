namespace AchieveAi.LmDotnetTools.Sandbox;

// A class, not a record, by exception to .github/instructions/csharp-code-rules.instructions.md "Data objects": the
// Sandbox request and result types are sealed classes (SandboxCreateRequest, SandboxInfo, SandboxArchivePreparation),
// and this request defensively copies its lists in its constructor.
/// <summary>
/// The source selection <see cref="SandboxClient.PrepareSourcesAsync"/> prepares. Send the SAME
/// <see cref="Marketplaces"/>, <see cref="ExplicitMarketplaces"/> and <see cref="PluginSelection"/> to
/// <see cref="SandboxClient.CreateAsync"/> with the prepared <see cref="SandboxSourcePreparation.SourceVersion"/> as
/// <see cref="SandboxCreateRequest.ExpectedSourceVersion"/>. No workspace, environment or credential is sent here.
/// </summary>
public sealed class SandboxSourcePreparationRequest
{
    /// <summary>
    /// Marketplace aliases, defensively copied. As on <see cref="SandboxCreateRequest.Marketplaces"/>: empty omits the
    /// field (the gateway default set) unless <see cref="ExplicitMarketplaces"/> is set, which sends <c>[]</c>.
    /// </summary>
    public IReadOnlyList<string> Marketplaces { get; }

    /// <summary>Sends <see cref="Marketplaces"/> exactly, an empty list as an explicit <c>[]</c>.</summary>
    public bool ExplicitMarketplaces { get; init; }

    /// <summary>Tri-state plugin selection, as on <see cref="SandboxCreateRequest.PluginSelection"/>.</summary>
    public IReadOnlyList<SandboxPluginRef>? PluginSelection { get; }

    /// <summary>Fetch the selected Git sources before capturing them. Consumed here; never sent to create.</summary>
    public bool Refresh { get; init; }

    /// <summary>Refuse a capture that is not fresh. Send the same value on <see cref="SandboxCreateRequest.RequireFresh"/>.</summary>
    public bool RequireFresh { get; init; }

    /// <summary>
    /// The longest <see cref="SandboxClient.PrepareSourcesAsync"/> waits for a <c>preparing</c> operation, submission
    /// and polling together. Default 5 minutes. Each HTTP call is also bounded by the transport timeout.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = DefaultMaxWait;

    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    public SandboxSourcePreparationRequest(
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxPluginRef>? pluginSelection = null
    )
    {
        Marketplaces = marketplaces is null ? [] : [.. marketplaces];
        PluginSelection = pluginSelection is null ? null : [.. pluginSelection];
    }
}
