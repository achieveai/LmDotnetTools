import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';

// #246 — browser-hosted client tools (AskUserQuestion). The finalized server contract:
// outbound `{ $type: 'client_tool_result', toolCallId, result, isError? }` (unchanged, verified in
// wsClient.test.ts); inbound ack `{ $type: 'client_tool_result_ack', toolCallId, status:
// 'resolved' | 'duplicate' }` / error `{ $type: 'client_tool_result_error', toolCallId | null, code
// }`. `useChat.submitClientToolResult` reuses/opens the persistent WebSocket and reconciles the
// promise it returns via the (pre-normalized-to-boolean) `onClientToolResultAck` /
// `onClientToolResultError` callbacks wired into every `createWebSocketConnection` call.

const wsMocks = vi.hoisted(() => ({
  createWebSocketConnection: vi.fn(),
  sendWebSocketMessage: vi.fn(),
  closeWebSocketConnection: vi.fn(),
  sendClientToolResult: vi.fn(),
}));

vi.mock('@/api/wsClient', () => ({
  createWebSocketConnection: wsMocks.createWebSocketConnection,
  sendWebSocketMessage: wsMocks.sendWebSocketMessage,
  closeWebSocketConnection: wsMocks.closeWebSocketConnection,
  sendClientToolResult: wsMocks.sendClientToolResult,
}));

const convMocks = vi.hoisted(() => ({
  loadConversationMessages: vi.fn(),
  getRunState: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
  getRunState: convMocks.getRunState,
}));

function deferredResult(toolCallId: string) {
  return {
    $type: MessageType.ToolCallResult,
    role: 'tool',
    tool_call_id: toolCallId,
    result: '',
    is_deferred: true,
  };
}

function resolvedResult(toolCallId: string, result: string) {
  return {
    $type: MessageType.ToolCallResult,
    role: 'tool',
    tool_call_id: toolCallId,
    result,
    is_deferred: false,
  };
}

describe('useChat — hasPendingClientQuestion (#246)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    wsMocks.sendClientToolResult.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();
  });

  it('is false with no tool results and true once a deferred result arrives', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    expect(chat.hasPendingClientQuestion.value).toBe(false);

    // Feed the deferred placeholder straight through the message handler via a live connection.
    wsMocks.createWebSocketConnection.mockResolvedValue({
      socket: { readyState: WebSocket.OPEN },
      connectionId: 'ws-1',
      threadId: 'thread-1',
      isConnected: true,
    });
    chat.setThreadId('thread-1');
    await chat.sendMessage('ask me something');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    options.onMessage(deferredResult('call-1'));

    expect(chat.hasPendingClientQuestion.value).toBe(true);
  });

  it('flips back to false once the follow-up resolved result overwrites the placeholder', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    wsMocks.createWebSocketConnection.mockResolvedValue({
      socket: { readyState: WebSocket.OPEN },
      connectionId: 'ws-1',
      threadId: 'thread-1',
      isConnected: true,
    });
    chat.setThreadId('thread-1');
    await chat.sendMessage('ask me something');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    options.onMessage(deferredResult('call-1'));
    expect(chat.hasPendingClientQuestion.value).toBe(true);

    options.onMessage(resolvedResult('call-1', '{"answers":[]}'));
    expect(chat.hasPendingClientQuestion.value).toBe(false);
  });
});

describe('useChat — submitClientToolResult (#246)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    wsMocks.sendClientToolResult.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();
  });

  it('opens a connection when none exists, sends the frame, and resolves acked/duplicate:false on a "resolved" ack', async () => {
    let captured: any;
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured = options;
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    const outcomePromise = chat.submitClientToolResult('call-1', '{"answers":[]}', false);
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1));
    await vi.waitFor(() => expect(wsMocks.sendClientToolResult).toHaveBeenCalledTimes(1));
    expect(wsMocks.sendClientToolResult.mock.calls[0][1]).toBe('call-1');
    expect(wsMocks.sendClientToolResult.mock.calls[0][2]).toBe('{"answers":[]}');

    captured.onClientToolResultAck('call-1', false);
    const outcome = await outcomePromise;
    expect(outcome).toEqual({ status: 'acked', duplicate: false });
  });

  it('resolves acked/duplicate:true on a "duplicate" ack', async () => {
    let captured: any;
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured = options;
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    const outcomePromise = chat.submitClientToolResult('call-1', '{"answers":[]}');
    await vi.waitFor(() => expect(wsMocks.sendClientToolResult).toHaveBeenCalledTimes(1));
    captured.onClientToolResultAck('call-1', true);

    const outcome = await outcomePromise;
    expect(outcome).toEqual({ status: 'acked', duplicate: true });
  });

  it('resolves an error outcome when the server rejects with client_tool_result_error', async () => {
    let captured: any;
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured = options;
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    const outcomePromise = chat.submitClientToolResult('call-1', 'boom', true);
    await vi.waitFor(() => expect(wsMocks.sendClientToolResult).toHaveBeenCalledTimes(1));
    captured.onClientToolResultError('call-1', 'conflict', 'Already answered');

    const outcome = await outcomePromise;
    expect(outcome).toEqual({ status: 'error', code: 'conflict', message: 'Already answered' });
  });

  it('reuses the existing open connection for the current thread instead of opening a new one', async () => {
    let captured: any;
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured = options;
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);

    const outcomePromise = chat.submitClientToolResult('call-1', '{"answers":[]}');
    await vi.waitFor(() => expect(wsMocks.sendClientToolResult).toHaveBeenCalledTimes(1));
    // No second connection opened — the existing socket for thread-1 was reused.
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);

    captured.onClientToolResultAck('call-1', false);
    await outcomePromise;
  });
});

describe('useChat — resumeStreamIfActive opens a subscribe-only connection for a pending client question (#246)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    wsMocks.sendClientToolResult.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();
  });

  it('reconnects WITHOUT raising isLoading when no run is in progress but a client question is pending', async () => {
    let captured: any;
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured = options;
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: 'ws-1',
        threadId: options.threadId,
        isConnected: true,
      };
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('ask me something');
    captured.onMessage(deferredResult('call-1'));
    expect(chat.hasPendingClientQuestion.value).toBe(true);

    // Switch away, then back — the run itself is no longer in progress (e.g. it completed while
    // deferred), but the question is still unanswered.
    await chat.disconnectWebSocket();
    convMocks.getRunState.mockResolvedValue({ threadId: 'thread-1', isInProgress: false, currentRunId: null });

    await chat.resumeStreamIfActive('thread-1');

    // Reconnected (subscribe-only) so a submit still has a socket to use, but NOT flagged as streaming.
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value).toBe(false);
    expect(chat.isSending.value).toBe(false);
  });

  it('does not reconnect when no run is in progress and there is no pending client question', async () => {
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: 'ws-1',
      threadId: options.threadId,
      isConnected: true,
    }));

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    await chat.disconnectWebSocket();
    convMocks.getRunState.mockResolvedValue({ threadId: 'thread-1', isInProgress: false, currentRunId: null });

    await chat.resumeStreamIfActive('thread-1');

    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    expect(chat.isLoading.value).toBe(false);
  });
});
