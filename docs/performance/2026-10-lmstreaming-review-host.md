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
| Busy `503` request lines logged at Error, each walked by caller info | ~1.5 of ~3.4 cores (27 Oct, under load) | Request lines skip caller info; a busy `503` logs at Information | `HostLogging`, `ConversationsController.HierarchyBusy` |
| o200k re-counts the whole history several times a turn | ~0.7 cores (27 Oct, under load) | Count each text once per string instance | `CompactionHostSetup.CountOncePerString` |

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
  - HTTP request log lines 19.0%, including 4.6% `503` responses logged as errors (Information since
    Profile 3).
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

## Profile 3: under load, 27 October

The host ran at ~3.4 cores (user 2.1, system 1.26) with reviews in flight. A 30 s `dotnet-trace` showed 5.55
threads busy; `dotnet-counters` ran for 60 s.

- **Caller info on busy `503` lines: 1.52 threads (27%).** `IndexedConversationStore.ReadChildrenAsync` answers
  `503 hierarchy_busy` while any metadata write is pending anywhere on the host, so 47–74% of recursive
  sub-agent polls failed (~9 per second). Request logging wrote each one at Error, and the Warning-and-above
  caller-info enricher demystified a full stack for each: ~165 ms of CPU per line. The caller it found was
  misleading: 45% of those lines named the closing brace of `SqliteConversationStore.LoadMetadataAsync`, the
  read whose completion resumed the request; the rest named nothing.
  - Fix: `HostLogging` keeps caller info off request lines, and the controller marks a busy `503` so its
    request line is Information. The controller's own `Hierarchy discovery busy` line (Information) keeps the
    reason. A caller-facing retry is not an application retry, so it is not a Warning: alert on that line or on
    `StatusCode` 503, not on the request line's level.
- **o200k token counting: 0.67 threads (12%).** Compaction sizes the whole request several times a turn
  (`EvaluateAsync`, `ObserveContextAsync`, escalation). `CountOncePerString` caches each string's count by
  reference, so repeat passes reuse it. `AgentMessage.Text` is built on every read, so its envelope is always
  counted again; it is 0.8% of the text compaction sizes in active threads (5.1% in the most affected one).
- **SQLite: ~1.1 threads, CPU plus kernel time.** 57 MB/s of page-cache reads and 12.5 MB/s of writes.
  `thread_metadata.metadata_json` is 35 KB at p50; 66% of it is `context.observations`, rewritten every turn.
  Deferred until a steady-state trace: this one included a browser burst of 1,022 transcript fetches in 2 min.
- **Smaller:** thread-pool spin 0.34 threads; `AnthropicStreamParser` re-parses tool input on every delta
  (~340 caught `JsonException`s per second, ~0.1 threads); every log line is written twice (file and console).
- **Deferred:** a busy check per tree instead of per host. Today's behaviour is pinned by
  `GetContext_ReturnsHierarchyBusy503DuringUnrelatedPublication_ThenRecovers`, so changing it is a design change.

## Measured and deferred

- **Usage records inside thread metadata.** `ConversationUsageProjection.SaveAsync` merges and rewrites the
  whole records list into `ThreadMetadata.Properties` on every update. That is quadratic in a conversation's
  attempts and makes `thread_metadata` rows large. But the measured CPU was ≤1% on the old host, even with the
  file store. Moving records to an append-only table needs a schema change and migration for every store, so it
  is left as a separate change.
- **Request logging noise.** HTTP request lines are 19% of the log. Busy `503` responses now log at Information
  (Profile 3). Still open: log successful polling GETs at Debug, and stop polling the sub-agent endpoint while
  the hierarchy is busy.
- **118 `MultiTurnAgentLoop` instances alive on an idle host**, 19 with transcript-mirror subscriptions.
  Not yet traced to an owner.
- **Other options built per instance** (`CopilotDynamicToolBridge`, `CodexDynamicToolBridge`,
  `ChatWebSocketManager`, `IMessageWebSocketHandler`). Same pattern as the Anthropic parser, but no evidence that
  they are hot.

## Next measurement

The 16 October CPU trace was taken while the host was idle; Profile 3 (27 October) was under load. Repeat it
after each deploy, with a batch running:

```bash
dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time --duration 00:00:00:30 -o cpu.nettrace
dotnet-counters collect -p <pid> --format json -o counters.json --duration 00:00:01:00
```

Expect no `CallerInfo`, `Tiktoken.Encoder` construction, or `ReflectionEmitMemberAccessor` frames among the
top stacks, and a heap with one o200k encoder.
