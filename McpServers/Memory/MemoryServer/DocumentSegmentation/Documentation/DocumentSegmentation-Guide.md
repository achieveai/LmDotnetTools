# Document Segmentation System - Comprehensive Guide

## Overview

The Document Segmentation System is a sophisticated component of the MemoryServer that intelligently breaks down large documents into manageable, coherent segments. It provides both rule-based and LLM-powered segmentation strategies with comprehensive quality assessment.

## Architecture

### Core Components

1. **DocumentSegmentationService** - Main orchestrator for segmentation operations
2. **DocumentSizeAnalyzer** - Analyzes document metrics and determines segmentation necessity  
3. **SegmentationPromptManager** - Manages YAML-based LLM prompts with hot reload
4. **DocumentSegmentRepository** - Database operations with FTS search capabilities
5. **MemoryService Integration** - Automatic segmentation during memory creation

### Key Features

- **Intelligent Routing**: Automatically determines if documents need segmentation
- **Multiple Strategies**: Topic-based, structure-based, narrative-based, and hybrid approaches
- **Quality Assessment**: Comprehensive scoring for coherence, independence, and consistency
- **Session Isolation**: Complete isolation between different user sessions
- **Graceful Fallbacks**: Rule-based segmentation when LLM services are unavailable
- **Performance Optimized**: Efficient database operations with FTS search

## Configuration

### appsettings.json Configuration

```json
{
  "MemoryServer": {
    "DocumentSegmentation": {
      "Thresholds": {
        "MinDocumentSizeWords": 1500,
        "MaxDocumentSizeWords": 50000,
        "TargetSegmentSizeWords": 1000,
        "MaxSegmentSizeWords": 2000,
        "MinSegmentSizeWords": 100
      },
      "LlmOptions": {
        "EnableLlmSegmentation": true,
        "SegmentationCapability": "document_segmentation",
        "MaxRetries": 3,
        "TimeoutSeconds": 60
      },
      "Quality": {
        "MinCoherenceScore": 0.6,
        "MinIndependenceScore": 0.5,
        "MinTopicConsistencyScore": 0.7,
        "EnableQualityValidation": true
      },
      "Performance": {
        "MaxConcurrentOperations": 10,
        "EnableCaching": true,
        "CacheExpirationMinutes": 60
      },
      "Prompts": {
        "FilePath": "prompts.yml",
        "DefaultLanguage": "en",
        "EnableHotReload": true,
        "CacheExpirationMinutes": 30
      }
    }
  }
}
```

### Document Type Thresholds

Different document types have optimized thresholds:

- **Email**: 250 words minimum (optimized for email content)
- **Chat**: 150 words minimum (optimized for chat conversations) 
- **Research Papers**: 2000 words minimum (accounts for academic structure)
- **Legal Documents**: 1000 words minimum (preserves legal context)
- **Technical Documents**: 1500 words minimum (maintains technical coherence)
- **Generic**: Uses configured default threshold

## Database Schema

### document_segments Table

```sql
CREATE TABLE document_segments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_document_id INTEGER NOT NULL,
    segment_id TEXT UNIQUE NOT NULL,
    sequence_number INTEGER NOT NULL,
    content TEXT NOT NULL,
    title TEXT,
    summary TEXT,
    coherence_score REAL DEFAULT 0.0,
    independence_score REAL DEFAULT 0.0,
    topic_consistency_score REAL DEFAULT 0.0,
    user_id TEXT NOT NULL,
    agent_id TEXT,
    run_id TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    metadata TEXT
);
```

### segment_relationships Table

```sql
CREATE TABLE segment_relationships (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_segment_id TEXT NOT NULL,
    target_segment_id TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    strength REAL DEFAULT 1.0,
    user_id TEXT NOT NULL,
    agent_id TEXT,
    run_id TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    metadata TEXT
);
```

### FTS5 Search Table

```sql
CREATE VIRTUAL TABLE document_segments_fts USING fts5(
    content,
    title,
    summary,
    content='document_segments',
    content_rowid='id'
);
```

## API Reference

### IDocumentSegmentationService

#### SegmentDocumentAsync
```csharp
Task<DocumentSegmentationResult> SegmentDocumentAsync(
    string content,
    DocumentSegmentationRequest request,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
```

#### ShouldSegmentAsync
```csharp
Task<bool> ShouldSegmentAsync(
    string content,
    DocumentType documentType = DocumentType.Generic,
    CancellationToken cancellationToken = default);
```

#### DetermineOptimalStrategyAsync
```csharp
Task<SegmentationStrategy> DetermineOptimalStrategyAsync(
    string content,
    DocumentType documentType = DocumentType.Generic,
    CancellationToken cancellationToken = default);
```

### IDocumentSegmentRepository

#### StoreSegmentsAsync
```csharp
Task<List<string>> StoreSegmentsAsync(
    ISqliteSession session,
    List<DocumentSegment> segments,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);
```

#### SearchSegmentsAsync
```csharp
Task<List<DocumentSegment>> SearchSegmentsAsync(
    ISqliteSession session,
    string query,
    SessionContext sessionContext,
    int limit = 10,
    CancellationToken cancellationToken = default);
```

## Usage Examples

