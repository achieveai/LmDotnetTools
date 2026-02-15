using Microsoft.AspNetCore.Http;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.DynamicDescriptions;

/// <summary>
/// Resolves the context key for dynamic description lookup.
/// The context key is used by <see cref="IToolDescriptionProvider"/> implementations
/// to determine which description variant to return.
/// </summary>
public interface IToolDescriptionContextResolver
{
    /// <summary>
    /// The name of the HTTP header used to extract the context key.
    /// </summary>
    string HeaderName { get; }

    /// <summary>
    /// Extracts the context key from the current request.
    /// </summary>
    /// <returns>Context key (e.g., "NeetPG", "MDS") or null if not available</returns>
    string? GetContextKey();
}

/// <summary>
/// Default implementation that extracts the context key from an HTTP header.
/// Uses X-Exam-Type header by default.
/// </summary>
public class HttpHeaderContextResolver : IToolDescriptionContextResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Default header name for exam type selection.
    /// </summary>
    public const string DefaultHeaderName = "X-Exam-Type";

    /// <inheritdoc />
    public string HeaderName { get; }

    /// <summary>
    /// Initializes a new instance with the default header name (X-Exam-Type).
    /// </summary>
    /// <param name="httpContextAccessor">The HTTP context accessor.</param>
    public HttpHeaderContextResolver(IHttpContextAccessor httpContextAccessor)
        : this(httpContextAccessor, DefaultHeaderName)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom header name.
    /// </summary>
    /// <param name="httpContextAccessor">The HTTP context accessor.</param>
    /// <param name="headerName">The HTTP header name to use for context extraction.</param>
    public HttpHeaderContextResolver(IHttpContextAccessor httpContextAccessor, string headerName)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        HeaderName = headerName ?? throw new ArgumentNullException(nameof(headerName));
    }

    /// <inheritdoc />
    public string? GetContextKey()
    {
        return _httpContextAccessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();
    }
}
