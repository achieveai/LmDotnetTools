// markdown-render-audit.mjs — drives ONE markdown-rich assistant reply through the `test-anthropic`
// mock and reports, for every GFM construct, whether it rendered and what CSS it actually got.
//
// Purpose: evidence for "is the chat's markdown rendering presentable?" — headings, lists, tables,
// blockquotes, links, code blocks, task lists, hr, images, nested lists. The mock's
// `{"text": "..."}` explicit-text instruction (InstructionChainParser.cs) lets us pin the exact
// markdown source, so the audit is deterministic and needs no real LLM.
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/markdown-render-audit.mjs" })
//
// Prompt source: PromptExamples.md -> "Markdown Rendering Showcase". MARKDOWN below must stay
// identical to the copy in gen-md-showcase-prompt.mjs (this file has to be a single self-contained
// function expression, so it cannot import it).
async (page) => {
  const BASE = 'http://127.0.0.1:5000';
  const PROVIDER_ID = 'test-anthropic';
  const OUT_DIR = 'B:/sources/LmDotnetTools/.worktrees/WT1/.logs';

  // Professional markdown showcase — every construct the chat client is expected to render.
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

  const steps = [];
  const notes = {};
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const px = (v) => parseFloat(v || '0') || 0;
  // A colour is "transparent" when fully alpha-zero.
  const isTransparent = (c) => !c || c === 'transparent' || /rgba\(\s*0,\s*0,\s*0,\s*0\s*\)/.test(c);

  async function waitForLabelMatch(getLabel, regex, timeoutMs = 8000, intervalMs = 200) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await getLabel();
      if (regex.test(last ?? '')) return last;
      await page.waitForTimeout(intervalMs);
    }
    return last;
  }

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // Provider must be chosen BEFORE the first send — the thread locks to the active provider.
    // Gate on the *rendered* label: reading it straight after the click races Vue's re-render and
    // the send then goes out against whichever provider was previously selected.
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER_ID}`).click({ timeout: 10000 });
    const providerLabel = await waitForLabelMatch(
      () => tid('provider-selector-button').textContent(),
      /Test \(Anthropic\)/i
    );
    record('provider-selected', /Test \(Anthropic\)/i.test(providerLabel ?? ''), (providerLabel || '').trim());

    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    // Gate on stream idle.
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 }).catch(() => {});
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 }).catch(() => {});

    const bubbleCount = await tid('assistant-text').count();
    record('assistant-bubble-rendered', bubbleCount > 0, { bubbleCount });

    // Single bulk extraction: which constructs rendered + the CSS they actually got.
    const audit = await page.evaluate(() => {
      const root = [...document.querySelectorAll('[data-testid="assistant-text"]')].pop();
      if (!root) return { error: 'no assistant-text element' };

      const cs = (el, props) => {
        const s = getComputedStyle(el);
        const out = {};
        for (const p of props) out[p] = s.getPropertyValue(p);
        return out;
      };
      const first = (sel) => root.querySelector(sel);
      const count = (sel) => root.querySelectorAll(sel).length;

      const box = ['margin-top', 'margin-bottom', 'padding-left', 'font-size', 'font-weight', 'line-height'];

      const present = {
        h1: count('h1'), h2: count('h2'), h3: count('h3'),
        p: count('p'), ul: count('ul'), ol: count('ol'), li: count('li'),
        table: count('table'), th: count('th'), td: count('td'),
        blockquote: count('blockquote'), pre: count('pre'),
        codeInline: count('code:not(pre code)'), codeBlock: count('pre code'),
        a: count('a'), hr: count('hr'),
        taskCheckbox: count('input[type="checkbox"]'),
        strong: count('strong'), em: count('em'),
      };

      const styles = {};
      const probe = (name, sel, props) => {
        const el = first(sel);
        styles[name] = el ? cs(el, props) : null;
      };
      probe('h1', 'h1', [...box, 'border-bottom-width']);
      probe('h2', 'h2', [...box, 'border-bottom-width']);
      probe('h3', 'h3', box);
      probe('ul', 'ul', box);
      probe('ol', 'ol', box);
      probe('li', 'li', box);
      probe('table', 'table', ['border-collapse', 'width', 'font-size', 'margin-bottom', 'overflow-x']);
      probe('th', 'th', ['border-bottom-width', 'border-bottom-style', 'padding-top', 'padding-left', 'text-align', 'background-color', 'font-weight']);
      probe('td', 'td', ['border-bottom-width', 'border-bottom-style', 'padding-top', 'padding-left', 'text-align']);
      probe('blockquote', 'blockquote', ['border-left-width', 'border-left-style', 'border-left-color', 'padding-left', 'margin-left', 'background-color', 'color']);
      probe('pre', 'pre', ['background-color', 'padding-top', 'padding-left', 'border-radius', 'border-top-width', 'font-size', 'overflow-x']);
      probe('preCode', 'pre code', ['background-color', 'padding-top', 'font-family', 'color']);
      probe('inlineCode', 'code:not(pre code)', ['background-color', 'padding-top', 'padding-left', 'border-radius', 'font-family', 'font-size']);
      probe('a', 'a', ['color', 'text-decoration-line']);
      probe('hr', 'hr', ['border-top-width', 'background-color', 'margin-top', 'height']);
      probe('taskLi', 'li:has(> input[type="checkbox"])', ['list-style-type', 'margin-left']);

      // Are code blocks syntax-highlighted? Presence of spans is not enough -- a theme that
      // failed to load still leaves the spans, just all one colour. Measure DISTINCT colours.
      const preCode = first('pre code');
      const highlightSpans = preCode ? preCode.querySelectorAll('span').length : 0;
      const highlightColors = preCode
        ? [...new Set([...preCode.querySelectorAll('span')].map((el) => getComputedStyle(el).color))]
        : [];
      const codeHasHljsClass = preCode ? preCode.className : null;

      // Table chrome: an outer rounded border, with cells drawing dividers rather than full boxes.
      const tableEl = first('table');
      const tdEl = first('td');
      const tdNextEl = root.querySelector('td + td');
      const tableChrome = tableEl
        ? cs(tableEl, ['border-top-width', 'border-top-left-radius', 'border-collapse', 'font-variant-numeric'])
        : null;
      const tdBorders = tdEl
        ? (() => {
            const s = getComputedStyle(tdEl);
            return ['top', 'right', 'bottom', 'left'].map((side) => s.getPropertyValue(`border-${side}-width`));
          })()
        : null;
      const tdDividerLeft = tdNextEl ? getComputedStyle(tdNextEl).borderLeftWidth : null;

      // GFM column alignment arrives as an inline style and must survive the stylesheet.
      const alignments = [...root.querySelectorAll('thead th')].map((el) => getComputedStyle(el).textAlign);

      // Does anything overflow the bubble horizontally?
      const overflowing = [...root.querySelectorAll('*')]
        .filter((el) => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflowX === 'visible')
        .map((el) => el.tagName.toLowerCase());

      // Scrollable panels must actually SCROLL, and their content must be REACHABLE.
      //
      // The bug this guards is a silent clip, and it is subtle to detect: with an *inline* inner
      // <code>, the <code> contributes no intrinsic width to the <pre>'s scroll area, so the
      // browser reports scrollWidth === clientWidth -- no overflow, no scrollbar -- while the long
      // line is cut off. So "if it overflows it must scroll" passes VACUOUSLY against the very bug
      // it is meant to catch.
      //
      // The anchor is therefore the content's own rendered width. An inline box's
      // getBoundingClientRect() is the union of its line boxes, so `contentWidth` is the widest
      // rendered line whether <code> is inline or block -- it stays honest under the bug, and the
      // panel's scroll area has to cover it.
      const scrollProbe = (el) => {
        const s = getComputedStyle(el);
        const chrome = parseFloat(s.borderTopWidth) + parseFloat(s.borderBottomWidth);
        const inner = el.querySelector('code');
        const needed = inner
          ? Math.round(inner.getBoundingClientRect().width + parseFloat(s.paddingLeft) + parseFloat(s.paddingRight))
          : 0;
        const restore = el.scrollLeft;
        el.scrollLeft = 10000;
        const maxScrollLeft = el.scrollLeft;
        el.scrollLeft = restore;
        return {
          tag: el.tagName.toLowerCase(),
          scrollWidth: el.scrollWidth,
          clientWidth: el.clientWidth,
          needed,
          contentReachable: el.scrollWidth + 1 >= needed,
          overflows: el.scrollWidth > el.clientWidth + 1,
          maxScrollLeft,
          scrollbarPx: Math.round(el.offsetHeight - el.clientHeight - chrome),
        };
      };
      const scrollPanels = [...root.querySelectorAll('pre, table')].map(scrollProbe);

      return {
        present,
        styles,
        highlightSpans,
        highlightColors,
        codeHasHljsClass,
        tableChrome,
        tdBorders,
        tdDividerLeft,
        alignments,
        overflowing,
        scrollPanels,
        bodyFontSize: getComputedStyle(root).fontSize,
        textLen: root.textContent.trim().length,
      };
    });

    record('markdown-audit-collected', !audit.error, audit);

    if (!audit.error) {
      const s = audit.styles;

      // --- every construct still parses ---
      record('table-rendered', audit.present.table > 0 && audit.present.th === 4 && audit.present.td === 12,
        { table: audit.present.table, th: audit.present.th, td: audit.present.td });
      record('task-checkboxes-rendered', audit.present.taskCheckbox === 3, { taskCheckbox: audit.present.taskCheckbox });

      // --- the styling gaps this change is meant to close ---
      record('headings-have-vertical-rhythm',
        !!(s.h2 && px(s.h2['margin-top']) > 0 && px(s.h2['margin-bottom']) > 0), s.h2);
      record('h1-h2-have-underline-rule',
        !!(s.h1 && s.h2 && px(s.h1['border-bottom-width']) > 0 && px(s.h2['border-bottom-width']) > 0),
        { h1: s.h1 && s.h1['border-bottom-width'], h2: s.h2 && s.h2['border-bottom-width'] });
      record('lists-are-indented',
        !!(s.ul && s.ol && px(s.ul['padding-left']) > 0 && px(s.ol['padding-left']) > 0),
        { ul: s.ul && s.ul['padding-left'], ol: s.ol && s.ol['padding-left'] });
      record('task-list-marker-suppressed',
        !!(s.taskLi && s.taskLi['list-style-type'] === 'none'), s.taskLi);
      record('table-has-cell-dividers',
        !!(audit.tdBorders && px(audit.tdBorders[2]) > 0 && px(audit.tdDividerLeft) > 0),
        { tdBorders: audit.tdBorders, tdDividerLeft: audit.tdDividerLeft });
      record('table-has-rounded-outer-border',
        !!(audit.tableChrome && px(audit.tableChrome['border-top-width']) > 0
           && px(audit.tableChrome['border-top-left-radius']) > 0),
        audit.tableChrome);
      record('table-uses-tabular-numerals',
        !!(audit.tableChrome && /tabular-nums/.test(audit.tableChrome['font-variant-numeric'])),
        audit.tableChrome && audit.tableChrome['font-variant-numeric']);
      record('table-header-is-distinct',
        !!(s.th && !isTransparent(s.th['background-color']) && px(s.th['padding-left']) > 0), s.th);
      record('table-column-alignment-preserved',
        JSON.stringify(audit.alignments) === JSON.stringify(['left', 'right', 'right', 'center']),
        audit.alignments);
      record('blockquote-has-left-rule',
        !!(s.blockquote && px(s.blockquote['border-left-width']) > 0 && px(s.blockquote['padding-left']) > 0),
        s.blockquote);
      record('inline-code-is-chipped',
        !!(s.inlineCode && !isTransparent(s.inlineCode['background-color']) && px(s.inlineCode['padding-left']) > 0),
        s.inlineCode);
      record('code-block-has-no-chip-leak',
        !!(s.preCode && isTransparent(s.preCode['background-color']) && px(s.preCode['padding-top']) === 0),
        s.preCode);
      record('code-block-is-a-panel',
        !!(s.pre && !isTransparent(s.pre['background-color']) && px(s.pre['border-top-width']) > 0 && s.pre['overflow-x'] === 'auto'),
        s.pre);
      // Spans alone prove nothing -- an unloaded theme leaves the markup and paints it all one
      // colour. Distinct computed colours are the only evidence highlighting actually landed.
      record('code-block-is-syntax-highlighted',
        audit.highlightSpans >= 3 && audit.highlightColors.length >= 2,
        { spans: audit.highlightSpans, distinctColors: audit.highlightColors, class: audit.codeHasHljsClass });
      record('code-uses-a-monospace-face',
        !!(s.preCode && /mono|consolas|menlo|courier/i.test(s.preCode['font-family'])),
        s.preCode && s.preCode['font-family']);
      // A heading that isn't bigger than the text under it isn't a heading.
      record('heading-scale-descends',
        !!(s.h1 && s.h2 && s.h3
           && px(s.h1['font-size']) > px(s.h2['font-size'])
           && px(s.h2['font-size']) > px(s.h3['font-size'])
           && px(s.h3['font-size']) > px(audit.bodyFontSize)),
        { h1: s.h1 && s.h1['font-size'], h2: s.h2 && s.h2['font-size'], h3: s.h3 && s.h3['font-size'], body: audit.bodyFontSize });
      record('hr-visible', !!(s.hr && (px(s.hr['height']) > 0 || px(s.hr['border-top-width']) > 0)), s.hr);
      record('links-are-underlined',
        !!(s.a && /underline/.test(s.a['text-decoration-line'])), s.a);
      record('nothing-overflows-the-bubble', audit.overflowing.length === 0, audit.overflowing);

      // Verified RED/GREEN against the defect: forcing `pre code { display: inline }` back in-page
      // drops both panels to unreachable (402 < 413 and 476 < 490) and fails this check.
      const unreachable = audit.scrollPanels.filter((p) => !p.contentReachable);
      record('code-panel-content-is-reachable-not-clipped', unreachable.length === 0,
        { unreachable, panels: audit.scrollPanels });

      // And a panel that does overflow has to offer the affordance: real scroll range + a bar.
      const noAffordance = audit.scrollPanels
        .filter((p) => p.overflows)
        .filter((p) => !(p.maxScrollLeft > 0) || p.scrollbarPx <= 0);
      record('overflowing-panels-offer-a-scroll-affordance', noAffordance.length === 0,
        { noAffordance, panels: audit.scrollPanels });
    }

    // Deliberately NOT pass/fail: needs an npm dependency and is held pending a decision.
    notes.htmlSanitizer = 'none (marked output goes straight into v-html)';

    await page.screenshot({ path: `${OUT_DIR}/markdown-after-viewport.png` });
    await tid('assistant-text').last().screenshot({ path: `${OUT_DIR}/markdown-after-bubble.png` }).catch(() => {});
    record('screenshots-captured', true, [`${OUT_DIR}/markdown-after-viewport.png`, `${OUT_DIR}/markdown-after-bubble.png`]);
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, notes, steps };
}
