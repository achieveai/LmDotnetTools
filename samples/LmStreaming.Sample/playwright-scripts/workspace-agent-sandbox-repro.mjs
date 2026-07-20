// workspace-agent-sandbox-repro.mjs — single-call repro for the "Workspace Agent" mode session
// creation failure against a REAL sandbox gateway + REAL Copilot provider (no mocks). Run with:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/workspace-agent-sandbox-repro.mjs" })
//
// ⚠ MANUAL-ONLY, LIVE-ENVIRONMENT DIAGNOSTIC — NOT part of the automated test suite and NOT a CI
// regression. It talks to live external services (a real sandbox gateway + a real Copilot provider)
// and, when confirmed, sends a STATE-CHANGING prompt (it asks the agent to clone/init a git repo and
// create a worktree inside the target workspace). Because of that side effect it is SAFE BY DEFAULT:
//
//   * DRY-RUN by default — it navigates, selects workspace/provider/mode, and reports readiness, but
//     does NOT send the prompt. This is the safe way to smoke-test the selection wiring.
//   * To actually send the state-changing prompt you must OPT IN explicitly by setting the env var
//     LMSTREAMING_REPRO_CONFIRM to one of: 1 / true / send / yes.
//
// Everything is parameterizable via environment variables (with the documented defaults below), so the
// script hardcodes no environment-specific workspace UUID / provider / URL:
//   - LMSTREAMING_URL          (default 'http://127.0.0.1:5050')
//   - LMSTREAMING_WORKSPACE_ID (default '192a3465-67a1-4945-9323-44c2168aeb2b' → "LmDotnetTools")
//   - LMSTREAMING_PROVIDER_ID  (default 'claude-sonnet-5' → "Claude Sonnet 5 (Copilot)")
//   - LMSTREAMING_MODE_ID      (default 'workspace-agent' → "Workspace Agent")
//   - LMSTREAMING_REPRO_PROMPT (default the git-init/worktree repro prompt below)
//   - LMSTREAMING_REPRO_CONFIRM (unset ⇒ dry-run; 1/true/send/yes ⇒ actually send the prompt)
async (page) => {
  const env = process.env;
  const BASE = env.LMSTREAMING_URL || 'http://127.0.0.1:5050';
  const WORKSPACE_ID = env.LMSTREAMING_WORKSPACE_ID || '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = env.LMSTREAMING_PROVIDER_ID || 'claude-sonnet-5'; // "Claude Sonnet 5 (Copilot)"
  const MODE_ID = env.LMSTREAMING_MODE_ID || 'workspace-agent'; // "Workspace Agent"
  const PROMPT =
    env.LMSTREAMING_REPRO_PROMPT ||
    "Can you initialize .git directory as `bare` repository for `https://github.com/achieveai/LmDotnetTools` and also create 'worktrees/master' worktree pointing to master branch. Also make sure CLAUDE.md is there and the structure follows what's expected there.";
  const CONFIRM_SEND = /^(1|true|send|yes)$/i.test((env.LMSTREAMING_REPRO_CONFIRM || '').trim());

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();

    // Workspace
    await tid('workspace-selector-button').click();
    await tid(`workspace-option-${WORKSPACE_ID}`).click();
    const wsLabel = await tid('workspace-selector-button').textContent();
    record('workspace-selected', !!(wsLabel && wsLabel.trim()), { workspaceId: WORKSPACE_ID, wsLabel });

    // Provider
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_ID}`).click();
    const providerLabel = await tid('provider-selector-button').textContent();
    record('provider-selected', !!(providerLabel && providerLabel.trim()), { providerId: PROVIDER_ID, providerLabel });

    // Mode
    await tid('mode-selector-button').click();
    await tid(`mode-option-${MODE_ID}`).click();
    const modeLabel = await tid('mode-selector-button').textContent();
    record('mode-selected', !!(modeLabel && modeLabel.trim()), { modeId: MODE_ID, modeLabel });

    // SAFETY GATE: the prompt below is state-changing (it asks the live agent to init a git repo +
    // worktree inside the workspace). Only send it when the caller has explicitly opted in.
    if (!CONFIRM_SEND) {
      record('dry-run-skipped-send', true, {
        note: 'DRY-RUN: selection wiring exercised; state-changing prompt NOT sent. Set LMSTREAMING_REPRO_CONFIRM=1 to send.',
        // Metadata only — the prompt is arbitrary operator-supplied text that could carry repo data or
        // credentials, so report its length, never a content preview.
        promptLength: PROMPT.length,
      });
      const dryFailures = steps.filter((s) => !s.pass).map((s) => s.name);
      return { pass: dryFailures.length === 0, dryRun: true, sent: false, failures: dryFailures, steps };
    }

    // Confirmed: send the state-changing prompt.
    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    // Wait for a terminal state: either an error banner or an assistant answer bubble.
    const result = await Promise.race([
      tid('error-banner')
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'error'),
      tid('assistant-text')
        .first()
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'assistant-text'),
    ]).catch(() => 'timeout');

    // Return only NON-LEAKING metadata about the terminal state. The live agent's assistant answer and
    // error-banner text can contain repository data, credentials, or EUII, so surface presence + length
    // (enough to diagnose "did it answer / error, and roughly how much"), never the content itself.
    const errorBannerCount = await tid('error-banner').count();
    const assistantTextCount = await tid('assistant-text').count();
    const errorBannerLength =
      errorBannerCount > 0 ? ((await tid('error-banner').textContent()) || '').length : 0;
    const assistantTextLength =
      assistantTextCount > 0 ? ((await tid('assistant-text').first().textContent()) || '').length : 0;

    record('reached-terminal-state', result !== 'timeout', {
      result,
      hasError: errorBannerCount > 0,
      hasAssistantText: assistantTextCount > 0,
      errorBannerLength,
      assistantTextLength,
    });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, dryRun: false, sent: CONFIRM_SEND, failures, steps };
}
