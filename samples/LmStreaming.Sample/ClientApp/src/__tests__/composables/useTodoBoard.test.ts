import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ref, nextTick } from 'vue';
import { useTodoBoard } from '@/composables/useTodoBoard';
import type { ConversationTodoMessage } from '@/types/messages';
import boardSource from '@/composables/useTodoBoard.ts?raw';

const mocks = vi.hoisted(() => ({
  getConversationTodos: vi.fn(),
}));

vi.mock('@/api/todosApi', () => ({
  getConversationTodos: mocks.getConversationTodos,
}));

/**
 * The board's wire payload, exactly as the design doc specifies it for both `GET /todos` and the
 * `conversation_todo` push frame. Every test below goes through this shape, so a contract change in
 * PR 1/PR 2 surfaces here rather than in production.
 */
function wireTasks() {
  return [
    {
      id: '1',
      status: 'InProgress',
      title: 'Wire the SSE endpoint',
      notes: ['waiting on schema'],
      subTasks: [{ id: '1.1', status: 'Completed', title: 'Add the map', notes: [], subTasks: [] }],
    },
    { id: '2', status: 'NotStarted', title: 'Renderer registry', notes: [], subTasks: [] },
  ];
}

function frame(threadId: string | undefined, tasks: unknown = wireTasks()): ConversationTodoMessage {
  return { $type: 'conversation_todo', threadId, tasks } as unknown as ConversationTodoMessage;
}

