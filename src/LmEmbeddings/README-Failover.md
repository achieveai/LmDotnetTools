# Embedding & Reranking Failover

Automatic primary/backup failover for `IEmbeddingService` and `IRerankService` with timeout-based switching, error detection, and automatic recovery.

## When to Use

Use failover when your primary embedding or reranking server can enter bad states (timeouts, 5xx errors, downtime) and you have a paid backup service as a fallback.

## Quick Start

### Option 1: Configuration via `appsettings.json`

Add to your `appsettings.json`:

```json
{
  "Embeddings": {
    "Failover": {
      "Primary": {
        "Endpoint": "https://self-hosted.example.com",
        "Model": "jina-embeddings-v3",
        "EmbeddingSize": 1024,
        "ApiKey": "primary-key"
      },
      "Backup": {
        "Endpoint": "https://api.jina.ai/v1",
        "Model": "jina-embeddings-v3",
        "EmbeddingSize": 1024,
        "ApiKey": "backup-key"
      },
      "PrimaryRequestTimeoutSeconds": 5,
      "FailoverOnHttpError": true,
      "RecoveryIntervalSeconds": 120
    }
  },
  "Reranking": {
    "Failover": {
      "Primary": {
        "Endpoint": "https://self-hosted.example.com/v1/rerank",
        "Model": "jina-reranker-m0",
        "ApiKey": "primary-key"
      },
      "Backup": {
        "Endpoint": "https://api.jina.ai/v1/rerank",
        "Model": "jina-reranker-m0",
        "ApiKey": "backup-key"
      },
      "PrimaryRequestTimeoutSeconds": 5,
      "FailoverOnHttpError": true,
      "RecoveryIntervalSeconds": 120
    }
  }
}
```

Register in `Program.cs` or `Startup.cs`:

```csharp
using AchieveAi.LmDotnetTools.LmEmbeddings.Configuration;

services.AddFailoverEmbeddings(configuration.GetSection("Embeddings:Failover"));
services.AddFailoverReranking(configuration.GetSection("Reranking:Failover"));
```

Then inject `IEmbeddingService` or `IRerankService` as usual -- the failover wrapper is transparent.

### Option 2: Pre-built instances

For custom providers or testing scenarios:

```csharp
using AchieveAi.LmDotnetTools.LmEmbeddings.Configuration;

var options = new FailoverOptions
{
    PrimaryRequestTimeout = TimeSpan.FromSeconds(5),
    FailoverOnHttpError = true,
    RecoveryInterval = TimeSpan.FromMinutes(2)
};

services.AddFailoverEmbeddings(primaryService, backupService, options);
services.AddFailoverReranking(primaryReranker, backupReranker, options);
```

### Option 3: Manual construction (no DI)

```csharp
using var primary = new ServerEmbeddings(
    endpoint: "https://self-hosted.example.com",
    model: "jina-embeddings-v3",
    embeddingSize: 1024,
    apiKey: "primary-key");

using var backup = new ServerEmbeddings(
    endpoint: "https://api.jina.ai/v1",
    model: "jina-embeddings-v3",
    embeddingSize: 1024,
    apiKey: "backup-key");

using var service = new FailoverEmbeddingService(primary, backup, options);

// Use it like any IEmbeddingService
var vector = await service.GetEmbeddingAsync("hello world");
```

## Configuration Reference

### FailoverOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `PrimaryRequestTimeout` | `TimeSpan` | *(required)* | Max wait for primary before triggering failover |
| `FailoverOnHttpError` | `bool` | `true` | Whether 4xx/5xx responses trigger failover |
| `RecoveryInterval` | `TimeSpan?` | `null` | Time on backup before probing primary. `null` = manual reset only |

### appsettings.json Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `Primary:Endpoint` | `string` | *(required)* | Primary service URL |
| `Primary:Model` | `string` | *(required)* | Model identifier |
| `Primary:EmbeddingSize` | `int` | `1536` | Vector dimensions (embedding only) |
| `Primary:ApiKey` | `string` | *(required)* | API key |
| `Backup:*` | | | Same as Primary |
| `PrimaryRequestTimeoutSeconds` | `double` | `5` | Timeout in seconds |
| `FailoverOnHttpError` | `bool` | `true` | Failover on HTTP errors |
| `RecoveryIntervalSeconds` | `double?` | `120` | Recovery probe interval. `null` = manual reset |

## Runtime Behavior

```
            Request
               |
        [ShouldUsePrimary?]
          /          \
        YES           NO (in cooldown)
         |             |
    Try Primary    Use Backup directly
     /       \
  Success    Failure (timeout or HTTP error)
     |            |
  Return     Mark unhealthy, set probe timer
  result          |
              Try Backup
              /        \
          Success    Both failed
             |          |
          Return    Throw PrimaryBackupFailoverException
          result    (preserves both errors)

--- After RecoveryInterval expires ---

     Next request probes primary:
       Success -> restore primary
       Failure -> stay on backup, reset timer
```

### Failover Triggers

- **Timeout**: Primary doesn't respond within `PrimaryRequestTimeout`
- **HTTP errors**: 4xx/5xx responses (when `FailoverOnHttpError = true`)
- **Connection failures**: `HttpRequestException` from network issues

### What Does NOT Trigger Failover

- **Caller cancellation**: If the caller cancels the `CancellationToken`, failover state is unchanged
- **HTTP errors when disabled**: When `FailoverOnHttpError = false`, only timeouts trigger failover

### Manual Reset

When `RecoveryInterval` is `null`, automatic recovery is disabled. Use `ResetToPrimary()`:

```csharp
var failoverService = (FailoverEmbeddingService)embeddingService;
failoverService.ResetToPrimary();
```

## Monitoring via Structured Logs

All failover events are logged with structured properties. Query with DuckDB:

```sql
-- Find all failover events
SELECT "@t" as Time, "@l" as Level, SourceContext, "@mt" as MessageTemplate
FROM read_json('/path/to/.logs/tests/tests.jsonl')
WHERE SourceContext LIKE '%Failover%'
ORDER BY "@t";

-- Count failovers by service
SELECT SourceContext, COUNT(*) as FailoverCount
FROM read_json('/path/to/.logs/tests/tests.jsonl')
WHERE "@mt" LIKE '%failing over to backup%'
GROUP BY SourceContext;
```

### Log Messages Reference

| Level | Message | Meaning |
|---|---|---|
| Debug | `Primary embedding service request succeeded.` | Request routed to primary, succeeded |
| Debug | `Primary embedding service marked unhealthy; routing request directly to backup.` | In cooldown, skipping primary |
| Warning | `Primary embedding service failed; failing over to backup.` | Primary failed, switching to backup |
| Error | `Backup embedding service also failed after primary failure.` | Both services failed |
| Information | `Manual reset: restoring primary embedding service.` | `ResetToPrimary()` called |

## Constraints

- **Embedding sizes must match**: Primary and backup must produce vectors of the same dimension
- **Singleton lifetime**: The failover service tracks state across requests, so it's registered as a singleton
- **Thread-safe**: `FailoverStateController` uses locking; safe for concurrent requests
- **Single-flight probe**: Only one concurrent request probes primary after cooldown; others go to backup
