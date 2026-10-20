# LmStreaming review host: profile and fixes (October 2026)

The Reviewd batch drives `LmStreaming.Sample` as its review host. In October 2026 the host ran at ~2.5 cores
and 10.2 GB of memory with ~10 reviews in flight. This note records what was measured, what was fixed, and what
was measured but left for later.

## Summary

| Finding | Measured | Fix | Where |
|---|---|---|---|
| Caller-info log enricher | 69% of busy thread time (old host) | Enrich only Warning and above | `samples/LmStreaming.Sample/Program.cs` |
| Whole-file conversation store | 19% of busy time; ~80% of large-object churn | SQLite store (earlier commit) | `SqliteConversationStore` |
| One o200k tokenizer per agent loop | 19 copies, ~850 MB of live memory | One shared tokenizer per process | `CompactionHostSetup.ResolveTextTokens` |
| Serializer options per Anthropic stream | 16,858 emitted accessor methods on the heap; 142 KB per new parser | Shared static options | `AnthropicStreamParser`, `AnthropicClient` |
| Inner options copied per message | ~5% of busy time (old host); 4.7 KB per round trip | Cached per caller options | `IMessageJsonConverter` |
| Per-chunk Anthropic Debug logs | 66% of all log lines | Logged at Trace | `AnthropicAgent.GenerateStreamingMessages` |
| Recursive config file watching | 7.3% of host CPU; ~117,000 inotify watches | `DOTNET_hostBuilder__reloadConfigOnChange=false` | `samples/Reviewd.Sample/RUNBOOK.md` step 2 |
| Usage records rewritten with thread metadata | ≤1% CPU; 1.9 MB largest row | Deferred: needs a schema migration | `ConversationUsageProjection` |

## How it was measured

- `dotnet-trace` with `dotnet-sampled-thread-time` (CPU), `gc-verbose` (allocations by stack).
- `dotnet-counters` for CPU, GC and exception rates.
- `dotnet-dump collect --type Heap`, then `dumpheap -stat`, `gcroot` and `dumpobj` for live memory and owners.
  `dotnet-gcdump` was not usable: on .NET 9 it captured ~1 GB of a 9 GB heap.
- inotify watch counts from `/proc/<pid>/fdinfo`.
- DuckDB over the Serilog JSON log files for log volume by template.
- Shares of "busy time" exclude threads that were waiting, so they are thread time, not pure CPU.

## Profile 1: old host, 15 October (before any fix)

- ~2.5 cores, 10.2 GB resident, ~10 reviews in flight. Allocation 236 MB/s.
- **69%** of busy thread time was in `Serilog.Enrichers.CallerInfo`. It captures and demystifies a full stack
  trace (Ben.Demystifier) for every log event. 45% came from the two per-chunk `LogDebug` calls in
  `AnthropicAgent`.
- **19%** was `FileConversationStore.ReadJsonFileAsync`: whole JSON files re-read on every message or metadata
  load. About 80% of the 1.14 GB of large objects allocated per 30 s came from the file store.
- ~5% was System.Text.Json metadata lookups for the options copy `IMessageJsonConverter` made on every read and
  write (6.2% including the serialization itself).
- Both log files hit Serilog's 1 GB/day file cap, so later events were enriched and then dropped.

## Profile 2: new host, 16 October (enricher fix and SQLite deployed)

- 0.39 cores on average over its 2.6 hours (3,639 CPU-seconds) at ~3.1 reviews in flight. That is about
  0.13 cores per review, against about 0.25 before. The workloads differ, so treat this as rough.
- 2.6 GB resident, against 10.2 GB before.
- Live managed heap 1.01 GB. Large-object heap 1.6 GB: 478 MB live, 1.12 GB dead. The dead part is waiting
  for a full GC, which an idle host rarely runs. It is not a leak.
- **19 Tiktoken o200k encoders** were alive, ~45 MB each with their small objects. Root chain:
  `MultiTurnAgentLoop` → `CompactionRuntime` → `Func<string,long>` → `CompactionHostSetup` closure →
  `Tiktoken.Encoder`. `ResolveTextTokens` built a new encoder and a compiled regex for every root loop.
- **16,858 `DynamicMethod` objects.** Sampled names were System.Text.Json accessors for Anthropic types:
  `CacheReadInputTokensSetter`, `ThinkingSetter`, `UsageSetter`, `UrlSetter`. Every streamed request
  built a new `AnthropicStreamParser`, which built new options with new converter instances.
  System.Text.Json only reuses metadata across options whose converters are the same instances, so each request
  rebuilt the Anthropic type metadata.
