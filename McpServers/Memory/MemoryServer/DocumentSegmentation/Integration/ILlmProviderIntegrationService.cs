using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Integration;

/// <summary>
/// Interface for LLM provider integration services.
/// Provides access to LLM capabilities for document segmentation analysis.
/// </summary>
public interface ILlmProviderIntegrationService
{
  /// <summary>
  /// Analyzes document to determine optimal segmentation strategy using intelligent analysis and LLM enhancement.
  /// </summary>
  /// <param name="content">The document content to analyze</param>
  /// <param name="documentType">Type of document for context</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Strategy recommendation with confidence score</returns>
  Task<StrategyRecommendation> AnalyzeOptimalStrategyAsync(
    string content,
    DocumentType documentType,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Tests connectivity to LLM providers.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if connectivity is successful</returns>
  Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Generates topic-based segmentation using an LLM and returns the raw JSON response.
  /// A default implementation is provided so existing implementers remain compatible.
  /// </summary>
  /// <param name="content">Document content to segment</param>
  /// <param name="documentType">Type of document (provides additional context)</param>
  /// <param name="maxSegments">Optional maximum number of segments desired</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Raw JSON returned by the LLM (empty string if not available)</returns>
  public virtual Task<string> GenerateTopicSegmentationJsonAsync(
    string content,
    DocumentType documentType,
    int? maxSegments = null,
    CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
}
