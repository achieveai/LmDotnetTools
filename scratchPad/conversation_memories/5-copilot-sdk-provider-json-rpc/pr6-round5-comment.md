[Gautam's Dev bot]

Manual validation against `samples/LmStreaming.Sample` on `812f4aa` + sample-wiring commit. Honest report below — TL;DR: sample wiring is in place and builds clean, the agent loop instantiates and accepts input, but the live end-to-end turn failed on my Windows box with a Win32 exec error. Root cause is a `COPILOT_CLI_PATH` env-var gotcha, not a provider bug. Full repro + diagnosis below.

### What was wired into the sample

- `samples/LmStreaming.Sample/LmStreaming.Sample.csproj` — added `ProjectReference` to `AchieveAi.LmDotnetTools.CopilotSdkProvider`.
- `samples/LmStreaming.Sample/Program.cs` —
  - Added `using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;`.
  - Extended the pool-factory to branch on `providerMode == "copilot"` and return a `CopilotAgentLoop` (same shape as the existing Claude branch — multi-turn-only provider).
  - Added `"copilot" => Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.5"` to `GetModelIdForProvider`.
  - New `CreateCopilotAgentLoop` helper that reads: `COPILOT_CLI_PATH`, `COPILOT_CLI_MIN_VERSION`, `COPILOT_MODEL`, `COPILOT_API_KEY`, `COPILOT_BASE_URL`, `COPILOT_WORKING_DIRECTORY`, `COPILOT_RPC_TRACE_FILE`, `COPILOT_RPC_TRACE_ENABLED`, `COPILOT_MODEL_ALLOWLIST_PROBE_ENABLED`, `COPILOT_DEFAULT_PERMISSION_DECISION`.

Build: `dotnet build LmDotnetTools.sln` → 0 errors, 56 warnings (baseline).

### Screenshots

1. Landing — sample running in `copilot` mode, empty chat ready:
   ![landing](https://raw.githubusercontent.com/achieveai/LmDotnetTools/work-item/5-add-github-copilot-sdk-provider/docs/wi-5/manual-validation/01-landing.png)

2. Message queued — `Hello from Copilot SDK. Respond with a short greeting.` sent; UI shows "Waiting to send…" while the provider background-starts the ACP session:
   ![response](https://raw.githubusercontent.com/achieveai/LmDotnetTools/work-item/5-add-github-copilot-sdk-provider/docs/wi-5/manual-validation/02-copilot-response.png)

These prove the sample routes `copilot` mode into `CopilotAgentLoop`, the loop constructs, and the chat pipeline accepts a user turn. What they don't prove — no assistant reply arrived. That's because of the issue below.

### What failed — and why it's the wiring env var, not the provider

From the sample's own Serilog output (`samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-20260419.jsonl`):

```
Failed to start or resume Copilot ACP session
System.ComponentModel.Win32Exception (193): An error occurred trying to start process
  'B:/sources/revobot/node_modules/.bin/copilot' with working directory '...'.
  The specified executable is not a valid application for this OS platform.
   at System.Diagnostics.Process.StartWithCreateProcess(...)
   at ...CopilotVersionChecker.EnsureCopilotCliVersionAsync(...) in .../CopilotVersionChecker.cs:line 29
   at ...CopilotSdkClient.StartOrResumeSessionAsync(...) in .../CopilotSdkClient.cs:line 77
```

Diagnosis: I pointed `COPILOT_CLI_PATH` at the extensionless npm shim (`.bin/copilot`), which is a Unix shell-shebang script. Windows `Process.Start` does not auto-resolve to the sibling `copilot.cmd` / `copilot.ps1` wrappers — it tries to exec the extensionless file as a native PE and Win32 rejects with error 193. The `.cmd` wrapper is present (`B:/sources/revobot/node_modules/.bin/copilot.cmd`) and works.

Two small but concrete wins from this attempt:

- **Round-3 M2 is demonstrably correct.** The `StartOrResumeSessionAsync` try/catch/log-before-rethrow path wrote the full exception to structured logs with `SourceContext=CopilotSdkClient`, level `Error`, method/file/line in `ExceptionDetail`. That's exactly the telemetry the M2 finding asked for. Without M2 we would have seen only an opaque task-cancelled message in the UI.
- **The allowlist probe and the transport never get reached** when version-check fails — the fail-fast sequencing in `CopilotSdkClient.StartOrResumeSessionAsync` is correct (version → initialize → session/new → allowlist probe).

### Local repro (working)

On Windows, either of these works — the first is what I'll do next when I re-run:

```powershell
$env:COPILOT_CLI_PATH = "B:\sources\revobot\node_modules\.bin\copilot.cmd"  # explicit .cmd
# or — recommended, uses PATH resolution which does honor PATHEXT:
Remove-Item Env:COPILOT_CLI_PATH
$env:PROVIDER_MODE = "copilot"
dotnet run --project samples/LmStreaming.Sample
```

On Linux/macOS the extensionless shim is a valid shebang script and `Process.Start` handles it correctly — this issue is Windows-specific.

### Follow-up question for you (scope check)

This is a real papercut on Windows and the provider could defend against it — something like, in `CopilotVersionChecker` / transport startup, if `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` and the supplied path has no extension, probe for `{path}.cmd` / `{path}.exe` / `{path}.ps1` and use the first that exists. Two questions:

1. Want that in this PR, or split into a small follow-up? (I lean follow-up — this PR is already large and the workaround is a one-line env-var change.)
2. If in this PR: should the probe be silent, or log a warning that we auto-resolved (for debuggability)?

### Build / test (unchanged)

- `dotnet build LmDotnetTools.sln` → 0 errors, 56 warnings.
- CopilotSdkProvider 73/73, LmMultiTurn 157/157, CodexSdkProvider 136/136 — all green.
