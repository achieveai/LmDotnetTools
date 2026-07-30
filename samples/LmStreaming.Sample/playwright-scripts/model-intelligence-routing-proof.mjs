// model-intelligence-routing-proof.mjs — real-provider, discriminating end-to-end proof.
// Runs ONE workflow containing tier-1, tier-3, and tier-5 marketplace templates with no authored
// per-task model override. It correlates immutable controller Agent arguments to each exact child
// thread, then requires that child's OWN usage and fromAgent metadata to match the configured tier.
//
// Run in one Playwright MCP call:
// browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/model-intelligence-routing-proof.mjs" })
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const PROVIDER_ID = 'gpt-5.6-luna';
  const MODE_ID = 'workspace-agent';
  const RUN_TIMEOUT_MS = 30 * 60 * 1000;
  const nonce = `MIR-${Date.now()}`;
  const cases = [
    { key: 'tier1', template: 'code-reviewer:temp-code-review', tier: 1, expected: ['gpt-5.6-luna', 'claude-haiku-4.5', 'deepseek-v4-pro'] },
    { key: 'tier3', template: 'code-reviewer:pr-context-gatherer', tier: 3, expected: ['claude-sonnet-5', 'gpt-5.6-terra'] },
    { key: 'tier5', template: 'code-reviewer:architecture-review', tier: 5, expected: ['claude-opus-5', 'gpt-5.6-sol'] },
  ];
  const prompt = [
    'Use StartWorkflowAgent to author and run exactly one workflow.',
    'The workflow must have one parallel step containing exactly these three agents:',
    ...cases.map(c => `- ${c.template}: reply exactly ${nonce}-${c.key}`),
    'IMPORTANT: omit modelIntelligence from every workflow task and do not specify any model.',
    'The point is to rely only on each discovered template frontmatter modelintelligence.',
    'Launch it once, wait for completion, and report the three exact replies.',
    'Do not use GitHub, PR, posting, approval, merge, write, edit, or shell tools.',
  ].join('\n');

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const api = async (path, options) => {
    const response = await page.request.fetch(BASE + path, options);
    const text = await response.text();
    let body;
    try { body = JSON.parse(text); } catch { body = text; }
    return { ok: response.ok(), status: response.status(), body };
  };
  const parseMessage = row => {
    try { return JSON.parse(row.messageJson || '{}'); } catch { return {}; }
  };

  try {
    const catalog = await api('/api/workspaces');
    const workspace = catalog.body.workspaces?.find(w => w.name === 'LmDotNettools');
    record('remote-compatible-workspace', catalog.body.gateway?.canonicalBaseUrl === 'http://192.168.11.139:3000' && workspace?.compatibility === 'compatible', { gateway: catalog.body.gateway, workspace });
    if (!workspace) return { pass: false, failures: ['remote-compatible-workspace'], steps };

    const providers = await api('/api/providers');
    const available = new Set((providers.body.providers || []).filter(p => p.available).map(p => p.id));
    const oracle = Object.fromEntries(cases.map(c => [c.key, c.expected.find(id => available.has(id)) || null]));
    record('oracle-resolved-from-live-catalog', Object.values(oracle).every(Boolean), { oracle, available: [...available] });

    const provision = await api('/api/conversations', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      data: { workspaceId: workspace.id, providerId: PROVIDER_ID, modeId: MODE_ID },
    });
    if (!provision.ok) throw new Error(`provision: ${provision.status} ${JSON.stringify(provision.body)}`);
    const threadId = provision.body.threadId;
    const sent = await api(`/api/conversations/${threadId}/messages`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, data: { text: prompt },
    });
    if (!sent.ok) throw new Error(`send: ${sent.status} ${JSON.stringify(sent.body)}`);
    record('prompt-sent', true, { threadId, inputId: sent.body.inputId, nonce });

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await page.getByTestId('chat-input-textarea').waitFor({ timeout: 30000 });

    let terminal;
    const deadline = Date.now() + RUN_TIMEOUT_MS;
    while (Date.now() < deadline) {
      const response = await api(`/api/conversations/${threadId}/subagents`);
      const rows = Array.isArray(response.body) ? response.body : [];
      const workflows = rows.filter(r => r.kind === 'workflow');
      const delegates = rows.filter(r => r.kind === 'subagent');
      const running = rows.filter(r => !['completed', 'error', 'stopped'].includes(String(r.status).toLowerCase()));
      if (workflows.length === 1 && delegates.length >= 3 && running.length === 0) {
        terminal = { workflows, delegates };
        break;
      }
      await page.waitForTimeout(10000);
    }
    record('one-terminal-workflow', !!terminal && terminal.workflows[0].status === 'completed', terminal);
    if (!terminal) return { pass: false, failures: steps.filter(s => !s.pass).map(s => s.name), steps, threadId, nonce };

    const matched = {};
    for (const c of cases) {
      const rows = terminal.delegates.filter(d => d.template === c.template && d.task.includes(`${nonce}-${c.key}`));
      record(`${c.key}-exactly-one-child`, rows.length === 1, rows);
      if (rows.length === 1) matched[c.key] = rows[0];
    }
    const duplicateNames = [...terminal.delegates.reduce((m, d) => m.set(d.name, (m.get(d.name) || 0) + 1), new Map())].filter(([, count]) => count > 1);
    record('no-duplicate-unit-names', duplicateNames.length === 0, duplicateNames);

    const workflowThreadId = terminal.workflows[0].threadId;
    const controllerMessagesResponse = await api(`/api/conversations/${encodeURIComponent(workflowThreadId)}/messages`);
    const controllerMessages = Array.isArray(controllerMessagesResponse.body) ? controllerMessagesResponse.body.map(parseMessage) : [];
    const rawAgentCalls = controllerMessages
      .filter(m => m.$type === 'tool_call' && m.function_name === 'Agent')
      .map(m => {
        let args = m.function_args;
        if (typeof args === 'string') { try { args = JSON.parse(args); } catch { /* retain raw */ } }
        return { toolCallId: m.tool_call_id, args };
      });

    const evidence = [];
    for (const c of cases) {
      const child = matched[c.key];
      if (!child) continue;
      const usageResponse = await api(`/api/conversations/${encodeURIComponent(child.threadId)}/usage`);
      const messagesResponse = await api(`/api/conversations/${encodeURIComponent(child.threadId)}/messages`);
      const usageRows = Array.isArray(usageResponse.body?.perModel) ? usageResponse.body.perModel : [];
      const childMessages = Array.isArray(messagesResponse.body) ? messagesResponse.body.map(parseMessage) : [];
      const assistant = childMessages.find(m => m.$type === 'text' && m.role === 'assistant' && m.text === `${nonce}-${c.key}`);
      const raw = rawAgentCalls.find(call => call.args && call.args.name === child.name);
      const actualModels = usageRows.map(r => r.modelId);
      const row = {
        key: c.key,
        authoredTemplateTier: c.tier,
        expectedModel: oracle[c.key],
        controllerRawArgs: raw?.args || null,
        projectedRouting: {
          effectiveModelId: child.effectiveModelId,
          effectiveModelIntelligence: child.effectiveModelIntelligence,
          modelSelectionSource: child.modelSelectionSource,
        },
        child: { name: child.name, template: child.template, threadId: child.threadId, status: child.status },
        childUsage: usageRows,
        childFromAgent: assistant?.fromAgent || null,
        childReply: assistant?.text || null,
      };
      evidence.push(row);
      record(`${c.key}-raw-controller-call-captured`, !!raw, row.controllerRawArgs);
      record(
        `${c.key}-projected-routing-matches-oracle`,
        child.effectiveModelId === oracle[c.key]
          && child.effectiveModelIntelligence === c.tier
          && child.modelSelectionSource === 'template-tier',
        row.projectedRouting
      );
      record(`${c.key}-child-billed-only-expected-model`, actualModels.length === 1 && actualModels[0] === oracle[c.key] && usageRows[0].attemptCount === 1, row);
      record(`${c.key}-nonce-reply-from-child`, !!assistant, { reply: row.childReply, fromAgent: row.childFromAgent });
    }

    const distinctActualModels = new Set(evidence.flatMap(e => e.childUsage.map(u => u.modelId)));
    record('routing-is-discriminating-not-constant', distinctActualModels.size === 3, [...distinctActualModels]);
    const rawPollutionOverridden = evidence.filter(e => e.controllerRawArgs?.modelIntelligence === 0 && e.expectedModel !== 'gpt-5.6-luna');
    record('tier-zero-placeholder-overridden-for-higher-tiers', rawPollutionOverridden.length === 2 && rawPollutionOverridden.every(e => e.childUsage[0]?.modelId === e.expectedModel), rawPollutionOverridden);

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await page.getByTestId('chat-input-textarea').waitFor({ timeout: 30000 });
    const workflowTab = page
      .locator('[data-testid="conversation-tab"]')
      .filter({ has: page.locator('[data-testid="workflow-tab-badge"]') })
      .first();
    await workflowTab.waitFor({ state: 'visible', timeout: 30000 });
    await workflowTab.click();
    const pills = page.locator('[data-testid="tool-call-pill"][data-tool-name="Agent"]');
    await pills.first().waitFor({ state: 'visible', timeout: 30000 });
    const visibleRouting = [];
    for (let i = 0; i < await pills.count(); i++) {
      const pill = pills.nth(i);
      const summary = await pill.locator('.tool-pill__summary').textContent();
      const c = cases.find(candidate => summary?.includes(`${nonce}-${candidate.key}`));
      if (!c) continue;
      await pill.locator('.tool-pill__header').click();
      const requested = await pill.getByTestId('agent-controller-request').textContent();
      const effective = await pill.getByTestId('agent-effective-routing').textContent();
      visibleRouting.push({ key: c.key, requested, effective });
      const raw = rawAgentCalls.find(call => call.args && call.args.name === matched[c.key]?.name);
      const rawModel = raw?.args && Object.hasOwn(raw.args, 'model')
        ? String(raw.args.model ?? '')
        : 'undefined';
      const rawTier = raw?.args && Object.hasOwn(raw.args, 'modelIntelligence')
        ? String(raw.args.modelIntelligence ?? '')
        : 'undefined';
      record(
        `${c.key}-pill-shows-requested-versus-effective`,
        requested?.includes(`model: ${rawModel}`)
          && requested?.includes(`model intelligence: ${rawTier}`)
          && effective?.includes(oracle[c.key])
          && effective?.includes(String(c.tier))
          && effective?.includes('template tier'),
        { requested, effective, rawModel, rawTier }
      );
    }
    record('all-three-routing-pills-visible', visibleRouting.length === 3, visibleRouting);

    const forbidden = new Set(['Write', 'Edit', 'Bash', 'PowerShell', 'post-pr-review', 'create-pr', 'approve-pr', 'merge-pr']);
    const allToolCalls = [
      ...controllerMessages,
      ...await Promise.all(Object.values(matched).map(async child => {
        const r = await api(`/api/conversations/${encodeURIComponent(child.threadId)}/messages`);
        return Array.isArray(r.body) ? r.body.map(parseMessage) : [];
      })).then(groups => groups.flat()),
    ].filter(m => m.$type === 'tool_call').map(m => m.function_name);
    const forbiddenUsed = allToolCalls.filter(name => forbidden.has(name));
    record('no-forbidden-mutating-tools', forbiddenUsed.length === 0, forbiddenUsed);

    const failures = steps.filter(s => !s.pass).map(s => s.name);
    return { pass: failures.length === 0, failures, threadId, workflowThreadId, nonce, oracle, evidence, rawAgentCalls, steps };
  } catch (error) {
    record('exception', false, String(error?.stack || error));
    return { pass: false, failures: ['exception'], nonce, steps };
  }
}