- **File watching was 7.3% of CPU** (265 CPU-seconds on the "File Watch" thread). Three recursive watchers: two
  on the working directory (the repo, 55,526 folders each) and one on the content root (6,093). They come from
  the host's own `ContentRootFileProvider` and config reload. On a test copy, the watches were 55,527 with the
  repo as working directory, 4 from a small folder, and 0 with `DOTNET_hostBuilder__reloadConfigOnChange=false`.
  The API answered 200 in all three.
- **Logs.** The 16 October file (1,290,390 lines) hit the 1 GB cap at 02:34. By template:
  - `Streaming event processed` and `Message transformed`: 66.3% together (both per chunk, `AnthropicAgent`).
  - HTTP request log lines 19.0%, including 4.6% `503` responses logged as errors.
  - `Hierarchy discovery busy` 4.6%.
  - All other Debug templates together 8.5%.
- **SQLite `conversations.db` 1.22 GB.** `messages` 270,000 rows (869 MB). `thread_metadata` 1,009 rows (145 MB);
  the largest row is 1.9 MB, 91% of it `usage.records`. Twelve rows exceed 1 MB.

## Fixes in this change

1. **Caller info only at Warning and above** (`Program.cs`). `Enrich.AtLevel(LogEventLevel.Warning, ...)` keeps
   file and line on the events that need them. Checked on a test boot: Information lines carry no caller
   properties, and Warning lines do.
2. **One o200k tokenizer per process** (`CompactionHostSetup`). `ResolveTextTokens("o200k")` now returns one
   lazily built counter. Test: `O200kTextTokenizer_IsSharedByEveryLoopsSetup`.
   - Tiktoken 2.2 keeps an unbounded per-piece count cache (`Encoder.EnableCache`, a `ConcurrentDictionary`).
     Sharing the encoder makes that cache process-wide. Measured on 8.2 MB of this repo's C#: the cache held
     1.3 MB and made counting ~1.45× faster (25 vs 17 MB/s), so it stays on. The cache grows with the number
     of distinct word pieces, which flattens out quickly.
3. **Shared Anthropic serializer options** (`AnthropicStreamParser`, `AnthropicClient`). Both now use one
   static options instance, as `AnthropicAgent` already did. A new parser handling one `message_start` event
   allocated 142,413 bytes before and 2,944 after. Test: `NewParserPerStream_ReusesTheSerializerMetadata`
   (threshold 32 KB).
4. **Cached inner options in `IMessageJsonConverter`.** The copy without the converter is built once per caller
   options instance (`ConditionalWeakTable`). A `TextMessage` round trip allocated 4,682 bytes before and 3,410
   after. There is no threshold test: the gap is too small for a stable one. Existing converter tests cover
   behaviour.
5. **Per-chunk Anthropic logs at Trace** (`AnthropicAgent`). This removes ~66% of log lines. The library
   namespace stays at Debug, so the remaining diagnostics are kept.
6. **Runbook start command sets `DOTNET_hostBuilder__reloadConfigOnChange=false`.** Trade-off: edits to the
   deployed `appsettings.json` take effect only on the next start. Local development keeps live reload.

## Measured and deferred

- **Usage records inside thread metadata.** `ConversationUsageProjection.SaveAsync` merges and rewrites the
  whole records list into `ThreadMetadata.Properties` on every update. That is quadratic in a conversation's
  attempts and makes `thread_metadata` rows large. But the measured CPU was ≤1% on the old host, even with the
  file store. Moving records to an append-only table needs a schema change and migration for every store, so it
  is left as a separate change.
- **Request logging noise.** HTTP request lines are 19% of the log, and expected `503 HierarchyBusy` responses
  are logged as errors (~10 per second at peak). Candidates: log successful polling GETs at Debug, and stop
  polling the sub-agent endpoint while the hierarchy is busy.
- **118 `MultiTurnAgentLoop` instances alive on an idle host**, 19 with transcript-mirror subscriptions.
  Not yet traced to an owner.
- **Other options built per instance** (`CopilotDynamicToolBridge`, `CodexDynamicToolBridge`,
  `ChatWebSocketManager`, `IMessageWebSocketHandler`). Same pattern as the Anthropic parser, but no evidence that
  they are hot.

## Next measurement

The 16 October CPU trace was taken while the host was idle, so these fixes still need a CPU profile under load.
With a batch running:

```bash
dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time --duration 00:00:00:30 -o cpu.nettrace
dotnet-counters collect -p <pid> --format json -o counters.json --duration 00:00:01:00
```

Expect no `CallerInfo`, `Tiktoken.Encoder` construction, or `ReflectionEmitMemberAccessor` frames among the
top stacks, and a heap with one o200k encoder.
