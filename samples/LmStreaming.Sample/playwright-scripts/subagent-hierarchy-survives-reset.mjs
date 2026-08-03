// subagent-hierarchy-survives-reset.mjs — single-call Playwright verification for PR #245's
// SubAgentScanCoverageCache owner/generation-keyed invalidation, run against the REAL app (mock
// providers, no live LLM). Proves the sub-agent hierarchy view (GET /api/conversations/{id}/subagents,
// surfaced as center-pane tabs) keeps showing a previously-spawned child across BOTH kinds of live
// SubAgentManager reset the review called out:
//   1. a PROVIDER switch while idle (ConversationsController.SwitchProvider -> MultiTurnAgentPool
//      recreates the agent -> brand-new SubAgentManager instance)
//   2. a MODE switch while idle (ConversationsController.SwitchMode -> same recreate path)
// each of which is itself exercised TWICE (two consecutive reset cycles), matching the "two
// manager-reset cycles" RED->GREEN scenario from AgentHierarchyServiceTests. Without owner-keying,
// the cold-path scan gate (SubAgentManager.ListAgents().Count == 0) would still recover the child on
// the FIRST poll after a reset (a fresh manager starts empty), so this script's real bar is that the
// child is not just present once but SURVIVES repeated resets without ever disappearing.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/subagent-hierarchy-survives-reset.mjs" })
//
// Returns { pass, failures, steps }. Uses MOCK providers only: test-anthropic / claude-mock.
async (page) => {
  const BASE = 'http://localhost:5273/dist/';
  const PROVIDER_A = 'test-anthropic';
  const PROVIDER_B = 'claude-mock';
  const MODE_DEFAULT = 'default';
  const MODE_OTHER = 'medical-knowledge';

  const inner = JSON.stringify({
    instruction_chain: [
      { id: 'c1', messages: [{ text: 'Child reporting: task complete.' }] },
    ],
  });
  const SPAWN_PROMPT =
    `<|instruction_start|>${JSON.stringify({
      instruction_chain: [
        {
          id: 'spawn-one',
          id_message: 'Spawn one background worker',
          messages: [
            {
              tool_call: [
                {
                  name: 'Agent',
                  args: {
                    subagent_type: 'general-purpose',
                    name: 'persistent-child',
                    run_in_background: true,
                    prompt: `<|instruction_start|>${inner}<|instruction_end|>`,
                  },
                },
              ],
            },
          ],
        },
        { id: 'done', id_message: 'Wrap up', messages: [{ text: 'Spawned persistent-child.' }] },
      ],
    })}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 60000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const threadId = () =>
    page.evaluate(() =>
      document.querySelector('[data-testid=conversation-item]')?.getAttribute('data-thread-id') ?? null
    );
  const subAgentIds = (id) =>
    page.evaluate(async (t) => {
      const res = await fetch(`/api/conversations/${t}/subagents`);
      if (!res.ok) return null;
      const body = await res.json();
      const list = Array.isArray(body) ? body : body.subAgents || body.subagents || [];
      return list.map((s) => s.agentId ?? s.AgentId);
    }, id);
  // childId is null until the FIRST successful poll discovers the spawned child's real agentId (a
  // generated id, not the "persistent-child" display name) — every later poll then looks for that
  // EXACT id, so an owner-mismatch cache bug that silently swaps in a DIFFERENT (re-scanned, but
  // wrongly-keyed) roster would be caught, not just "some row present".
  const pollForChild = async (id, timeoutMs, childId) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await subAgentIds(id);
      if (last && last.length > 0 && (childId ? last.includes(childId) : true)) return last;
      await page.waitForTimeout(500);
    }
    return last;
  };

  try {
    // 0. Fresh chat, default mode, mock provider A.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_A}`).click();

    // 1. Spawn the background child and wait for it to complete + show up in the hierarchy.
    await send(SPAWN_PROMPT);
    await waitIdle();
    const id = await threadId();
    const afterSpawn = await pollForChild(id, 20000, null);
    const childId = afterSpawn?.[0] ?? null;
    record('child appears in hierarchy right after spawn', !!childId, { afterSpawn });

    // 2. PROVIDER switch #1 (idle) -> new SubAgentManager instance (owner reset #1).
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_B}`).click();
    const afterProviderSwitch1 = await pollForChild(id, 20000, childId);
    record('child survives PROVIDER switch #1', !!afterProviderSwitch1?.includes(childId), { afterProviderSwitch1, childId });

    // 3. PROVIDER switch #2, back to A (idle) -> new SubAgentManager instance again (owner reset #2 —
    //    proves the fix covers MULTIPLE consecutive resets, not just a single one-shot invalidation).
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_A}`).click();
    const afterProviderSwitch2 = await pollForChild(id, 20000, childId);
    record('child survives PROVIDER switch #2 (second reset cycle)', !!afterProviderSwitch2?.includes(childId), { afterProviderSwitch2, childId });

    // 4. MODE switch (idle) -> the OTHER manager-reset trigger the review called out.
    await tid('mode-selector-button').click();
    await tid(`mode-option-${MODE_OTHER}`).click();
    const afterModeSwitch1 = await pollForChild(id, 20000, childId);
    record('child survives MODE switch #1', !!afterModeSwitch1?.includes(childId), { afterModeSwitch1, childId });

    // 5. MODE switch back to default (idle) -> second mode-triggered reset cycle.
    await tid('mode-selector-button').click();
    await tid(`mode-option-${MODE_DEFAULT}`).click();
    const afterModeSwitch2 = await pollForChild(id, 20000, childId);
    record('child survives MODE switch #2 (second reset cycle)', !!afterModeSwitch2?.includes(childId), { afterModeSwitch2, childId });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
