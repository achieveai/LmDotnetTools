// todo-board.mjs — single-call whole-flow check for the ToDo board panel (#583, PR 3).
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/todo-board.mjs" })
//
// Returns { pass, failures, steps } — plus `blocked: 'backend'` when the read path is not deployed.
//
// ===================================================================================================
// PENDING BACKEND — this script CANNOT pass until PRs 1 and 2 of #583 are merged.
//
//   PR 1 adds `GET /api/conversations/{threadId}/todos` and holds the TaskManager on AgentEntry.
//   PR 2 adds the `conversation_todo` push frame.
//
// Without them the client has no way to learn the board exists, so the panel correctly renders
// nothing and every assertion below fails. The script DETECTS that case up front and returns
// `{ pass: false, blocked: 'backend' }` with an explanation, so an absent backend can never be
// mistaken for a passing run. Re-run it once PR 2 lands; it should go green with no edits.
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
   * Idle means the SERVER says so: an OK response carrying an explicit `isInProgress === false`.
   * A 500, a dropped connection, or a 404 falling through to the SPA (which parses to json === null)
   * all keep polling rather than being mistaken for idle.
   */
  const waitRunIdle = (threadId, timeoutMs) =>
    pollUntil(async () => {
      const rs = await api(`/api/conversations/${encodeURIComponent(threadId)}/run-state`);
      const readable = !!(rs.ok && rs.json && typeof rs.json.isInProgress === 'boolean');
      return { done: readable && rs.json.isInProgress === false, readable, status: rs.status, runState: rs.json };
    }, timeoutMs);

  try {
    // 1. Fresh chat on the mock provider, then drive the real task tools.
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.getByRole('button', { name: '+ New Chat' }).click();
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

    const idle = await waitRunIdle(threadId, 60000);
    record('run drained (server run-state, not UI idle)', idle.done === true, idle);

    // 3. Is the read path deployed at all? A 404 here is PRs 1-2 missing, not a product bug.
    const todos = await api(`/api/conversations/${encodeURIComponent(threadId)}/todos`);
    if (todos.status === 404 || todos.json === null) {
      record('GET /todos is deployed', false, { status: todos.status, body: todos.text });
      return {
        pass: false,
        blocked: 'backend',
        reason:
          'GET /api/conversations/{id}/todos is absent (404 or non-JSON). PRs 1-2 of #583 are not ' +
          'deployed on this host, so the panel correctly renders nothing. This is NOT a panel failure.',
        failures: ['todo-board-backend-missing'],
        steps,
      };
    }
    record('GET /todos is deployed', true, { status: todos.status });

    // 4. The endpoint's payload must match what the client's types pin (camelCase, enum NAMES).
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

    // 5. The panel is mounted and shows the board.
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
    record('tiles counted the board',
      /1/.test(board.done ?? '') && /1/.test(board.active ?? ''), board);
    record('latest note shows under the active row', board.note === 'waiting on schema', board);
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
