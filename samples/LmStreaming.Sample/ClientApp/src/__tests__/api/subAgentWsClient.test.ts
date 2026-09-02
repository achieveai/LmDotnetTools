import { afterEach, describe, expect, it, vi } from 'vitest';
import { type SubAgentWsCallbacks, connectSubAgent } from '@/api/subAgentWsClient';

// #246: the focused sub-agent stream (`/ws/subagent`) reuses the SHARED `openWebSocketConnection`
// wiring, so `client_tool_result_ack` / `client_tool_result_error` inbound-frame parsing is already
// covered generically by wsClient.test.ts. What's specific to THIS module is the callback contract:
// `SubAgentWsCallbacks` must accept and forward `onClientToolResultAck` / `onClientToolResultError`
// so a descendant-scoped submit (useSubAgentPanel.submitToFocusedChild) can settle its promise from
// an ack/error that arrives over the CHILD's own connection, not the root's.

class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: MockWebSocket[] = [];

  readyState = MockWebSocket.CONNECTING;
  onopen: ((ev?: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onerror: ((ev?: unknown) => void) | null = null;
  onclose: ((ev: { wasClean: boolean; code: number; reason: string }) => void) | null = null;

  constructor(public url: string) {
    MockWebSocket.instances.push(this);
  }
  close(): void {
    this.readyState = MockWebSocket.CLOSED;
  }
  send(): void {}
}

afterEach(() => {
  vi.unstubAllGlobals();
  MockWebSocket.instances = [];
});

/** Open a child connection with the given optional callbacks and return its (opened) socket. */
async function connect(callbacks: Partial<SubAgentWsCallbacks>) {
  vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
  const promise = connectSubAgent('parent-1', 'child-1', 'subagent-child-1', {
    onMessage: () => {},
    onDone: () => {},
    onError: () => {},
    ...callbacks,
  });
  const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
  socket.readyState = MockWebSocket.OPEN;
  socket.onopen?.();
  await promise;
  return socket;
}

describe('connectSubAgent client_tool_result_ack / client_tool_result_error passthrough (#246)', () => {
  it('routes a client_tool_result_ack frame on the child socket to onClientToolResultAck', async () => {
    const onClientToolResultAck = vi.fn();
    const socket = await connect({ onClientToolResultAck });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_ack', toolCallId: 'call-1', status: 'resolved' }),
    });

    expect(onClientToolResultAck).toHaveBeenCalledWith('call-1', false);
  });

  it('routes a client_tool_result_error frame on the child socket to onClientToolResultError', async () => {
    const onClientToolResultError = vi.fn();
    const socket = await connect({ onClientToolResultError });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_error', toolCallId: 'call-2', code: 'not_found', message: 'Unknown call' }),
    });

    expect(onClientToolResultError).toHaveBeenCalledWith('call-2', 'not_found', 'Unknown call');
  });

  it('does not throw when the callbacks are omitted (backward compatible)', async () => {
    const socket = await connect({});
    expect(() =>
      socket.onmessage?.({
        data: JSON.stringify({ $type: 'client_tool_result_ack', toolCallId: 'call-3', status: 'resolved' }),
      })
    ).not.toThrow();
  });
});

// #278: a child's provider stream can be cut mid-reply and the SAME turn retried under a new
// generation id on this still-open socket. The frame parsing is generic (wsClient), but the child
// callback contract is this module's: `SubAgentWsCallbacks` must accept and forward
// `onGenerationAbandoned` so useSubAgentPanel can retire the abandoned partial from the FOCUSED
// transcript. Without the declaration the panel's handler is dropped at the wrapper.
describe('connectSubAgent generation_abandoned passthrough (#278)', () => {
  it('routes a generation_abandoned frame on the child socket to onGenerationAbandoned', async () => {
    const onGenerationAbandoned = vi.fn();
    const onMessage = vi.fn();
    const socket = await connect({ onGenerationAbandoned, onMessage });

    socket.onmessage?.({
      data: JSON.stringify({
        $type: 'generation_abandoned',
        threadId: 'subagent-child-1',
        runId: 'run-1',
        generationId: 'gen-A',
      }),
    });

    expect(onGenerationAbandoned).toHaveBeenCalledWith(
      expect.objectContaining({ runId: 'run-1', generationId: 'gen-A' })
    );
    // A control frame is not transcript content — routing it to onMessage would render it as a block.
    expect(onMessage).not.toHaveBeenCalled();
  });
});
