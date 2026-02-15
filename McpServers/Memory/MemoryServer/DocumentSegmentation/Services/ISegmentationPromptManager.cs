using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Service interface for managing YAML-based segmentation prompts with hot reload capability.
/// </summary>
public interface ISegmentationPromptManager
{
    /// <summary>
    ///     Gets a prompt template for a specific segmentation strategy.
    /// </summary>
    /// <param name="strategy">The segmentation strategy</param>
    /// <param name="language">Language code (default: "en")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Prompt template for the strategy</returns>
    Task<PromptTemplate> GetPromptAsync(
        SegmentationStrategy strategy,
        string language = "en",
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets a prompt template for quality validation.
    /// </summary>
    /// <param name="language">Language code (default: "en")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Quality validation prompt template</returns>
    Task<PromptTemplate> GetQualityValidationPromptAsync(
        string language = "en",
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets domain-specific instructions for document types.
    /// </summary>
    /// <param name="documentType">Type of document</param>
    /// <param name="language">Language code (default: "en")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Domain-specific instructions</returns>
    Task<string> GetDomainInstructionsAsync(
        DocumentType documentType,
        string language = "en",
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Reloads all prompts from the YAML configuration file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if reload was successful</returns>
    Task<bool> ReloadPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates that all required prompts are properly configured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if configuration is valid</returns>
    Task<bool> ValidatePromptConfigurationAsync(CancellationToken cancellationToken = default);
}
