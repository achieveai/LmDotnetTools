// mock-streaming-and-contrast.mjs — two mock-provider sanity checks in one run:
//   (a) DEFAULT mode + test-anthropic: an InstructionChain prompt that fires TWO tool calls in one
//       message (the "Multiple Tool Calls in One Message" example from PromptExamples.md, used
//       verbatim) renders tool-call pills and the run streams to idle.
//   (b) WORKFLOW-AUTHOR mode + test-anthropic: with the sandbox gateway UP, sending a plain "Say hi"
//       must NOT surface a sandbox-unavailable error-banner and must reach a terminal state (the
//       agent boots with Read/Grep/Skill). This is the positive contrast to the copilot rejection.
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/mock-streaming-and-contrast.mjs" })
//
// Leak-avoidance: tool-call pill results can carry repo content, so only PRESENCE/COUNT is asserted
// for pills; only the (secret-free) error-banner status text is captured in full if one appears.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'test-anthropic'; // "Test (Anthropic)" mock

  // VERBATIM from PromptExamples.md — "Multiple Tool Calls in One Message" (calculator then weather).
  const MULTI_TOOL_PROMPT =
    '<|instruction_start|>{"instruction_chain":[{"id":"multi-tools","id_message":"Using multiple tools","messages":[{"tool_call":[{"name":"calculate","args":{"a":32,"operation":"multiply","b":1.8}},{"name":"get_weather","args":{"location":"Tokyo"}}]}]}]}<|instruction_end|>';

  // A sandbox-unavailable banner would carry one of these phrases; used to distinguish a real
  // sandbox failure from any incidental banner.
  const SANDBOX_ERR_RE = /sandbox|gateway|not wired|unavailable|400/i;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const pill = () => page.locator('[data-testid="tool-call-pill"]');

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

  async function provision(modeId) {
    return page.evaluate(
      async ({ workspaceId, providerId, mode }) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ workspaceId, providerId, modeId: mode }),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return res.json();
      },
      { workspaceId: WORKSPACE_ID, providerId: PROVIDER_ID, mode: modeId }
    );
  }

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // ---- (a) DEFAULT mode: multi-tool instruction chain ----
    {
      const provisioned = await provision('default');
      const threadId = provisioned && provisioned.threadId;
      record('a:provisioned-thread', !!threadId, { threadId });

      await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
      await tid('chat-input-textarea').waitFor({ timeout: 20000 });

      const modeLabel = await waitForLabelMatch(
        () => tid('mode-selector-button').textContent(),
        /Default|General|Assistant/
      );
      record('a:mode-bound', !!modeLabel, modeLabel);

      await tid('chat-input-textarea').fill(MULTI_TOOL_PROMPT);
      await tid('send-button').click();

      // Wait on stream idle (stop hidden + send visible).
      await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 }).catch(() => {});
      await tid('send-button').waitFor({ state: 'visible', timeout: 60000 }).catch(() => {});

      const pillCount = await pill().count();
      record('a:tool-call-pill-present', pillCount > 0, { pillCount });

      const assistantCount = await tid('assistant-text').count();
      let assistantLen = 0;
      if (assistantCount > 0) {
        assistantLen = ((await tid('assistant-text').last().textContent()) || '').trim().length;
      }
      record('a:assistant-text-rendered', assistantCount > 0 && assistantLen > 0, {
        assistantCount,
        assistantLen,
      });

      const aBanner =
        (await tid('error-banner').count()) > 0 ? await tid('error-banner').textContent() : null;
      record('a:no-error-banner', !aBanner, { errorBannerText: aBanner });
    }

    // ---- (b) WORKFLOW-AUTHOR mode: plain "Say hi" must NOT hit a sandbox-unavailable error ----
    {
      const provisioned = await provision('workflow-author');
      const threadId = provisioned && provisioned.threadId;
      record('b:provisioned-thread', !!threadId, { threadId });

      await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
      await tid('chat-input-textarea').waitFor({ timeout: 20000 });

      const modeLabel = await waitForLabelMatch(
        () => tid('mode-selector-button').textContent(),
        /Workflow Author/
      );
      record('b:mode-bound (Workflow Author)', /Workflow Author/.test(modeLabel ?? ''), modeLabel);

      await tid('chat-input-textarea').fill('Say hi');
      await tid('send-button').click();

      const outcome = await Promise.race([
        tid('error-banner').waitFor({ state: 'visible', timeout: 60000 }).then(() => 'error'),
        tid('assistant-text').first().waitFor({ state: 'visible', timeout: 60000 }).then(() => 'assistant-text'),
        pill().first().waitFor({ state: 'visible', timeout: 60000 }).then(() => 'pill'),
      ]).catch(() => 'timeout');

      // Settle to idle to catch any late banner.
      await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 }).catch(() => {});
      await tid('send-button').waitFor({ state: 'visible', timeout: 60000 }).catch(() => {});

      const bBanner =
        (await tid('error-banner').count()) > 0 ? await tid('error-banner').textContent() : null;
      const isSandboxErr = !!(bBanner && SANDBOX_ERR_RE.test(bBanner));
      record('b:no-sandbox-unavailable-error', !isSandboxErr, {
        outcome,
        errorBannerText: bBanner,
        isSandboxErr,
      });

      const reachedTerminal = outcome !== 'timeout';
      record('b:reached-terminal-state', reachedTerminal, { outcome });
    }
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
