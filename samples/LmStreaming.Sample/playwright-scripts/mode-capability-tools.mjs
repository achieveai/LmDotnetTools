// mode-capability-tools.mjs — single-call Playwright check for "the Modes editor can manage the
// sandbox / sub-agent / workflow tools, and a CLONED mode actually gets them".
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/mode-capability-tools.mjs" })
//
// Returns { pass, failures, steps }. The reported defect had two halves and this drives both:
//   (a) the editor never listed sandbox/sub-agent/workflow tools at all, so there was nothing to tick;
//   (b) those families were granted by `mode.Id == "workspace-agent"`, so a copy — which has a fresh
//       id — silently got none of them.
// Half (b) is only really proven by RUNNING the clone, so the last steps provision a conversation on
// the clone with a MOCK provider and read the model's actual tool list back with the `tools_list`
// instruction (PromptExamples.md → "Mode capability selection (cloned modes)").
//
// Prereqs: app running (adjust BASE) with a reachable sandbox gateway. Mock provider only — no real
// LLM calls.
async (page) => {
  const BASE = 'http://localhost:5287';
  const PROVIDER_ID = 'test-anthropic';
  const CLONE_NAME = 'Capability Clone';
  const TOOLS_LIST =
    '<|instruction_start|>{"instruction_chain":[{"id":"tools-list","id_message":"Listing available tools","messages":[{"tools_list":{}}]}]}<|instruction_end|>';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const api = (path, init) =>
    page.evaluate(
      async ([p, i]) => {
        const res = await fetch(`${location.origin}${p}`, i ?? undefined);
        const text = await res.text();
        return { ok: res.ok, status: res.status, body: text ? JSON.parse(text) : null };
      },
      [path, init ?? null]
    );

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });

    // ---- 0. Clean up a clone left by an earlier run so the script is re-runnable. ------------
    const existing = await api('/api/chat-modes');
    for (const m of existing.body.filter((m) => m.name === CLONE_NAME)) {
      await api(`/api/chat-modes/${encodeURIComponent(m.id)}`, { method: 'DELETE' });
    }
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });

    // ---- 1. The catalog itself offers the three families the editor used to be blind to. ------
    const catalog = (await api('/api/tools')).body;
    const groups = [...new Set(catalog.map((t) => t.group))];
    record('catalog-has-capability-groups', ['sandbox', 'subagents', 'workflow'].every((g) => groups.includes(g)), groups);

    const sandboxRows = catalog.filter((t) => t.group === 'sandbox');
    record(
      'catalog-sandbox-listed-live',
      sandboxRows.length > 1 && sandboxRows.every((t) => !t.catalogWarning),
      { count: sandboxRows.length, names: sandboxRows.map((t) => t.name) }
    );

    // ---- 2. Clone Workspace Agent through the UI. ---------------------------------------------
    await tid('mode-selector-button').click();
    await page.locator('.manage-item').click();
    await tid('mode-management-modal').waitFor({ timeout: 10000 });

    await tid('mode-copy-workspace-agent').click();
    await tid('mode-copy-name').fill(CLONE_NAME);
    await tid('mode-copy-confirm').click();

    const cloneId = await page.evaluate(
      async ([name]) => {
        for (let i = 0; i < 40; i++) {
          const res = await fetch(`${location.origin}/api/chat-modes`);
          const modes = await res.json();
          const found = modes.find((m) => m.name === name);
          if (found) return found.id;
          await new Promise((r) => setTimeout(r, 250));
        }
        return null;
      },
      [CLONE_NAME]
    );
    record('clone-created', !!cloneId, cloneId);
    if (!cloneId) return { pass: false, failures: ['clone-created'], steps };

    // ---- 3. The clone's editor shows the capability tools, pre-ticked from the original. ------
    await tid(`mode-edit-${cloneId}`).click();
    await tid('mode-editor').waitFor({ timeout: 10000 });

    for (const group of ['sandbox', 'subagents', 'workflow']) {
      record(`editor-shows-group-${group}`, (await tid(`tool-group-${group}`).count()) > 0, group);
    }

    const checked = async (id) => {
      const loc = tid(`tool-${id}`);
      if ((await loc.count()) === 0) return null;
      return loc.isChecked();
    };
    record('editor-pre-ticks-sandbox-wildcard', (await checked('sandbox:*')) === true, 'sandbox:*');
    record('editor-pre-ticks-subagents-wildcard', (await checked('subagents:*')) === true, 'subagents:*');
    record(
      'editor-marks-wildcard-covered-rows-readonly',
      (await tid('tool-sandbox:Bash').isChecked()) &&
        (await tid('tool-sandbox:Bash').isDisabled()),
      'sandbox:Bash under sandbox:*'
    );
    record(
      'editor-warns-about-sandbox-cost',
      (await tid('tools-sandbox-note').count()) > 0,
      await tid('tools-sandbox-note').textContent().catch(() => null)
    );

    // ---- 4. Narrow the clone to a read-only sandbox slice and save. ---------------------------
    await tid('tool-sandbox:*').uncheck();
    await tid('tool-sandbox:Read').check();
    await tid('mode-editor-save').click();
    await tid('mode-management-modal').waitFor({ timeout: 10000 });

    const saved = await page.evaluate(
      async ([id]) => {
        for (let i = 0; i < 40; i++) {
          const res = await fetch(`${location.origin}/api/chat-modes/${encodeURIComponent(id)}`);
          const mode = await res.json();
          if (mode.enabledCapabilityTools && !mode.enabledCapabilityTools.includes('sandbox:*')) return mode;
          await new Promise((r) => setTimeout(r, 250));
        }
        const res = await fetch(`${location.origin}/api/chat-modes/${encodeURIComponent(id)}`);
        return res.json();
      },
      [cloneId]
    );
    const caps = saved.enabledCapabilityTools ?? [];
    record('save-narrowed-sandbox-to-read', caps.includes('sandbox:Read') && !caps.includes('sandbox:*'), caps);
    record('save-kept-the-other-families', caps.includes('subagents:*') && caps.some((c) => c.startsWith('workflow:')), caps);
    // The pre-existing bug where every save dropped the mode's built-in selection.
    record('save-kept-built-in-selection', JSON.stringify(saved.enabledBuiltInTools) === JSON.stringify(['web_search']), saved.enabledBuiltInTools);

    // ---- 5. Reopen the editor: the narrowed selection round-trips. ----------------------------
    await tid(`mode-edit-${cloneId}`).click();
    await tid('mode-editor').waitFor({ timeout: 10000 });
    record('reopen-shows-read-only', (await checked('sandbox:Read')) === true && (await checked('sandbox:*')) === false, {
      read: await checked('sandbox:Read'),
      wildcard: await checked('sandbox:*'),
    });
    record('reopen-shows-bash-unticked', (await checked('sandbox:Bash')) === false, 'sandbox:Bash');
    await page.locator('.mode-editor .btn-secondary').click();
    await page.keyboard.press('Escape');

    // ---- 6. RUN the clone: the tools it was granted are the tools the model sees. -------------
    const workspaceId = (await api('/api/workspaces')).body?.workspaces?.[0]?.id ?? 'default';
    const provisioned = await page.evaluate(
      async ([providerId, modeId, wsId]) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ providerId, modeId, workspaceId: wsId }),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return res.json();
      },
      [PROVIDER_ID, cloneId, workspaceId]
    );
    const threadId = provisioned && provisioned.threadId;
    record('clone-conversation-provisioned', !!threadId, threadId);
    if (!threadId) return { pass: false, failures: ['clone-conversation-provisioned'], steps };

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });
    await tid('chat-input-textarea').fill(TOOLS_LIST);
    await tid('send-button').click();
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 120000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 120000 });

    const transcript = await page.locator('.assistant-message-wrapper').last().innerText();
    record('clone-run-has-sandbox-read', /\bRead\b/.test(transcript), transcript.slice(0, 600));
    record(
      'clone-run-lacks-unselected-sandbox-tools',
      !/\bBash\b/.test(transcript) && !/\bPowerShell\b/.test(transcript),
      transcript.slice(0, 600)
    );
    record('clone-run-has-subagent-tool', /\bAgent\b/.test(transcript), transcript.slice(0, 600));
  } catch (err) {
    record('exception', false, String(err && err.stack ? err.stack : err));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps }
}
