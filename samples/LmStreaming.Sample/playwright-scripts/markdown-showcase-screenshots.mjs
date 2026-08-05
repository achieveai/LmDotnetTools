// markdown-showcase-screenshots.mjs — renders the markdown showcase through the `test-anthropic`
// mock and captures screenshots of the RUNNING APP (full window: sidebar, header, composer and the
// conversation), so the shots show markdown as a user actually sees it rather than a cropped
// element. Companion to markdown-render-audit.mjs, which asserts; this one just shows.
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/markdown-showcase-screenshots.mjs" })
//
// The MARKDOWN block must stay identical to the copies in markdown-render-audit.mjs and
// gen-md-showcase-prompt.mjs (each script has to be a single self-contained function expression,
// so they cannot share an import).
async (page) => {
  const BASE = 'http://127.0.0.1:5000';
  const PROVIDER_ID = 'test-anthropic';
  const OUT_DIR = 'B:/sources/LmDotnetTools/.worktrees/WT1/.logs/markdown-shots';

  // The MCP browser context runs at deviceScaleFactor 1, so `page.screenshot()` writes CSS pixels:
  // a 1600x1000 viewport yields a 1600x1000 PNG that looks small on a high-DPI display and gains
  // nothing from maximising the image viewer.
  //
  // `deviceScaleFactor` is a CONTEXT option and cannot be changed on a live context. Overriding it
  // per-page over CDP does NOT work either -- Playwright re-applies the context's own device
  // metrics while taking the screenshot, silently discarding the override (observed: every PNG came
  // out at the stale 900x1000 viewport). So capture in a DEDICATED high-DPI context instead, and
  // close it afterwards so the shared MCP page is left exactly as it was found.
  const DPR = 2;

  const MARKDOWN = [
    '# Streaming Architecture Review',
    '',
    'The pipeline is **healthy** overall, with _two_ follow-ups worth tracking. Latency is measured at the `MessageTransformationMiddleware` boundary.',
    '',
    '## Findings',
    '',
    '### 1. Merge keys collide across turns',
    '',
    'Turn 2+ reuses the run-scoped `generationId`, so `(generationId, messageOrderIdx)` is not unique.',
    '',
    '> Any fix that changes message identity must ship a multi-turn end-to-end test.',
    '> Single-turn green is not evidence.',
    '',
    '### 2. Tool pills duplicate on resume',
    '',
    'Steps to reproduce:',
    '',
    '1. Start a tool-heavy run',
    '2. Switch to another conversation mid-run',
    '3. Switch back before the run completes',
    '   - the pill count freezes',
    '   - a full page reload "fixes" it',
    '',
    '## Latency by stage',
    '',
    '| Stage | p50 (ms) | p99 (ms) | Budget |',
    '| --- | ---: | ---: | :---: |',
    '| Provider stream | 42 | 180 | ok |',
    '| Transform | 3 | 11 | ok |',
    '| WebSocket fan-out | 8 | 260 | over |',
    '',
    '## Suggested patch',
    '',
    '```csharp',
    'if (msg.RunId is null && currentRunId is not null)',
    '{',
    '    msg = msg with { RunId = currentRunId };',
    '}',
    '```',
    '',
    'And on the client:',
    '',
    '```ts',
    "const key = [kind, runId, generationId, orderIdx].join('-');",
    '```',
    '',
    '## Checklist',
    '',
    '- [x] Reproduce with 12 tool calls',
    '- [ ] Add `useChatResume` coverage',
    '- [ ] Backfill persisted conversations',
    '',
    '---',
    '',
    'See the [pipeline notes](https://example.com/pipeline) for the full trace. Terms: HTTP/2, SSE.',
  ].join('\n');

  const PROMPT =
    '<|instruction_start|>' +
    JSON.stringify({
      instruction_chain: [
        { id: 'md-showcase', id_message: 'Markdown showcase', messages: [{ text: MARKDOWN }] },
      ],
    }) +
    '<|instruction_end|>';

  const shots = [];
  let hiDpiContext = null;
  let pg = page;

  try {
    const browser = page.context().browser();
    if (browser) {
      hiDpiContext = await browser.newContext({
        viewport: { width: 1800, height: 1150 },
        deviceScaleFactor: DPR,
      });
      pg = await hiDpiContext.newPage();
    }

    const tid = (id) => pg.locator(`[data-testid="${id}"]`);

    async function waitForLabelMatch(getLabel, regex, timeoutMs = 8000, intervalMs = 200) {
      const deadline = Date.now() + timeoutMs;
      let last = null;
      while (Date.now() < deadline) {
        last = await getLabel();
        if (regex.test(last ?? '')) return last;
        await pg.waitForTimeout(intervalMs);
      }
      return last;
    }

    // Whole-window shot. `pg.screenshot()` (not a locator screenshot) is the point: the app chrome
    // -- sidebar, provider header, composer -- is part of what we are showing. The PNG's real pixel
    // size is read back out of the IHDR header (bytes 16..24) so the run PROVES it captured at DPR
    // rather than trusting a setting that may have been silently discarded.
    async function shot(name, note) {
      const file = `${OUT_DIR}/${name}.png`;
      const buf = await pg.screenshot({ path: file });
      shots.push({
        name,
        file,
        css: pg.viewportSize(),
        png: { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) },
        note,
      });
    }

    // The conversation lives in an inner scroll container, so `fullPage` would not reach the parts
    // below the fold -- scroll the container itself and shoot the window again.
    const scrollFeed = (top) =>
      pg.evaluate((y) => {
        const list = document.querySelector('[data-testid="message-list"]');
        const el = list && list.scrollHeight > list.clientHeight ? list : document.scrollingElement;
        el.scrollTop = y === 'end' ? el.scrollHeight : y;
        return {
          scrollTop: el.scrollTop,
          scrollHeight: el.scrollHeight,
          clientHeight: el.clientHeight,
        };
      }, top);

    await pg.setViewportSize({ width: 1800, height: 1150 });
    await pg.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // Start a FRESH thread. The app restores the last conversation on load, so without this the
    // showcase is appended to whatever was there before and the screenshots show old traffic.
    // `.new-chat-btn` is the documented selector (PlaywrightTestingGuide.md) -- it has no testid.
    await pg.locator('.new-chat-btn').click();
    await pg.waitForFunction(
      () => document.querySelectorAll('[data-testid="assistant-text"]').length === 0,
      null,
      { timeout: 10000 }
    );

    // Provider must be chosen BEFORE the first send -- the thread locks to the active provider.
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_ID}`).click({ timeout: 10000 });
    await waitForLabelMatch(
      () => tid('provider-selector-button').textContent(),
      /Test \(Anthropic\)/i
    );

    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 }).catch(() => {});
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 }).catch(() => {});
    await tid('assistant-text').last().waitFor({ timeout: 20000 });

    // 1-3: the app at a wide desktop size, scrolled through the reply.
    const geom = await scrollFeed(0);
    await shot('1-app-top', 'desktop 1800x1150 at 2x, top of the conversation');
    await scrollFeed(Math.round(geom.clientHeight * 0.8));
    await shot('2-app-middle', 'scrolled to the table');
    await scrollFeed('end');
    await shot('3-app-bottom', 'scrolled to the code blocks and checklist');

    // 4: one tall window so the entire reply is visible in app chrome at once.
    await pg.setViewportSize({ width: 1800, height: 1700 });
    await scrollFeed('end');
    await shot('4-app-whole-reply', 'tall window, entire reply in one frame');

    // 5-6: narrow window -- this is where the long `ts` line used to be SILENTLY CLIPPED rather
    // than scrolled. Shot before and after scrolling the code panel to its end.
    await pg.setViewportSize({ width: 1000, height: 1100 });
    const tsPre = tid('assistant-text').last().locator('pre').nth(1);
    await tsPre.scrollIntoViewIfNeeded();
    await shot('5-app-narrow-code-start', 'narrow window, code panel at scrollLeft 0');
    await tsPre.evaluate((el) => {
      el.scrollLeft = 10000;
    });
    await shot(
      '6-app-narrow-code-scrolled',
      'same panel scrolled to its end -- tail reachable, scrollbar painted'
    );

    const scroll = await tsPre.evaluate((el) => ({
      scrollWidth: el.scrollWidth,
      clientWidth: el.clientWidth,
      scrolledTo: el.scrollLeft,
    }));

    // Every shot must be DPR-scaled, or the run silently reproduces the small-image problem.
    const scaled = shots.every((s) => s.css && s.png.width === s.css.width * DPR);
    return { pass: shots.length === 6 && scaled, hiDpiContext: hiDpiContext !== null, shots, scroll };
  } catch (err) {
    return { pass: false, error: String(err && err.stack ? err.stack : err), shots };
  } finally {
    if (hiDpiContext) await hiDpiContext.close().catch(() => {});
  }
}
