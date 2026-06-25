import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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

const conversationsMocks = vi.hoisted(() => ({
  loadConversationMessages: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: conversationsMocks.loadConversationMessages,
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

describe('useChat rehydration merge-key (BUG A)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    conversationsMocks.loadConversationMessages.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  it('merges a streaming Text update into a rehydrated message instead of duplicating it', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'persisted-1',
        threadId: 'thread-x',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 0,
        timestamp: 1000,
        messageType: 'text',
        role: 'assistant',
        messageJson: JSON.stringify({
          $type: MessageType.Text,
          role: 'assistant',
          text: 'partial',
        }),
      },
    ]);

    const chat = useChat({ getModeId: () => 'default' });

    await chat.loadMessagesFromBackend('thread-x');

    // Rehydrated message should render exactly one assistant text bubble.
    const afterLoad = chat.displayItems.value.filter(
      (i) => i.type === 'assistant-message'
    );
    expect(afterLoad).toHaveLength(1);

    await chat.sendMessage('continue');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    // Finalizing Text message shares the SAME run/generation/messageOrderIdx as the rehydrated one.
    options.onMessage({
      $type: MessageType.Text,
      role: 'assistant',
      text: 'partial and more',
      runId: 'run-1',
      generationId: 'gen-1',
      messageOrderIdx: 0,
      threadId: 'thread-x',
    });

    const bubbles = chat.displayItems.value.filter(
      (i) => i.type === 'assistant-message'
    );
    expect(bubbles).toHaveLength(1);
    expect(
      (bubbles[0] as { content?: { text?: string } }).content?.text
    ).toBe('partial and more');
  });
});

describe('useChat rehydration duplicate merge-key (BLOCKER 1)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    conversationsMocks.loadConversationMessages.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  // Stream persistence can hold several records that collapse to the SAME logical merge key
  // (e.g. an intermediate TextUpdate-derived record and the finalizing Text, same
  // run/generation/messageOrderIdx). The old loader pushed EVERY record into messageOrder, so the
  // shared key accumulated multiple entries while messageIndex overwrote — the same final message
  // then rendered/sorted multiple times. The loader must append to messageOrder only on first
  // insert and merge/overwrite the existing entry afterwards.
  it('appends one messageOrder entry when two persisted records share a merge key', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'persisted-1',
        threadId: 'thread-x',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 0,
        timestamp: 1000,
        messageType: 'text',
        role: 'assistant',
        messageJson: JSON.stringify({
          $type: MessageType.Text,
          role: 'assistant',
          text: 'partial',
        }),
      },
      {
        id: 'persisted-2',
        threadId: 'thread-x',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 0,
        timestamp: 1001,
        messageType: 'text',
        role: 'assistant',
        messageJson: JSON.stringify({
          $type: MessageType.Text,
          role: 'assistant',
          text: 'partial and final',
        }),
      },
    ]);

    const chat = useChat({ getModeId: () => 'default' });
    await chat.loadMessagesFromBackend('thread-x');

    // Both records collapse to ONE merge key → exactly one rendered bubble, holding the last value.
    const bubbles = chat.displayItems.value.filter((i) => i.type === 'assistant-message');
    expect(bubbles).toHaveLength(1);
    expect((bubbles[0] as { content?: { text?: string } }).content?.text).toBe('partial and final');
  });
});

describe('useChat socket reuse honors thread (BUG B)', () => {
  beforeEach(() => {
    // happy-dom does not define a WebSocket global; the production reuse guard and the mock
    // socket both read WebSocket.OPEN, so stub the readyState constants for this block.
    vi.stubGlobal('WebSocket', { CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 });

    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: `ws-${options.threadId}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('does not reuse a socket bound to a different thread', async () => {
    const chat = useChat({ getModeId: () => 'default' });

    await chat.sendMessage('to thread A');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    const threadAConnection = await wsMocks.createWebSocketConnection.mock.results[0]?.value;
    expect(threadAConnection.threadId).toBeDefined();

    // Switch to a different thread (conversation switch).
    chat.setThreadId('thread-B');

    await chat.sendMessage('to thread B');

    // The stale thread-A socket must NOT be reused for the thread-B send: a fresh
    // connection bound to thread-B must be created instead.
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
    const threadBOptions = wsMocks.createWebSocketConnection.mock.calls[1]?.[0];
    expect(threadBOptions.threadId).toBe('thread-B');

    // The stale connection should have been closed, not messaged.
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(threadAConnection);
    expect(wsMocks.sendWebSocketMessage).not.toHaveBeenCalledWith(
      threadAConnection,
      'to thread B'
    );
  });
});

describe('useChat concurrent tool-call grouping', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: 1 },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  // GPT-5.5 (OpenAI Responses) emits several finalized tool_call messages in one turn that share
  // runId/generationId/messageOrderIdx and differ only by tool_call_id. They must render as
  // distinct pills inside ONE pillbox — not collapse into a single pill via a colliding merge key.
  it('renders concurrent tool calls in one turn as distinct pills under one pillbox', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('do three calculations');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', runId: 'run-1', generationId: 'gen-1', messageOrderIdx: 0 };
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_1', function_name: 'calculate', function_args: '{"a":12,"b":30}' });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_2', function_name: 'calculate', function_args: '{"a":100,"b":45}' });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_3', function_name: 'calculate', function_args: '{"a":6,"b":7}' });

    const pills = chat.displayItems.value.filter((i) => i.type === 'pill');
    expect(pills, 'concurrent calls stay in ONE pillbox').toHaveLength(1);
    expect(
      (pills[0] as { items: unknown[] }).items,
      'each concurrent tool call is its own pill, not collapsed'
    ).toHaveLength(3);
  });

  // TEST 4: the aggregate single-call ToolsCallMessage branch of getMergeKey. Some providers finalize
  // each concurrent call as its own MessageType.ToolsCall (one tool_call) sharing
  // run/generation/messageOrderIdx and differing only by tool_calls[0].tool_call_id. These must NOT
  // collide on the messageOrderIdx-only key — each must render as a distinct pill.
  it('renders single-call ToolsCallMessages with distinct tool_call_id as distinct pills', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('do two calculations');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', runId: 'run-1', generationId: 'gen-1', messageOrderIdx: 0 };
    options.onMessage({
      ...base,
      $type: MessageType.ToolsCall,
      tool_calls: [{ tool_call_id: 'call_1', function_name: 'calculate', function_args: '{"a":1}' }],
    });
    options.onMessage({
      ...base,
      $type: MessageType.ToolsCall,
      tool_calls: [{ tool_call_id: 'call_2', function_name: 'calculate', function_args: '{"a":2}' }],
    });

    const pills = chat.displayItems.value.filter((i) => i.type === 'pill');
    expect(pills, 'aggregate single-call messages stay in ONE pillbox').toHaveLength(1);
    expect(
      (pills[0] as { items: unknown[] }).items,
      'two single-call ToolsCallMessages with distinct ids must not collide'
    ).toHaveLength(2);
  });
});
