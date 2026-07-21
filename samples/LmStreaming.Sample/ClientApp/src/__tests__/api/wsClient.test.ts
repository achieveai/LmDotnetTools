import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  closeWebSocketConnection,
  normalizeKeys,
  openWebSocketConnection,
  type WebSocketConnection,
} from '@/api/wsClient';
import { logger } from '@/utils';

// BLOCKER 3: tool-call wire JSON uses snake_case identity fields (e.g. `generation_id`). The merge
// key reads camelCase `generationId`, so without a snake_case alias these messages fall back to
// 'default' and fail to group with their camelCase siblings. normalizeKeys must alias the snake_case
// identity fields → camelCase at the deserialize boundary so all downstream consumers see one shape.
describe('normalizeKeys snake_case identity aliasing (BLOCKER 3)', () => {
  it('aliases snake_case generation_id -> generationId', () => {
    const out = normalizeKeys({
      $type: 'tool_call',
      generation_id: 'gen-1',
      run_id: 'run-1',
      parent_run_id: 'parent-1',
      message_order_idx: 2,
      tool_call_id: 'call_1',
    }) as Record<string, unknown>;

    expect(out.generationId).toBe('gen-1');
    expect(out.runId).toBe('run-1');
    expect(out.parentRunId).toBe('parent-1');
    expect(out.messageOrderIdx).toBe(2);
    // tool_call_id is already consumed directly by the merge key; keep it intact.
    expect(out.tool_call_id).toBe('call_1');
  });

  it('still aliases PascalCase keys and does not clobber existing camelCase', () => {
    const out = normalizeKeys({
      GenerationId: 'gen-pascal',
      generation_id: 'gen-snake',
    }) as Record<string, unknown>;

    // An explicit camelCase wins; aliases are write-once and must not overwrite it.
    expect(out.generationId).toBe('gen-pascal');
  });

  it('recurses into nested objects and arrays', () => {
    const out = normalizeKeys({
      tool_calls: [{ generation_id: 'g', tool_call_id: 'c' }],
    }) as { tool_calls: Array<Record<string, unknown>> };

    expect(out.tool_calls[0].generationId).toBe('g');
  });
});

// FINDING D (PR #209): closeWebSocketConnection is the teardown helper the sub-agent panel relies on
// to close a focused child's socket (unfocus / refocus / parent-switch / dispose). It must ONLY call
// socket.close when the socket is OPEN — closing an already CLOSED/CLOSING socket is redundant and, on
// some runtimes, throws. Cover it directly with a fake WebSocket instead of a live connection.
describe('closeWebSocketConnection (FINDING D)', () => {
  function fakeConnection(readyState: number): { connection: WebSocketConnection; close: ReturnType<typeof vi.fn> } {
    const close = vi.fn();
    const socket = { readyState, close } as unknown as WebSocket;
    const connection: WebSocketConnection = {
      socket,
      connectionId: 'conn-1',
      threadId: 'thread-1',
      isConnected: true,
    };
    return { connection, close };
  }

  it('closes an OPEN socket with a normal-closure code and reason', () => {
    const { connection, close } = fakeConnection(WebSocket.OPEN);
    closeWebSocketConnection(connection);
    expect(close).toHaveBeenCalledTimes(1);
    expect(close).toHaveBeenCalledWith(1000, 'Client closing');
  });

  it('is a no-op when the socket is already CLOSED', () => {
    const { connection, close } = fakeConnection(WebSocket.CLOSED);
    closeWebSocketConnection(connection);
    expect(close).not.toHaveBeenCalled();
  });

  it('is a no-op when the socket is CLOSING', () => {
    const { connection, close } = fakeConnection(WebSocket.CLOSING);
    closeWebSocketConnection(connection);
    expect(close).not.toHaveBeenCalled();
  });
});

// A minimal driveable WebSocket so we can exercise openWebSocketConnection's onmessage/onerror/onclose
// handlers deterministically. happy-dom's real WebSocket would attempt a live connection.
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

// PR #209 review — #2 (EUII) + error-code plumbing. The SHARED wsClient onmessage handler now carries
// focused sub-agent transcript frames (prompts/reasoning/tool content). A parse failure must log ONLY
// content-free metadata (never `event.data` / payload text), and structured error frames must forward
// their `code` to onError so callers can distinguish terminal application errors.
describe('openWebSocketConnection onmessage sanitization + error-code plumbing (PR #209)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    MockWebSocket.instances = [];
  });

  async function open(callbacks: {
    onMessage?: (m: unknown) => void;
    onDone?: () => void;
    onError?: (error: string, code?: string) => void;
  }): Promise<{ socket: MockWebSocket; connection: WebSocketConnection }> {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const promise = openWebSocketConnection('ws://x/ws', 'thread-42', 'conn-7', {
      onMessage: callbacks.onMessage ?? (() => {}),
      onDone: callbacks.onDone ?? (() => {}),
      onError: (callbacks.onError ?? (() => {})) as (error: string) => void,
    });
    const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    socket.readyState = MockWebSocket.OPEN;
    socket.onopen?.();
    const connection = await promise;
    return { socket, connection };
  }

  it('a parse failure logs only content-free metadata, never event.data / payload text', async () => {
    const logSpy = vi.spyOn(logger as unknown as { _logWithComponent: (...a: unknown[]) => void }, '_logWithComponent');
    const onError = vi.fn();
    const { socket } = await open({ onError });

    // Malformed JSON whose payload carries sensitive content — must NOT be logged anywhere.
    const secret = 'SECRET_PROMPT_AND_REASONING_CONTENT';
    const malformed = `{"$type":"text","role":"assistant","text":"${secret}",`;
    socket.onmessage?.({ data: malformed });

    // onError still fires so the UI surfaces the failure (behavior otherwise identical).
    expect(onError).toHaveBeenCalledTimes(1);

    // No logger call anywhere included the raw payload text.
    for (const call of logSpy.mock.calls) {
      const serialized = JSON.stringify(call);
      expect(serialized).not.toContain(secret);
      expect(serialized).not.toContain(malformed);
    }

    // The parse-failure log carries content-free metadata only.
    const parseLog = logSpy.mock.calls.find((c) => c[1] === 'Failed to parse WebSocket message');
    expect(parseLog).toBeTruthy();
    const meta = parseLog![2] as Record<string, unknown>;
    expect(Object.prototype.hasOwnProperty.call(meta, 'data')).toBe(false);
    expect(meta.threadId).toBe('thread-42');
    expect(meta.connectionId).toBe('conn-7');
    expect(meta.type).toBe('text'); // the $type discriminator is safe metadata, not content
    expect(typeof meta.byteLength).toBe('number');
    expect(typeof meta.errorName).toBe('string');
  });

  it('forwards a structured error frame code to onError as (message, code)', async () => {
    const onError = vi.fn();
    const { socket } = await open({ onError });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'error', code: 'subagent_unavailable', message: "Sub-agent 'a1' is not available." }),
    });

    expect(onError).toHaveBeenCalledWith("Sub-agent 'a1' is not available.", 'subagent_unavailable');
  });

  it('passes undefined code for an error frame without a code (backward compatible)', async () => {
    const onError = vi.fn();
    const { socket } = await open({ onError });

    socket.onmessage?.({ data: JSON.stringify({ $type: 'error', message: 'Unstructured failure' }) });

    expect(onError).toHaveBeenCalledWith('Unstructured failure', undefined);
  });
});
