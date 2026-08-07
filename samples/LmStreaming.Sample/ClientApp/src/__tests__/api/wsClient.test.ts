import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  closeWebSocketConnection,
  normalizeKeys,
  createWebSocketConnection,
  openWebSocketConnection,
  sendClientToolResult,
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
    onSandboxSessionRefresh?: (deferred: boolean) => void;
  }): Promise<{ socket: MockWebSocket; connection: WebSocketConnection }> {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const promise = openWebSocketConnection('ws://x/ws', 'thread-42', 'conn-7', {
      onMessage: callbacks.onMessage ?? (() => {}),
      onDone: callbacks.onDone ?? (() => {}),
      onError: (callbacks.onError ?? (() => {})) as (error: string) => void,
      onSandboxSessionRefresh: callbacks.onSandboxSessionRefresh,
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

  it('forwards sandbox refresh through the chat connection wrapper', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const onSandboxSessionRefresh = vi.fn();
    const promise = createWebSocketConnection({
      threadId: 'thread-wrapper',
      onMessage: () => {},
      onDone: () => {},
      onError: () => {},
      onSandboxSessionRefresh,
    });
    const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    socket.readyState = MockWebSocket.OPEN;
    socket.onopen?.();
    await promise;

    socket.onmessage?.({ data: JSON.stringify({ $type: 'sandbox_session_refresh' }) });

    expect(onSandboxSessionRefresh).toHaveBeenCalledWith(false);
  });

  it('surfaces sandbox session refresh as a non-error reconnect signal', async () => {
    const onError = vi.fn();
    const onSandboxSessionRefresh = vi.fn();
    const { socket } = await open({ onError, onSandboxSessionRefresh });

    socket.onmessage?.({ data: JSON.stringify({ $type: 'sandbox_session_refresh' }) });

    expect(onSandboxSessionRefresh).toHaveBeenCalledWith(false);
    expect(onError).not.toHaveBeenCalled();
  });

  it('marks a sandbox refresh as deferred while the current run finishes', async () => {
    const onSandboxSessionRefresh = vi.fn();
    const { socket } = await open({ onSandboxSessionRefresh });

    socket.onmessage?.({ data: JSON.stringify({ $type: 'sandbox_session_refresh_deferred' }) });

    expect(onSandboxSessionRefresh).toHaveBeenCalledWith(true);
  });
});

// #246: browser-hosted client tools (AskUserQuestion). The browser answers a deferred tool call
// over the SAME socket with `{ $type: 'client_tool_result', toolCallId, result, isError? }` and the
// server replies with a typed `client_tool_result_ack` / `client_tool_result_error` frame — the
// resolved value itself always arrives separately as an ordinary ToolCallResultMessage.
describe('sendClientToolResult (#246 outbound frame)', () => {
  function fakeConnection(): { connection: WebSocketConnection; send: ReturnType<typeof vi.fn> } {
    const send = vi.fn();
    const socket = { readyState: WebSocket.OPEN, send } as unknown as WebSocket;
    const connection: WebSocketConnection = {
      socket,
      connectionId: 'conn-1',
      threadId: 'thread-1',
      isConnected: true,
    };
    return { connection, send };
  }

  it('sends the client_tool_result frame with toolCallId and result', () => {
    const { connection, send } = fakeConnection();
    sendClientToolResult(connection, 'call-1', '{"answers":[]}');
    expect(send).toHaveBeenCalledTimes(1);
    const sent = JSON.parse(send.mock.calls[0][0] as string);
    expect(sent).toEqual({ $type: 'client_tool_result', toolCallId: 'call-1', result: '{"answers":[]}' });
  });

  it('includes isError only when true', () => {
    const { connection, send } = fakeConnection();
    sendClientToolResult(connection, 'call-2', 'boom', true);
    const sent = JSON.parse(send.mock.calls[0][0] as string);
    expect(sent).toEqual({ $type: 'client_tool_result', toolCallId: 'call-2', result: 'boom', isError: true });
  });

  it('throws when the socket is not open', () => {
    const socket = { readyState: WebSocket.CLOSED, send: vi.fn() } as unknown as WebSocket;
    const connection: WebSocketConnection = { socket, connectionId: 'c', threadId: 't', isConnected: false };
    expect(() => sendClientToolResult(connection, 'call-3', '{}')).toThrow();
  });
});

