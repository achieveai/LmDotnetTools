# Embeddings & Reranking Services Design

**Date:** 2025-06-01

## 1. Overview
This document describes two core services in the Agentic.Memory.Embeddings module:

- **Embedding Service**: Converts input text into fixed-size vector embeddings.
- **Reranking Service**: Sorts a list of documents by relevance to a query.

There are two implementations of the embedding service:
- **ServerEmbeddings**: Batch-requests a remote HTTP API.
- **LocalEmbeddings**: Generates embeddings locally via LLama.NET.

## 2. Architecture
```
            +-----------------------+
            |      Client Layer     |
            +-----------------------+
                     |       |
          +----------+       +-------------+
          |                                |
+-------------------+            +----------------------+
| IEmbeddingService |            |  RerankingService    |
|  (interface)      |            |  (class)             |
+-------------------+            +----------------------+
     /         \                        |
    /           \                       |
+-----------+ +---------------+         |
| Server    | | Local         |         |
| Embeddings| | Embeddings    |         |
+-----------+ +---------------+         |
         HTTP/Local                 HTTP|
            API Call                     |
                                        |
                              +----------------------+
                              |    Remote Service    |
                              +----------------------+
```

## 3. Data Models

### 3.1 Embedding Models
- **EmbeddingRequest**
  - `string Input`
  - `TaskCompletionSource<float[]> Tcs`

- **EmbeddingResponse**
  - `List<EmbeddingData> Data`

- **EmbeddingData**
  - `float[] Embedding`

- **EmbeddingApiType (enum)**
  - `Default`
  - `Jina`

### 3.2 Reranking Models
- **RerankRequest**
  - `string Model`
  - `string Query`
  - `List<string> Documents`

- **RerankResponse**
  - `List<RankedDocument> Results`

- **RankedDocument**
  - `int Index`
  - `float Score`

## 4. Service APIs

### 4.1 IEmbeddingService
```csharp
public interface IEmbeddingService : IDisposable
{
    int EmbeddingSize { get; }
    Task<float[]> GetEmbeddingAsync(string sentence);
}
```

### 4.2 ServerEmbeddings
```csharp
public class ServerEmbeddings : IEmbeddingService
{
    public ServerEmbeddings(
        string endpoint,
        string model,
        int embeddingSize,
        string? apiKey = null,
        int maxBatchSize = 1024 * 25,
        EmbeddingApiType apiType = EmbeddingApiType.Default
    );

    public int EmbeddingSize { get; }
    public Task<float[]> GetEmbeddingAsync(string sentence);
}
```

### 4.3 LocalEmbeddings
```csharp
public class LocalEmbeddings : IEmbeddingService, IDisposable
{
    public LocalEmbeddings(string modelPath);
    public LocalEmbeddings(ModelParams modelParams);

    public int EmbeddingSize { get; }
    public Task<float[]> GetEmbeddingAsync(string sentence);
}
```

### 4.4 RerankingService
```csharp
public class RerankingService : IDisposable
{
    public RerankingService(
        string endpoint,
        string model,
        string? apiKey = null
    );

    public Task<List<RankedDocument>> RerankAsync(
        string query,
        IEnumerable<string> documents
    );
}
```

## 5. Retry Logic & Progressive Backoff

### 5.1 Embedding Service
- **Method**: `PostWithRetryAsync<T>(string endpoint, T content, int maxRetries)`
- **Default**: `maxRetries = 1`
- **Backoff**: Linear backoff before each retry (1s × retryCount):
```csharp
private async Task<HttpResponseMessage> PostWithRetryAsync<T>(...)
{
    var retryCount = 0;
    while (true)
    {
        try
        {
            return await _httpClient.PostAsJsonAsync(endpoint, content);
        }
        catch when (retryCount < maxRetries)
        {
            retryCount++;
            // linear backoff: wait 1s * retryCount
            await Task.Delay(1000 * retryCount);
        }
    }
}
```

### 5.2 Reranking Service
- **Retries**: up to 2 attempts
- **Behavior**:
  1. First attempt: send full documents.
  2. On `HttpRequestException`:
     - Truncate each document to 1024 tokens.
     - Wait `500ms * retryCount`.
     - Retry.

## 6. Configuration Parameters
- `endpoint`: URL of remote API
- `model`: model identifier (e.g. "text-embedding-ada-002")
- `apiKey`: optional authentication
- `embeddingSize`: expected vector dimension
- `maxBatchSize`: max bytes per batch (ServerEmbeddings)
- `apiType`: EmbeddingApiType enum for request format (ServerEmbeddings)

## 7. Sequence Flows

### 7.1 Embedding Flow
1. Client calls `GetEmbeddingAsync(sentence)`.
2. Text is chunked if too long.
3. Each chunk is enqueued and processed in batch.
4. `PostWithRetryAsync` posts to `/embeddings` endpoint.
5. Responses are collected, merged, and returned.

### 7.2 Reranking Flow
1. Client calls `RerankAsync(query, documents)`.
2. Builds `RerankRequest` payload.
3. Posts to `/rerank` endpoint.
4. Deserializes `RerankResponse` to `List<RankedDocument>`.

## 8. Usage Example
```csharp
using var embedSvc = new ServerEmbeddings(
    "https://api.example.com/embeddings",
    "text-embedding-ada-002",
    embeddingSize: 1536,
    apiKey: Environment.GetEnvironmentVariable("API_KEY"),
    maxBatchSize: 1024 * 25,
    apiType: EmbeddingApiType.Default
);

float[] vector = await embedSvc.GetEmbeddingAsync("Hello world");

using var rankSvc = new RerankingService(
    "https://api.example.com/rerank",
    "ranker-model-v1",
    apiKey: Environment.GetEnvironmentVariable("API_KEY")
);

var docs = new[] { "doc1", "doc2", "doc3" };
var ranked = await rankSvc.RerankAsync("search query", docs);
