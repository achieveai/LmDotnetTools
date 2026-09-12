namespace LmStreaming.Sample.Configuration;

/// <summary>Host-only publication callback configuration. Never passed to a model or workspace.</summary>
public sealed class WorkflowPublicationOptions : WorkflowPublicationEndpoint
{
    public const string SectionName = "WorkflowPublication";
    public const string HttpClientName = "WorkflowPublication";
    public const string ModeId = "code-review-daemon";
    public Dictionary<string, WorkflowPublicationEndpoint> Callbacks { get; init; } = new(StringComparer.Ordinal);

    /// <summary>When a map exists, an unknown caller cannot fall through to another daemon.</summary>
    public WorkflowPublicationEndpoint? Resolve(string? appId) =>
        Callbacks.Count > 0
            ? appId is not null && Callbacks.TryGetValue(appId, out var endpoint) && endpoint.IsConfigured
                ? endpoint
                : null
            : IsConfigured
                ? this
                : null;

    public void Validate()
    {
        if (
            ((!string.IsNullOrWhiteSpace(CallbackUrl) || !string.IsNullOrWhiteSpace(SharedSecret)) && !IsConfigured)
            || Callbacks.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || !pair.Value.IsConfigured
            )
        )
            throw new InvalidOperationException(
                "WorkflowPublication callbacks require a valid CallbackUrl and SharedSecret for every configured AppId."
            );
    }
}

/// <summary>One trusted callback destination and its host-only credential.</summary>
public class WorkflowPublicationEndpoint
{
    public string? CallbackUrl { get; init; }
    public string? SharedSecret { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SharedSecret)
        && Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);
}
