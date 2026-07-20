// smoke-test-egress.mjs — single-call live smoke test of the LmStreaming chat app. When an HTTPS/Traefik
// hostname is configured (LMSTREAMING_HTTPS_URL) it is reached through that reverse-proxy first (egress
// path), falling back to the direct Kestrel origin if it fails to load (e.g. self-signed cert not
// trusted in headless mode). When LMSTREAMING_HTTPS_URL is UNSET the HTTPS attempt is skipped and the
// direct Kestrel origin is used, so no environment-specific hostname is baked in. Run with:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/smoke-test-egress.mjs" })
//
// Verifies (in ONE conversation, mock provider only — no real LLM calls):
//   1. The chat UI loads (chat-input-textarea + send-button present).
//   2. A mock/test provider can be selected.
//   3. A basic tool-call instruction-chain prompt (calculate) streams to completion and renders a
//      tool-call pill.
//   4. A second, meaningfully different instruction-chain prompt (multi-turn + reasoning + parallel
//      tool calls) sent in the SAME conversation streams to completion and renders thinking-pills,
//      more tool-call pills, and final assistant text — proving multi-turn context works.
//   5. One screenshot is captured for the record.
//
// Prompts are taken verbatim from PromptExamples.md ("Calculate Tool" and "Multiturn Example").
// Do NOT drive this with snapshot->act->screenshot loops. Assert only DETERMINISTIC, browser-observable
// state (DOM/data-testid).
//
// Portability: nothing here is hardcoded to one machine. Override via environment variables:
//   - LMSTREAMING_HTTPS_URL  (OPTIONAL, no default) — a Traefik/HTTPS host to try first. When unset, the
//     HTTPS attempt is skipped entirely and only the portable Kestrel origin is used.
//   - LMSTREAMING_URL        (default 'http://127.0.0.1:5050/')                — the direct Kestrel origin.
//   - LMSTREAMING_OUTPUT_DIR (default the OS temp dir)                         — where the screenshot lands.
async (page) => {
  const os = await import('node:os');
  const path = await import('node:path');

  const HTTPS_URL = process.env.LMSTREAMING_HTTPS_URL || null;
  const FALLBACK_URL = process.env.LMSTREAMING_URL || 'http://127.0.0.1:5050/';
  const OUTPUT_DIR = process.env.LMSTREAMING_OUTPUT_DIR || os.tmpdir();
  const SCREENSHOT_PATH = path.join(OUTPUT_DIR, 'smoke-test-egress-result.png');
  const PROVIDER = 'test-anthropic';

  // Prompt 1: basic tool-call test (Calculate Tool > Basic addition), verbatim from PromptExamples.md.
  const PROMPT_1 =
    '<|instruction_start|>{"instruction_chain":[{"id":"calc-add","id_message":"Adding numbers","messages":[{"tool_call":[{"name":"calculate","args":{"a":10,"operation":"add","b":5}}]}]}]}<|instruction_end|>';

  // Prompt 2: multiturn example with reasoning + parallel tool calls + text, verbatim from
  // PromptExamples.md ("Multiturn Example (multiple turns with reasoning)").
  const PROMPT_2 =
    '<|instruction_start|>{"instruction_chain":[{"id":"turn1_parallel_tools","reasoning":{"length":30},"messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}},{"name":"get_weather","args":{"location":"San Francisco"}}]}]},{"id":"turn2_final","reasoning":{"length":50},"messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}}]},{"text_message":{"length":50}}]},{"id":"turn3_summary","reasoning":{"length":10},"messages":[{"text_message":{"length":100}}]}]}<|instruction_end|>';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  // The configured URLs are operator-supplied (env vars) and can carry user-info or a query token
  // (EUII). Reduce any URL that reaches diagnostic output to just scheme/host/port — never the path,
  // query, or credentials. Likewise, a browser/navigation exception string embeds the raw URL, so
  // captured error output surfaces only structured metadata (the error name), never the raw exception.
  const sanitizeOrigin = (u) => {
    if (!u) return null;
    try {
      const { protocol, hostname, port } = new URL(u);
      return port ? `${protocol}//${hostname}:${port}` : `${protocol}//${hostname}`;
    } catch {
      return '<unparseable-url>';
    }
  };
  const errName = (e) => (e && e.name) || 'Error';
  const waitStreaming = () => tid('stop-button').waitFor({ state: 'visible', timeout: 15000 });
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const domSnapshot = () =>
    page.evaluate(() => ({
      toolCallPills: [...document.querySelectorAll('[data-testid="tool-call-pill"]')].map(
        (n) => n.getAttribute('data-tool-name')
      ),
      thinkingPillCount: document.querySelectorAll('[data-testid="thinking-pill"]').length,
      assistantTexts: [...document.querySelectorAll('[data-testid="assistant-text"]')].map((n) =>
        n.textContent.trim()
      ),
      errorBanner: document.querySelector('[data-testid="error-banner"]')?.textContent?.trim() ?? null,
    }));

  const consoleErrors = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });
  page.on('pageerror', (err) => consoleErrors.push(String(err)));

  const errors = [];
  let urlUsed = null;
  let pageLoaded = false;
  let providerSelected = null;
  const messagesSent = [];
  const responsesReceived = [];
  let toolCallsRendered = false;

  try {
    // 1. Navigate. When LMSTREAMING_HTTPS_URL is set, prefer that Traefik/HTTPS hostname and fall back
    //    to the direct Kestrel origin on failure. When it is UNSET, skip the HTTPS attempt entirely and
    //    go straight to the portable Kestrel origin — no environment-specific host is hardcoded.
    if (HTTPS_URL) {
      try {
        await page.goto(HTTPS_URL, { waitUntil: 'domcontentloaded', timeout: 20000 });
        await tid('chat-input-textarea').waitFor({ timeout: 15000 });
        urlUsed = sanitizeOrigin(HTTPS_URL);
      } catch (e) {
        errors.push(
          `HTTPS navigation to ${sanitizeOrigin(HTTPS_URL)} failed, falling back to ${sanitizeOrigin(FALLBACK_URL)}: ${errName(e)}`
        );
        // The failed HTTPS attempt races Chromium's async transition to its cert-error interstitial
        // (chrome-error://chromewebdata/); an immediate second goto() can be interrupted by it. Let that
        // settle, then retry the fallback navigation once more if it happens anyway.
        await page.waitForTimeout(500).catch(() => {});
        try {
          await page.goto(FALLBACK_URL, { waitUntil: 'domcontentloaded', timeout: 20000 });
        } catch (e2) {
          errors.push(`Fallback navigation raced the HTTPS interstitial, retrying: ${errName(e2)}`);
          await page.waitForTimeout(500).catch(() => {});
          await page.goto(FALLBACK_URL, { waitUntil: 'domcontentloaded', timeout: 20000 });
        }
        await tid('chat-input-textarea').waitFor({ timeout: 15000 });
        urlUsed = sanitizeOrigin(FALLBACK_URL);
      }
    } else {
      // No HTTPS host configured — go directly to the portable Kestrel origin.
      await page.goto(FALLBACK_URL, { waitUntil: 'domcontentloaded', timeout: 20000 });
      await tid('chat-input-textarea').waitFor({ timeout: 15000 });
      urlUsed = sanitizeOrigin(FALLBACK_URL);
    }

    // 2. Chat UI present.
    const hasTextarea = (await tid('chat-input-textarea').count()) > 0;
    const hasSendButton = (await tid('send-button').count()) > 0;
    pageLoaded = hasTextarea && hasSendButton;
    record('chat-ui-loaded (textarea + send-button)', pageLoaded, { urlUsed, hasTextarea, hasSendButton });

    // 3. Fresh chat; select the mock provider BEFORE the first send.
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();
    const providerLabel = (await tid('provider-selector-button').textContent())?.trim() ?? null;
    providerSelected = /test/i.test(providerLabel ?? '') ? PROVIDER : providerLabel;
    record('provider-selected (test-anthropic)', /test/i.test(providerLabel ?? ''), providerLabel);

    // 4. Prompt 1 — basic tool-call test (calculate).
    await send(PROMPT_1);
    messagesSent.push(PROMPT_1);
    await waitStreaming().catch(() => {});
    await waitIdle();
    const afterPrompt1 = await domSnapshot();
    responsesReceived.push(afterPrompt1);
    record(
      'prompt1-tool-call-rendered (calculate)',
      afterPrompt1.toolCallPills.includes('calculate'),
      afterPrompt1
    );

    // 5. Prompt 2 — multi-turn + reasoning + parallel tool calls, SAME conversation (context retained).
    await send(PROMPT_2);
    messagesSent.push(PROMPT_2);
    await waitStreaming().catch(() => {});
    await waitIdle();
    const afterPrompt2 = await domSnapshot();
    responsesReceived.push(afterPrompt2);
    const weatherPillCount = afterPrompt2.toolCallPills.filter((n) => n === 'get_weather').length;
    record(
      'prompt2-multiturn-rendered (thinking + weather tool calls + final text)',
      afterPrompt2.thinkingPillCount > 0 && weatherPillCount >= 2 && afterPrompt2.assistantTexts.length > 0,
      { ...afterPrompt2, weatherPillCount }
    );

    toolCallsRendered =
      afterPrompt1.toolCallPills.includes('calculate') && afterPrompt2.toolCallPills.includes('get_weather');

    // 6. Same-conversation sanity: both user turns landed in the one thread.
    const userTurnCount = await tid('user-message-group').count();
    record('multi-turn-same-conversation (2 user turns)', userTurnCount >= 2, { userTurnCount });

    // 7. No error banner surfaced at any point.
    record('no-error-banner', !afterPrompt1.errorBanner && !afterPrompt2.errorBanner, {
      afterPrompt1: afterPrompt1.errorBanner,
      afterPrompt2: afterPrompt2.errorBanner,
    });

    // 8. Screenshot for the record (final state, after both responses). Written to a portable
    // location (OS temp dir, or LMSTREAMING_OUTPUT_DIR) so the script never writes into an arbitrary
    // checkout path.
    await page.screenshot({
      path: SCREENSHOT_PATH,
      fullPage: true,
    });
  } catch (e) {
    // A navigation/Playwright exception embeds the raw (operator-supplied) URL in its message/stack, so
    // capture only the structured error name here — never the raw exception string.
    errors.push(errName(e));
    record('exception', false, errName(e));
  }

  errors.push(...consoleErrors);

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return {
    pass: failures.length === 0,
    failures,
    steps,
    urlUsed,
    pageLoaded,
    providerSelected,
    messagesSent,
    responsesReceived,
    toolCallsRendered,
    screenshotPath: SCREENSHOT_PATH,
    errors,
  };
}
