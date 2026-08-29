// todo-board.mjs — single-call whole-flow check for the ToDo board panel (#583, PR 3).
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/todo-board.mjs" })
//
// Returns { pass, failures, steps } — plus `blocked: 'backend'` when the read path is not deployed.
//
// ===================================================================================================
// Both backend PRs are merged, so this runs for real: PR 1 (#584's dependency) added
// `GET /api/conversations/{threadId}/todos`, and PR 2 (#590) added the `conversation_todo` frame.
//
// The `blocked: 'backend'` early-out below is KEPT deliberately. It is not dead: it fires whenever
// this script runs against a host built before those PRs — a stale checkout, an old container, a
// bisect — and it is the difference between "the read path is not deployed here" and "the panel is
// broken". Removing it would turn a deployment mistake into a phantom panel bug.
// ===================================================================================================
//
// Prereqs: app running (adjust BASE). Uses the `test-anthropic` MOCK — no real LLM. The mock's
// instruction chain drives the REAL TaskManager tools (`bulk-initialize`, `update-task`, `add-note`),
// so the board under test is built by the same code path an agent would use.
//
// Waiting: gates on `GET /api/conversations/{id}/run-state` reporting `isInProgress: false`, not on
// UI idle and never on a timer. Design doc §9 asks for this, and the reason is real: the board is
// shared with sub-agents (SubAgentManager repopulates from the parent's handlers, so one board per
// conversation), and the parent's stop-button hides as soon as the PARENT's stream ends while
// detached children are still writing to the board. UI-idle would sample the board mid-write.
async (page) => {
  const BASE = 'http://localhost:5273';
  const PROVIDER = 'test-anthropic';

  // Three tasks, then task 1 moved to in-progress with a note. That gives one row of every live
  // status plus a note sub-line — exactly what the panel must show.
  const CHAIN = [
    {
      id: 'todo-seed',
      id_message: 'Setting up the board',
      messages: [
        {
          tool_call: [
            {
              name: 'bulk-initialize',
              args: {
                tasks: [
                  { task: 'Wire the SSE endpoint', subTasks: ['Add the map'], notes: [] },
                  { task: 'Renderer registry', subTasks: [], notes: [] },
                  { task: 'Vitest coverage', subTasks: [], notes: [] },
                ],
              },
            },
          ],
        },
        { tool_call: [{ name: 'update-task', args: { taskId: '1', status: 'in progress' } }] },
        { tool_call: [{ name: 'add-note', args: { taskId: '1', noteText: 'waiting on schema' } }] },
        // Completing task 2 takes TWO calls, and a second agent name.
        //
        // TaskManager refuses `completed` unless the task is already claimed and in progress
        // (TaskManager.cs: "must be claimed and in progress before it can be completed"), so the
        // one-shot `completed` this used to send was rejected by the tool and the board simply never
        // had a done row -- the panel was reporting the board correctly the whole time.
        // The claim needs a DIFFERENT assignee from task 1's because the manager allows only one
        // in-progress task per assignee.
        {
          tool_call: [
            { name: 'update-task', args: { taskId: '2', status: 'in progress', agent: 'e2e-second' } },
          ],
        },
        { tool_call: [{ name: 'update-task', args: { taskId: '2', status: 'completed' } }] },
      ],
    },
  ];
  const PROMPT = `<|instruction_start|>${JSON.stringify({ instruction_chain: CHAIN })}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  const api = (path) =>
    page.evaluate(async (p) => {
      const r = await fetch(p, { headers: { Accept: 'application/json' } });
      const text = await r.text();
      let json = null;
      try {
        json = JSON.parse(text);
      } catch {
        json = null; // an unknown /api path can fall through to the SPA and return index.html
      }
      return { ok: r.ok, status: r.status, json, text: text.slice(0, 400) };
    }, path);

  /** Polls a predicate to a deadline. Never sleeps a fixed budget and calls it done. */
  const pollUntil = async (fn, timeoutMs) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await fn();
      if (last.done) return last;
      await page.waitForTimeout(250);
    }
    return { ...(last ?? {}), done: false, timedOut: true };
  };

  /**
   * Waits for the run to be OVER — which is not the same as `isInProgress === false`.
   *
   * `isInProgress: false` is ALSO true before the run is registered, so polling it alone returns
   * instantly on the very first tick and reports a drained run that never started. That is not
   * hypothetical: it is exactly what this script did on its first real execution, passing "run
   * drained" against an empty transcript while the stop button was still lit.
   *
   * So the gate is the run's own SIDE EFFECT, not its precondition: keep polling until the tool call
   * is actually in the persisted transcript AND the server reports idle. `isInProgress: false` then
   * means "finished" rather than "not started yet", because the transcript proves it started.
   * A 500, a dropped connection, or a 404 falling through to the SPA (json === null) all keep
   * polling rather than being mistaken for a settled run.
   */
  const waitRunSettled = (threadId, timeoutMs) =>
    pollUntil(async () => {
      const rs = await api(`/api/conversations/${encodeURIComponent(threadId)}/run-state`);
      const msgs = await api(`/api/conversations/${encodeURIComponent(threadId)}/messages`);
      const idle = !!(rs.ok && rs.json && rs.json.isInProgress === false);
      const toolsRan =
        Array.isArray(msgs.json) &&
        msgs.json.some(
          (m) => typeof m.messageJson === 'string' && m.messageJson.includes('bulk-initialize')
        );
      return {
        done: idle && toolsRan,
        idle,
        toolsRan,
        messageCount: Array.isArray(msgs.json) ? msgs.json.length : null,
        status: rs.status,
        runState: rs.json,
      };
    }, timeoutMs);

  try {
    // 0. Tee the live socket BEFORE any navigation.
    //
    // The board can be filled two ways: the `GET /todos` hydrate, or a pushed `conversation_todo`
    // frame. A green panel alone does not say which one did it, so the push path could rot silently
    // behind a working hydrate. This captures the RAW frames off the wire, which lets the run assert
    // the push path specifically -- including that the producer stamps `threadId`, without which the
    // client's guard drops the frame and the board never updates.
    await page.addInitScript(() => {
      window.__todoFrames = [];
      const Native = window.WebSocket;
      window.WebSocket = function (...args) {
        const socket = new Native(...args);
        socket.addEventListener('message', (ev) => {
          if (typeof ev.data !== 'string' || !ev.data.includes('conversation_todo')) return;
          try {
            window.__todoFrames.push(JSON.parse(ev.data));
          } catch {
            window.__todoFrames.push({ $unparsed: ev.data.slice(0, 400) });
          }
        });
        return socket;
      };
      window.WebSocket.prototype = Native.prototype;
      Object.assign(window.WebSocket, Native);
    });

    // Every `/todos` RESPONSE the page received. Statuses are what matter: if the app's own hydrate
    // only ever got 404s, then any row on screen cannot have come from the REST read, which makes
    // the push frame the only possible source. That is a stronger claim than counting requests.
    const todoResponses = [];
    page.on('response', (r) => {
      if (r.url().includes('/todos')) todoResponses.push({ url: r.url(), status: r.status() });
    });

    // 1. Fresh chat on the mock provider, then drive the real task tools.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();

    // 1b. REFUSE to measure a conversation that already has a board.
    //
    // Every assertion below counts rows and pins exact ids, so it is only meaningful on a board this
    // run built. Re-running against a browser profile that restored the previous conversation
    // appends a SECOND copy of the same three tasks and the counts quietly go wrong -- which is
    // precisely what happened the first time this ran twice: same threadId, 8 rows, `update-task`
    // hitting the wrong duplicate. That is a dirty fixture, not a product bug, and the two must
    // never be confusable. If a board is already on screen, stop and say so.
    await page.waitForTimeout(500);
    const preRows = await page.evaluate(
      () => document.querySelectorAll('[data-testid="todo-row"]').length
    );
    if (preRows > 0) {
      return {
        pass: false,
        blocked: 'dirty-conversation',
        reason:
          `"+ New Chat" did not yield an empty board -- ${preRows} row(s) were already on screen ` +
          'before this run sent anything, so the app restored an existing conversation. Every count ' +
          'and id assertion below would be measuring the previous run\'s tasks plus this one\'s. ' +
          'Close the browser (fresh context) and re-run. This is NOT a panel failure.',
        failures: ['dirty-conversation'],
        steps,
      };
    }
    record('starting from an empty board (fresh conversation)', true, { preRows });

    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();
    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();
    await tid('stop-button').waitFor({ state: 'visible', timeout: 15000 });

    // 2. Resolve the conversation, then wait for the WHOLE run to drain server-side.
    const convs = await api('/api/conversations?limit=1&offset=0');
    const threadId = convs.json && convs.json[0] && convs.json[0].threadId;
    record('resolved-threadId', !!threadId, { threadId, status: convs.status });
    if (!threadId) {
      return { pass: false, failures: steps.filter((s) => !s.pass).map((s) => s.name), steps };
    }

    const settled = await waitRunSettled(threadId, 90000);
    record('run settled (tool call in transcript AND server idle)', settled.done === true, settled);

    // 2b. THE PUSH PATH, asserted before this script fetches anything itself.
    //
    // Order matters. Everything below reads only what the APP already did: at this point the run has
    // drained and the script has not yet called `/todos`, so nothing here can be satisfied by a
    // hydrate the script itself provoked. The app's own hydrate ran once, on the thread-id watcher,
    // BEFORE any task existed -- so a populated board here can only have come from a pushed frame.
    const appTodoResponses = todoResponses.slice();
    const frames = await page.evaluate(() => window.__todoFrames ?? []);
    const todoFrame = frames.find((f) => f && f.$type === 'conversation_todo');

    record('a conversation_todo frame actually arrived on the socket', !!todoFrame, {
      frameCount: frames.length,
      types: frames.map((f) => (f && f.$type) ?? '(unparsed)'),
    });

    // The carry-forward from review-584: the client type marks `threadId` optional and the frame
    // guard fails CLOSED, so an unstamped frame is silently dropped and the board never updates.
    // This is the assertion that proves the real producer stamps it.
    record(
      'the pushed frame stamps a threadId naming THIS conversation',
      !!todoFrame && typeof todoFrame.threadId === 'string' && todoFrame.threadId === threadId,
      { frameThreadId: todoFrame ? (todoFrame.threadId ?? null) : null, threadId }
    );
    record(
      'the pushed frame carries tasks[] directly',
      !!todoFrame && Array.isArray(todoFrame.tasks) && todoFrame.tasks.length > 0,
      { taskCount: todoFrame && Array.isArray(todoFrame.tasks) ? todoFrame.tasks.length : null }
    );

    // The board is on screen, and the ONLY /todos fetch was the app's initial empty hydrate.
    const pushedRows = await page.evaluate(
      () => document.querySelectorAll('[data-testid="todo-row"]').length
    );
    // Only THIS conversation's hydrates count. On first paint the app restores whatever conversation
    // was last open and hydrates THAT one, which can legitimately return 200 for a board that has
    // nothing to do with this run -- an earlier version of this assertion counted that foreign 200
    // and failed a run whose own hydrate had 404d exactly as expected.
    const mine = appTodoResponses.filter((r) => r.url.includes(encodeURIComponent(threadId)));
    const hydrateEverReturnedABoard = mine.some((r) => r.status === 200);
    record(
      'the panel is populated from the push alone (no successful hydrate happened)',
      pushedRows > 0 && mine.length > 0 && !hydrateEverReturnedABoard,
      {
        pushedRows,
        thisConversationsTodoResponses: mine.map((r) => r.status),
        allTodoResponses: appTodoResponses.map((r) => r.status),
        note: 'rows on screen while every /todos read for THIS thread 404d leaves the push as the only source',
      }
    );

    // 3. Did the task tools actually RUN?
    //
    // This check exists to stop the script misdiagnosing itself. `/todos` answers 404 for BOTH "the
    // route is not deployed" and "the route is fine but this conversation has no board" — a fresh
    // conversation is legitimately 404 until the first `add-task`. So if the instruction chain below
    // ever drifts (a renamed tool, a changed arg), the board stays empty, `/todos` 404s, and without
    // this the script would confidently blame a backend that is working.
    const msgs = await api(`/api/conversations/${encodeURIComponent(threadId)}/messages`);
    const toolsRan =
      Array.isArray(msgs.json) &&
      msgs.json.some((m) => typeof m.messageJson === 'string' && m.messageJson.includes('bulk-initialize'));
    record('task tools ran (bulk-initialize is in the transcript)', toolsRan, {
      messageCount: Array.isArray(msgs.json) ? msgs.json.length : null,
      status: msgs.status,
    });

    // 4. Is the read path deployed? Read together with `toolsRan`, a 404 is now unambiguous.
    const todos = await api(`/api/conversations/${encodeURIComponent(threadId)}/todos`);
    if (todos.status === 404 || todos.json === null) {
      record('GET /todos returned a board', false, { status: todos.status, body: todos.text });
      return {
        pass: false,
        blocked: toolsRan ? 'backend' : 'script',
        reason: toolsRan
          ? 'The task tools ran but GET /api/conversations/{id}/todos is absent (404 or non-JSON), so ' +
            'PRs 1-2 of #583 are not deployed on this host and the panel correctly renders nothing. ' +
            'This is NOT a panel failure.'
          : 'The task tools did NOT run — `bulk-initialize` never reached the transcript — so the board ' +
            'is empty for a reason that has nothing to do with the read path. Fix this script\'s ' +
            'instruction chain (tool names / arg shapes) before drawing any conclusion about the backend.',
        failures: ['todo-board-no-board'],
        steps,
      };
    }
    record('GET /todos returned a board', true, { status: todos.status });

    // 5. The endpoint's payload must match what the client's types pin (camelCase, enum NAMES).
    const wire = Array.isArray(todos.json && todos.json.tasks) ? todos.json.tasks : null;
    const first = wire && wire[0];
    record(
      'wire shape: tasks[] with id/status/title/notes/subTasks',
      !!first &&
        typeof first.id === 'string' &&
        typeof first.title === 'string' &&
        ['NotStarted', 'InProgress', 'Completed', 'Removed'].includes(first.status) &&
        Array.isArray(first.notes) &&
        Array.isArray(first.subTasks),
      { first }
    );

    // 6. The panel is mounted and shows the board.
    await tid('todo-panel').waitFor({ state: 'visible', timeout: 10000 });
    const board = await page.evaluate(() => {
      const text = (sel) => document.querySelector(sel)?.textContent?.trim() ?? null;
      const rows = [...document.querySelectorAll('[data-testid="todo-row"]')].map((n) => ({
        id: n.getAttribute('data-task-id'),
        status: n.getAttribute('data-status'),
      }));
      return {
        done: text('[data-testid="todo-tile-completed"]'),
        active: text('[data-testid="todo-tile-in-progress"]'),
        pending: text('[data-testid="todo-tile-not-started"]'),
        percent: document.querySelector('[data-testid="todo-progress"]')?.getAttribute('data-percent'),
        note: text('[data-testid="todo-row-note"]'),
        rows,
      };
    });

    // bulk-initialize made 1 + 1.1 + 2 + 3; then 1 -> in progress and 2 -> completed.
    record('rows render in tree order with their status',
      board.rows.map((r) => r.id).join(',') === '1,1.1,2,3', board.rows);
    record('active row carries data-status=InProgress',
      board.rows.some((r) => r.id === '1' && r.status === 'InProgress'), board.rows);
    // Exact counts, not a loose /1/ match that "11done" would also satisfy. After the chain above:
    // task 1 in progress, task 2 completed, task 3 and sub-task 1.1 still not started.
    const digits = (s) => (s ?? '').replace(/[^0-9]/g, '');
    record(
      'tiles counted the board (1 done, 1 active, 2 todo)',
      digits(board.done) === '1' && digits(board.active) === '1' && digits(board.pending) === '2',
      board
    );
    record('latest note shows under the active row', board.note === 'waiting on schema', board);
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
