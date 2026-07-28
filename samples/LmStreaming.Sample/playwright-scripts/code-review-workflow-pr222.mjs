// code-review-workflow-pr222.mjs — single-call Playwright verification that a real workflow-agent PR
// review LAUNCHES CLEANLY on gpt-5.6-luna after the sub-agent model-override validation fix. The point
// is NOT to complete a full 89-file review (expensive) but to prove the model path is healthy:
//   • the workflow run + its sub-agent delegates surface,
//   • every model the run actually uses is a REAL Copilot catalog id (no invented gpt-5 / o3-mini and
//     no misfilled subagent_type like "general-purpose" reaching the provider),
//   • usage accumulates at a normal rate (no invalid-model BadRequest retry storm).
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/code-review-workflow-pr222.mjs" })
//
// Returns { pass, failures, steps, threadId, workflowThreadId, delegateThreadIds, usage, snapshots }.
// The authoritative "no BadRequest / correct models" check is done by correlating the returned
// threadId/workflowThreadId against the server log; this script gathers the browser-observable half.
//
// ⚠ REAL-PROVIDER, SLOW: drives a live LLM + sandbox gateway. Bounded observation window — it returns
// as soon as there is enough evidence (workflow + a delegate + usage>0) instead of waiting for the
// whole review, so the caller can stop the run and avoid paying for all 89 files.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const PROVIDER_ID = 'gpt-5.6-luna';
  const MODE_ID = 'workspace-agent';
  // Workspace is resolved by NAME/dir at runtime (a hardcoded id can go stale across data dirs).
  // The LmDotnetTools repo workspace (sandbox checkout 'lm-dotnet-tools', claude-plugins marketplace
  // that provides the code-reviewer:pr-review skill) is where PR#222 lives.
  const WORKSPACE_NAME = 'LmDotnetTools';
  const WORKSPACE_DIR = 'lm-dotnet-tools';
  const WORKSPACE_MARKETPLACES = ['claude-plugins', 'superpowers'];
  const SHOT = 'B:/sources/LmDotnetTools/.logs/manual/code-review-workflow-pr222.png';
  const OBSERVE_TIMEOUT_MS = 6 * 60 * 1000;
  const MIN_OBSERVE_MS = 60 * 1000; // let it get going before we call it "enough evidence"

  // The deployment's SANCTIONED override set = the routable Copilot ids named across
  // appsettings.Development.json SubAgentIntelligence:Tiers (deepseek-v4-pro is anthropic-compat →
  // excluded), which already includes the parent model luna (a tier-1 id). After the override-restriction
  // fix, every billed model must fall inside this set; a billed id outside it (e.g. gpt-5.4 /
  // claude-sonnet-4.6) means an unsanctioned override slipped through. Keep in sync with that config.
  const SANCTIONED = new Set([
    'gpt-5.6-luna', 'claude-haiku-4.5', // tier 1 (routable)
    'claude-sonnet-5', 'gpt-5.6-terra', // tier 3
    'claude-opus-5', 'gpt-5.6-sol', // tier 5
  ]);

  const PROMPT =
    'using code-reviewre:pr-review skill, Can you review PR#222 using workflow agent? ' +
    'Do not post any comments just yet.';

  const steps = [];
  const snapshots = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const api = (path) => page.evaluate((p) => fetch(p).then((r) => r.json()).catch(() => null), path);

  const waitForLabelMatch = async (getLabel, regex, timeoutMs = 15000, intervalMs = 300) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await getLabel().catch(() => null);
      if (regex.test(last ?? '')) return last;
      await page.waitForTimeout(intervalMs);
    }
    return last;
  };

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 0. Resolve (or create) the LmDotnetTools repo workspace by name/dir. If absent, create it
    //    against the shared-gateway 'lm-dotnet-tools' checkout with the claude-plugins marketplace
    //    (which provides the code-reviewer:pr-review skill).
    const workspaceId = await page.evaluate(
      async ({ name, dir, marketplaces }) => {
        const list = await fetch(`${location.origin}/api/workspaces`).then((r) => r.json());
        const found = (Array.isArray(list) ? list : []).find(
          (w) => w.name === name || w.directoryRelPath === dir
        );
        if (found) return found.id;
        const res = await fetch(`${location.origin}/api/workspaces`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, directoryRelPath: dir, marketplaces }),
        });
        if (!res.ok) throw new Error(`workspace create failed: ${res.status} ${await res.text()}`);
        return (await res.json()).id;
      },
      { name: WORKSPACE_NAME, dir: WORKSPACE_DIR, marketplaces: WORKSPACE_MARKETPLACES }
    );
    record('workspace-resolved', !!workspaceId, workspaceId);
    if (!workspaceId) return { pass: false, failures: ['workspace-resolved'], steps };

    // 1. Provision a workspace-bound conversation headlessly (race-free) on gpt-5.6-luna.
    const provisioned = await page.evaluate(
      async ({ workspaceId, providerId, modeId }) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ workspaceId, providerId, modeId }),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return res.json();
      },
      { workspaceId, providerId: PROVIDER_ID, modeId: MODE_ID }
    );
    const threadId = provisioned && provisioned.threadId;
    record('provisioned-thread', !!threadId, threadId);
    if (!threadId) return { pass: false, failures: ['provisioned-thread'], steps };

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 2. Confirm workspace + provider + mode bound BEFORE sending (sending into an unbound deep-link
    //    silently no-ops).
    const wsLabel = await waitForLabelMatch(async () => {
      const badge = await tid('workspace-locked-badge').count();
      if (badge > 0) return tid('workspace-locked-badge').textContent();
      const btn = await tid('workspace-selector-button').count();
      return btn > 0 ? tid('workspace-selector-button').textContent() : null;
    }, /LmDotnetTools/);
    record('workspace-bound (LmDotnetTools)', /LmDotnetTools/.test(wsLabel ?? ''), wsLabel);

    const modeLabel = await waitForLabelMatch(() => tid('mode-selector-button').textContent(), /Workspace/);
    record('mode-bound (Workspace Agent)', /Workspace/.test(modeLabel ?? ''), modeLabel);

    const provLabel = await waitForLabelMatch(
      () => tid('provider-selector-button').textContent(),
      /Luna/i
    );
    record('provider-bound (gpt-5.6-luna)', /Luna/i.test(provLabel ?? ''), provLabel);

    const notFound = await tid('conversation-not-found').count();
    record('deep-link-resolved', notFound === 0, { notFound });

    if (steps.some((s) => !s.pass)) {
      record('aborted-before-send', false, 'pre-send binding check failed; refusing to send into a dead thread');
      return { pass: false, failures: steps.filter((s) => !s.pass).map((s) => s.name), steps, threadId };
    }

    // 3. Send the user's exact PR-review prompt (real LLM authors + runs the workflow).
    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();
    await tid('user-message-group').first().waitFor({ state: 'visible', timeout: 20000 }).catch(() => {});
    record('prompt-sent', (await tid('user-message-group').count()) > 0, null);

    // 4. Bounded observation: poll /subagents + /usage until we have enough evidence (workflow run +
    //    ≥1 delegate + usage>0) or the workflow completes, capturing periodic snapshots so a retry
    //    storm (usage climbing with no progress / error text) would be visible.
    const subUrl = `/api/conversations/${threadId}/subagents`;
    const usageUrl = `/api/conversations/${threadId}/usage`;
    const started = Date.now();
    let final = null;
    let lastUsage = null;
    while (Date.now() - started < OBSERVE_TIMEOUT_MS) {
      const subs = await api(subUrl);
      const usage = await api(usageUrl);
      lastUsage = usage;
      const arr = Array.isArray(subs) ? subs : [];
      const wf = arr.find((s) => s.kind === 'workflow');
      const delegates = arr.filter((s) => s.kind === 'subagent');
      const snap = {
        t: Math.round((Date.now() - started) / 1000),
        workflow: wf ? { status: wf.status, threadId: wf.threadId } : null,
        delegates: delegates.map((d) => ({ agentId: d.agentId, status: d.status, threadId: d.threadId })),
        totalTokens: usage && (usage.totalTokens ?? 0),
        perModel: usage && usage.perModel,
      };
      snapshots.push(snap);

      const wfDone = wf && String(wf.status).toLowerCase() === 'completed';
      const enough = wf && delegates.length >= 1 && (usage?.totalTokens ?? 0) > 0
        && Date.now() - started >= MIN_OBSERVE_MS;
      if (wfDone || enough) {
        final = { wf, delegates, usage };
        break;
      }
      await page.waitForTimeout(6000);
    }

    if (!final) {
      record('workflow-launched', false, 'no workflow run + delegate + usage within the observation window');
      return { pass: false, failures: ['workflow-launched'], steps, threadId, snapshots, usage: lastUsage };
    }

    record('workflow-launched', true, { status: final.wf.status, threadId: final.wf.threadId });
    record(
      'delegates-dispatched',
      final.delegates.length >= 1,
      `${final.delegates.length}: ${final.delegates.map((d) => `${d.agentId}(${d.status})`).join(', ')}`
    );

    // 5. THE MODEL-PATH PROOF (browser half): every model the run actually billed is (a) a real Copilot
    //    catalog id AND (b) within the deployment's SANCTIONED tier set — so the override-restriction fix
    //    held and no gpt-5.4 / claude-sonnet-4.6 slipped in. usage.perModel is an ARRAY of rows
    //    ({ modelId, totalTokens, ... }) — read row.modelId, NOT Object.keys (which on an array yields
    //    "0","1",…). Fetch the live catalog from /api/providers (Copilot groups) to confirm each billed
    //    id is real; an invented id (gpt-5 / o3-mini) or a misfilled subagent_type ("general-purpose")
    //    would show up as unknown.
    const catalog = await api('/api/providers');
    const knownIds = new Set(
      ((catalog && catalog.providers) || []).map((p) => p.id)
    );
    const perModelRows = Array.isArray(final.usage && final.usage.perModel) ? final.usage.perModel : [];
    const modelKeys = perModelRows.map((row) => row.modelId).filter(Boolean);
    const unknownModels = modelKeys.filter((m) => !knownIds.has(m));
    record(
      'all-billed-models-are-real-copilot-ids',
      modelKeys.length > 0 && unknownModels.length === 0,
      { billedModels: modelKeys, unknownModels }
    );

    // The override-restriction proof: no billed model outside the sanctioned tier set. Before the fix the
    // controller could pick gpt-5.4 / claude-sonnet-4.6 (real catalog ids, but NOT tier-sanctioned) and
    // burn tokens on them; now such an override is dropped and the delegate inherits luna.
    const unsanctionedModels = modelKeys.filter((m) => !SANCTIONED.has(m));
    record(
      'all-billed-models-are-tier-sanctioned',
      modelKeys.length > 0 && unsanctionedModels.length === 0,
      { billedModels: modelKeys, unsanctionedModels, sanctioned: [...SANCTIONED] }
    );

    const totalTokens = (final.usage && final.usage.totalTokens) || 0;
    record('usage-accumulating', totalTokens > 0, { totalTokens, perModel: perModelRows });

    // 6. No error banner / surfaced provider error in the main conversation.
    const errorBanner = await tid('error-banner').count();
    record('no-error-banner', errorBanner === 0, { errorBanner });

    await page.screenshot({ path: SHOT, fullPage: false }).catch(() => {});

    const failures = steps.filter((s) => !s.pass).map((s) => s.name);
    return {
      pass: failures.length === 0,
      failures,
      steps,
      threadId,
      workflowThreadId: final.wf.threadId,
      delegateThreadIds: final.delegates.map((d) => d.threadId),
      usage: final.usage,
      snapshots,
    };
  } catch (err) {
    record('exception', false, String(err && err.message ? err.message : err));
    return { pass: false, failures: ['exception'], steps, snapshots };
  }
}