describe('openWebSocketConnection client_tool_result_ack / client_tool_result_error inbound frames (#246)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    MockWebSocket.instances = [];
  });

  async function open(callbacks: {
    onMessage?: (m: unknown) => void;
    onDone?: () => void;
    onError?: (error: string, code?: string) => void;
    onClientToolResultAck?: (toolCallId: string, duplicate: boolean) => void;
    onClientToolResultError?: (toolCallId: string | undefined, code: string, message: string) => void;
  }): Promise<{ socket: MockWebSocket; connection: WebSocketConnection }> {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const promise = openWebSocketConnection('ws://x/ws', 'thread-42', 'conn-7', {
      onMessage: callbacks.onMessage ?? (() => {}),
      onDone: callbacks.onDone ?? (() => {}),
      onError: (callbacks.onError ?? (() => {})) as (error: string) => void,
      onClientToolResultAck: callbacks.onClientToolResultAck,
      onClientToolResultError: callbacks.onClientToolResultError,
    });
    const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    socket.readyState = MockWebSocket.OPEN;
    socket.onopen?.();
    const connection = await promise;
    return { socket, connection };
  }

  it('routes a client_tool_result_ack frame with status "resolved" to onClientToolResultAck(id, false)', async () => {
    const onClientToolResultAck = vi.fn();
    const onMessage = vi.fn();
    const { socket } = await open({ onClientToolResultAck, onMessage });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_ack', toolCallId: 'call-1', status: 'resolved' }),
    });

    expect(onClientToolResultAck).toHaveBeenCalledWith('call-1', false);
    // Must not also fall through to the generic message handler.
    expect(onMessage).not.toHaveBeenCalled();
  });

  it('routes a client_tool_result_ack frame with status "duplicate" to onClientToolResultAck(id, true)', async () => {
    const onClientToolResultAck = vi.fn();
    const onMessage = vi.fn();
    const { socket } = await open({ onClientToolResultAck, onMessage });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_ack', toolCallId: 'call-1', status: 'duplicate' }),
    });

    expect(onClientToolResultAck).toHaveBeenCalledWith('call-1', true);
    expect(onMessage).not.toHaveBeenCalled();
  });

  it('routes a client_tool_result_error frame to onClientToolResultError with code/message', async () => {
    const onClientToolResultError = vi.fn();
    const onMessage = vi.fn();
    const { socket } = await open({ onClientToolResultError, onMessage });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_error', toolCallId: 'call-2', code: 'conflict', message: 'already answered' }),
    });

    expect(onClientToolResultError).toHaveBeenCalledWith('call-2', 'conflict', 'already answered');
    expect(onMessage).not.toHaveBeenCalled();
  });

  // PR #249 review: a prior server bug omitted `message` from every client_tool_result_error frame
  // (ChatWebSocketManager.SendClientToolResultErrorAsync serialized only $type/toolCallId/code), which
  // this fallback silently papered over as "Unknown error" for every real rejection. This frame has NO
  // `message` key at all — reproducing exactly the shape the buggy server used to send — to prove the
  // fallback contract independently of whether the server currently populates the field. Regressing the
  // server fix again would surface here as user-visible "Unknown error" for every outcome.
  it('falls back to "Unknown error" when the server omits message entirely', async () => {
    const onClientToolResultError = vi.fn();
    const { socket } = await open({ onClientToolResultError });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_error', toolCallId: 'call-9', code: 'store_failed' }),
    });

    expect(onClientToolResultError).toHaveBeenCalledWith('call-9', 'store_failed', 'Unknown error');
  });

  // Mirrors the real server shape (ChatWebSocketManager now always serializes $type/toolCallId/code/
  // message) for every outcome code it can produce, rather than a single synthetic conflict frame — so
  // this fails if a future server change drops `message` for any one of them specifically.
  it.each([
    ['invalid', 'The client_tool_result frame was malformed and could not be parsed.'],
    ['not_found', 'No deferred tool call was found with this identifier.'],
    ['conflict', 'This tool call was already resolved with different content.'],
    ['store_failed', 'The result could not be saved; please retry.'],
    ['cancelled', 'The request was cancelled before it could be saved; please retry.'],
  ])('passes through server code %s with its safe diagnostic message', async (code, message) => {
    const onClientToolResultError = vi.fn();
    const { socket } = await open({ onClientToolResultError });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'client_tool_result_error', toolCallId: 'call-10', code, message }),
    });

    expect(onClientToolResultError).toHaveBeenCalledWith('call-10', code, message);
  });
});

