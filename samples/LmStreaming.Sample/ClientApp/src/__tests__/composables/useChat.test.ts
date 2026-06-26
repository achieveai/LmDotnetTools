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

describe('useChat multi-turn reasoning (BUG #8 thinking collapse)', () => {
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

  // Proven from recording thread-1782467009826-a6pnxeu (GPT-5.5/Copilot, 24 turns): every turn's
  // finalized reasoning shares the RUN's generationId (#105/H1 stamps the run generationId on every
  // message) and the per-turn messageOrderIdx counter resets each turn, so turn N and turn N+1
  // reasoning collide on (generationId, messageOrderIdx). Tool calls avoid this because their merge
  // key already carries tool_call_id; reasoning had no per-instance discriminator, so later turns'
  // thinking collapsed onto the first ~2 blocks. Each turn's reasoning must render as its own block.
  function reasoningTextsIn(items: ReturnType<typeof useChat>['displayItems']['value']): string[] {
    return items
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ $type?: string; reasoning?: string }> }).items)
      .filter((m) => m.$type === MessageType.Reasoning)
      .map((m) => m.reasoning ?? '');
  }

  it('renders each turn reasoning as a distinct block when generationId+messageOrderIdx collide across turns', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('do a multi-step task');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    // runId omitted (null on the wire → 'default'); generationId is the run's, constant across turns.
    const base = { role: 'assistant', generationId: 'gen-run-1', threadId: 'thread-1' };

    // Turn 1: reasoning (moi 1) then a tool call (moi 3).
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Turn 1: read the file', visibility: 1, messageOrderIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_1', function_name: 'Read', function_args: '{}', messageOrderIdx: 3 });

    // Turn 2: reasoning REUSES generationId + messageOrderIdx=1 (the collision) with new content.
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Turn 2: edit the file', visibility: 1, messageOrderIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_2', function_name: 'Edit', function_args: '{}', messageOrderIdx: 3 });

    // Turn 3: same collision again.
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Turn 3: run the tests', visibility: 1, messageOrderIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_3', function_name: 'Bash', function_args: '{}', messageOrderIdx: 3 });

    const texts = reasoningTextsIn(chat.displayItems.value);
    expect(texts, 'every turn reasoning must survive, not collapse onto the first').toEqual([
      'Turn 1: read the file',
      'Turn 2: edit the file',
      'Turn 3: run the tests',
    ]);
  });

  it('does not fragment a single turn streaming reasoning: updates then finalize stay one block', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('stream then finalize');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', generationId: 'gen-run-1', threadId: 'thread-1', messageOrderIdx: 0 };
    // Streamed reasoning deltas then the finalizing reasoning, all same generationId+messageOrderIdx
    // (the within-turn merge case). The turn epoch must NOT advance between them.
    options.onMessage({ ...base, $type: MessageType.ReasoningUpdate, reasoning: 'Think', visibility: 1, chunkIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.ReasoningUpdate, reasoning: 'ing...', visibility: 1, chunkIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Thinking...', visibility: 1 });

    const texts = reasoningTextsIn(chat.displayItems.value);
    expect(texts, 'streamed + finalized reasoning of one turn render as a single block').toEqual(['Thinking...']);
  });

  it('keeps two reasoning blocks within ONE turn (distinct messageOrderIdx) and does not bump turn epoch mid-turn', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('multi-part thinking');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', generationId: 'gen-run-1', threadId: 'thread-1' };
    // One turn, two distinct reasoning parts (moi 1 and 2), then a tool call.
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Part A', visibility: 1, messageOrderIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Part B', visibility: 1, messageOrderIdx: 2 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_1', function_name: 'Read', function_args: '{}', messageOrderIdx: 3 });
    // Next turn reuses moi 1 with new content.
    options.onMessage({ ...base, $type: MessageType.Reasoning, reasoning: 'Part C', visibility: 1, messageOrderIdx: 1 });

    const texts = reasoningTextsIn(chat.displayItems.value);
    expect(texts).toEqual(['Part A', 'Part B', 'Part C']);
  });
});

describe('useChat multi-turn text interleaving (BUG: text between tool calls collapses to top)', () => {
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

  // Project displayItems to a compact shape: assistant text bubbles and tool-call pills in order.
  function layout(items: ReturnType<typeof useChat>['displayItems']['value']): string[] {
    return items
      .filter((i) => i.type === 'assistant-message' || i.type === 'pill')
      .map((i) =>
        i.type === 'assistant-message'
          ? `text:${(i as { content: { text?: string } }).content.text}`
          : `pill:${(i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items
              .map((m) => m.tool_calls?.[0]?.tool_call_id)
              .join(',')}`
      );
  }

  // The backend narrates with text BETWEEN tool calls across turns; every turn shares the run's
  // generationId (#105/H1) and resets messageOrderIdx, so turn N and turn N+1 text collide on the
  // client merge key AND the merger accumulator. Before the fix the text collapsed into a single
  // block pinned to the top (first-insert position) instead of interleaving with the tool pills.
  it('interleaves streamed text with the tool calls of each turn instead of collapsing at the top', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('multi-step with narration');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', generationId: 'gen-run-1', threadId: 'thread-1' };

    // Turn 1: streamed narration (moi 0) then a tool call (moi 1).
    options.onMessage({ ...base, $type: MessageType.TextUpdate, text: 'Let me ', messageOrderIdx: 0, chunkIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.TextUpdate, text: 'read the file.', messageOrderIdx: 0, chunkIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.Text, text: 'Let me read the file.', messageOrderIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_1', function_name: 'Read', function_args: '{}', messageOrderIdx: 1 });

    // Turn 2: narration reuses moi 0 (reset) with NEW content, then another tool call.
    options.onMessage({ ...base, $type: MessageType.TextUpdate, text: 'Now I will ', messageOrderIdx: 0, chunkIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.TextUpdate, text: 'edit it.', messageOrderIdx: 0, chunkIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.Text, text: 'Now I will edit it.', messageOrderIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_2', function_name: 'Edit', function_args: '{}', messageOrderIdx: 1 });

    expect(layout(chat.displayItems.value)).toEqual([
      'text:Let me read the file.',
      'pill:call_1',
      'text:Now I will edit it.',
      'pill:call_2',
    ]);
  });

  it('keeps finalized (non-streamed) text distinct per turn when generationId+messageOrderIdx collide', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('finalized text per turn');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    const base = { role: 'assistant', generationId: 'gen-run-1', threadId: 'thread-1' };
    options.onMessage({ ...base, $type: MessageType.Text, text: 'First answer.', messageOrderIdx: 0 });
    options.onMessage({ ...base, $type: MessageType.ToolCall, tool_call_id: 'call_1', function_name: 'Read', function_args: '{}', messageOrderIdx: 1 });
    options.onMessage({ ...base, $type: MessageType.Text, text: 'Second answer.', messageOrderIdx: 0 });

    const texts = chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text);
    expect(texts).toEqual(['First answer.', 'Second answer.']);
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
