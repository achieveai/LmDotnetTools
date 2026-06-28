import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';

// Resume bug: an ongoing (streaming) conversation stops streaming if you switch to another
// conversation and come back, or refresh. Switching tears down the WebSocket and returning only
// reloads persisted REST history (everything marked completed) — it never re-subscribes to the
// still-running backend run. These tests pin the fix: on return, when the backend reports an
// in-flight run, the client re-opens the WebSocket (subscribe-only) and resumes the live stream.

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

const convMocks = vi.hoisted(() => ({
  loadConversationMessages: vi.fn(),
  getRunState: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
  getRunState: convMocks.getRunState,
}));

function textUpdate(text: string) {
  return {
    $type: 'text_update',
    text,
    role: 'assistant',
    runId: 'run-1',
    generationId: 'gen-1',
    messageOrderIdx: 0,
  };
}

describe('useChat — resume in-flight stream after switch/refresh', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured.push(options);
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: options.threadId,
        isConnected: true,
      };
    });
    convMocks.loadConversationMessages.mockResolvedValue([]);
  });

  it('reconnects (subscribe-only) and resumes when the conversation has an in-flight run', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // Start a streaming run on the first connection and stream a partial delta.
    await chat.sendMessage('hi');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    captured[0].onMessage(textUpdate('Hel'));

    // Switch away (disconnect), then switch back: load history (empty) + attempt resume.
    await chat.disconnectWebSocket();
    await chat.loadMessagesFromBackend('thread-1');

    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: true,
      currentRunId: 'run-1',
    });
    await chat.resumeStreamIfActive('thread-1');

    // A second connection is opened to RESUME, and it is subscribe-only (no new chat message sent).
    expect(convMocks.getRunState).toHaveBeenCalledWith('thread-1');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledTimes(1); // only the original send

    // Backend replays the in-flight run on the resumed connection, then completes.
    captured[1].onMessage(textUpdate('Hel'));
    captured[1].onMessage(textUpdate('lo'));
    captured[1].onDone();

    expect(chat.isLoading.value).toBe(false);
    const bubbles = chat.displayItems.value.filter((i) => i.type === 'assistant-message');
    expect((bubbles[0] as { content?: { text?: string } }).content?.text).toBe('Hello');
  });

  it('does NOT reconnect when no run is in progress', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    await chat.sendMessage('hi');
    captured[0].onMessage(textUpdate('done'));
    await chat.disconnectWebSocket();
    await chat.loadMessagesFromBackend('thread-1');

    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: false,
      currentRunId: null,
    });
    await chat.resumeStreamIfActive('thread-1');

    // No active run ⇒ no reconnect.
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
  });
});