/** Defers a mock resolution so a test can interleave a frame with an in-flight REST read. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.getConversationTodos.mockResolvedValue(null);
});

describe('useTodoBoard — architecture', () => {
  it('never imports useChat: the board must be testable without the chat machinery', () => {
    expect(boardSource).not.toContain("from './useChat'");
    expect(boardSource).not.toContain("from '@/composables/useChat'");
  });
});

describe('useTodoBoard — the absent board (PRs 1-2 not merged)', () => {
  it('starts empty with no board, and reports nothing to mount', () => {
    const board = useTodoBoard(
      () => 't1',
      () => null
    );
    expect(board.tasks.value).toEqual([]);
    expect(board.hasBoard.value).toBe(false);
    expect(board.counts.value.total).toBe(0);
  });

  it('renders an empty board when the endpoint answers 404 (api maps that to null)', async () => {
    mocks.getConversationTodos.mockResolvedValue(null);
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    await board.hydrate();

    expect(board.tasks.value).toEqual([]);
    expect(board.hasBoard.value).toBe(false);
    expect(board.isLoading.value).toBe(false);
  });

  it('swallows a thrown fetch into an empty board rather than surfacing an error', async () => {
    // A build without PR 1's route reaches here on every conversation. The panel is an accessory:
    // it must degrade to absent, never to an error banner over the chat.
    mocks.getConversationTodos.mockRejectedValue(new Error('Failed to fetch todos: 500'));
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    await expect(board.hydrate()).resolves.toBeUndefined();
    expect(board.tasks.value).toEqual([]);
    expect(board.isLoading.value).toBe(false);
  });

  it('does not call the endpoint at all with no thread id', async () => {
    const board = useTodoBoard(
      () => null,
      () => null
    );
    await board.hydrate();
    expect(mocks.getConversationTodos).not.toHaveBeenCalled();
  });
});

describe('useTodoBoard — REST hydrate', () => {
  it('loads the documented snapshot shape and derives counts, rows and the active row', async () => {
    mocks.getConversationTodos.mockResolvedValue({ threadId: 't1', tasks: wireTasks() });
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    await board.hydrate();

    expect(mocks.getConversationTodos).toHaveBeenCalledWith('t1');
    expect(board.hasBoard.value).toBe(true);
    expect(board.rows.value.map((r) => [r.id, r.depth])).toEqual([
      ['1', 0],
      ['1.1', 1],
      ['2', 0],
    ]);
    expect(board.counts.value).toEqual({
      done: 1,
      inProgress: 1,
      pending: 1,
      removed: 0,
      total: 3,
    });
    expect(board.activeTaskId.value).toBe('1');
  });

  it('parses the real PR-1 body, envelope fields and all', async () => {
    // Verbatim from src/LmCore/Models/TodoBoardSnapshot.cs: the snapshot sits at the TOP LEVEL (no
    // board/items/snapshot wrapper) and carries schemaVersion + capturedAtUtc alongside tasks.
    mocks.getConversationTodos.mockResolvedValue({
      threadId: 't1',
      schemaVersion: 1,
      capturedAtUtc: '2026-08-28T12:00:00+00:00',
      tasks: wireTasks(),
    });
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    await board.hydrate();

    expect(board.rows.value.map((r) => r.id)).toEqual(['1', '1.1', '2']);
  });

  it('ignores an unrecognized schemaVersion rather than blanking the board', async () => {
    // Rejecting a whole board on a version bump would blank a panel that could still render most of
    // it. The tolerant per-task parser is the guard, not a version gate.
    //
    // NOTE this pins a contract; it does NOT cover a reachable path today. PR 1's projection read
    // filters a newer-schema blob to null, so an old server sitting on a new blob answers 404, not a
    // version-99 body, and the live path always stamps the version the build knows. The only way
    // this payload arrives is from a NEWER server — which is the case the tolerance is for. Do not
    // count this as coverage of code that runs.
    mocks.getConversationTodos.mockResolvedValue({
      threadId: 't1',
      schemaVersion: 99,
      tasks: wireTasks(),
    });
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    await board.hydrate();

    expect(board.hasBoard.value).toBe(true);
  });

  it('holds the loading flag only while in flight', async () => {
    const gate = deferred<{ tasks: unknown[] }>();
    mocks.getConversationTodos.mockReturnValue(gate.promise);
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    const inFlight = board.hydrate();
    expect(board.isLoading.value).toBe(true);

    gate.resolve({ tasks: wireTasks() });
    await inFlight;
    expect(board.isLoading.value).toBe(false);
  });
});

describe('useTodoBoard — live frames', () => {
  it('SETS the board from a frame rather than accumulating into it', async () => {
    mocks.getConversationTodos.mockResolvedValue({ tasks: wireTasks() });
    const board = useTodoBoard(
      () => 't1',
      () => null
    );
    await board.hydrate();
    expect(board.rows.value).toHaveLength(3);

    board.applyFrame(frame('t1', [{ id: '9', status: 'Completed', title: 'only me', notes: [], subTasks: [] }]));

    // Three rows replaced by one, not appended to. The server sends the whole board every time.
    expect(board.rows.value.map((r) => r.id)).toEqual(['9']);
    expect(board.counts.value.done).toBe(1);
  });

  it('applies a frame automatically when the watched frame ref changes', async () => {
    const latest = ref<ConversationTodoMessage | null>(null);
    const board = useTodoBoard(
      () => 't1',
      () => latest.value
    );

    latest.value = frame('t1');
    await nextTick();

    expect(board.hasBoard.value).toBe(true);
    expect(board.activeTaskId.value).toBe('1');
  });

  it('ignores a frame addressed to a DIFFERENT conversation', () => {
    // The frame ref keeps holding the last frame after a conversation switch; without this guard,
    // re-entering a conversation could paint another one's board.
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    board.applyFrame(frame('some-other-thread'));

    expect(board.tasks.value).toEqual([]);
    expect(board.hasBoard.value).toBe(false);
  });

  it('accepts a frame that omits threadId', () => {
    const board = useTodoBoard(
      () => 't1',
      () => null
    );
    board.applyFrame(frame(undefined));
    expect(board.hasBoard.value).toBe(true);
  });

  it('degrades a malformed frame to an empty board instead of throwing', () => {
    const board = useTodoBoard(
      () => 't1',
      () => null
    );
    expect(() => board.applyFrame(frame('t1', 'not-an-array'))).not.toThrow();
    expect(board.tasks.value).toEqual([]);
  });
});

describe('useTodoBoard — supersession', () => {
  it('does NOT let an in-flight REST read clobber a newer live frame', async () => {
    // The ordering bug this exists for: hydrate starts, a push frame lands, hydrate resolves last
    // and paints its older snapshot over the newer one.
    const gate = deferred<{ tasks: unknown[] }>();
    mocks.getConversationTodos.mockReturnValue(gate.promise);
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    const inFlight = board.hydrate();
    board.applyFrame(frame('t1', [{ id: '9', status: 'InProgress', title: 'newer', notes: [], subTasks: [] }]));

    gate.resolve({ tasks: wireTasks() });
    await inFlight;

    expect(board.rows.value.map((r) => r.id)).toEqual(['9']);
    // The superseded read still owns the flag it set, so the panel does not spin forever.
    expect(board.isLoading.value).toBe(false);
  });

  it('does not let a failing older read blank a board a newer frame just set', async () => {
    const gate = deferred<never>();
    mocks.getConversationTodos.mockReturnValue(gate.promise);
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    const inFlight = board.hydrate();
    board.applyFrame(frame('t1'));

    gate.reject(new Error('boom'));
    await inFlight;

    expect(board.hasBoard.value).toBe(true);
  });

  it('lets the newest hydrate win over an older one that resolves later', async () => {
    const first = deferred<{ tasks: unknown[] }>();
    const second = deferred<{ tasks: unknown[] }>();
    mocks.getConversationTodos.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const board = useTodoBoard(
      () => 't1',
      () => null
    );

    const a = board.hydrate();
    const b = board.hydrate();

    second.resolve({ tasks: [{ id: 'new', status: 'NotStarted', title: 'second', notes: [], subTasks: [] }] });
    await b;
    first.resolve({ tasks: [{ id: 'old', status: 'NotStarted', title: 'first', notes: [], subTasks: [] }] });
    await a;

    expect(board.rows.value.map((r) => r.id)).toEqual(['new']);
    expect(board.isLoading.value).toBe(false);
  });
});

describe('useTodoBoard — conversation switch', () => {
  it('drops the previous board and re-hydrates when the thread id changes', async () => {
    const threadId = ref<string | null>('t1');
    mocks.getConversationTodos.mockResolvedValue({ tasks: wireTasks() });
    const board = useTodoBoard(
      () => threadId.value,
      () => null
    );
    await board.hydrate();
    expect(board.hasBoard.value).toBe(true);

    mocks.getConversationTodos.mockResolvedValue(null);
    threadId.value = 't2';
    await nextTick();

    expect(mocks.getConversationTodos).toHaveBeenLastCalledWith('t2');
    // The old board must not linger over the new conversation even for a frame.
    await Promise.resolve();
    expect(board.hasBoard.value).toBe(false);
  });

  it('reset clears the board immediately', async () => {
    mocks.getConversationTodos.mockResolvedValue({ tasks: wireTasks() });
    const board = useTodoBoard(
      () => 't1',
      () => null
    );
    await board.hydrate();

    board.reset();

    expect(board.tasks.value).toEqual([]);
    expect(board.isLoading.value).toBe(false);
  });
});
