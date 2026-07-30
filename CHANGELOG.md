# Changelog

All notable changes to the LmDotnetTools project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Sandbox**: New `AchieveAi.LmDotnetTools.Sandbox` package (`net8.0`/`net9.0`, no ASP.NET/provider dependencies) — a credential-scoped `SandboxClient` for the sandbox gateway's control plane: authenticated create/get/list/delete lifecycle, marketplace preview, and session discovery; command execution (`ExecuteAsync`) over the gateway's direct operations API (`POST`/`GET .../operations`, with gateway-owned exact-byte stdout/stderr artifacts downloaded through the files API) — idempotency/recovery is gateway-scoped (a same-operation-id resubmit is answered from the gateway's retained state) and NOT durable across a gateway restart; and exact UTF-8 file transfer over the direct files/directories API (`ReadTextFileAsync`/`WriteTextFileAsync` = `GET`/`PUT .../files/{mount_id}`, `ListDirectoryAsync` = paginated `GET .../directories/{mount_id}`). Ships with owned or borrowed `HttpClient` transport, stable `SandboxErrorKind`/`SandboxException` error classification, secret-redacted `SandboxClientOptions`, and an `AUTH_ENFORCE=true` live-gateway contract CI job. (#187, #188, #189, #190, #197)
- **LmWorkflow**: `StartWorkflow`/`CheckWorkflow`/`WaitWorkflow` agent-facing tools (`StartWorkflowToolProvider`) that launch a pre-authored `WorkflowDefinition` on an isolated controller loop via the new `WorkflowManager`, with bounded concurrency and proactive completion notifications (`NotifyKinds.WorkflowCompletion`). (#179)
- **LmMultiTurn**: `SubAgentOptions.NonInheritedToolNames` to exclude specific tools from sub-agent inheritance, and a public `MultiTurnAgentLoop.RegisteredToolNames` accessor.
- **LmLifecycle** (new package, dependency-neutral): a versioned wire contract for agent lifecycle events and tool-approval requests — `LifecycleEventEnvelope`, `LifecycleCorrelation`, `LifecycleVocabulary` (event types and capability names), and the approval request/decision types. Deliberately free of any provider, ASP.NET, or sandbox dependency so a subscriber can reference the contract without referencing the runtime that emits it. (#227, ADR 0002)
- **LmMultiTurn / LmCore**: fail-closed tool approval. Host-executed tool calls can be gated behind an approval decision, and the gate fails **closed** — an approver that errors, times out, or is absent denies the call rather than admitting it. Approval is an overload of `ToolCallExecutor.ExecuteAsync`, so an unapproved path cannot be reached by forgetting a parameter. Arguments are hashed over the exact executed bytes, so a decision cannot be transplanted onto different arguments. (#227, ADR 0003)
- **LmMultiTurn**: run lifecycle observation — `run_started` / `run_completed` from all four multi-turn loops, `context_loaded` from the request that carries it, turn finalization at a provider-neutral seam, and sandbox inventory/session events reported once at their commit point with replacement linkage. Backed by a durable lifecycle store beside the run ledger; exactly-once completion is enforced both in-process and durably, and the in-process decision wins on fault. (#227, ADRs 0004, 0006, 0007)
- **LmAgentInfra**: default-disabled service-to-service lifecycle delivery and remote tool approval. Two independent flags (observation, approval); with both off, no behaviour and no route changes in a host that references the package. Includes a bounded subscription registry with server-derived scope (register / rotate-secret / revoke), capability gates (`lifecycle.content.full`, `tool.approval.decide`), a bounded delivery pipeline with retry and destination-scoped quarantine, and an authenticated async decision endpoint whose status says how far the request got: a decision that settles it — the allow that completes the approver set, or any deny — answers `200 OK` with the outcome that stands, while an allow that is merely recorded because another frozen approver has not answered yet answers `202 Accepted`. Either way the decision is acted on by the waiting run, not applied inline. The first valid decision wins and an identical retry returns the same result. A decision is bound to the approver it was asked of: it counts only when it arrives on the subscription the request was delivered to, so one approver cannot answer on another's behalf, and when several approvers were asked **every** one of them must allow — a single deny is final and the rest are moot. Conversation ownership is read only from a binding's **caller** credential, so interactive conversations have no remote owner and are never delivered off-box. (#227, ADR 0005)
- **LmAgentInfra**: callback egress defences for the delivery client. An allow-listed host name is re-resolved and the address behind it re-validated on **every** connection attempt — loopback, link-local (including cloud metadata), and private space are refused unless explicitly opened — so repointing an admitted name at internal infrastructure does not redirect a signed body there. Redirects are never followed; a 3xx is reported as the subscriber rejecting the delivery. Host names are compared in punycode on both sides of the allow-list, so a destination the quarantine treats as one endpoint cannot look like two to the allow-list. (#227, ADR 0005)
- **LmAgentInfra**: `src/LmAgentInfra/Webhooks/` — raw-body HMAC signing and verification, timestamp window, delivery-replay rejection, and key rotation, extracted from `CodeReviewDaemon.Sample` so the sample and the lifecycle control plane share one implementation. Adds `WebhookRequestSigner` (the outbound half, previously absent).
- **Codex/Copilot transports**: provider tool requests are dispatched off the stdio JSON-RPC read loop through a bounded per-session dispatcher, so the read loop stays responsive while an approval is pending and out-of-order responses stay correlated. (#227, ADR 0008)

### Changed

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
