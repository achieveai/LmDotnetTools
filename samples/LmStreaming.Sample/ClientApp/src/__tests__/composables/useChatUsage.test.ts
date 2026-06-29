import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';

// Mock the transport + history APIs so useChat can open a (fake) connection and we can hand it
// usage messages directly via the captured onMessage callback.
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
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
}));

function usageMessage(promptTokens: number, completionTokens: number, cachedTokens: number) {
  return {
    $type: MessageType.Usage,
    role: 'assistant',
    runId: 'run-1',
    generationId: 'gen-1',
    usage: {
      prompt_tokens: promptTokens,
      completion_tokens: completionTokens,
      total_tokens: promptTokens + completionTokens,
      // OpenAI-family shape: cached_tokens is a SUBSET of prompt_tokens.
      input_tokens_details: { cached_tokens: cachedTokens },
    },
  };
}

describe('useChat — cumulative usage cached/uncached accounting', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();

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

  it('reports In as uncached input (prompt - cached), disjoint from Cached, summing to Total', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');

    // A real gpt-5.5 turn: 13696 of 14986 prompt tokens were served from cache.
    captured[0].onMessage(usageMessage(14986, 121, 13696));

    const u = chat.cumulativeUsage.value;
    expect(u.promptTokens).toBe(14986); // total input, backend-consistent
    expect(u.cachedTokens).toBe(13696);
    expect(u.uncachedInputTokens).toBe(1290); // 14986 - 13696 — the value shown as "In"
    expect(u.completionTokens).toBe(121);
    expect(u.totalTokens).toBe(15107);
    // In + Cached + Out must reconcile to Total (no double counting).
    expect(u.uncachedInputTokens + u.cachedTokens + u.completionTokens).toBe(u.totalTokens);
  });

  it('accumulates uncached input across multiple turns', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');

    captured[0].onMessage(usageMessage(14986, 121, 13696)); // uncached 1290
    captured[0].onMessage(usageMessage(20000, 200, 18000)); // uncached 2000

    const u = chat.cumulativeUsage.value;
    expect(u.uncachedInputTokens).toBe(3290);
    expect(u.cachedTokens).toBe(31696);
    expect(u.promptTokens).toBe(34986);
  });

  it('falls back to prompt when cached exceeds prompt (additive-cache providers) — never negative', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');

    // Anthropic-style: input_tokens (200) excludes the cache read (13000), which is additive.
    captured[0].onMessage(usageMessage(200, 50, 13000));

    const u = chat.cumulativeUsage.value;
    expect(u.uncachedInputTokens).toBe(200); // not -12800
  });
});
