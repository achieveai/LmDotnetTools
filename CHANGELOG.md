# Changelog

All notable changes to the LmDotnetTools project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Sandbox**: New `AchieveAi.LmDotnetTools.Sandbox` package (`net8.0`/`net9.0`, no ASP.NET/provider dependencies) — a credential-scoped `SandboxClient` for the sandbox gateway's control plane: authenticated create/get/list/delete lifecycle, marketplace preview, and session discovery; command execution (`ExecuteAsync`) over the gateway's direct operations API (`POST`/`GET .../operations`, with gateway-owned exact-byte stdout/stderr artifacts downloaded through the files API) — idempotency/recovery is gateway-scoped (a same-operation-id resubmit is answered from the gateway's retained state) and NOT durable across a gateway restart; and exact UTF-8 file transfer over the direct files/directories API (`ReadTextFileAsync`/`WriteTextFileAsync` = `GET`/`PUT .../files/{mount_id}`, `ListDirectoryAsync` = paginated `GET .../directories/{mount_id}`). Ships with owned or borrowed `HttpClient` transport, stable `SandboxErrorKind`/`SandboxException` error classification, secret-redacted `SandboxClientOptions`, and an `AUTH_ENFORCE=true` live-gateway contract CI job. (#187, #188, #189, #190, #197)
- **LmWorkflow**: `StartWorkflow`/`CheckWorkflow`/`WaitWorkflow` agent-facing tools (`StartWorkflowToolProvider`) that launch a pre-authored `WorkflowDefinition` on an isolated controller loop via the new `WorkflowManager`, with bounded concurrency and proactive completion notifications (`NotifyKinds.WorkflowCompletion`). (#179)
- **LmMultiTurn**: `SubAgentOptions.NonInheritedToolNames` to exclude specific tools from sub-agent inheritance, and a public `MultiTurnAgentLoop.RegisteredToolNames` accessor.
- **LmMultiTurn**: Per-turn sub-agent spawn suppression — `UserInput.SuppressSubAgentSpawning` asks the run that consumes the input to withhold the `Agent` tool (contract hidden, handler refuses with `spawn_suppressed`), and `MultiTurnAgentLoop` latches it for the whole run, including a run resumed after a deferred tool call and after a host restart (the marker is persisted in thread metadata). Hosts discover the capability through `ISpawnSuppressingAgent` (which now also exposes `EnforcesSpawnSuppression`, so a host can refuse *before* enqueuing) and read `SendReceipt.SpawningSuppressed` as an enforcement statement to fail closed on.

### Changed

- **LmAgentInfra (source-breaking for external implementers)**: `IWorkspaceFileBrowser` gained a fifth member, `ResolveThreadWorkspaceSessionForBackgroundAsync(string threadId, string persistedWorkspaceId, CancellationToken ct = default)`. It is abstract with no default implementation, so any type outside this repository that implements the interface must add it to compile against this release; consumers that only *call* the interface are unaffected. It resolves a thread to its workspace session exactly as `ResolveThreadWorkspaceSessionAsync` does except that it performs no cross-actor provenance comparison, because in-process background work has no caller to compare — presenting `null` there was read as a foreign claim, which silently denied every transcript flush for an S2S-created conversation. **Do not call it from a request-handling path**: a route that reached it would hand any caller the owner's session. It grants no new reach of its own — the gateway call underneath uses the binding's stored credential either way. (#253, ADR 0013)
- **LmMultiTurn (binary-signature change; source-compatible)**: `SendReceipt` gained a fourth positional parameter, `bool SpawningSuppressed = false`. Source callers using the existing three arguments are unaffected, but the generated `Deconstruct` now has four parameters and compiled callers that positionally deconstruct a `SendReceipt` (or bind its primary constructor) must be recompiled against this release. `MultiTurnAgentBase.EnforcesSpawnSuppression` widened from `protected` to `public` for the same reason a host needs it: the capability has to be checkable before an input is queued.
- **LmWorkflow (breaking)**: renamed the `StartWorkflow` agent-facing tool to `StartWorkflowAgent` (`StartWorkflowToolProvider.StartWorkflowToolName`) and sharpened its description to foreground the agent-dispatch framing, so models more reliably choose it over performing the work inline. `CheckWorkflow`/`WaitWorkflow` are unchanged.
- **LmMultiTurn (behaviour) — delayed tool results now resolve as child runs.** A tool result arriving after its run ended previously resumed a single tracked "last deferring run", which collapsed several results into one resume and lost the attribution of which result caused what. Each resolution now gets its own child run caused by the real `ToolCallResultMessage`, never a fabricated user message.
  **Intentional timing, which can read as a missing response:** only the child run that clears the *last* outstanding tool call calls the provider. A request carries the whole history, and a single unfilled placeholder anywhere in it makes that request invalid — so sibling children complete with **zero model turns** and the `awaiting_sibling_results` outcome rather than pretending they finished the work. A caller resolving one of several outstanding calls should therefore expect a completed run with no model output; that is the contract, not a dropped response. (#227, ADR 0004)
- **LmMultiTurn — `ResolveToolCallAsync` behaviour on a store failure.** Resolution is three-phase under one lock (claim, durable write, commit) and the **durable write comes first**, so a store failure leaves the call genuinely unresolved and safe to retry unchanged; the previous ordering could leave an already-mutated history behind a failed write. `ResolveToolCallAsync` keeps its throwing shape and its exact messages. New `TryResolveToolCallAsync` reports the same operation as a `ResolveToolCallOutcome` value instead — `Resolved`/`Duplicate` are success, `NotFound`/`Conflict` are permanent rejections, and `StoreFailed`/`Cancelled` left the call untouched and are safe to redeliver — because the question a webhook receiver actually has is "can I redeliver this?", which an exception does not answer.
  Two defects the rewrite exposed are also fixed: the claimed path applied resolutions unconditionally, overwriting an already-resolved result instead of refusing it; and a history placeholder with no reservation was unresolvable, which is now adopted rather than left wedged.
- **LmWorkflow**: a workflow transition is ordered against the run observer with a publish-order watermark, closing a race where a terminal could render before the transition that produced it was observed. (ADR 0006)
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
