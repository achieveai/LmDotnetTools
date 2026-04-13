using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;

namespace MemoryServer.DocumentSegmentation.Integration;

/// <summary>
///     Service that integrates with LmConfig to provide LLM-powered document segmentation.
///     Supports multiple providers (OpenAI, Anthropic) with automatic failover.
/// </summary>
public class LlmProviderIntegrationService : ILlmProviderIntegrationService
{
    private readonly IProviderAgentFactory _agentFactory;
    private readonly LlmProviderConfiguration _configuration;
    private readonly IDocumentAnalysisService _documentAnalysisService;
    private readonly ILogger<LlmProviderIntegrationService> _logger;
    private readonly IModelResolver _modelResolver;
    private readonly ISegmentationPromptManager _promptManager;

    public LlmProviderIntegrationService(
        IProviderAgentFactory agentFactory,
        IModelResolver modelResolver,
        ISegmentationPromptManager promptManager,
        IDocumentAnalysisService documentAnalysisService,
        ILogger<LlmProviderIntegrationService> logger,
        LlmProviderConfiguration configuration
    )
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _modelResolver = modelResolver ?? throw new ArgumentNullException(nameof(modelResolver));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _documentAnalysisService =
            documentAnalysisService ?? throw new ArgumentNullException(nameof(documentAnalysisService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    ///     Analyzes document to determine optimal segmentation strategy using intelligent analysis and LLM enhancement.
    /// </summary>
    public async Task<StrategyRecommendation> AnalyzeOptimalStrategyAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        _logger.LogDebug(
            "Starting strategy analysis for document type {DocumentType}, content length: {Length}",
            documentType,
            content.Length
        );

        try
        {
            // Use document analysis service for intelligent analysis
            var analysisResult = await _documentAnalysisService.AnalyzeOptimalStrategyAsync(
                content,
                documentType,
                cancellationToken
            );

            // If confidence is high enough, return the analysis result
            if (analysisResult.Confidence >= 0.7)
            {
                _logger.LogInformation(
                    "High-confidence strategy recommendation from analysis service: {Strategy} ({Confidence:F2})",
                    analysisResult.Strategy,
                    analysisResult.Confidence
                );
                return analysisResult;
            }

            // For lower confidence or complex cases, enhance with LLM analysis
            _logger.LogDebug(
                "Analysis service confidence {Confidence:F2} below threshold, enhancing with LLM",
                analysisResult.Confidence
            );

            var llmEnhancedResult = await EnhanceStrategyWithLlmAsync(
                content,
                documentType,
                analysisResult,
                cancellationToken
            );

            return llmEnhancedResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during strategy analysis, falling back to default");
            return CreateDefaultStrategyRecommendation(documentType);
        }
    }

    /// <summary>
    ///     Tests connectivity to LLM providers.
    /// </summary>
    public async Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Testing LLM provider connectivity");

        try
        {
            var resolution = await _modelResolver.ResolveProviderAsync(
                _configuration.ModelPreferences["strategy_analysis"],
                new ProviderSelectionCriteria(),
                cancellationToken
            );

            if (resolution == null)
            {
                _logger.LogError("Could not resolve provider for strategy analysis");
                return false;
            }

            var agent = _agentFactory.CreateAgent(resolution);

            // Test with a simple prompt
            var testMessages = new List<IMessage>
            {
                new TextMessage { Text = "Test connectivity. Respond with 'OK'.", Role = Role.User },
            };

            var response = await agent.GenerateReplyAsync(
                testMessages,
                new GenerateReplyOptions { ModelId = _configuration.ModelPreferences["strategy_analysis"] },
                cancellationToken
            );

            _logger.LogDebug("LLM connectivity test successful");
            return response?.Any() == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM connectivity test failed");
            return false;
        }
    }

    #region Private Helper Methods