### Basic Segmentation

```csharp
// Automatic segmentation during memory creation
var memory = await memoryService.AddMemoryAsync(largeContent, sessionContext);
// Segmentation happens automatically if content exceeds thresholds

// Manual segmentation
var request = new DocumentSegmentationRequest
{
    DocumentType = DocumentType.ResearchPaper,
    Strategy = SegmentationStrategy.StructureBased
};

var result = await segmentationService.SegmentDocumentAsync(
    content, request, sessionContext);
```

### Quality Assessment

```csharp
var assessment = await segmentationService.ValidateSegmentationQualityAsync(
    result, cancellationToken);

if (assessment.MeetsQualityStandards)
{
    // Segmentation passed quality checks
    Console.WriteLine($"Quality Score: {assessment.OverallScore:P0}");
}
else
{
    // Review quality feedback
    foreach (var feedback in assessment.QualityFeedback)
    {
        Console.WriteLine($"Quality Issue: {feedback}");
    }
}
```

### Search Segments

```csharp
await using var session = await sessionFactory.CreateSessionAsync();
var segments = await repository.SearchSegmentsAsync(
    session, "machine learning", sessionContext, limit: 5);

foreach (var segment in segments)
{
    Console.WriteLine($"Segment: {segment.Title}");
    Console.WriteLine($"Relevance: {segment.Metadata["search_rank"]}");
}
```

## Deployment Guide

### Prerequisites

1. **SQLite with FTS5 Support**: Ensure SQLite has FTS5 extension enabled
2. **LLM Provider Configuration**: Set up Anthropic or OpenAI providers for LLM segmentation
3. **Prompt Configuration**: Deploy `prompts.yml` file with segmentation prompts

### Deployment Steps

1. **Database Migration**
   ```bash
   # Run schema migration
   dotnet ef migrations add DocumentSegmentation
   dotnet ef database update
   ```

2. **Configuration Setup**
   ```bash
   # Copy configuration files
   cp appsettings.Production.json /app/
   cp prompts.yml /app/DocumentSegmentation/Configuration/
   ```

3. **Service Registration**
   ```csharp
   // In Program.cs or Startup.cs
   services.AddMemoryServerCore(configuration, environment);
   services.AddDocumentSegmentationServices();
   ```

4. **Health Checks**
   ```csharp
   // Add health checks
   services.AddHealthChecks()
       .AddCheck<DocumentSegmentationHealthCheck>("document_segmentation");
   ```

### Performance Considerations

1. **Database Optimization**
   - Ensure proper indexing on session columns (user_id, agent_id, run_id)
   - Regular VACUUM operations for SQLite optimization
   - Monitor FTS5 index size and performance

2. **Memory Usage**
   - Configure appropriate cache expiration times
   - Monitor memory usage during large document processing
   - Consider streaming for very large documents

3. **Concurrency**
   - Adjust MaxConcurrentOperations based on system resources
   - Monitor database connection pool usage
   - Implement rate limiting for API endpoints

### Monitoring and Troubleshooting

#### Key Metrics to Monitor

- Segmentation success rate
- Average processing time per document
- Quality score distributions
- Database performance metrics
- LLM API response times and error rates

#### Common Issues

1. **Segmentation Not Triggered**
   - Check document word count vs thresholds
   - Verify document type configuration
   - Review logs for threshold calculations

2. **Poor Quality Scores**
   - Adjust segmentation strategy
   - Review prompt configuration
   - Consider document-type specific optimizations

3. **Search Performance Issues**
   - Rebuild FTS5 indexes
   - Optimize search queries
   - Consider adding more specific indexes

4. **Session Isolation Problems**
   - Verify SessionContext is properly set
   - Check database query filters
   - Review test isolation patterns

### Security Considerations

1. **Data Privacy**
   - All segments inherit session isolation
   - No cross-session data leakage
   - Metadata sanitization for sensitive content

2. **Input Validation**
   - Content size limits enforced
   - Malicious content detection
   - SQL injection prevention

3. **LLM Security**
   - Prompt injection prevention
   - Content filtering for sensitive data
   - API key rotation policies

## Testing

### Unit Tests
```bash
dotnet test --filter "DocumentSegmentation.Tests.Services"
```

### Integration Tests
```bash
dotnet test --filter "DocumentSegmentation.Integration"
```

### Performance Tests
```bash
dotnet test --filter "DocumentSegmentation.Performance"
```

## Future Enhancements

1. **Advanced LLM Integration**
   - Multi-model strategy selection
   - Custom fine-tuned models
   - Streaming segmentation for real-time processing

2. **Enhanced Search**
   - Semantic search with embeddings
   - Multi-language support
   - Advanced relevance scoring

3. **Quality Improvements**
   - Machine learning-based quality assessment
   - A/B testing for segmentation strategies
   - User feedback integration

4. **Performance Optimizations**
   - Distributed processing support
   - Caching layer improvements
   - Background processing queues

## Support

For technical support and questions:
- Review logs in `/app/logs/memory-server.log`
- Check health check endpoints
- Monitor application metrics
- Consult API documentation for detailed error codes

---

*Last Updated: 2025-06-25*
*Version: 1.0.0*
