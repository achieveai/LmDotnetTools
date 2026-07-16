// workspace-agent-sandbox-repro.mjs — single-call repro for the "Workspace Agent" mode session
// creation failure against a REAL sandbox gateway + REAL Copilot provider (no mocks). Run with:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/workspace-agent-sandbox-repro.mjs" })
//
// This is a diagnostic script (not a CI regression — it depends on live external services: the
// sandbox gateway + Copilot). It navigates a fresh chat, selects the "LmDotnetTools" workspace,
// the "Claude Sonnet 5 (Copilot)" provider, and "Workspace Agent" mode, sends the given prompt,
// and reports whatever terminal state the UI reaches (assistant text vs error banner) plus the
// exact error-banner text so the sandbox_unavailable failure body can be inspected.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'claude-sonnet-5'; // "Claude Sonnet 5 (Copilot)"
  const MODE_ID = 'workspace-agent'; // "Workspace Agent"
  const PROMPT =
    "Can you initialize .git directory as `bare` repository for `https://github.com/achieveai/LmDotnetTools` and also create 'worktrees/master' worktree pointing to master branch. Also make sure CLAUDE.md is there and the structure follows what's expected there.";

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
    record('workspace-selected (LmDotnetTools)', /LmDotnetTools/.test(wsLabel ?? ''), wsLabel);

    // Provider
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_ID}`).click();
    const providerLabel = await tid('provider-selector-button').textContent();
    record('provider-selected (Claude Sonnet 5)', /Claude Sonnet 5/.test(providerLabel ?? ''), providerLabel);

    // Mode
    await tid('mode-selector-button').click();
    await tid(`mode-option-${MODE_ID}`).click();
    const modeLabel = await tid('mode-selector-button').textContent();
    record('mode-selected (Workspace Agent)', /Workspace Agent/.test(modeLabel ?? ''), modeLabel);

    // Send the prompt
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

    const errorBannerText = (await tid('error-banner').count()) > 0 ? await tid('error-banner').textContent() : null;
    const assistantText =
      (await tid('assistant-text').count()) > 0 ? await tid('assistant-text').first().textContent() : null;

    record('reached-terminal-state', result !== 'timeout', { result, errorBannerText, assistantText });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
