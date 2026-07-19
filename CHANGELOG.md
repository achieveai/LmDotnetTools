# Changelog

All notable changes to the LmDotnetTools project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Sandbox**: New `AchieveAi.LmDotnetTools.Sandbox` package (`net8.0`/`net9.0`, no ASP.NET/provider dependencies) — a credential-scoped `SandboxClient` for the sandbox gateway's control plane: authenticated create/get/list/delete lifecycle, marketplace preview, and session discovery; command execution (`ExecuteAsync`) over the gateway's direct operations API (`POST`/`GET .../operations`, with gateway-owned exact-byte stdout/stderr artifacts downloaded through the files API) — idempotency/recovery is gateway-scoped (a same-operation-id resubmit is answered from the gateway's retained state) and NOT durable across a gateway restart; and exact UTF-8 file transfer over the direct files/directories API (`ReadTextFileAsync`/`WriteTextFileAsync` = `GET`/`PUT .../files/{mount_id}`, `ListDirectoryAsync` = paginated `GET .../directories/{mount_id}`). Ships with owned or borrowed `HttpClient` transport, stable `SandboxErrorKind`/`SandboxException` error classification, secret-redacted `SandboxClientOptions`, and an `AUTH_ENFORCE=true` live-gateway contract CI job. (#187, #188, #189, #190, #197)
- **LmWorkflow**: `StartWorkflow`/`CheckWorkflow`/`WaitWorkflow` agent-facing tools (`StartWorkflowToolProvider`) that launch a pre-authored `WorkflowDefinition` on an isolated controller loop via the new `WorkflowManager`, with bounded concurrency and proactive completion notifications (`NotifyKinds.WorkflowCompletion`). (#179)
- **LmMultiTurn**: `SubAgentOptions.NonInheritedToolNames` to exclude specific tools from sub-agent inheritance, and a public `MultiTurnAgentLoop.RegisteredToolNames` accessor.
- **LmMultiTurn**: Optional agent-wide `IAgentPublicationObserver` hook (`AgentPublication`/`AgentPublicationKind`) observing every message at `MultiTurnAgentBase.PublishToAllAsync`, intended as the future authority for durable child replay. `AgentPublication.Sequence` is a monotonic per-agent counter assigned under the same lock used for v1 replay-buffer bookkeeping; concurrent publications are dispatched to the observer strictly in that `Sequence` order (never concurrently), and one publication's observer failure faults only its own publishing caller without blocking later publications. The `CancellationToken` an observer receives is scoped to the publishing agent's own disposal, never the publishing run/request's token, so a cancelled run cannot cut off observer durability. Threaded an optional trailing `publicationObserver` constructor parameter (binary-signature change — source callers using named/defaulted args are unaffected; compiled callers should recompile against this release) through `MultiTurnAgentBase`, `MultiTurnAgentLoop`, `ClaudeAgentLoop`, `CodexAgentLoop` (both overloads), and `CopilotAgentLoop`. (WI #194 tasks 5-6)
- **LmMultiTurn**: Opt-in `strictCanonicalPersistence` constructor flag (default `false`, intended for captured child loops) on `MultiTurnAgentBase` durably ordering canonical child-history persistence: every `AddToHistory` canonical append and every `ReplacePersistedAsync` replacement now enters one per-agent ordered persistence queue, guaranteeing a replacement always runs after the placeholder append it replaces; a strict-mode replacement store failure propagates to its caller (e.g. `MultiTurnAgentLoop.ResolveToolCallAsync`) instead of being logged/swallowed; and `CompleteRunAsync` awaits that queue's flush before any terminal run-ledger write or `RunCompletedMessage` publication, failing closed (no terminal success, no terminal message, on either the natural or the error-completion path) when any queued write failed. Default (`false`) mode is completely unaffected — fire-and-forget appends and best-effort-swallowed replacement failures keep their exact current behavior and timing. Threaded an optional trailing `strictCanonicalPersistence` constructor parameter (binary-signature change — source callers using named/defaulted args are unaffected; compiled callers should recompile against this release) through `MultiTurnAgentBase`, `MultiTurnAgentLoop`, `ClaudeAgentLoop`, `CodexAgentLoop` (both overloads), and `CopilotAgentLoop` (both overloads). `SubAgentManager` is not yet wired to this flag. (WI #194 tasks 7-8)

### Fixed

- **LmMultiTurn**: `strictCanonicalPersistence`'s actual canonical store writes (the queued `AddToHistory`/`AddDeferredToHistoryAsync` appends and `ReplacePersistedAsync` replacements) now run under a dedicated per-agent durability lifetime token, never the run/request caller's own token — a `CancelCurrentRunAsync` cancellation or an external `ResolveToolCallAsync` request's cancellation now cancels only that caller's own wait for the queued write (`WaitAsync(ct)`) and can no longer abort the underlying store write, record a sticky canonical-persistence fault, or permanently brick a later terminal `CompleteRunAsync` flush. `DisposeAsync` now cancels this durability token (instead of the write silently ignoring disposal) to unblock a permanently gated write before its bound drain, and that cancellation is never itself recorded as a sticky fault. Also closed an enqueue-vs-disposal TOCTOU: `DisposeAsync` now marks the agent disposed and snapshots the canonical persistence chain's tail immediately (no intervening await) under the same lock every enqueue call takes, so an enqueue that loses the race to disposal now fails with a predictable `ObjectDisposedException` before appending a node, instead of silently landing a write disposal's snapshot already missed. Default (best-effort) mode remains unaffected. (WI #194 tasks 7-8 follow-up)
- **LmMultiTurn**: Closed a caller-cancellation-after-enqueue consistency gap in strict `AddDeferredToHistoryAsync`/`ReplacePersistedAsync`: previously, once a placeholder append or replacement was enqueued onto the durability-token-scoped chain, a caller-token cancellation of that call's own `WaitAsync(ct)` (a `CancelCurrentRunAsync`/Stop landing mid-write, or an external `ResolveToolCallAsync` request's own cancellation) could make the method throw `OperationCanceledException` while the durable write still completed in the background — leaving `MultiTurnAgentLoop.ExecuteAndPublishToolCallAsync` to unwind its `_deferred` registration (and skip the in-memory placeholder append) for a write that landed anyway, or leaving `ResolveToolCallAsync` to skip publishing the resolved message and cleaning up `_deferred`/scheduling auto-resume while the durable replacement still completed — orphaning the canonical store relative to live/`_deferred` state. Both methods now check `ct` only *before* enqueueing (the point of no return); once enqueued, they await the queued node unconditionally, so a caller-token cancellation firing after that point can no longer abort or short-circuit the append/publish-and-cleanup step that follows. Cancellation before enqueue is still honored immediately, and a genuine store failure still propagates and still lets `AddDeferredToHistoryAsync`'s caller unwind `_deferred`. Default (best-effort) mode is unaffected. (WI #194 tasks 7-8 follow-up)

### Changed

- **LmWorkflow (breaking — coordinated API changes for the controller-isolation invariant; warrants a minor/major version bump at release)** (#179):
  - `WorkflowRuntime` and `WorkflowToolProvider` constructors are now `internal` (were `public`) so the workflow-authoring/mutation tools stay confined to a controller loop. External consumers that instantiated these types must go through `WorkflowSession`/`WorkflowManager`. A public compatibility shim is intentionally *not* provided — it would reopen the isolation boundary this change exists to enforce.
  - `WorkflowSession.StartAsync` gained optional `includeAuthoringTool` / `controllerMaxTurnsPerRun` / `controllerDefaultOptions` parameters (before the trailing `CancellationToken`), a binary-signature change. Source callers using named/defaulted args are unaffected; compiled callers should recompile against this release.

---

## [1.0.33] - 2026-05-23

### Added

- Copilot CLI: project `--disable-mcp-server` / `--disable-builtin-mcps` from `CopilotSdkOptions` (#60)

### Fixed

- Copilot CLI: route MCP servers via `--additional-mcp-config` file instead of inline args (#61)
- Copilot CLI: trim disabled MCP server names in `BuildCliArguments`

---

## [1.0.0] - 2025-08-01

### Added

- Initial release of LmDotnetTools NuGet packages
- **AchieveAi.LmDotnetTools.LmCore** - Core functionality and base classes
- **AchieveAi.LmDotnetTools.LmConfig** - Configuration management library
- **AchieveAi.LmDotnetTools.LmEmbeddings** - Embeddings support for language models
- **AchieveAi.LmDotnetTools.AnthropicProvider** - Anthropic AI provider integration
- **AchieveAi.LmDotnetTools.OpenAIProvider** - OpenAI provider integration
- **AchieveAi.LmDotnetTools.McpMiddleware** - Model Context Protocol middleware
- **AchieveAi.LmDotnetTools.McpSampleServer** - Sample MCP server implementation
- **AchieveAi.LmDotnetTools.Misc** - Miscellaneous utilities and helpers

### Features

- Multi-provider support for OpenAI, Anthropic, and OpenRouter
- Streaming and synchronous request/response patterns
- Extensible middleware pipeline
- Type-safe models and responses
- Performance optimized for high-throughput scenarios
- Comprehensive testing utilities
- OpenRouter usage tracking with automatic cost monitoring
- Function calling support
- Structured logging and telemetry

### Documentation

- Complete README with quick start guide
- OpenRouter usage tracking documentation
- Component-specific documentation for each package
- Testing utilities documentation

---

## Version Update Instructions

To update the version for all packages:

1. **Edit Directory.Build.props** and update only these values:

   ```xml
   <MajorVersion>1</MajorVersion>
   <MinorVersion>0</MinorVersion>
   <PatchVersion>0</PatchVersion>
   <PreReleaseLabel></PreReleaseLabel> <!-- Use 'alpha', 'beta', 'rc.1' or leave empty -->
   ```

2. **Update this CHANGELOG.md** with new version information

3. **Run the build and publish script**:

   ```bash
   .\update-version.ps1 -NewVersion "1.1.0"
   ```

### Version Number Guidelines

- **Major Version**: Breaking changes, incompatible API changes
- **Minor Version**: New features, backward compatible
- **Patch Version**: Bug fixes, backward compatible
- **Pre-release**: Use for alpha, beta, or release candidates

### Examples

- `1.0.0` - Stable release
- `1.1.0-alpha` - Alpha pre-release
- `1.1.0-beta.2` - Second beta release
- `1.1.0-rc.1` - Release candidate
