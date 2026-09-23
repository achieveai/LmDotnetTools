// mode-switch-keeps-subagent.mjs — single-call Playwright verification that a mode or model switch on
// the API-backed provider arm is served ON THE LIVE LOOP, so the conversation's running sub-agents are
// never torn down.
//
// This is the top-of-pyramid check for the in-place switch. Its sibling script
// subagent-hierarchy-survives-reset.mjs asks a WEAKER question — that a FINISHED child stays VISIBLE
// across a switch — which the recovery scan can answer even when the manager was destroyed. Here the
// child is deliberately still RUNNING (parked on a 10-minute timer), so a switch that recreated the
// agent would dispose its SubAgentManager and stamp the child `host_shutdown`. "Still listed" is
// therefore not enough: the bar is the SAME agentId, still Running, after each switch.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/mode-switch-keeps-subagent.mjs" })
//
// Prompt: PromptExamples.md -> "Backgrounded sub-agent parked long enough to outlive a mode/model
// switch". Uses MOCK providers only (test-anthropic / test): the Wait tool family is wired for the
// test-mode providers, and no real credential or network is involved.
//
// Returns { pass, failures, steps }.
async (page) => {
  const BASE = 'http://localhost:5273/dist/';
  const PROVIDER_A = 'test-anthropic';
  // The second API-arm provider. Crossing to a CLI arm (claude-mock, codex-mock, ...) is a different
  // case on purpose: those branches cannot be served by reassignment and still recreate.
  const PROVIDER_B = 'test';
  const MODE_OTHER = 'math-helper';
  const CHILD_NAME = 'switch-survivor';

  const childChain = JSON.stringify({
    instruction_chain: [
      {
        id: 'survivor-arm',
        messages: [
          {
            tool_call: [
              {
                name: 'Wait',
                args: {
                  kind: 'timer',
                  args: { delay: '10m' },
                  timeout: '20m',
                  label: 'switch-survivor-park',
                },
              },
            ],
          },
        ],
      },
      { id: 'survivor-done', messages: [{ text: 'switch-survivor resumed after its wait' }] },
    ],
  });
  const SPAWN_PROMPT =
    `<|instruction_start|>${JSON.stringify({
      instruction_chain: [
        {
          id: 'spawn-survivor',
          id_message: 'Background-spawn switch-survivor parked on a 10m wait',
          messages: [
            {
              tool_call: [
                {
                  name: 'Agent',
                  args: {
                    subagent_type: 'general-purpose',
                    name: CHILD_NAME,
                    run_in_background: true,
                    prompt: `<|instruction_start|>${childChain}<|instruction_end|>`,
                  },
                },
              ],
            },
          ],
        },
        {
          id: 'parent-ack',
          id_message: 'Parent continues while switch-survivor is parked',
          messages: [{ text: 'switch-survivor spawned in the background and parked on its wait.' }],
        },
      ],
    })}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 60000 });
  };
  const threadId = () =>
    page.evaluate(
      () =>
        document.querySelector('[data-testid=conversation-item]')?.getAttribute('data-thread-id') ?? null
    );
  // The rows as the hierarchy endpoint reports them, normalised over the two casings the payload has
  // carried, so the assertions below read one shape.
  const subAgents = (id) =>
    page.evaluate(async (t) => {
      const res = await fetch(`/api/conversations/${t}/subagents`);
      if (!res.ok) return null;
      const body = await res.json();
      const list = Array.isArray(body) ? body : body.subAgents || body.subagents || [];
      return list.map((s) => ({
        agentId: s.agentId ?? s.AgentId,
        name: s.name ?? s.Name,
        status: String(s.status ?? s.Status ?? ''),
      }));
    }, id);
  const pollForRunningChild = async (id, timeoutMs, agentId) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await subAgents(id);
      const hit = (last || []).find((s) => (agentId ? s.agentId === agentId : s.name === CHILD_NAME));
      if (hit && /running/i.test(hit.status)) return { rows: last, hit };
      await page.waitForTimeout(500);
    }
    return { rows: last, hit: null };
  };

  try {
    // 0. Fresh chat on the first API-arm mock provider.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.locator('[data-testid="sidebar-new-chat"]').click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_A}`).click();

    // 1. Spawn the background child and let the PARENT's run finish. The child stays parked, so the
    //    conversation is idle — which is what makes the switches below legal in the first place.
    await tid('chat-input-textarea').fill(SPAWN_PROMPT);
    await tid('send-button').click();
    await waitIdle();
    const id = await threadId();

    const spawned = await pollForRunningChild(id, 30000, null);
    const childId = spawned.hit?.agentId ?? null;
    record('child is spawned and RUNNING before any switch', !!childId, {
      rows: spawned.rows,
    });

    // 2. MODE switch while idle. In place, so the SubAgentManager and its parked child are untouched.
    await tid('mode-selector-button').click();
    await tid(`mode-option-${MODE_OTHER}`).click();
    const afterMode = await pollForRunningChild(id, 30000, childId);
    record('the SAME child is still RUNNING after a MODE switch', !!afterMode.hit, {
      childId,
      rows: afterMode.rows,
    });

    // 3. MODEL switch while idle, between two providers that are both on the API arm.
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_B}`).click();
    const afterProvider = await pollForRunningChild(id, 30000, childId);
    record('the SAME child is still RUNNING after a MODEL switch', !!afterProvider.hit, {
      childId,
      rows: afterProvider.rows,
    });

    // 4. The sharpest single fact: `host_shutdown` is disposal's stamp, so seeing it anywhere here
    //    would mean the switch tore the manager down even if a recovery scan re-listed the row.
    //    A null listing (non-OK fetch) or a listing without the child would make `every` pass
    //    vacuously, so both are failures in their own right.
    const finalRows = await subAgents(id);
    const childListed = Array.isArray(finalRows) && finalRows.some((s) => s.agentId === childId);
    record(
      'no child was ever stamped host_shutdown',
      childListed && finalRows.every((s) => !/host_shutdown/i.test(s.status)),
      { finalRows, childListed }
    );
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
