import { afterEach, describe, expect, it, vi } from 'vitest';
import { connectQuestionEvents, type QuestionEventHandlers } from '@/api/eventsWsClient';

// `/ws/events` is the app-wide pending-question stream. What is specific to THIS module is the frame
// contract (`snapshot` / `question_pending` / `question_settled`) and the reconnect: a parked
// question is invisible to the conversation list, so if this socket stays down after a drop the only
// thing left is the 30-second sweep, which is the latency the push exists to remove.

class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: MockWebSocket[] = [];

  readyState: number = MockWebSocket.CONNECTING;
  onopen: ((ev?: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onerror: ((ev?: unknown) => void) | null = null;
  onclose: ((ev: { wasClean: boolean; code: number; reason: string }) => void) | null = null;
  closed: Array<{ code: number; reason: string }> = [];

  constructor(public url: string) {
    MockWebSocket.instances.push(this);
  }

  open(): void {
    this.readyState = MockWebSocket.OPEN;
    this.onopen?.();
  }

  deliver(frame: unknown): void {
    this.onmessage?.({ data: JSON.stringify(frame) });
  }

  drop(): void {
    this.readyState = MockWebSocket.CLOSED;
    this.onclose?.({ wasClean: false, code: 1006, reason: '' });
  }

  close(code = 1000, reason = ''): void {
    this.readyState = MockWebSocket.CLOSED;
    this.closed.push({ code, reason });
  }

  send(): void {}
}

afterEach(() => {
  vi.unstubAllGlobals();
  MockWebSocket.instances = [];
});

function question(overrides: Record<string, unknown> = {}) {
  return {
    rootThreadId: 'root-1',
    agentId: null,
    childThreadId: null,
    toolCallId: 'call-1',
    prompt: 'Which colour?',
    conversationTitle: 'Palette',
    agentName: null,
    raisedAtUtc: '2026-09-21T10:00:00.0000000Z',
    ...overrides,
  };
}

function start(handlers: Partial<QuestionEventHandlers> = {}, retryDelaysMs = [0]) {
  vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
  // Typed mocks rather than `{ ...vi.fn(), ...handlers }`: spreading a Partial of the real handler
  // type widens each member to `Handler | Mock`, and the test type-check then refuses `.mock` on it.
  const calls = {
    onSnapshot: vi.fn<QuestionEventHandlers['onSnapshot']>(handlers.onSnapshot),
    onPending: vi.fn<QuestionEventHandlers['onPending']>(handlers.onPending),
    onSettled: vi.fn<QuestionEventHandlers['onSettled']>(handlers.onSettled),
  };
  const stream = connectQuestionEvents(calls, {
    url: 'ws://test/ws/events',
    retryDelaysMs,
    openSocket: (url) => new MockWebSocket(url) as unknown as WebSocket,
  });
  const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
  socket.open();
  return { stream, socket, calls };
}

describe('connectQuestionEvents frame contract', () => {
  it('routes snapshot, question_pending and question_settled to their own handlers', () => {
    const { stream, socket, calls } = start();

    socket.deliver({ $type: 'snapshot', questions: [question(), question({ toolCallId: 'call-2' })] });
    socket.deliver({ $type: 'question_pending', ...question({ toolCallId: 'call-3' }) });
    socket.deliver({ $type: 'question_settled', rootThreadId: 'root-1', toolCallId: 'call-1' });

    expect(calls.onSnapshot).toHaveBeenCalledTimes(1);
    expect(calls.onSnapshot.mock.calls[0][0].map((row: { toolCallId: string }) => row.toolCallId)).toEqual([
      'call-1',
      'call-2',
    ]);
    expect(calls.onPending).toHaveBeenCalledTimes(1);
    expect(calls.onPending.mock.calls[0][0]).toMatchObject({
      rootThreadId: 'root-1',
      toolCallId: 'call-3',
      prompt: 'Which colour?',
      conversationTitle: 'Palette',
    });
    expect(calls.onSettled).toHaveBeenCalledWith('root-1', 'call-1');
    stream.close();
  });

  it("carries a sub-agent's agentId and childThreadId through unchanged", () => {
    const { stream, socket, calls } = start();

    socket.deliver({
      $type: 'question_pending',
      ...question({
        agentId: 'agent-2',
        childThreadId: 'subagent-0123456789ab-agent-2',
        agentName: 'Reviewer',
      }),
    });

    expect(calls.onPending.mock.calls[0][0]).toMatchObject({
      rootThreadId: 'root-1',
      agentId: 'agent-2',
      childThreadId: 'subagent-0123456789ab-agent-2',
      agentName: 'Reviewer',
    });
    stream.close();
  });

  it('drops a question with no identity instead of announcing one that cannot be opened', () => {
    const { stream, socket, calls } = start();

    socket.deliver({ $type: 'question_pending', ...question({ toolCallId: null }) });
    socket.deliver({ $type: 'snapshot', questions: [question({ rootThreadId: null }), question()] });
    socket.deliver({ $type: 'something_else' });
    socket.onmessage?.({ data: 'not json' });

    expect(calls.onPending).not.toHaveBeenCalled();
    expect(calls.onSnapshot).toHaveBeenCalledTimes(1);
    expect(calls.onSnapshot.mock.calls[0][0]).toHaveLength(1);
    stream.close();
  });
});

describe('connectQuestionEvents reconnect', () => {
  it('reopens after a dropped socket and re-applies the snapshot the new connection sends', async () => {
    const { stream, socket, calls } = start();

    socket.deliver({ $type: 'snapshot', questions: [question()] });
    socket.drop();

    await vi.waitFor(() => expect(MockWebSocket.instances).toHaveLength(2));
    const reconnected = MockWebSocket.instances[1];
    reconnected.open();
    // Answered while the socket was down: the second snapshot is how the client finds that out.
    reconnected.deliver({ $type: 'snapshot', questions: [question({ toolCallId: 'call-9' })] });

    expect(calls.onSnapshot).toHaveBeenCalledTimes(2);
    expect(calls.onSnapshot.mock.calls[1][0].map((row: { toolCallId: string }) => row.toolCallId)).toEqual([
      'call-9',
    ]);
    stream.close();
  });

  it('stops reconnecting once closed by the caller', async () => {
    const { stream, socket } = start();

    stream.close();
    expect(socket.closed).toEqual([{ code: 1000, reason: 'Client closing' }]);

    socket.onclose?.({ wasClean: true, code: 1000, reason: 'Client closing' });
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(MockWebSocket.instances).toHaveLength(1);
  });
});
