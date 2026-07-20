// usage-banner.mjs — single-call Playwright smoke check for the conversation-wide token-usage banner
// (#196). Runs the WHOLE flow in ONE call and returns structured JSON:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/usage-banner.mjs" })
//
// Returns { pass, failures, steps }. Assert only DETERMINISTIC, browser-observable state. Uses the
// MOCK provider `test-anthropic`, whose scripted SSE emits a fixed 100 input / 50 output tokens per
// generation (AnthropicSseStreamHttpContent), so token totals are exact.
//
// Scenarios (prompts from PromptExamples.md → "Usage banner (#196) UI tests"):
//   1. single turn  -> banner "Total: 150 | In: 100 | Out: 50"
//   2. second turn  -> accumulates to "Total: 300 | In: 200 | Out: 100" (additive, not max'd)
//   3. reload       -> banner restored to Total 300 from the persisted aggregate (#196 persistence)
//   4. sub-agent    -> a parent->Agent->sub-agent delegation folds the descendant's tokens into the
//                      SAME banner total (> the 300 the two parent turns alone produce)
//
// Prereq: app running with the built SPA (non-Development serves wwwroot/dist). Adjust BASE to match.
async (page) => {
  const BASE = 'http://localhost:5000';
  const PROVIDER = 'test-anthropic';
  const SIMPLE =
    '<|instruction_start|>{"instruction_chain":[{"id":"u1","id_message":"Reply","messages":[{"text_message":{"length":20}}]}]}<|instruction_end|>';
  // Parent delegates via the Agent tool; the nested chain drives the sub-agent (calculate then text).
  const SUBAGENT =
    '<|instruction_start|>{"instruction_chain":[{"id":"parent","id_message":"Delegate to sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\\"instruction_chain\\":[{\\"id\\":\\"sub-tool\\",\\"messages\\":[{\\"tool_call\\":[{\\"name\\":\\"calculate\\",\\"args\\":{\\"a\\":2,\\"operation\\":\\"add\\",\\"b\\":3}}]}]},{\\"id\\":\\"sub-text\\",\\"messages\\":[{\\"text\\":\\"hi from agent\\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent2","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent finished."}]}]}<|instruction_end|>';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const bannerText = async () => (await tid('usage-banner').textContent().catch(() => null)) ?? '';
  const waitBanner = async (needle, timeout = 20000) => {
    const deadline = Date.now() + timeout;
    let text = '';
    while (Date.now() < deadline) {
      text = await bannerText();
      if (text.includes(needle)) return text;
      await new Promise((r) => setTimeout(r, 200));
    }
    return text;
  };
  const totalOf = (t) => parseInt((t.match(/Total:\s*(\d+)/) || [])[1] || '0', 10);
  const newChat = async () => {
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();
  };

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 1. Single turn -> Total 150 | In 100 | Out 50.
    await newChat();
    await send(SIMPLE);
    await waitIdle();
    const b1 = await waitBanner('Total: 150');
    record(
      'single-turn 150/100/50',
      /Total:\s*150/.test(b1) && /In:\s*100/.test(b1) && /Out:\s*50/.test(b1),
      b1
    );

    // 2. Second turn accumulates -> Total 300 | In 200 | Out 100.
    await send(SIMPLE);
    await waitIdle();
    const b2 = await waitBanner('Total: 300');
    record(
      'two-turn accumulate 300/200/100',
      /Total:\s*300/.test(b2) && /In:\s*200/.test(b2) && /Out:\s*100/.test(b2),
      b2
    );

    // 3. Reload -> banner restored from the persisted aggregate.
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    const b3 = await waitBanner('Total: 300');
    record('reload persists 300', /Total:\s*300/.test(b3), b3);

    // 4. Sub-agent delegation -> the descendant's tokens fold into the PERSISTED aggregate (#196
    //    "visible on reopen"). The live banner reflects only usage STREAMED to the client (parent =
    //    300); the sub-agent's usage is relayed server-side into the root ledger, so it shows up in
    //    the REST aggregate and on reload — which must exceed the 300 the two parent turns produce.
    await newChat();
    await send(SUBAGENT);
    await waitIdle();
    const liveTotal = totalOf(await bannerText());
    const threadId = await page.evaluate(
      () => document.querySelector('[data-testid=conversation-item]')?.getAttribute('data-thread-id') ?? null
    );
    const agg = await page.evaluate(async (id) => {
      const r = await fetch(`/api/conversations/${id}/usage`);
      return r.ok ? await r.json() : { status: r.status };
    }, threadId);
    // Reload -> banner restored from the persisted aggregate (includes the descendant).
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    const reloadedTotal = totalOf(await waitBanner('Total:', 20000));
    record(
      'sub-agent folds descendant tokens into persisted aggregate (>300)',
      (agg?.totalTokens ?? 0) > 300 && reloadedTotal > 300,
      { liveTotal, aggregateTotal: agg?.totalTokens, reloadedTotal, agg }
    );
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
