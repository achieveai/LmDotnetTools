// subagent-tabs.mjs — single-call Playwright smoke check for sub-agent CENTER-PANE TABS.
// Runs the WHOLE flow in ONE call and returns structured JSON:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/subagent-tabs.mjs" })
//
// Returns { pass, failures, steps }. Assert only DETERMINISTIC, browser-observable state.
//
// A parent BACKGROUND-spawns two named sub-agents (alpha, beta) via the Agent tool; each nested chain
// (built with JSON.stringify so the escaping rule is handled automatically — inner tags stay literal,
// inner quotes get escaped) drives that child and persists its transcript. The feature under test:
//   1. a `main` tab plus one colored tab per sub-agent appears in the center tab strip (via the poll)
//   2. the two sub-agent tabs get DISTINCT assigned colors (dots differ)
//   3. selecting a tab switches the center pane to that child's persisted transcript
//   4. the parent's inline Agent-call pills are tinted (colored left border) to match their tab
//   5. selecting `main` returns to the parent conversation (sub-agent view unmounts)
//
// Prompt format = PromptExamples.md → "Sub-Agent Delegation" + "Multiple sub-agents → colored tabs".
// Uses the MOCK provider `test-anthropic`. BASE: this repo's .env sets ASPNETCORE_URLS=:5055 (default
// sample port is :5000) — adjust if yours differs.
async (page) => {
  const BASE = 'http://localhost:5055';
  const PROVIDER = 'test-anthropic';
  const SHOT_DIR = 'B:/sources/LmDotnetTools/.worktrees/WT3/.logs/manual';

  // Build the parent prompt programmatically so the nested-chain escaping is correct by construction.
  const innerAlpha = JSON.stringify({
    instruction_chain: [
      { id: 'a1', messages: [{ text: 'Alpha reporting: I found three fresh AI papers today.' }] },
    ],
  });
  const innerBeta = JSON.stringify({
    instruction_chain: [
      { id: 'b1', messages: [{ tool_call: [{ name: 'calculate', args: { a: 40, operation: 'add', b: 2 } }] }] },
      { id: 'b2', messages: [{ text: 'Beta reporting: 40 + 2 = 42.' }] },
    ],
  });
  const parent = {
    instruction_chain: [
      {
        id: 'spawn-two',
        id_message: 'Spawn two background workers',
        messages: [
          {
            tool_call: [
              { name: 'Agent', args: { subagent_type: 'researcher', name: 'alpha', run_in_background: true, prompt: `<|instruction_start|>${innerAlpha}<|instruction_end|>` } },
              { name: 'Agent', args: { subagent_type: 'general-purpose', name: 'beta', run_in_background: true, prompt: `<|instruction_start|>${innerBeta}<|instruction_end|>` } },
            ],
          },
        ],
      },
      { id: 'parent-done', id_message: 'Wrap up', messages: [{ text: 'Spawned alpha and beta in the background.' }] },
    ],
  };
  const PROMPT = `<|instruction_start|>${JSON.stringify(parent)}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const shot = (name) => page.screenshot({ path: `${SHOT_DIR}/${name}.png` }).catch(() => {});
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const subTabs = () => page.locator('[data-testid="conversation-tab"]:not([data-tab-id="main"])');
  // computed dot color for a sub-agent tab, keyed by label text
  const dotColorByLabel = (label) =>
    page.evaluate((lbl) => {
      const tabs = [...document.querySelectorAll('[data-testid="conversation-tab"]')];
      const tab = tabs.find((t) => t.textContent.trim().includes(lbl));
      const dot = tab?.querySelector('.conversation-tab__dot');
      return dot ? getComputedStyle(dot).backgroundColor : null;
    }, label);

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // Fresh chat on the mock provider (General Assistant is the default mode → Agent + calculate wired).
    // Clear first so this box is isolated from any prior/concurrent conversation content on this dev host.
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('clear-button').click().catch(() => {});
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();

    // 1. Spawn two background sub-agents; wait for the run to settle.
    await send(PROMPT);
    await waitIdle();

    // 2. Two sub-agent tabs appear (poll every 3s) alongside `main`.
    await subTabs().nth(1).waitFor({ state: 'attached', timeout: 20000 });
    const labels = (await subTabs().allInnerTexts()).map((t) => t.trim());
    record('two sub-agent tabs appear (alpha, beta)',
      labels.length === 2 && labels.some((l) => l.includes('alpha')) && labels.some((l) => l.includes('beta')),
      { labels });
    await shot('01-tabs-on-main');

    // 3. The two tabs get DISTINCT assigned colors.
    const [alphaDot, betaDot] = [await dotColorByLabel('alpha'), await dotColorByLabel('beta')];
    record('sub-agent tabs have distinct colors', !!alphaDot && !!betaDot && alphaDot !== betaDot, { alphaDot, betaDot });

    // 4. The two sub-agent tab colors each appear as an inline Agent-call pill border in the parent
    //    conversation — proving the inline calls are tinted to MATCH their tabs. (Robust to any stale
    //    uncolored Agent pills from unrelated conversation content on a shared dev host.)
    const pillBorders = await page.evaluate(() =>
      [...document.querySelectorAll('[data-testid="main-view"] [data-testid="tool-call-pill"][data-tool-name="Agent"]')]
        .map((p) => getComputedStyle(p).borderLeftColor));
    record('inline Agent pills tinted to match tabs',
      pillBorders.includes(alphaDot) && pillBorders.includes(betaDot),
      { pillBorders, alphaDot, betaDot });

    // 5. Selecting the alpha tab shows alpha's persisted transcript (exact match so the sub-agent's
    //    raw user-prompt <p> — which also contains this phrase — doesn't create a strict-mode clash).
    await subTabs().filter({ hasText: 'alpha' }).first().click();
    await tid('subagent-view').waitFor({ state: 'visible', timeout: 10000 });
    await tid('subagent-transcript')
      .getByText('Alpha reporting: I found three fresh AI papers today.', { exact: true })
      .first()
      .waitFor({ timeout: 20000 });
    record('alpha tab shows alpha transcript', true);
    await shot('02-alpha-tab');

    // 6. Selecting the beta tab swaps to beta's transcript (calculate → text).
    await subTabs().filter({ hasText: 'beta' }).first().click();
    await tid('subagent-transcript')
      .getByText('Beta reporting: 40 + 2 = 42.', { exact: true })
      .first()
      .waitFor({ timeout: 20000 });
    record('beta tab shows beta transcript', true);
    await shot('03-beta-tab');

    // 7. Back to main: the parent conversation is shown; the sub-agent view unmounts.
    await page.locator('[data-testid="conversation-tab"][data-tab-id="main"]').click();
    await tid('main-view').waitFor({ state: 'visible', timeout: 10000 });
    const subViewCount = await tid('subagent-view').count();
    record('main tab returns to parent (sub-agent view unmounts)', subViewCount === 0, { subViewCount });
    await shot('04-back-to-main');
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot('99-error');
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
