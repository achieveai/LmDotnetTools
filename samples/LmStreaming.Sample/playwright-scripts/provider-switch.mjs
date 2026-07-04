// provider-switch.mjs — single-call Playwright smoke check for "switch a conversation's provider
// when idle" (locked while streaming). Runs the WHOLE flow in ONE call and returns structured JSON:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/provider-switch.mjs" })
//
// Returns { pass, failures, steps }. Do NOT drive this with snapshot->act->screenshot loops — that is
// slow and token-heavy. Assert only DETERMINISTIC, browser-observable states here; the exact HTTP
// codes (409 while streaming / 503 unavailable) are covered deterministically by
// tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerTests.cs (a browser race against
// the fast mock is unreliable for those).
//
// Prereqs: app running (adjust BASE). Uses MOCK providers only (no real LLM):
//   A = "test-anthropic" (mock, streams text — client keeps rendering, so "disabled while streaming"
//       has a wide, reliable window)
//   B = "claude-mock"    (mock, completes silently — proves the recreated agent runs a new turn)
// Prompts come from PromptExamples.md → "Manual UI test prompts (conversation UX)".
async (page) => {
  const BASE = 'http://localhost:5273';
  const LONG =
    '<|instruction_start|>{"instruction_chain":[{"id":"long-text","id_message":"Long response","messages":[{"text_message":{"length":300}}]}]}<|instruction_end|>';
  const SHORT =
    '<|instruction_start|>{"instruction_chain":[{"id":"short","id_message":"Short","messages":[{"text_message":{"length":20}}]}]}<|instruction_end|>';
  const PROVIDER_A = 'test-anthropic';
  const PROVIDER_B = 'claude-mock';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitStreaming = () => tid('stop-button').waitFor({ state: 'visible', timeout: 15000 });
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

  try {
    // 1. Fresh chat; pick the streaming mock provider BEFORE the first send.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_A}`).click();

    // 2. Streaming -> the selector is DISABLED and there is NO permanent lock badge (client-side gate).
    await send(LONG);
    await waitStreaming();
    const midDom = await page.evaluate(() => ({
      streaming: !!document.querySelector('[data-testid=stop-button]'),
      providerBtnDisabled: document.querySelector('[data-testid=provider-selector-button]')?.disabled === true,
      hasLockedBadge: !!document.querySelector('[data-testid=provider-locked-badge]'),
    }));
    record(
      'disabled-while-streaming (no lock badge)',
      midDom.streaming && midDom.providerBtnDisabled && !midDom.hasLockedBadge,
      midDom
    );

    // 3. Idle -> the selector is an editable dropdown (no lock badge, button enabled).
    await waitIdle();
    await tid('provider-selector-button').click();
    const idleDom = await page.evaluate(() => ({
      dropdownOpen: !!document.querySelector('.dropdown-menu'),
      hasLockedBadge: !!document.querySelector('[data-testid=provider-locked-badge]'),
      btnDisabled: document.querySelector('[data-testid=provider-selector-button]')?.disabled === true,
    }));
    record(
      'editable-when-idle (dropdown, no badge)',
      idleDom.dropdownOpen && !idleDom.hasLockedBadge && !idleDom.btnDisabled,
      idleDom
    );

    // 4. Switch provider while idle -> persisted in /api/conversations + label updates (POST 200).
    const id = await threadId();
    await tid(`provider-option-${PROVIDER_B}`).click();
    await waitIdle();
    const afterSwitch = await page.evaluate(async (t) => {
      const deadline = Date.now() + 8000;
      let c;
      while (Date.now() < deadline) {
        const list = await (await fetch('/api/conversations')).json();
        c = (list.conversations || list).find((x) => x.threadId === t);
        if (c?.provider === 'claude-mock') break;
        await new Promise((res) => setTimeout(res, 200));
      }
      return {
        persistedProvider: c?.provider,
        label: document.querySelector('[data-testid=provider-selector-button]')?.textContent?.trim(),
      };
    }, id);
    record(
      'switch-when-idle (persisted + label)',
      afterSwitch.persistedProvider === 'claude-mock' && /claude/i.test(afterSwitch.label ?? ''),
      afterSwitch
    );

    // 5. A new turn runs on the recreated (switched) agent — the run reaches idle and the user turn is
    //    recorded (claude-mock completes silently, so we assert completion, not assistant text).
    const userBefore = await tid('user-message-group').count();
    await send(SHORT);
    await waitStreaming().catch(() => {});
    await waitIdle();
    const userAfter = await tid('user-message-group').count();
    record('new-turn-on-switched-provider (completes)', userAfter > userBefore, { userBefore, userAfter });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