// `generation_abandoned`: the server cut a provider stream mid-reply, threw that generation away and
// is retrying the same turn under a NEW generation id — on this SAME, still-open socket. It must
// reach its own dedicated callback and must NOT fall through to `onMessage` (it is a control frame
// carrying no renderable content; routing it as a message would push a junk block into the
// transcript).
describe('openWebSocketConnection generation_abandoned inbound frame', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    MockWebSocket.instances = [];
  });

  async function open(callbacks: {
    onMessage?: (m: unknown) => void;
    onDone?: () => void;
    onError?: (error: string, code?: string) => void;
    onGenerationAbandoned?: (info: { threadId?: string; runId?: string; generationId?: string }) => void;
  }): Promise<{ socket: MockWebSocket; connection: WebSocketConnection }> {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const promise = openWebSocketConnection('ws://x/ws', 'thread-42', 'conn-7', {
      onMessage: callbacks.onMessage ?? (() => {}),
      onDone: callbacks.onDone ?? (() => {}),
      onError: (callbacks.onError ?? (() => {})) as (error: string) => void,
      onGenerationAbandoned: callbacks.onGenerationAbandoned,
    });
    const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    socket.readyState = MockWebSocket.OPEN;
    socket.onopen?.();
    const connection = await promise;
    return { socket, connection };
  }

  it('routes a generation_abandoned frame to onGenerationAbandoned and not to onMessage', async () => {
    const onGenerationAbandoned = vi.fn();
    const onMessage = vi.fn();
    const onError = vi.fn();
    const { socket } = await open({ onGenerationAbandoned, onMessage, onError });

    socket.onmessage?.({
      data: JSON.stringify({
        $type: 'generation_abandoned',
        threadId: 'thread-42',
        runId: 'run-1',
        generationId: 'gen-A',
      }),
    });

    expect(onGenerationAbandoned).toHaveBeenCalledTimes(1);
    expect(onGenerationAbandoned).toHaveBeenCalledWith(
      expect.objectContaining({ threadId: 'thread-42', runId: 'run-1', generationId: 'gen-A' }),
    );
    // Must not also fall through to the generic message handler...
    expect(onMessage).not.toHaveBeenCalled();
    // ...and it is NOT a failure: the socket stays open and the run continues.
    expect(onError).not.toHaveBeenCalled();
  });

  // The frame is content-free by contract; this handler is shared with the sub-agent transcript
  // stream, so the diagnostic must stay ids-only even if a server ever over-populates it.
  it('logs only identifiers for a generation_abandoned frame', async () => {
    const logSpy = vi.spyOn(
      logger as unknown as { _logWithComponent: (...a: unknown[]) => void },
      '_logWithComponent',
    );
    const { socket } = await open({ onGenerationAbandoned: vi.fn() });

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'generation_abandoned', generationId: 'gen-A' }),
    });

    const entry = logSpy.mock.calls.find((c) => c[1] === 'Received generation_abandoned');
    expect(entry).toBeTruthy();
    expect(Object.keys(entry![2] as Record<string, unknown>).sort()).toEqual([
      'generationId',
      'runId',
      'threadId',
    ]);
  });

  // The chat wrapper destructures its options AND rebuilds an explicit literal for
  // openWebSocketConnection (it does not spread), so a callback added to only one of the two is
  // silently dropped. Pin the wrapper path independently of the shared opener.
  it('forwards generation_abandoned through the chat connection wrapper', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket as unknown as typeof WebSocket);
    const onGenerationAbandoned = vi.fn();
    const promise = createWebSocketConnection({
      threadId: 'thread-wrapper',
      onMessage: () => {},
      onDone: () => {},
      onError: () => {},
      onGenerationAbandoned,
    });
    const socket = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    socket.readyState = MockWebSocket.OPEN;
    socket.onopen?.();
    await promise;

    socket.onmessage?.({
      data: JSON.stringify({ $type: 'generation_abandoned', generationId: 'gen-A' }),
    });

    expect(onGenerationAbandoned).toHaveBeenCalledWith(
      expect.objectContaining({ generationId: 'gen-A' }),
    );
  });
});