    private async Task<StrategyRecommendation> EnhanceStrategyWithLlmAsync(
        string content,
        DocumentType documentType,
        StrategyRecommendation initialAnalysis,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Get strategy analysis prompt (using a special key for strategy analysis)
            var prompt = await _promptManager.GetPromptAsync(SegmentationStrategy.Hybrid, "en", cancellationToken);

            if (prompt == null)
            {
                _logger.LogWarning("Strategy analysis prompt not found, using analysis service result");
                return initialAnalysis;
            }

            // Create LLM request
            var messages = new List<IMessage>
            {
                new TextMessage { Text = prompt.SystemPrompt, Role = Role.System },
                new TextMessage
                {
                    Text = FormatStrategyAnalysisPrompt(content, documentType, initialAnalysis, prompt.UserPrompt),
                    Role = Role.User,
                },
            };

            var response = await ExecuteWithFailoverAsync(
                _configuration.ModelPreferences["strategy_analysis"],
                messages,
                cancellationToken
            );

            return ParseStrategyAnalysisResponse(response, initialAnalysis);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LLM strategy enhancement, using analysis service result");
            return initialAnalysis;
        }
    }

    private async Task<IMessage> ExecuteWithFailoverAsync(
        string modelName,
        List<IMessage> messages,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < _configuration.MaxRetries; attempt++)
        {
            try
            {
                var resolution = await _modelResolver.ResolveProviderAsync(
                    modelName,
                    new ProviderSelectionCriteria(),
                    cancellationToken
                );

                if (resolution == null)
                {
                    _logger.LogWarning(
                        "Could not resolve provider for model {ModelName} on attempt {Attempt}",
                        modelName,
                        attempt + 1
                    );
                    continue;
                }

                var agent = _agentFactory.CreateAgent(resolution);
                var response = await agent.GenerateReplyAsync(
                    messages,
                    new GenerateReplyOptions { ModelId = modelName },
                    cancellationToken
                );

                if (response?.Any() == true)
                {
                    return response.First();
                }

                _logger.LogWarning("Empty response on attempt {Attempt}", attempt + 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attempt {Attempt} failed", attempt + 1);

                if (attempt == _configuration.MaxRetries - 1)
                {
                    throw;
                }

                // Wait before retry
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 1000), cancellationToken);
            }
        }

        throw new InvalidOperationException("All attempts failed");
    }

    private StrategyRecommendation ParseStrategyAnalysisResponse(IMessage response, StrategyRecommendation fallback)
    {
        try
        {
            // Try to parse structured JSON response
            var content = ExtractTextFromMessage(response);
            if (content.Contains('{') && content.Contains('}'))
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);

                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                // Extract strategy and confidence if available
                if (
                    root.TryGetProperty("recommended_strategy", out var strategyElement)
                    && root.TryGetProperty("confidence", out var confidenceElement)
                )
                {
                    var strategyString = strategyElement.GetString();
                    var confidence = confidenceElement.GetDouble();

                    if (Enum.TryParse<SegmentationStrategy>(strategyString, out var strategy))
                    {
                        var reasoning = root.TryGetProperty("reasoning", out var reasoningElement)
                            ? reasoningElement.GetString() ?? fallback.Reasoning
                            : fallback.Reasoning;

                        _logger.LogInformation(
                            "Successfully parsed LLM strategy analysis response: {Strategy} with confidence {Confidence}",
                            strategy,
                            confidence
                        );

                        return new StrategyRecommendation
                        {
                            Strategy = strategy,
                            Confidence = confidence,
                            Reasoning = reasoning,
                            Alternatives = fallback.Alternatives,
                        };
                    }
                }
            }

            // If parsing fails or no structured response, enhance the fallback
            var enhancedFallback = new StrategyRecommendation
            {
                Strategy = fallback.Strategy,
                Confidence = Math.Min(fallback.Confidence + 0.1, 1.0),
                Reasoning =
                    $"{fallback.Reasoning} Enhanced with LLM analysis: {content[..Math.Min(200, content.Length)]}...",
                Alternatives = fallback.Alternatives,
            };

            return enhancedFallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing strategy analysis response");
            return fallback;
        }
    }

    private static string ExtractTextFromMessage(IMessage message)
    {
        return message switch
        {
            TextMessage textMessage => textMessage.Text,
            ICanGetText textProvider => textProvider.GetText() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string FormatStrategyAnalysisPrompt(
        string content,
        DocumentType documentType,
        StrategyRecommendation initialAnalysis,
        string template
    )
    {
        return template
            .Replace(
                "{DocumentContent}",
                content.Length > 2000 ? string.Concat(content.AsSpan(0, 2000), "...") : content
            )
            .Replace("{DocumentType}", documentType.ToString())
            .Replace("{InitialStrategy}", initialAnalysis.Strategy.ToString())
            .Replace("{InitialConfidence}", initialAnalysis.Confidence.ToString("F2"))
            .Replace("{InitialReasoning}", initialAnalysis.Reasoning);
    }

    private static StrategyRecommendation CreateDefaultStrategyRecommendation(DocumentType documentType)
    {
        return new StrategyRecommendation
        {
            Strategy = documentType switch
            {
                DocumentType.ResearchPaper => SegmentationStrategy.StructureBased,
                DocumentType.Legal => SegmentationStrategy.StructureBased,
                DocumentType.Technical => SegmentationStrategy.Hybrid,
                DocumentType.Email => SegmentationStrategy.TopicBased,
                DocumentType.Chat => SegmentationStrategy.TopicBased,
                _ => SegmentationStrategy.Hybrid,
            },
            Confidence = 0.6,
            Reasoning = $"Default strategy for {documentType} documents",
            Alternatives = [SegmentationStrategy.Hybrid],
        };
    }

    #endregion
}
