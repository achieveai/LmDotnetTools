import { beforeEach, describe, expect, it, vi } from 'vitest';
import { getDisplayText, isTestInstruction, useChat } from '@/composables/useChat';
import { MessageType } from '@/types';

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

describe('isTestInstruction', () => {
  it('should return true for messages with both start and end markers', () => {
    const text = '<|instruction_start|>{"instruction_chain": []}<|instruction_end|>';
    expect(isTestInstruction(text)).toBe(true);
  });

  it('should return true for complex instruction with JSON content', () => {
    const text = '<|instruction_start|>{"instruction_chain":[{"type":"test","content":"hello"}]}<|instruction_end|>';
    expect(isTestInstruction(text)).toBe(true);
  });

  it('should return false for messages without markers', () => {
    const text = 'Hello, this is a normal message';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for messages with only start marker', () => {
    const text = '<|instruction_start|> some content without end';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for messages with only end marker', () => {
    const text = 'some content without start <|instruction_end|>';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for empty string', () => {
    expect(isTestInstruction('')).toBe(false);
  });
});

describe('getDisplayText', () => {
  it('should return emoji text for instruction messages', () => {
    const text = '<|instruction_start|>{"instruction_chain": []}<|instruction_end|>';
    expect(getDisplayText(text)).toBe('🧪 Test instruction sent');
  });

  it('should return original text for normal messages', () => {
    const text = 'Hello, this is a normal message';
    expect(getDisplayText(text)).toBe('Hello, this is a normal message');
  });

  it('should return original text for empty string', () => {
    expect(getDisplayText('')).toBe('');
  });

  it('should return original text for messages with partial markers', () => {
    const text = '<|instruction_start|> incomplete instruction';
    expect(getDisplayText(text)).toBe('<|instruction_start|> incomplete instruction');
  });
});

describe('useChat mode-aware websocket lifecycle', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  it('recreates websocket with updated modeId after disconnect', async () => {
    let modeId = 'default';
    const chat = useChat({ getModeId: () => modeId });

    await chat.sendMessage('first message');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection.mock.calls[0]?.[0]?.modeId).toBe('default');

    await chat.disconnectWebSocket();

    modeId = 'math-helper';
    await chat.sendMessage('second message');

    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
    expect(wsMocks.createWebSocketConnection.mock.calls[1]?.[0]?.modeId).toBe('math-helper');
  });

  it('keeps reasoning pill when reasoning and text share run/generation without messageOrderIdx', async () => {
    const chat = useChat({ getModeId: () => 'default' });

    await chat.sendMessage('test message');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    options.onMessage({
      $type: MessageType.Reasoning,
      role: 'assistant',
      reasoning: 'Thinking about the answer',
      runId: 'run-1',
      generationId: 'gen-1',
      threadId: 'thread-1',
    });

    options.onMessage({
      $type: MessageType.Text,
      role: 'assistant',
      text: 'Final answer',
      runId: 'run-1',
      generationId: 'gen-1',
      threadId: 'thread-1',
    });

    const items = chat.displayItems.value;
    expect(items.some((i) => i.type === 'pill')).toBe(true);
    expect(
      items.some(
        (i) =>
          i.type === 'assistant-message' &&
          (i as { content?: { text?: string } }).content?.text === 'Final answer'
      )
    ).toBe(true);
  });
});

describe('useChat deferred-auth prompts', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  // Drives the same `options.onAuthEvent` seam the real wsClient calls, so these exercise the
  // production handleAuthEvent / dismissAuthRequest / pendingAuthRequests state machine.
  async function openChatAndCaptureAuthEvent() {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('hi');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options?.onAuthEvent).toBeTypeOf('function');
    return { chat, onAuthEvent: options.onAuthEvent as (e: unknown) => void };
  }

  const providerIds = (chat: { pendingAuthRequests: { value: Array<{ providerId: string }> } }) =>
    chat.pendingAuthRequests.value.map((r) => r.providerId).sort();

  it('adds a pending request on auth_required and supports multiple providers', async () => {
    const { chat, onAuthEvent } = await openChatAndCaptureAuthEvent();

    onAuthEvent({ $type: 'auth_required', providerId: 'github', signinUrl: '/auth/github', reason: 'x' });
    expect(chat.pendingAuthRequests.value).toHaveLength(1);
    expect(chat.pendingAuthRequests.value[0]?.providerId).toBe('github');
    expect(chat.pendingAuthRequests.value[0]?.signinUrl).toBe('/auth/github');

    onAuthEvent({ $type: 'auth_required', providerId: 'ado', signinUrl: '/auth/ado' });
    expect(providerIds(chat)).toEqual(['ado', 'github']);
  });

  it('removes the right provider on auth_completed and on auth_denied', async () => {
    const { chat, onAuthEvent } = await openChatAndCaptureAuthEvent();

    onAuthEvent({ $type: 'auth_required', providerId: 'github', signinUrl: '/auth/github' });
    onAuthEvent({ $type: 'auth_required', providerId: 'ado', signinUrl: '/auth/ado' });
    expect(providerIds(chat)).toEqual(['ado', 'github']);

    // auth_completed dismisses only its own provider...
    onAuthEvent({ $type: 'auth_completed', providerId: 'github' });
    expect(providerIds(chat)).toEqual(['ado']);

    // ...and auth_denied (timeout / failed) is equally terminal.
    onAuthEvent({ $type: 'auth_denied', providerId: 'ado', reason: 'timeout' });
    expect(chat.pendingAuthRequests.value).toHaveLength(0);
  });

  it('dismissAuthRequest removes a provider and is a no-op for an unknown one', async () => {
    const { chat, onAuthEvent } = await openChatAndCaptureAuthEvent();

    onAuthEvent({ $type: 'auth_required', providerId: 'github', signinUrl: '/auth/github' });
    expect(providerIds(chat)).toEqual(['github']);

    chat.dismissAuthRequest('does-not-exist');
    expect(providerIds(chat)).toEqual(['github'], 'unknown provider must not change the set');

    chat.dismissAuthRequest('github');
    expect(chat.pendingAuthRequests.value).toHaveLength(0);
  });
});
