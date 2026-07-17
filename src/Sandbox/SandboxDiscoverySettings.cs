namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Tells the gateway where to deliver context-discovery events for a sandbox created with this
/// setting. The SDK only carries this value through to the gateway's <c>discovery</c> field — it
/// does not decide whether discovery is enabled or where the webhook lives; that decision belongs
/// to the application.
/// </summary>
public sealed class SandboxDiscoverySettings
{
    /// <summary>App webhook URL the gateway delivers discovery events to.</summary>
    public string WebhookUrl { get; }

    /// <summary>
    /// Gateway↔webhook shared secret the gateway presents when calling <see cref="WebhookUrl"/>.
    /// SECRET — never logged, never included in an exception message, and never rendered by
    /// <see cref="ToString"/>.
    /// </summary>
    public string WebhookAuth { get; }

    public SandboxDiscoverySettings(string webhookUrl, string webhookAuth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookAuth);
        WebhookUrl = webhookUrl;
        WebhookAuth = webhookAuth;
    }

    /// <summary>Redacted rendering — never prints <see cref="WebhookAuth"/>.</summary>
    public override string ToString() => $"SandboxDiscoverySettings {{ WebhookUrl = {WebhookUrl}, WebhookAuth = [REDACTED] }}";
}
