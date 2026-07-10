# Changelog

All notable changes to the LmDotnetTools project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **LmWorkflow**: `StartWorkflow`/`CheckWorkflow`/`WaitWorkflow` agent-facing tools (`StartWorkflowToolProvider`) that launch a pre-authored `WorkflowDefinition` on an isolated controller loop via the new `WorkflowManager`, with bounded concurrency and proactive completion notifications (`NotifyKinds.WorkflowCompletion`). (#179)
- **LmMultiTurn**: `SubAgentOptions.NonInheritedToolNames` to exclude specific tools from sub-agent inheritance, and a public `MultiTurnAgentLoop.RegisteredToolNames` accessor.

### Changed

- **LmWorkflow (breaking, internal API narrowing)**: `WorkflowRuntime` and `WorkflowToolProvider` constructors are now `internal` (were `public`) so the workflow-authoring/mutation tools stay confined to a controller loop. No in-repo callers construct them directly; external consumers of the `AchieveAi.LmDotnetTools.LmWorkflow` package that instantiated these types must go through `WorkflowSession`/`WorkflowManager`. (#179)

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
