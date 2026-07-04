// queue-button.mjs — single-call Playwright smoke check for the "Queue button while streaming" feature
// (Bug 4). Runs the WHOLE flow in ONE call, returns { pass, failures, steps }:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/queue-button.mjs" })
//
// Do NOT drive this with snapshot->act->screenshot loops. Deterministic browser-observable states only.
// Prereqs: app running (adjust BASE). Uses the test-anthropic MOCK (no real LLM). The 300-word reply
// keeps the client rendering long enough that the composer stays in the streaming state while we type.
async (page) => {
  const BASE = 'http://localhost:5273';
  const LONG =
    '<|instruction_start|>{"instruction_chain":[{"id":"long-text","id_message":"Long response","messages":[{"text_message":{"length":300}}]}]}<|instruction_end|>';
  const FOLLOW_UP = 'a queued follow-up typed mid-stream';
  const PROVIDER = 'test-anthropic';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitStreaming = () => tid('stop-button').waitFor({ state: 'visible', timeout: 15000 });

  try {
    // 1. Fresh chat on the streaming mock; start a long run.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();
    await tid('chat-input-textarea').fill(LONG);
    await tid('send-button').click();
    await waitStreaming();

    // 2. Streaming + empty box -> red Stop, no Queue.
    const empty = await page.evaluate(() => ({
      hasStop: !!document.querySelector('[data-testid=stop-button]'),
      hasQueue: !!document.querySelector('[data-testid=queue-button]'),
    }));
    record('streaming-empty-box (Stop, no Queue)', empty.hasStop && !empty.hasQueue, empty);

    // 3. Streaming + text typed -> blue Queue replaces Stop.
    await tid('chat-input-textarea').fill(FOLLOW_UP);
    await tid('queue-button').waitFor({ state: 'visible', timeout: 5000 });
    const typed = await page.evaluate(() => ({
      hasQueue: !!document.querySelector('[data-testid=queue-button]'),
      hasStop: !!document.querySelector('[data-testid=stop-button]'),
      queueLabel: document.querySelector('[data-testid=queue-button]')?.textContent?.trim(),
    }));
    record(
      'streaming-with-text (Queue replaces Stop)',
      typed.hasQueue && !typed.hasStop && /queue/i.test(typed.queueLabel ?? ''),
      typed
    );

    // 4. Click Queue -> box clears, message enters the pending "Waiting to send…" list, button reverts
    //    to Stop (the run is still streaming and the box is now empty).
    await tid('queue-button').click();
    await tid('stop-button').waitFor({ state: 'visible', timeout: 5000 });
    const afterQueue = await page.evaluate(() => ({
      textareaEmpty: (document.querySelector('[data-testid=chat-input-textarea]')?.value ?? 'x') === '',
      hasStop: !!document.querySelector('[data-testid=stop-button]'),
      hasQueue: !!document.querySelector('[data-testid=queue-button]'),
      pendingText: [...document.querySelectorAll('.pending-queue')].map((n) => n.textContent.trim()).join(' '),
    }));
    record(
      'click-Queue (box clears, pending, reverts to Stop)',
      afterQueue.textareaEmpty &&
        afterQueue.hasStop &&
        !afterQueue.hasQueue &&
        afterQueue.pendingText.includes('queued follow-up'),
      afterQueue
    );
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
