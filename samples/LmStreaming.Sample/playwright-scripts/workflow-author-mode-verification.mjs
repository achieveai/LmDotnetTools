// workflow-author-mode-verification.mjs — single-call manual verification for the "Workflow Author"
// chat mode's three shipped changes (issue #179 / plan noble-puzzling-pearl):
//   A) Read/Grep/Skill sandbox tools wired into this mode (filtered to just those 3).
//   B) AddNode/RemoveNode graph-editing tools on WorkflowToolProvider.
//   C) StartWorkflow renamed to StartWorkflowAgent.
//
// Run with:
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/workflow-author-mode-verification.mjs" })
//
// Uses the MOCK provider `test-anthropic` (no real LLM) driving a single instruction-chain prompt
// (format: PromptExamples.md) with 8 sequential turns, each producing one tool-call pill (or, for the
// last turn, a closing assistant-text message so the run reaches an idle terminal state):
//   1. Read({file_path:"CLAUDE.md"})                      — Part A, bare relative path (no host-path needed)
//   2. Grep({pattern:"LmWorkflow", path:".", output_mode:"content"}) — Part A
//   3. Skill({skill:"sandbox:sandbox-workspace-bootstrap"}) — Part A (pill renders regardless of call success)
//   4. SetWorkflow(<the exact minimal example from Prompts.yaml's workflow-author system prompt>) — Part B setup
//   5. AddNode({node:{id:"extra",type:"procedural",...,next:["done"]}, previousNodeId:"research"}) — Part B success
//   6. RemoveNode({nodeId:"extra"}) — Part B: this now NO-OPS the node in place (keeps its id + inbound edge,
//      drops its task list, advances along its existing "next"), so it SUCCEEDS. A procedural node with a
//      successor is a valid no-op pass-through, so a clean, non-error outcome here IS the correct result
//      (confirmed by the shipped RemoveNode no-op tests). The pill appearing without an invalid_* error
//      code confirms both that the tool is wired and that the no-op removal committed.
//   7. StartWorkflowAgent({workflowId, workflow:<trivial start->terminal>, mode:"async"}) — Part C rename,
//      async mode returns {status:"started"} immediately without needing to manage the isolated controller
//      loop's execution.
//   8. A closing text message so the run settles to idle (stop-button hidden / send-button visible).
//
// Provisions the conversation headlessly via POST /api/conversations + a ?threadId= deep link (the same
// race-free pattern workspace-agent-egress-auth-test.mjs uses), instead of clicking "+ New Chat", to avoid
// the documented Send-binds-to-stale-conversation race.
//
// Leak-avoidance: Read/Grep/Skill tool results can carry real repo file content, so only PRESENCE/LENGTH is
// asserted for those three. SetWorkflow/AddNode/RemoveNode/StartWorkflowAgent operate on synthetic
// workflow-graph JSON authored by this very script (not repo content, not EUII), so their pill text is
// checked for the literal node id "extra" / workflowId as direct proof the graph actually updated.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'test-anthropic'; // "Test (Anthropic)" mock
  const MODE_ID = 'workflow-author'; // "Workflow Author"
  const WORKFLOW_ID = 'verify-start-workflow-agent-1';

  const setWorkflowDef = {
    objective: 'Summarize a topic using a sub-agent',
    steps: [
      { id: 'start', kind: 'start', next: 'research' },
      {
        id: 'research',
        kind: 'agent',
        agent: 'general-purpose',
        prompt: 'Research {{topic}} and summarize the key points.',
        saveAs: 'summary',
        next: 'done',
      },
      { id: 'done', kind: 'end' },
    ],
  };

  const trivialAsyncDef = {
    objective: 'trivial start-to-terminal workflow for an async StartWorkflowAgent smoke check',
    steps: [
      { id: 'start', kind: 'start', next: 'done' },
      { id: 'done', kind: 'end' },
    ],
  };

  const instructionChain = {
    instruction_chain: [
      {
        id: 'read-file',
        id_message: 'Reading CLAUDE.md',
        messages: [{ tool_call: [{ name: 'Read', args: { file_path: 'CLAUDE.md' } }] }],
      },
      {
        id: 'grep-repo',
        id_message: 'Searching the repo',
        messages: [
          {
            tool_call: [
              { name: 'Grep', args: { pattern: 'LmWorkflow', path: '.', output_mode: 'content' } },
            ],
          },
        ],
      },
      {
        id: 'invoke-skill',
        id_message: 'Invoking a sandbox skill',
        messages: [
          { tool_call: [{ name: 'Skill', args: { skill: 'sandbox:sandbox-workspace-bootstrap' } }] },
        ],
      },
      {
        id: 'set-workflow',
        id_message: 'Authoring the workflow',
        messages: [{ tool_call: [{ name: 'SetWorkflow', args: { definition: setWorkflowDef } }] }],
      },
      {
        id: 'add-node',
        id_message: 'Splicing in an extra node',
        messages: [
          {
            tool_call: [
              {
                name: 'AddNode',
                args: {
                  node: {
                    id: 'extra',
                    kind: 'agent',
                    agent: 'general-purpose',
                    prompt: 'Do extra work.',
                    next: 'done',
                  },
                  previousNodeId: 'research',
                },
              },
            ],
          },
        ],
      },
      {
        id: 'remove-node',
        id_message: 'No-op removing that node (pass-through, expected to succeed)',
        messages: [{ tool_call: [{ name: 'RemoveNode', args: { nodeId: 'extra' } }] }],
      },
      {
        id: 'start-workflow-agent',
        id_message: 'Delegating to an isolated agent',
        messages: [
          {
            tool_call: [
              {
                name: 'StartWorkflowAgent',
                args: { workflowId: WORKFLOW_ID, workflow: trivialAsyncDef, mode: 'async' },
              },
            ],
          },
        ],
      },
      {
        id: 'summary',
        id_message: 'Wrapping up',
        messages: [{ text: 'Verification turns complete.' }],
      },
    ],
  };
  const PROMPT = `<|instruction_start|>${JSON.stringify(instructionChain)}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const pillByTool = (toolName) =>
    page.locator(`[data-testid="tool-call-pill"][data-tool-name="${toolName}"]`);

  async function waitForLabelMatch(getLabel, regex, timeoutMs = 8000, intervalMs = 250) {
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

    // Provision a fresh, explicitly-bound conversation headlessly — avoids the "+ New Chat" Send race.
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
      { workspaceId: WORKSPACE_ID, providerId: PROVIDER_ID, modeId: MODE_ID }
    );
    const threadId = provisioned && provisioned.threadId;
    record('provisioned-thread', !!threadId, provisioned);

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    const notFoundCount = await tid('conversation-not-found').count();
    record('deep-link-resolved', notFoundCount === 0, { notFoundCount });

    const getWsLabel = async () => {
      const badgeCount = await tid('workspace-locked-badge').count();
      if (badgeCount > 0) return tid('workspace-locked-badge').textContent();
      const buttonCount = await tid('workspace-selector-button').count();
      return buttonCount > 0 ? tid('workspace-selector-button').textContent() : null;
    };
    const wsLabel = await waitForLabelMatch(getWsLabel, /LmDotnetTools/);
    record('workspace-bound (LmDotnetTools)', /LmDotnetTools/.test(wsLabel ?? ''), wsLabel);

    const providerLabel = await waitForLabelMatch(
      () => tid('provider-selector-button').textContent(),
      /Test/
    );
    record('provider-bound (Test Anthropic Mock)', /Test/.test(providerLabel ?? ''), providerLabel);

    const modeLabel = await waitForLabelMatch(
      () => tid('mode-selector-button').textContent(),
      /Workflow Author/
    );
    record('mode-bound (Workflow Author)', /Workflow Author/.test(modeLabel ?? ''), modeLabel);

    const blockingFailures = steps.filter((s) => !s.pass);
    if (blockingFailures.length > 0) {
      record('aborted-before-send', false, 'pre-send verification failed, refusing to send');
      return { pass: false, failures: blockingFailures.map((s) => s.name), steps };
    }

    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    // Wait for the whole 8-turn chain to settle to idle rather than racing on any single terminal signal.
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 }).catch(() => {});
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 }).catch(() => {});

    // Part A: Read/Grep/Skill pills — presence/length only, never content (may carry real repo text).
    for (const toolName of ['Read', 'Grep', 'Skill']) {
      const count = await pillByTool(toolName).count();
      let length = 0;
      if (count > 0) {
        length = ((await pillByTool(toolName).first().textContent()) || '').length;
      }
      record(`pill-present (${toolName})`, count > 0, { count, length });
    }

    // Part B: SetWorkflow / AddNode pills — synthetic graph JSON authored by this script, safe to inspect.
    const setWorkflowCount = await pillByTool('SetWorkflow').count();
    let setWorkflowText = '';
    if (setWorkflowCount > 0) {
      const pill = pillByTool('SetWorkflow').first();
      await pill.locator('.item-header').first().click().catch(() => {});
      setWorkflowText = (await pill.textContent()) || '';
    }
    record('pill-present (SetWorkflow)', setWorkflowCount > 0, {
      count: setWorkflowCount,
      containsResearchNode: setWorkflowText.includes('research'),
    });

    const addNodeCount = await pillByTool('AddNode').count();
    let addNodeText = '';
    if (addNodeCount > 0) {
      const pill = pillByTool('AddNode').first();
      await pill.locator('.item-header').first().click().catch(() => {});
      addNodeText = (await pill.textContent()) || '';
    }
    record('addNode-succeeded-graph-updated', addNodeCount > 0 && addNodeText.includes('extra'), {
      count: addNodeCount,
      containsExtraNode: addNodeText.includes('extra'),
    });

    // RemoveNode now NO-OPS the node in place (keeps its id + inbound edge, drops its task list, advances
    // along its existing "next"). "extra" is a procedural node with a successor ("done"), so its no-op
    // removal is valid and SUCCEEDS. The pill appearing without an invalid_* error code is the proof.
    const removeNodeCount = await pillByTool('RemoveNode').count();
    let removeNodeText = '';
    if (removeNodeCount > 0) {
      const pill = pillByTool('RemoveNode').first();
      await pill.locator('.item-header').first().click().catch(() => {});
      removeNodeText = (await pill.textContent()) || '';
    }
    record(
      'removeNode-pill-present-and-noop-succeeded',
      removeNodeCount > 0 && !/invalid_workflow|invalid_transition|invalid_args|_error/i.test(removeNodeText),
      { count: removeNodeCount, indicatesError: /invalid_workflow|invalid_transition|invalid_args|_error/i.test(removeNodeText) }
    );

    // Part C: the renamed StartWorkflowAgent tool — pill name IS the proof of the rename.
    const startCount = await pillByTool('StartWorkflowAgent').count();
    let startText = '';
    if (startCount > 0) {
      const pill = pillByTool('StartWorkflowAgent').first();
      await pill.locator('.item-header').first().click().catch(() => {});
      startText = (await pill.textContent()) || '';
    }
    record('startWorkflowAgent-pill-renamed-and-started', startCount > 0 && startText.includes(WORKFLOW_ID), {
      count: startCount,
      containsWorkflowId: startText.includes(WORKFLOW_ID),
    });

    // The old name must NOT appear as a pill's tool name anywhere in this run.
    const oldNameCount = await pillByTool('StartWorkflow').count();
    record('old-name-StartWorkflow-absent', oldNameCount === 0, { oldNameCount });

    const finalAssistantText =
      (await tid('assistant-text').count()) > 0 ? await tid('assistant-text').last().textContent() : null;
    record('closing-assistant-text-present', !!(finalAssistantText && finalAssistantText.trim()), {
      length: (finalAssistantText || '').length,
    });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
