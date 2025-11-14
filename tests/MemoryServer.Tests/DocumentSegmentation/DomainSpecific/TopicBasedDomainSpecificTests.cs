using FluentAssertions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace MemoryServer.DocumentSegmentation.Tests.DomainSpecific;

/// <summary>
/// Domain-specific tests for TopicBasedSegmentationService.
/// Tests specialized handling of Legal, Technical, Academic, and other document types.
/// </summary>
public class TopicBasedDomainSpecificTests
{
    private readonly Mock<ILlmProviderIntegrationService> _mockLlmService;
    private readonly Mock<ISegmentationPromptManager> _mockPromptManager;
    private readonly ILogger<TopicBasedSegmentationService> _logger;
    private readonly TopicBasedSegmentationService _service;
    private readonly ITestOutputHelper _output;

    public TopicBasedDomainSpecificTests(ITestOutputHelper output)
    {
        _output = output;
        _mockLlmService = new Mock<ILlmProviderIntegrationService>();
        _mockPromptManager = new Mock<ISegmentationPromptManager>();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Information);
        });
        _logger = loggerFactory.CreateLogger<TopicBasedSegmentationService>();

        _service = new TopicBasedSegmentationService(_mockLlmService.Object, _mockPromptManager.Object, _logger);

        SetupDefaultMocks();
    }

    #region Legal Document Topic Detection

    [Fact]
    public async Task SegmentByTopicsAsync_WithLegalDocument_RecognizesLegalStructure()
    {
        // Arrange
        var legalDocument = CreateLegalDocument();
        var options = new TopicSegmentationOptions { MinSegmentSize = 100, UseLlmEnhancement = false };

        // Act
        var result = await _service.SegmentByTopicsAsync(legalDocument, DocumentType.Legal, options);

        // Assert
        result.Should().NotBeEmpty();

        // Legal documents should recognize section structure
        result.Should().HaveCountGreaterThan(3); // Should identify multiple legal sections

        // Verify segments contain legal terminology
        var allContent = string.Join(" ", result.Select(s => s.Content));
        allContent.Should().ContainAny("whereas", "hereby", "agreement", "party", "clause", "provision");

        // Legal segments should have reasonable size for clauses/sections
        result.All(s => s.Content.Length >= 50).Should().BeTrue();

        // Verify legal-specific metadata
        foreach (var segment in result)
        {
            segment.Metadata.Should().ContainKey("document_type");
            segment.Metadata["document_type"].Should().Be(DocumentType.Legal.ToString());
        }

        _output.WriteLine($"Legal Document Segmentation:");
        _output.WriteLine($"  Segments created: {result.Count()}");
        _output.WriteLine($"  Average segment length: {result.Average(s => s.Content.Length):F0} characters");

        foreach (var (segment, index) in result.Select((s, i) => (s, i)))
        {
            _output.WriteLine(
                $"  Segment {index + 1}: {segment.Content.Substring(0, Math.Min(50, segment.Content.Length))}..."
            );
        }
    }

    [Fact]
    public async Task DetectTopicBoundariesAsync_WithLegalDocument_FindsClauseBoundaries()
    {
        // Arrange
        var legalDocument = CreateLegalDocument();

        // Act
        var boundaries = await _service.DetectTopicBoundariesAsync(legalDocument, DocumentType.Legal);

        // Assert
        boundaries.Should().NotBeEmpty();

        // Legal documents should identify boundaries at clause transitions
        boundaries.Should().HaveCountGreaterThan(2);

        // Boundaries should be positioned at legal section markers
        // Check that boundaries don't split clauses inappropriately
        foreach (var boundary in boundaries)
        {
            boundary.Position.Should().BeInRange(0, legalDocument.Length - 1);
            boundary.Confidence.Should().BeInRange(0.0, 1.0);
        }

        _output.WriteLine($"Legal Boundary Detection:");
        _output.WriteLine($"  Boundaries found: {boundaries.Count}");
        foreach (var boundary in boundaries)
        {
            _output.WriteLine($"    Position: {boundary.Position}, Confidence: {boundary.Confidence:F2}");
        }
    }

    [Fact]
    public async Task AnalyzeThematicCoherenceAsync_WithLegalClause_ValidatesLegalCoherence()
    {
        // Arrange
        var legalClause =
            @"
            WHEREAS, the parties desire to enter into this agreement for the mutual benefit 
            of both parties; and WHEREAS, each party has the authority to enter into this 
            agreement; NOW, THEREFORE, in consideration of the mutual covenants contained 
            herein, the parties agree as follows: The Provider shall deliver services 
            in accordance with the specifications set forth in Exhibit A.
        ";

        // Act
        var analysis = await _service.AnalyzeThematicCoherenceAsync(legalClause);

        // Assert
        analysis.Should().NotBeNull();
        analysis.CoherenceScore.Should().BeInRange(0.0, 1.0);

        // Legal text should have reasonable coherence despite formal structure
        analysis
            .CoherenceScore.Should()
            .BeGreaterThan(0.6, "Legal clauses should maintain thematic coherence despite formal language");

        _output.WriteLine($"Legal Coherence Analysis:");
        _output.WriteLine($"  Coherence Score: {analysis.CoherenceScore:F2}");
        _output.WriteLine($"  Primary Topic: {analysis.PrimaryTopic}");
    }

    #endregion

    #region Technical Documentation Analysis

    [Fact]
    public async Task SegmentByTopicsAsync_WithTechnicalDocument_RecognizesTechnicalStructure()
    {
        // Arrange
        var technicalDocument = CreateTechnicalDocument();
        var options = new TopicSegmentationOptions { MinSegmentSize = 150, UseLlmEnhancement = false };

        // Act
        var result = await _service.SegmentByTopicsAsync(technicalDocument, DocumentType.Technical, options);

        // Assert
        result.Should().NotBeEmpty();

        // Technical docs should identify API sections, code examples, etc.
        result.Should().HaveCountGreaterThan(4);

        // Verify segments contain technical terminology
        var allContent = string.Join(" ", result.Select(s => s.Content));
        allContent.Should().ContainAny("API", "function", "method", "parameter", "return", "example", "code");

        // Technical segments should handle code blocks appropriately
        var codeSegments = result.Where(s => s.Content.Contains("```") || s.Content.Contains("function")).ToList();
        codeSegments.Should().NotBeEmpty("Should identify code-containing segments");

        _output.WriteLine($"Technical Document Segmentation:");
        _output.WriteLine($"  Segments created: {result.Count()}");
        _output.WriteLine($"  Code segments: {codeSegments.Count}");
        _output.WriteLine($"  Average segment length: {result.Average(s => s.Content.Length):F0} characters");
    }

    [Fact]
    public async Task DetectTopicBoundariesAsync_WithTechnicalDocument_FindsAPISectionBoundaries()
    {
        // Arrange
        var technicalDocument = CreateTechnicalDocument();

        // Act
        var boundaries = await _service.DetectTopicBoundariesAsync(technicalDocument, DocumentType.Technical);

        // Assert
        boundaries.Should().NotBeEmpty();

        // Technical docs should identify boundaries between different API sections
        boundaries.Should().HaveCountGreaterThan(3);

        // Should recognize transitions between concepts (API -> Examples -> Parameters)
        foreach (var boundary in boundaries)
        {
            boundary.Confidence.Should().BeInRange(0.0, 1.0);
        }

        _output.WriteLine($"Technical Boundary Detection:");
        _output.WriteLine($"  Boundaries found: {boundaries.Count}");
    }

    [Fact]
    public async Task AnalyzeThematicCoherenceAsync_WithCodeExample_ValidatesTechnicalCoherence()
    {
        // Arrange
        var codeExample =
            @"
            The getUserData function retrieves user information from the database.
            
            ```javascript
            async function getUserData(userId) {
                const user = await database.users.findById(userId);
                return {
                    id: user.id,
                    name: user.name,
                    email: user.email
                };
            }
            ```
            
            This function takes a userId parameter and returns a user object with
            the essential user properties. Error handling should be implemented
            to manage cases where the user is not found.
        ";

        // Act
        var analysis = await _service.AnalyzeThematicCoherenceAsync(codeExample);

        // Assert
        analysis.Should().NotBeNull();

        // Technical content mixing text and code should maintain coherence
        analysis
            .CoherenceScore.Should()
            .BeGreaterThan(0.7, "Code examples with explanations should maintain thematic coherence");

        _output.WriteLine($"Technical Coherence Analysis:");
        _output.WriteLine($"  Coherence Score: {analysis.CoherenceScore:F2}");
    }

    #endregion

    #region Academic Paper Segmentation

    [Fact]
    public async Task SegmentByTopicsAsync_WithAcademicPaper_RecognizesAcademicStructure()
    {
        // Arrange
        var academicPaper = CreateAcademicPaper();
        var options = new TopicSegmentationOptions { MinSegmentSize = 200, UseLlmEnhancement = false };

        // Act
        var result = await _service.SegmentByTopicsAsync(academicPaper, DocumentType.ResearchPaper, options);

        // Assert
        result.Should().NotBeEmpty();

        // Academic papers should identify standard sections
        result.Should().HaveCountGreaterThan(4); // Abstract, Introduction, Methods, Results, Discussion, etc.

        // Verify academic terminology and structure
        var allContent = string.Join(" ", result.Select(s => s.Content));
        allContent
            .Should()
            .ContainAny("abstract", "methodology", "results", "discussion", "conclusion", "research", "study");

        // Academic segments should be substantial
        result
            .Average(s => s.Content.Length)
            .Should()
            .BeGreaterThan(300, "Academic sections should be substantial in length");

        _output.WriteLine($"Academic Paper Segmentation:");
        _output.WriteLine($"  Segments created: {result.Count()}");
        _output.WriteLine($"  Average segment length: {result.Average(s => s.Content.Length):F0} characters");
    }

    [Fact]
    public async Task DetectTopicBoundariesAsync_WithAcademicPaper_FindsStandardSections()
    {
        // Arrange
        var academicPaper = CreateAcademicPaper();

        // Act
        var boundaries = await _service.DetectTopicBoundariesAsync(academicPaper, DocumentType.ResearchPaper);

        // Assert
        boundaries.Should().NotBeEmpty();

        // Academic papers should identify clear section boundaries
        boundaries.Should().HaveCountGreaterThan(3);

        // Boundaries should align with academic paper structure
        foreach (var boundary in boundaries)
        {
            boundary.Confidence.Should().BeInRange(0.0, 1.0);
        }

        _output.WriteLine($"Academic Boundary Detection:");
        _output.WriteLine($"  Boundaries found: {boundaries.Count}");
    }

    [Fact]
    public async Task AnalyzeThematicCoherenceAsync_WithAcademicSection_ValidatesAcademicCoherence()
    {
        // Arrange
        var academicMethodology =
            @"
            This study employed a mixed-methods approach to investigate the relationship
            between user interface design and user engagement metrics. The quantitative
            component consisted of A/B testing with 500 participants randomly assigned
            to two interface conditions. The qualitative component included semi-structured
            interviews with 20 participants to gather detailed feedback about their
            user experience. Data analysis involved statistical testing using ANOVA
            for quantitative measures and thematic analysis for qualitative responses.
        ";

        // Act
        var analysis = await _service.AnalyzeThematicCoherenceAsync(academicMethodology);

        // Assert
        analysis.Should().NotBeNull();

        // Academic methodology should have high coherence
        analysis
            .CoherenceScore.Should()
            .BeGreaterThan(0.8, "Academic methodology sections should maintain high thematic coherence");

        _output.WriteLine($"Academic Coherence Analysis:");
        _output.WriteLine($"  Coherence Score: {analysis.CoherenceScore:F2}");
    }

    #endregion

    #region Domain-Specific Quality Metrics

    [Theory]
    [InlineData(DocumentType.Legal)]
    [InlineData(DocumentType.Technical)]
    [InlineData(DocumentType.ResearchPaper)]
    public async Task ValidateTopicSegmentsAsync_WithDomainSpecificDocuments_AppliesDomainMetrics(
        DocumentType documentType
    )
    {
        // Arrange
        var document = documentType switch
        {
            DocumentType.Legal => CreateLegalDocument(),
            DocumentType.Technical => CreateTechnicalDocument(),
            DocumentType.ResearchPaper => CreateAcademicPaper(),
            _ => CreateGenericDocument(),
        };

        var segments = await _service.SegmentByTopicsAsync(document, documentType);

        // Act
        var validation = await _service.ValidateTopicSegmentsAsync(segments, document);

        // Assert
        validation.Should().NotBeNull();
        validation.OverallQuality.Should().BeInRange(0.0, 1.0);
        validation.AverageTopicCoherence.Should().BeInRange(0.0, 1.0);

        // Domain-specific documents should have reasonable quality scores
        validation
            .OverallQuality.Should()
            .BeGreaterThan(0.6, $"{documentType} documents should maintain good overall quality");

        _output.WriteLine($"Domain-Specific Quality Validation ({documentType}):");
        _output.WriteLine($"  Overall Quality: {validation.OverallQuality:F2}");
        _output.WriteLine($"  Topic Coherence: {validation.AverageTopicCoherence:F2}");
        _output.WriteLine($"  Boundary Accuracy: {validation.BoundaryAccuracy:F2}");
        _output.WriteLine($"  Issues found: {validation.Issues.Count}");
    }

    [Fact]
    public async Task SegmentByTopicsAsync_WithMixedDomainDocument_HandlesGracefully()
    {
        // Arrange - Document mixing legal and technical content
        var mixedDocument =
            @"
            Software License Agreement
            
            WHEREAS, Licensor owns certain proprietary software technology; and
            WHEREAS, Licensee desires to use said technology;
            
            NOW, THEREFORE, the parties agree:
            
            1. Grant of License: Licensor grants Licensee a non-exclusive license to use the Software.
            
            Technical Requirements:
            
            The software requires Node.js version 14 or higher. Installation instructions:
            
            ```bash
            npm install software-package
            npm start
            ```
            
            API Usage:
            
            ```javascript
            const software = require('software-package');
            software.initialize(config);
            ```
            
            2. Restrictions: Licensee may not distribute, modify, or reverse engineer the Software.
        ";

        var options = new TopicSegmentationOptions { UseLlmEnhancement = false };

        // Act
        var result = await _service.SegmentByTopicsAsync(mixedDocument, DocumentType.Generic, options);

        // Assert
        result.Should().NotBeEmpty();

        // Should handle mixed content appropriately
        result.Should().HaveCountGreaterThan(2);

        // Should not fail on mixed domain content
        result.All(s => !string.IsNullOrWhiteSpace(s.Content)).Should().BeTrue();

        _output.WriteLine($"Mixed Domain Document Segmentation:");
        _output.WriteLine($"  Segments created: {result.Count()}");
    }

    #endregion

    #region Helper Methods

    private void SetupDefaultMocks()
    {
        _mockPromptManager
            .Setup(x =>
                x.GetPromptAsync(It.IsAny<SegmentationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new PromptTemplate
                {
                    SystemPrompt = "You are a domain-aware topic analysis expert.",
                    UserPrompt = "Analyze the following {DocumentType} content: {DocumentContent}",
                    ExpectedFormat = "json",
                }
            );

        _mockLlmService.Setup(x => x.TestConnectivityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private string CreateLegalDocument()
    {
        return @"
            SOFTWARE LICENSE AGREEMENT
            
            This Software License Agreement (""Agreement"") is entered into as of the date of download
            or installation of the software by and between Software Company, a Delaware corporation
            (""Licensor""), and the person or entity downloading or installing the software (""Licensee"").
            
            RECITALS
            
            WHEREAS, Licensor has developed and owns certain proprietary software technology,
            including but not limited to computer programs, documentation, and related materials
            (collectively, the ""Software"");
            
            WHEREAS, Licensee desires to obtain a license to use the Software for its intended
            business purposes; and
            
            WHEREAS, Licensor is willing to grant such license subject to the terms and conditions
            set forth herein;
            
            NOW, THEREFORE, in consideration of the mutual covenants and agreements contained
            herein, the parties agree as follows:
            
            1. GRANT OF LICENSE
            
            Subject to the terms and conditions of this Agreement, Licensor hereby grants to
            Licensee a non-exclusive, non-transferable license to use the Software solely for
            Licensee's internal business purposes.
            
            2. RESTRICTIONS
            
            Licensee shall not: (a) distribute, sell, lease, or sublicense the Software;
            (b) modify, adapt, or create derivative works based upon the Software;
            (c) reverse engineer, decompile, or disassemble the Software; or
            (d) remove any proprietary notices from the Software.
            
            3. INTELLECTUAL PROPERTY
            
            Licensor retains all right, title, and interest in and to the Software,
            including all intellectual property rights therein. This Agreement does not
            grant Licensee any ownership rights in the Software.
            
            4. TERMINATION
            
            This Agreement shall terminate automatically if Licensee breaches any provision hereof.
            Upon termination, Licensee shall cease all use of the Software and destroy all copies
            in its possession or control.
        ";
    }

    private string CreateTechnicalDocument()
    {
        return @"
            User Authentication API Documentation
            
            The User Authentication API provides secure methods for user login, logout,
            and session management. This API follows RESTful principles and returns
            JSON responses for all endpoints.
            
            Authentication Overview
            
            All API requests must include a valid authentication token in the Authorization
            header. Tokens are obtained through the login endpoint and expire after 24 hours.
            
            Base URL: https://api.example.com/v1/auth
            
            Login Endpoint
            
            POST /login
            
            Authenticates a user and returns an access token.
            
            Request Body:
            ```json
            {
                ""email"": ""user@example.com"",
                ""password"": ""securepassword""
            }
            ```
            
            Response:
            ```json
            {
                ""success"": true,
                ""token"": ""eyJhbGciOiJIUzI1NiIs..."",
                ""expires_in"": 86400,
                ""user"": {
                    ""id"": 123,
                    ""email"": ""user@example.com"",
                    ""name"": ""John Doe""
                }
            }
            ```
            
            Code Example:
            
            ```javascript
            async function loginUser(email, password) {
                const response = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ email, password })
                });
                
                if (!response.ok) {
                    throw new Error('Login failed');
                }
                
                const data = await response.json();
                localStorage.setItem('authToken', data.token);
                return data.user;
            }
            ```
            
            Logout Endpoint
            
            POST /logout
            
            Invalidates the current session token.
            
            Headers:
            - Authorization: Bearer {token}
            
            Response:
            ```json
            {
                ""success"": true,
                ""message"": ""Successfully logged out""
            }
            ```
            
            Error Handling
            
            The API returns standard HTTP status codes and error messages:
            
            - 400 Bad Request: Invalid request parameters
            - 401 Unauthorized: Invalid or missing authentication token
            - 429 Too Many Requests: Rate limit exceeded
            - 500 Internal Server Error: Server-side error
            
            Rate Limiting
            
            The API implements rate limiting to prevent abuse. Each IP address is limited
            to 100 requests per minute. Rate limit headers are included in all responses.
        ";
    }

    private string CreateAcademicPaper()
    {
        return @"
            The Impact of User Interface Design on User Engagement in Mobile Applications:
            A Mixed-Methods Study
            
            Abstract
            
            This study investigates the relationship between user interface (UI) design elements
            and user engagement metrics in mobile applications. Using a mixed-methods approach,
            we conducted A/B testing with 500 participants and qualitative interviews with 20 users.
            Results indicate that simplified navigation structures and consistent visual hierarchies
            significantly improve user engagement, measured by session duration and task completion
            rates. The findings contribute to evidence-based design principles for mobile UI development.
            
            Keywords: user interface design, mobile applications, user engagement, UX research
            
            1. Introduction
            
            User engagement in mobile applications has become a critical success factor in the
            competitive app marketplace. Prior research has established connections between
            specific UI design elements and user behavior (Smith et al., 2019; Johnson & Lee, 2020).
            However, limited studies have examined the combined effects of multiple design factors
            on engagement metrics using rigorous experimental methods.
            
            This study addresses the research question: How do specific UI design elements
            influence user engagement in mobile applications? We hypothesize that simplified
            navigation and consistent visual design will positively correlate with engagement metrics.
            
            2. Literature Review
            
            Previous studies have examined various aspects of mobile UI design. Chen et al. (2021)
            found that navigation complexity significantly impacts user task completion. Similarly,
            Williams and Davis (2020) demonstrated that visual consistency affects user satisfaction
            and retention rates. However, these studies typically examined individual design elements
            in isolation rather than their combined effects.
            
            User engagement has been conceptualized in multiple ways across HCI literature.
            For this study, we adopt the definition proposed by O'Brien and Toms (2018),
            focusing on behavioral indicators such as session duration, interaction frequency,
            and task completion rates.
            
            3. Methodology
            
            This study employed a mixed-methods design combining quantitative experimentation
            with qualitative user interviews. The quantitative component consisted of A/B testing
            comparing two mobile app interface designs across multiple engagement metrics.
            
            3.1 Participants
            
            Five hundred participants (ages 18-65, M=32.4, SD=12.1) were recruited through
            online platforms. Participants were randomly assigned to one of two interface conditions:
            simplified design (n=250) or complex design (n=250). Demographics were balanced
            across conditions.
            
            3.2 Experimental Design
            
            The simplified interface featured flat design elements, minimal navigation hierarchy,
            and consistent color schemes. The complex interface included multiple navigation levels,
            varied visual elements, and diverse color palettes. Both interfaces provided identical
            functionality to ensure fair comparison.
            
            3.3 Measures
            
            User engagement was measured through three primary metrics:
            1. Session duration (total time spent in app)
            2. Task completion rate (percentage of successfully completed tasks)
            3. Interaction frequency (number of taps/swipes per session)
            
            3.4 Qualitative Component
            
            Semi-structured interviews were conducted with 20 participants (10 from each condition)
            to gather detailed feedback about their user experience. Interviews were transcribed
            and analyzed using thematic analysis following Braun and Clarke's (2006) framework.
            
            4. Results
            
            Quantitative analysis revealed statistically significant differences between conditions.
            The simplified interface group showed 23% longer session duration (t(498)=3.42, p<0.001),
            15% higher task completion rates (t(498)=2.87, p<0.01), and 18% more interactions
            per session (t(498)=2.14, p<0.05).
            
            Qualitative analysis identified three major themes: navigation clarity, visual appeal,
            and cognitive load. Participants in the simplified condition reported easier navigation
            and reduced mental effort required to complete tasks.
            
            5. Discussion
            
            The findings support our hypothesis that simplified UI design elements enhance user
            engagement in mobile applications. The significant improvements across all engagement
            metrics suggest that design simplicity facilitates more effective user interactions.
            
            These results align with cognitive load theory, suggesting that reduced interface
            complexity allows users to focus on primary tasks rather than navigation challenges.
            The qualitative findings provide additional context, indicating that users perceive
            simplified interfaces as more intuitive and less frustrating.
            
            6. Conclusion
            
            This study demonstrates the positive impact of simplified UI design on user engagement
            in mobile applications. The mixed-methods approach provides both statistical evidence
            and user perspective insights, strengthening the validity of our conclusions.
            
            Future research should explore long-term engagement effects and examine how these
            findings apply across different app categories and user demographics.
        ";
    }

    private string CreateGenericDocument()
    {
        return @"
            Modern Technology Trends and Their Impact on Society
            
            Technology continues to reshape our world at an unprecedented pace. From artificial
            intelligence to renewable energy, these innovations are transforming how we work,
            communicate, and live our daily lives.
            
            Artificial Intelligence and Machine Learning
            
            AI has moved beyond science fiction to become an integral part of many industries.
            Machine learning algorithms now power recommendation systems, autonomous vehicles,
            and medical diagnostic tools. The potential for AI to solve complex problems is
            enormous, but it also raises important questions about privacy and employment.
            
            Environmental Technology
            
            Climate change has accelerated the development of green technologies. Solar and
            wind power have become cost-competitive with fossil fuels in many markets.
            Electric vehicles are gaining mainstream adoption, supported by expanding
            charging infrastructure and improving battery technology.
            
            Digital Communication
            
            Remote work and digital communication tools have fundamentally changed how we
            collaborate. Video conferencing, instant messaging, and cloud-based document
            sharing have made location-independent work possible for millions of people.
            
            Future Considerations
            
            As technology continues to advance, society must adapt to both the opportunities
            and challenges these changes bring. Education systems, regulatory frameworks,
            and social institutions will need to evolve to harness the benefits while
            mitigating potential risks.
        ";
    }

    #endregion
}
