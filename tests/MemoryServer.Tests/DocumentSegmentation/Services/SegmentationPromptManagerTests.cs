using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using FluentAssertions;
using System.IO;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
/// Unit tests for SegmentationPromptManager service.
/// </summary>
public class SegmentationPromptManagerTests : IDisposable
{
    private readonly ILogger<SegmentationPromptManager> _logger;
    private readonly DocumentSegmentationOptions _options;
    private readonly SegmentationPromptManager _promptManager;
    private readonly string _testPromptsPath;

    public SegmentationPromptManagerTests()
    {
        _logger = new LoggerFactory().CreateLogger<SegmentationPromptManager>();

        // Create a temporary test prompts file
        _testPromptsPath = Path.Combine(Path.GetTempPath(), $"test_prompts_{Guid.NewGuid()}.yml");
        CreateTestPromptsFile();

        _options = new DocumentSegmentationOptions
        {
            Prompts = new PromptOptions
            {
                FilePath = _testPromptsPath,
                DefaultLanguage = "en",
                EnableHotReload = false,
                CacheExpiration = TimeSpan.FromMinutes(30)
            }
        };

        var optionsWrapper = Options.Create(_options);
        _promptManager = new SegmentationPromptManager(_logger, optionsWrapper);
    }

    [Fact]
    public async Task GetPromptAsync_WithValidStrategy_ReturnsPromptTemplate()
    {
        // Act
        var result = await _promptManager.GetPromptAsync(SegmentationStrategy.TopicBased);

        // Assert
        result.Should().NotBeNull();
        result.SystemPrompt.Should().NotBeEmpty();
        result.UserPrompt.Should().NotBeEmpty();
        result.ExpectedFormat.Should().Be("json");
        result.MaxTokens.Should().BeGreaterThan(0);
        result.Temperature.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public async Task GetPromptAsync_WithInvalidStrategy_ReturnsFallbackPrompt()
    {
        // Act
        var result = await _promptManager.GetPromptAsync((SegmentationStrategy)999); // Invalid strategy

        // Assert
        result.Should().NotBeNull();
        result.SystemPrompt.Should().NotBeEmpty();
        result.UserPrompt.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetQualityValidationPromptAsync_ReturnsValidPrompt()
    {
        // Act
        var result = await _promptManager.GetQualityValidationPromptAsync();

        // Assert
        result.Should().NotBeNull();
        result.SystemPrompt.Should().NotBeEmpty();
        result.UserPrompt.Should().NotBeEmpty();
        result.ExpectedFormat.Should().Be("json");
    }

    [Theory]
    [InlineData(DocumentType.ResearchPaper)]
    [InlineData(DocumentType.Legal)]
    [InlineData(DocumentType.Technical)]
    [InlineData(DocumentType.Email)]
    [InlineData(DocumentType.Chat)]
    public async Task GetDomainInstructionsAsync_WithValidDocumentType_ReturnsInstructions(
      DocumentType documentType)
    {
        // Act
        var result = await _promptManager.GetDomainInstructionsAsync(documentType);

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ValidatePromptConfigurationAsync_WithValidConfig_ReturnsTrue()
    {
        // Act
        var result = await _promptManager.ValidatePromptConfigurationAsync();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ReloadPromptsAsync_ReturnsTrue()
    {
        // Act
        var result = await _promptManager.ReloadPromptsAsync();

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(SegmentationStrategy.TopicBased)]
    [InlineData(SegmentationStrategy.StructureBased)]
    [InlineData(SegmentationStrategy.NarrativeBased)]
    [InlineData(SegmentationStrategy.Hybrid)]
    public async Task GetPromptAsync_WithAllMainStrategies_ReturnsValidPrompts(
      SegmentationStrategy strategy)
    {
        // Act
        var result = await _promptManager.GetPromptAsync(strategy);

        // Assert
        result.Should().NotBeNull();
        result.SystemPrompt.Should().NotBeEmpty();
        result.UserPrompt.Should().NotBeEmpty();
        result.UserPrompt.Should().Contain("{DocumentContent}"); // Should have placeholder
        result.UserPrompt.Should().Contain("{DocumentType}"); // Should have placeholder
    }

    private void CreateTestPromptsFile()
    {
        var testYamlContent = @"
# Test prompts configuration
topic_based:
  system_prompt: |
    You are a document segmentation expert specializing in topic-based analysis.
  user_prompt: |
    Document Type: {DocumentType}
    Document Content: {DocumentContent}
    Analyze this document and identify topic boundaries.
  expected_format: json
  max_tokens: 1200
  temperature: 0.1

structure_based:
  system_prompt: |
    You are a document segmentation expert specializing in structure-based analysis.
  user_prompt: |
    Document Type: {DocumentType}
    Document Content: {DocumentContent}
    Analyze this document and identify structural boundaries.
  expected_format: json
  max_tokens: 1200
  temperature: 0.1

narrative_based:
  system_prompt: |
    You are a document segmentation expert specializing in narrative flow analysis.
  user_prompt: |
    Document Type: {DocumentType}
    Document Content: {DocumentContent}
    Analyze this document and identify narrative boundaries.
  expected_format: json
  max_tokens: 1200
  temperature: 0.1

hybrid:
  system_prompt: |
    You are a comprehensive document analysis expert using hybrid approach.
  user_prompt: |
    Document Type: {DocumentType}
    Document Content: {DocumentContent}
    Apply hybrid segmentation approach.
  expected_format: json
  max_tokens: 1500
  temperature: 0.15

quality_validation:
  system_prompt: |
    You are a quality assessment expert for document segmentation.
  user_prompt: |
    Evaluate the quality of this segmentation.
  expected_format: json
  max_tokens: 1000
  temperature: 0.1

domain_instructions:
  research_paper: |
    Focus on academic structure with clear methodology sections.
  legal: |
    Preserve legal context and complete legal thoughts.
  technical: |
    Group technical procedures and maintain context.
  email: |
    Maintain conversation context and reply chains.
  chat: |
    Group related conversation topics together.
";

        File.WriteAllText(_testPromptsPath, testYamlContent);
    }

    public void Dispose()
    {
        if (File.Exists(_testPromptsPath))
        {
            File.Delete(_testPromptsPath);
        }
    }
}
