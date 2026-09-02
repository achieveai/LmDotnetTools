import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';

// Same fixture shape as useChatUsage.test.ts: mock the transport + history APIs so useChat opens a
// fake connection and we hand it frames through the captured onMessage callback.
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
  getConversationUsage: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
  getConversationUsage: convMocks.getConversationUsage,
}));

const provisionThreadId = async () => 'thread-provisioned';

function pressureFrame(generationOrdinal: number) {
  return {
    $type: MessageType.ContextPressure,
    role: 'assistant',
    threadId: 'thread-1',
    agentId: 'root',
    runId: 'run-1',
    generationId: `gen-${generationOrdinal}`,
    generationOrdinal,
    observedAtUtc: '2026-09-02T10:00:00Z',
    effectiveModelId: 'claude-sonnet-4-5-20250929',
    estimatedInputTokens: 1200,
    measuredInputTokens: null,
    provenance: 'Estimated',
    windowTokens: 200000,
    reserveTokens: 64000,
    utilization: 1200 / 136000,
    activeCheckpointId: null,
    rowsInView: 3,
  };
}

describe('useChat — context_pressure frames (#685)', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getConversationUsage.mockReset();
    convMocks.getConversationUsage.mockResolvedValue(null);
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

  it('holds the newest frame in contextPressure — SET, never accumulated — and renders nothing for it', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    const itemsBefore = chat.displayItems.value.length;

    captured[0].onMessage(pressureFrame(1));
    captured[0].onMessage(pressureFrame(2));

    expect(chat.contextPressure.value?.generationOrdinal).toBe(2);
    expect(chat.contextPressure.value?.windowTokens).toBe(200000);
    // A pressure frame is metadata, not content: the transcript must not grow a bubble for it.
    expect(chat.displayItems.value.length).toBe(itemsBefore);
  });

  it('starts with no frame and drops it on clearMessages (conversation switch / new chat)', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    expect(chat.contextPressure.value).toBeNull();

    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    captured[0].onMessage(pressureFrame(1));
    expect(chat.contextPressure.value).not.toBeNull();

    await chat.clearMessages();

    expect(chat.contextPressure.value).toBeNull();
  });
});
