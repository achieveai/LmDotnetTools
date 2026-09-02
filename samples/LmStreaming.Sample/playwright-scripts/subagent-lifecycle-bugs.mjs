// subagent-lifecycle-bugs.mjs — single-call Playwright check for the sub-agent lifecycle bugs fixed
// under WI #678 / #669: #688, #689, #690, #691. Runs the WHOLE flow in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/subagent-lifecycle-bugs.mjs" })
//
// Returns { pass, failures, steps }. Asserts only DETERMINISTIC, browser-observable state (DOM
// testids plus read-only `/api/conversations/*` reads).
//
// Uses the MOCK provider `test-anthropic` (no real LLM). The parent's instruction chain drives the
// real Agent / SendMessage / TaskManager tools. The child gets PLAIN TEXT only: the mock answers
// plain text with its lorem fallback, and two things rule chains out for the child —
//   - the completion notification embeds "Task: <task>" in the parent's history and the mock (newest
//     chain wins) would re-run a child chain in the parent and abandon the parent's own;
//   - an agent-to-agent message is delivered inside the escaped <agent-message> envelope, so the
//     mock cannot see a chain carried as message content.
//
// Turn 1 (parent):
//   - Agent(name: worker, background, plain-text task). It finishes at once, which disposes the
//     child's owned provider — the precondition for #690.
//   - bulk-initialize one task (baseline for the assignment notice).
//   - Agent(name: ghost, background, remove_tools: calculate) — a remove_tools-only request the
//     manager rejects (#691): the Agent pill carries the error and no conversation is created for
//     it. With collaboration on, the directory keeps a retired `error` roster row for a while (and
//     the sample persists it as a tab), so the check is "not live, no messages", not "no tab".
// Turn 2 (parent), after the worker's completion notification has landed in the parent:
//   - assign-task(1 → worker): the todo assignment notice is delivered to a FINISHED child. #690:
//     it must be routed through the manager (restart with a fresh provider), so the worker's tab
//     shows the `todo-nudge` notification followed by a new assistant reply and no error.
//   - SendMessage(question → worker). #688: the typed AgentMessage must reach the Anthropic mock as
//     a user text block; the worker's transcript shows the `agent-message` pill ("Agent asked")
//     FOLLOWED by a new assistant reply. If the message were dropped by the request mapper, the
//     restarted run would carry no new user input and no reply could follow the pill.
//   - SendMessage(task_update → worker) from the root, which holds no open delegation from the
//     worker. #689: refused with "no open delegated task" and the next valid action, shown in the
//     root's own SendMessage pill.
//
// Prereq: app running on BASE with `TodoNudges` assignment notices enabled (the default) AND agent
// collaboration on for the mode in use (`AgentCollaboration__Enabled=true`): only the typed
// SendMessage (content / msg_type) exercises #688 and #689; the legacy `prompt` form does neither.
// With collaboration on, every Agent spawn must also carry a `role` and a `description` (both are
// published in the shared agent directory).
async (page) => {
  const BASE = 'http://localhost:5273';
  const PROVIDER = 'test-anthropic';
  const SHOT_DIR = 'B:/sources/LmDotnetTools/.claude/worktrees/lmstreaming-dropped-conversations-f9dc50/.logs/manual';
  const STAMP = Date.now().toString(36);

  const chain = (plan) => `<|instruction_start|>${JSON.stringify(plan)}<|instruction_end|>`;

  const workerPrompt = `Worker ${STAMP}: stand by for instructions.`;
  const ghostPrompt = chain({ instruction_chain: [{ id: 'g1', messages: [{ text: 'ghost should never run' }] }] });
  const question = `Question ${STAMP}: report your status.`;

  const turn1 = chain({
    instruction_chain: [
      {
        id: 'spawn-worker',
        id_message: 'Spawn worker in the background',
        messages: [
          {
            tool_call: [
              {
                name: 'Agent',
                args: {
                  subagent_type: 'general-purpose',
                  name: 'worker',
                  role: 'lane 678 probe worker',
                  description: 'Stands by for a question from the root conversation.',
                  run_in_background: true,
                  prompt: workerPrompt,
                },
              },
            ],
          },
        ],
      },
      {
        id: 'seed-board',
        id_message: 'Seed one task',
        messages: [
          { tool_call: [{ name: 'bulk-initialize', args: { tasks: [{ task: 'Lane 678 nudge probe', subTasks: [], notes: [] }] } }] },
        ],
      },
      {
        id: 'spawn-ghost',
        id_message: 'Attempt a remove_tools-only spawn',
        messages: [
          {
            tool_call: [
              {
                name: 'Agent',
                args: {
                  subagent_type: 'general-purpose',
                  name: 'ghost',
                  role: 'ghost that must be rejected',
                  description: 'Must never be created: remove_tools without an allow-list.',
                  run_in_background: true,
                  remove_tools: 'calculate',
                  prompt: ghostPrompt,
                },
              },
            ],
          },
        ],
      },
      { id: 'turn1-done', id_message: 'Wrap up', messages: [{ text: `Turn 1 done ${STAMP}.` }] },
    ],
  });

  const turn2 = chain({
    instruction_chain: [
      {
        id: 'assign',
        id_message: 'Assign the task to the finished worker',
        messages: [{ tool_call: [{ name: 'assign-task', args: { taskId: '1', assignee: 'worker' } }] }],
      },
      {
        id: 'ask',
        id_message: 'Ask the worker a question',
        messages: [{ tool_call: [{ name: 'SendMessage', args: { target: 'worker', msg_type: 'question', content: question } }] }],
      },
      {
        id: 'update',
        id_message: 'Send a task_update without holding a delegation',
        messages: [
          { tool_call: [{ name: 'SendMessage', args: { target: 'worker', msg_type: 'task_update', content: 'progress: 50%' } }] },
        ],
      },
      { id: 'turn2-done', id_message: 'Wrap up', messages: [{ text: `Turn 2 done ${STAMP}.` }] },
    ],
  });

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const shot = (name) => page.screenshot({ path: `${SHOT_DIR}/lane678-${name}.png`, fullPage: true }).catch(() => {});
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const apiJson = (path) => page.evaluate(async (p) => (await fetch(p)).json(), path);
  const subTabs = () => page.locator('[data-testid="conversation-tab"]:not([data-tab-id="main"])');
  const mainTab = () => page.locator('[data-testid="conversation-tab"][data-tab-id="main"]');
  const openWorkerTab = async () => {
    // Re-select via main so the child view remounts and replays the persisted transcript.
    await mainTab().click();
    await tid('main-view').waitFor({ state: 'visible', timeout: 10000 });
    await subTabs().filter({ hasText: 'worker' }).first().click();
    await tid('subagent-view').waitFor({ state: 'visible', timeout: 10000 });
  };
  const transcript = () => tid('subagent-transcript');
  // Ordered testid trail of the transcript, e.g. ["assistant-text", "notification-pill:todo-nudge", ...].
  const transcriptTrail = () =>
    transcript().evaluate((el) =>
      [...el.querySelectorAll('[data-testid="assistant-text"],[data-testid="notification-pill"],[data-testid="tool-call-pill"]')].map(
        (n) => n.dataset.testid + (n.dataset.notifyKind ? `:${n.dataset.notifyKind}` : '') + (n.dataset.toolName ? `:${n.dataset.toolName}` : '')
      )
    );

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('clear-button').click().catch(() => {});
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();

    // ---- Turn 1 ------------------------------------------------------------------------------
    await send(turn1);
    await waitIdle();
    await page.getByText(`Turn 1 done ${STAMP}.`, { exact: true }).first().waitFor({ timeout: 20000 });
    // The conversation was created by the first send; "Last used" sorting puts it first.
    const threadId = await page.locator('[data-testid="conversation-item"]').first().getAttribute('data-thread-id');

    // The worker's completion is relayed to the parent as a notification: that is the moment its
    // owned provider has been disposed, which is the precondition for the #690 check.
    await page
      .locator('[data-testid="main-view"] [data-testid="notification-pill"][data-notify-kind="subagent-completion"]')
      .first()
      .waitFor({ timeout: 60000 });
    record('worker completion notification reached the parent', true, { threadId });

    await subTabs().first().waitFor({ state: 'attached', timeout: 20000 });
    // Give the 3s tab poll one more cycle so every roster row the server knows has been rendered.
    await page.waitForTimeout(4000);
    const tabLabels = (await subTabs().allInnerTexts()).map((t) => t.trim());

    // #691: the rejected spawn created no conversation. The worker is the only LIVE row; a ghost
    // row, if the collaboration directory retained one, is retired (`error`, not live) and its
    // thread has no messages — nothing was written for it.
    const rows = await apiJson(`/api/conversations/${threadId}/subagents`);
    const liveNames = rows.filter((r) => r.isLive).map((r) => r.name);
    const ghost = rows.find((r) => r.name === 'ghost');
    const ghostMessages = ghost ? await apiJson(`/api/conversations/${ghost.threadId}/messages`) : [];
    record(
      '#691: only the worker is live; the rejected ghost left no conversation behind',
      liveNames.length === 1 &&
        liveNames[0] === 'worker' &&
        (!ghost || (ghost.status === 'error' && !ghost.isLive)) &&
        ghostMessages.length === 0,
      { tabLabels, liveNames, ghost: ghost && { status: ghost.status, isLive: ghost.isLive, threadId: ghost.threadId }, ghostMessages: ghostMessages.length }
    );

    // #691: the ghost Agent pill carries the manager's rejection.
    const agentPills = page.locator('[data-testid="main-view"] [data-testid="tool-call-pill"][data-tool-name="Agent"]');
    const agentPillCount = await agentPills.count();
    await agentPills.nth(agentPillCount - 1).click();
    const rejection = page.locator('[data-testid="main-view"]').getByText(/Cannot specify removeTools/).first();
    const rejectionShown = await rejection.waitFor({ timeout: 10000 }).then(() => true).catch(() => false);
    record('#691: ghost spawn rejected with the removeTools message', rejectionShown, { agentPillCount });
    await shot('01-turn1-main');

    // Baseline: the worker's transcript after its plain-text task (one fallback reply, no pills).
    await openWorkerTab();
    await transcript().locator('[data-testid="assistant-text"]').first().waitFor({ timeout: 20000 });
    const assistantTextsBefore = await transcript().locator('[data-testid="assistant-text"]').count();
    await shot('02-worker-after-turn1');

    // ---- Turn 2 ------------------------------------------------------------------------------
    await mainTab().click();
    await tid('main-view').waitFor({ state: 'visible', timeout: 10000 });
    await send(turn2);
    await waitIdle();
    await page.getByText(`Turn 2 done ${STAMP}.`, { exact: true }).first().waitFor({ timeout: 20000 });

    // #689: the root's task_update was refused — it holds no open delegation from the worker.
    const sendPills = page.locator('[data-testid="main-view"] [data-testid="tool-call-pill"][data-tool-name="SendMessage"]');
    const sendPillCount = await sendPills.count();
    if (sendPillCount > 0) {
      await sendPills.nth(sendPillCount - 1).click();
    }
    const refusalShown = await page
      .locator('[data-testid="main-view"]')
      .getByText(/no open delegated task/)
      .first()
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);
    record('#689: task_update without a delegation is refused with the next valid action', refusalShown, { sendPillCount });
    await shot('03-turn2-main');

    // #688: the question reached the restarted worker: its `agent-message` pill is FOLLOWED by a
    // new assistant reply (the run the delivered message started).
    await openWorkerTab();
    const askedPill = transcript().locator('[data-testid="notification-pill"][data-notify-kind="agent-message"]').first();
    const askedShown = await askedPill.waitFor({ timeout: 60000 }).then(() => true).catch(() => false);
    // The reply streams after the pill; wait for the transcript to grow past it.
    await transcript()
      .locator('[data-testid="notification-pill"][data-notify-kind="agent-message"] ~ [data-testid="assistant-message-group"], [data-testid="assistant-text"]')
      .last()
      .waitFor({ timeout: 30000 })
      .catch(() => {});
    await page.waitForTimeout(1500);
    const trail = await transcriptTrail();
    const askedIdx = trail.indexOf('notification-pill:agent-message');
    const replyAfterAsked = askedIdx >= 0 && trail.slice(askedIdx + 1).includes('assistant-text');
    record('#688: AgentMessage reached the restarted worker (pill followed by a reply)', askedShown && replyAfterAsked, { trail });

    // #690: the assignment notice was delivered through the manager: a todo-nudge notification in
    // the worker's transcript, a new assistant reply after it, and no error.
    const nudgeIdx = trail.indexOf('notification-pill:todo-nudge');
    const replyAfterNudge = nudgeIdx >= 0 && trail.slice(nudgeIdx + 1).includes('assistant-text');
    const assistantTextsAfter = trail.filter((t) => t === 'assistant-text').length;
    const errorCount = await tid('subagent-error').count();
    const disposedText = await transcript().getByText(/disposed object|ObjectDisposedException/i).count();
    record('#690: todo nudge delivered to the finished worker', nudgeIdx >= 0);
    record(
      '#690: worker restarted on a fresh provider (new reply after the nudge, no disposed-provider error)',
      replyAfterNudge && assistantTextsAfter > assistantTextsBefore && errorCount === 0 && disposedText === 0,
      { assistantTextsBefore, assistantTextsAfter, errorCount, disposedText }
    );
    await shot('04-worker-after-turn2');
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot('99-error');
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
