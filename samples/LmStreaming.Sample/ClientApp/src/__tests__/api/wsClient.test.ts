import { describe, expect, it, vi } from 'vitest';
import { closeWebSocketConnection, normalizeKeys, type WebSocketConnection } from '@/api/wsClient';

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
