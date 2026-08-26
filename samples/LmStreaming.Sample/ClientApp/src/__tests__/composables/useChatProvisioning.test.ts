import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';

const wsMocks = vi.hoisted(() => ({
  createWebSocketConnection: vi.fn(),
  sendWebSocketMessage: vi.fn(),
  closeWebSocketConnection: vi.fn(),
}));

vi.mock('@/api/wsClient', () => ({
  createWebSocketConnection: wsMocks.createWebSocketConnection,
  sendWebSocketMessage: wsMocks.sendWebSocketMessage,
  closeWebSocketConnection: wsMocks.closeWebSocketConnection,
}));

const conversationsMocks = vi.hoisted(() => ({
  loadConversationMessages: vi.fn(),
  getConversationUsage: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: conversationsMocks.loadConversationMessages,
  getConversationUsage: conversationsMocks.getConversationUsage,
}));

/**
 * #435. `useChat` used to invent `thread-${Date.now()}-...` on the first send. Under
 * `Identity:Enforce=true` that id has no metadata row, so `/ws` refuses the handshake before it is
 * accepted. The composable now has no id of its own: it asks the injected provisioner (wired to
 * `useConversations.createNewConversation`, which POSTs `/api/conversations`) and connects on the
 * id the SERVER minted — and if that fails, no socket is opened at all.
 */
describe('useChat conversation provisioning (#435)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
  });

  it('provisions before opening the socket, and connects on the server-minted id', async () => {
    const order: string[] = [];
    const provisionThreadId = vi.fn(async () => {
      order.push('provision');
      return 'thread-server-minted';
    });
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      order.push('connect');
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ provisionThreadId });
    await chat.sendMessage('hello');

    // Ordering is the whole point: a socket opened first would be refused before the row exists.
    expect(order).toEqual(['provision', 'connect']);
    expect(provisionThreadId).toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection.mock.calls[0]?.[0]?.threadId).toBe(
      'thread-server-minted'
    );
    expect(chat.threadId.value).toBe('thread-server-minted');
  });

  it('opens no socket and surfaces the failure when provisioning is refused', async () => {
    const provisionThreadId = vi.fn(async () => {
      throw new Error('Provider "anthropic" is currently unavailable.');
    });

    const chat = useChat({ provisionThreadId });
    await chat.sendMessage('hello');

    expect(wsMocks.createWebSocketConnection).not.toHaveBeenCalled();
    expect(chat.error.value).toContain('currently unavailable');
    expect(chat.threadId.value).toBeNull();
    // Nothing reached the wire, so the prompt must not stay queued against a run that never starts.
    expect(chat.pendingMessages.value).toHaveLength(0);
  });

  it('does not provision again for a conversation that already has an id', async () => {
    const provisionThreadId = vi.fn(async () => 'thread-should-not-be-used');
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: 'ws-1',
      threadId: options.threadId,
      isConnected: true,
    }));

    const chat = useChat({ provisionThreadId });
    chat.setThreadId('thread-existing');
    await chat.sendMessage('hello');

    expect(provisionThreadId).not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection.mock.calls[0]?.[0]?.threadId).toBe('thread-existing');
  });

  it('refuses to start a conversation when no provisioner was supplied', async () => {
    const chat = useChat({});
    await chat.sendMessage('hello');

    // The old behaviour here was to mint an id locally; that is exactly what #435 removes.
    expect(wsMocks.createWebSocketConnection).not.toHaveBeenCalled();
    expect(chat.threadId.value).toBeNull();
    expect(chat.error.value).toBeTruthy();
  });
});
