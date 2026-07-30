// copilot-workflow-author-rejection.mjs — regression check that the bare `copilot` provider is
// REJECTED up front in "Workflow Author" mode (issue #179 follow-up). Workflow Author boots a
// sandbox agent (Read/Grep/Skill + workflow tools) which requires a provider wired for the sandbox
// (OpenAI/Anthropic-shaped). The bare Copilot CLI provider is not, so the app must surface a clear
// provider-unavailable error rather than silently degrading.
//
// The rejection can surface at TWO points:
//   1. POST /api/conversations returns non-OK (provisioning refuses the provider+mode combo), OR
//   2. provisioning succeeds but the first sent message trips a visible error-banner.
// This script handles both and asserts the surfaced text names the provider-unavailable condition.
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/copilot-workflow-author-rejection.mjs" })
//
// Leak-avoidance: the error text is a provider-availability status message (no secrets / no repo
// content), so returning it in full is the whole point of this diagnostic.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'copilot'; // bare Copilot CLI provider — NOT sandbox-wired
  const MODE_ID = 'workflow-author'; // "Workflow Author"
  const PROMPT = 'Say hi';

  // Matches the expected provider-unavailable phrasing (any one is sufficient).
  const REJECTION_RE = /not wired for the sandbox|provider.?unavailable|unavailable|supports the OpenAI\/Anthropic|OpenAI\/Anthropic|not supported|does not support/i;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  let errorText = null;
  let errorSource = null;

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // Attempt provisioning; capture a non-OK response body instead of throwing.
    const provisioned = await page.evaluate(
      async ({ workspaceId, providerId, modeId }) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ workspaceId, providerId, modeId }),
        });
        const bodyText = await res.text();
        let json = null;
        try {
          json = JSON.parse(bodyText);
        } catch {
          /* non-JSON body */
        }
        return { ok: res.ok, status: res.status, bodyText, json };
      },
      { workspaceId: WORKSPACE_ID, providerId: PROVIDER_ID, modeId: MODE_ID }
    );

    record('provision-response', true, { ok: provisioned.ok, status: provisioned.status });

    if (!provisioned.ok) {
      // Path 1: provisioning itself refused the provider+mode.
      errorText = provisioned.bodyText;
      errorSource = `provision (HTTP ${provisioned.status})`;
      record('error-surfaced-at-provision', true, { status: provisioned.status, errorText });
    } else {
      // Path 2: provisioning succeeded — the error must surface on the first send.
      const threadId = provisioned.json && provisioned.json.threadId;
      record('provisioned-thread', !!threadId, { threadId });

      if (threadId) {
        await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
        await tid('chat-input-textarea').waitFor({ timeout: 20000 });

        await tid('chat-input-textarea').fill(PROMPT);
        await tid('send-button').click();

        const outcome = await Promise.race([
          tid('error-banner')
            .waitFor({ state: 'visible', timeout: 60000 })
            .then(() => 'error'),
          tid('assistant-text')
            .first()
            .waitFor({ state: 'visible', timeout: 60000 })
            .then(() => 'assistant-text'),
        ]).catch(() => 'timeout');

        if ((await tid('error-banner').count()) > 0) {
          errorText = await tid('error-banner').textContent();
          errorSource = 'error-banner';
        }
        record('send-outcome', outcome === 'error', { outcome, hasBanner: !!errorText });
      }
    }

    const surfaced = !!(errorText && errorText.trim());
    const matches = surfaced && REJECTION_RE.test(errorText);
    record('rejection-surfaced-and-matches', surfaced && matches, {
      errorSource,
      surfaced,
      matches,
      errorText,
    });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, errorSource, errorText, steps };
}
