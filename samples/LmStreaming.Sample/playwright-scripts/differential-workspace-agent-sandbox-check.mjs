// differential-workspace-agent-sandbox-check.mjs — root-cause differential diagnosis for the
// "Sandbox gateway returned 400" failure hit while manually verifying Workflow Author mode
// (plan noble-puzzling-pearl, Verification step 4). Program.cs's sandbox-session-establishment
// block (`sandboxRegistry.GetOrCreateLiveSessionAsync(workspaceRef, credential: callerCredential)`)
// is called IDENTICALLY for `isWorkspaceMode` and `isWorkflowAuthorMode` — no mode-specific
// branching exists in that call. This script sends a trivial message in the UNMODIFIED
// "workspace-agent" mode against the SAME workspace + mock provider, to determine whether the
// gateway rejects session creation for EVERY mode/caller right now (pre-existing/environmental,
// e.g. a stale-session/resource-limit issue on the gateway) versus only for Workflow Author mode
// (which would indicate a genuine Part A regression).
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/differential-workspace-agent-sandbox-check.mjs" })
//
// Leak-avoidance: the error-banner text is the whole point of this diagnostic (it carries the
// gateway status code, not repo content or secrets), so it is returned in full, unlike other
// verification scripts that only assert presence/length for tool-call pills.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'test-anthropic'; // "Test (Anthropic)" mock
  const MODE_ID = 'workspace-agent'; // "Workspace Agent" — UNMODIFIED by this session's Part A/B/C work
  const PROMPT = 'Say hello.';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  async function waitForLabelMatch(getLabel, regex, timeoutMs = 8000, intervalMs = 250) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await getLabel();
      if (regex.test(last ?? '')) return last;
      await page.waitForTimeout(intervalMs);
    }
    return last;
  }

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    const provisioned = await page.evaluate(
      async ({ workspaceId, providerId, modeId }) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ workspaceId, providerId, modeId }),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return res.json();
      },
      { workspaceId: WORKSPACE_ID, providerId: PROVIDER_ID, modeId: MODE_ID }
    );
    const threadId = provisioned && provisioned.threadId;
    record('provisioned-thread', !!threadId, provisioned);

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    const modeLabel = await waitForLabelMatch(
      () => tid('mode-selector-button').textContent(),
      /Workspace Agent/
    );
    record('mode-bound (Workspace Agent)', /Workspace Agent/.test(modeLabel ?? ''), modeLabel);

    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    const result = await Promise.race([
      tid('error-banner')
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'error'),
      tid('assistant-text')
        .first()
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'assistant-text'),
    ]).catch(() => 'timeout');

    const errorBannerText =
      (await tid('error-banner').count()) > 0 ? await tid('error-banner').textContent() : null;
    const assistantText =
      (await tid('assistant-text').count()) > 0 ? await tid('assistant-text').first().textContent() : null;

    record('reached-terminal-state', result !== 'timeout', { result, errorBannerText, assistantText });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
