# WI-8 Browser E2E — Per-test Success Screenshots

Captured automatically by `LmStreaming.Sample.Browser.E2E.Tests` at the end of each
passing test (see `ScenarioSession.SaveSuccessScreenshotAsync`). Each PNG is the
final-state full-page snapshot the test asserted against — visual proof reviewers
can scan without re-running the suite.

When the suite runs locally the same PNGs are also written to
`<repo>/.logs/e2e-screenshots/` (git-ignored). The copies in this folder are the
post-merge visual record.

| Test class | Test case | Provider mode | File |
|---|---|---|---|
| `ModeSwitchingTests` | `Scripted_provider_serves_response_for` | `test` | `ModeSwitch.Scripted_provider_serves_response_for_test.png` |
| `ModeSwitchingTests` | `Scripted_provider_serves_response_for` | `test-anthropic` | `ModeSwitch.Scripted_provider_serves_response_for_test-anthropic.png` |
| `ModeSwitchingTests` | `ModeDropdown_switches_server_side_mode` | `test` | `ModeSwitch.ModeDropdown_switches_server_side_mode.png` |
| `MultiTurnConversationTests` | `Two_user_turns_render_text_and_thinking` | `test` | `MultiTurn.Two_user_turns_render_text_and_thinking_test.png` |
| `MultiTurnConversationTests` | `Two_user_turns_render_text_and_thinking` | `test-anthropic` | `MultiTurn.Two_user_turns_render_text_and_thinking_test-anthropic.png` |
| `SubAgentLifecycleTests` | `Parent_spawns_sub_agent_and_renders_result` | `test` | `SubAgent.Parent_spawns_sub_agent_and_renders_result_test.png` |
| `SubAgentLifecycleTests` | `Parent_spawns_sub_agent_and_renders_result` | `test-anthropic` | `SubAgent.Parent_spawns_sub_agent_and_renders_result_test-anthropic.png` |
| `RecursiveInstructionChainTests` | `Parent_and_sub_agent_instruction_chains_render_end_to_end` | `test` | `RecursiveChain.Parent_and_sub_agent_instruction_chains_render_end_to_end_test.png` |
| `RecursiveInstructionChainTests` | `Parent_and_sub_agent_instruction_chains_render_end_to_end` | `test-anthropic` | `RecursiveChain.Parent_and_sub_agent_instruction_chains_render_end_to_end_test-anthropic.png` |
| `ErrorHandlingTests` | `Provider_5xx_renders_error_banner` | `test` | `ErrorHandling.Provider_5xx_renders_error_banner_test.png` |
| `ErrorHandlingTests` | `Provider_5xx_renders_error_banner` | `test-anthropic` | `ErrorHandling.Provider_5xx_renders_error_banner_test-anthropic.png` |
| `CancellationTests` | `Stop_button_terminates_stream_and_restores_idle` | `test` | `Cancellation.Stop_button_terminates_stream_and_restores_idle_test.png` |
| `CancellationTests` | `Stop_button_terminates_stream_and_restores_idle` | `test-anthropic` | `Cancellation.Stop_button_terminates_stream_and_restores_idle_test-anthropic.png` |
