import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';
import type { ProviderRetryMessage } from '@/types';
import { formatProviderRetry } from '@/utils/providerRetry';

// Same fixture shape as useChatContextPressure.test.ts: mock the transport + history APIs so useChat
// opens a fake connection and we hand it frames through the captured onMessage callback.
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

function retryFrame(overrides: Partial<ProviderRetryMessage> = {}): ProviderRetryMessage {
  return {
    $type: MessageType.ProviderRetry,
    role: 'assistant',
    threadId: 'thread-1',
    runId: 'run-1',
    generationId: 'gen-1',
    attempt: 1,
    maxAttempts: 3,
    statusCode: 429,
    delayMs: 7000,
    fromRetryAfter: true,
    ...overrides,
  } as ProviderRetryMessage;
}

describe('useChat — provider_retry frames (#930)', () => {
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

  it('holds the retry while the server waits, renders no bubble, and clears once content arrives', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    const itemsBefore = chat.displayItems.value.length;

    captured[0].onMessage(retryFrame());

    expect(chat.providerRetry.value?.delayMs).toBe(7000);
    expect(chat.displayItems.value.length).toBe(itemsBefore);

    captured[0].onMessage({
      $type: MessageType.TextUpdate,
      role: 'assistant',
      text: 'Hello',
      runId: 'run-1',
      generationId: 'gen-1',
      isUpdate: true,
    });

    expect(chat.providerRetry.value).toBeNull();
  });

  // Each terminal path, with no content after the retry frame: only the terminal event can clear it.
  it.each([
    ['the stream completes', (_chat: ReturnType<typeof useChat>) => captured[0].onDone()],
    ['the server sends an error frame', (_chat: ReturnType<typeof useChat>) => captured[0].onError('boom', 'provider_error')],
    ['the user stops the run', (chat: ReturnType<typeof useChat>) => chat.cancelStream()],
  ])('clears the retry when %s', async (_name, end) => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    captured[0].onMessage(retryFrame());
    expect(chat.providerRetry.value).not.toBeNull();

    await end(chat);

    expect(chat.providerRetry.value).toBeNull();
  });

  // Out-of-band frames: another agent or a notification can speak while the provider wait runs.
  it.each([
    [
      'a notification',
      { $type: MessageType.Notify, role: 'user', text: 'sub-agent done', notify_kind: 'subagent-completion' },
    ],
    [
      'an agent message',
      {
        $type: MessageType.Agent,
        role: 'user',
        text: 'from helper',
        message_id: 'm-1',
        agent_message_type: 'TaskUpdate',
        from_agent_id: 'agent-2',
        from_name: 'helper',
      },
    ],
  ])('keeps the retry when %s arrives mid-wait', async (_name, frame) => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    captured[0].onMessage(retryFrame());

    captured[0].onMessage(frame);

    expect(chat.providerRetry.value?.delayMs).toBe(7000);
  });

  // A `replay_truncated` advisory holds incoming frames until its history fill lands, and the fill can
  // land after the run has ended. A notice drained then would raise a banner nothing clears.
  describe('when the retry is held behind an in-place history fill', () => {
    async function startRunWithRetryHeld() {
      const chat = useChat({ getModeId: () => 'default', provisionThreadId });
      chat.setThreadId('thread-1');
      await chat.sendMessage('hi');
      let resolveHistory!: (v: unknown) => void;
      convMocks.loadConversationMessages.mockImplementation(
        () => new Promise((resolve) => { resolveHistory = resolve; })
      );
      captured[0].onStreamRecovery?.({
        reason: 'replay_truncated',
        threadId: 'thread-1',
        runId: 'run-1',
        generationId: 'gen-1',
      });
      await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));
      captured[0].onMessage(retryFrame());
      expect(chat.providerRetry.value, 'the frame is held, not applied').toBeNull();

      const finishFill = async () => {
        resolveHistory([]);
        await new Promise((resolve) => setTimeout(resolve, 0));
      };
      return { chat, finishFill };
    }

    it('shows it once the fill lands on a run that is still live', async () => {
      const { chat, finishFill } = await startRunWithRetryHeld();

      await finishFill();

      expect(chat.providerRetry.value?.delayMs).toBe(7000);
    });

    it.each([
      ['the user stopped the run', (chat: ReturnType<typeof useChat>) => chat.cancelStream()],
      [
        'the server ended the run with an error',
        (_chat: ReturnType<typeof useChat>) => captured[0].onError('boom', 'provider_error'),
      ],
    ])('drops it when %s during the fill', async (_name, end) => {
      const { chat, finishFill } = await startRunWithRetryHeld();

      await end(chat);
      await finishFill();

      expect(chat.providerRetry.value).toBeNull();
    });
  });

  it('ignores a retry frame stamped for another conversation', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');

    captured[0].onMessage(retryFrame({ threadId: 'thread-other' }));

    expect(chat.providerRetry.value).toBeNull();
  });
});

describe('formatProviderRetry', () => {
  it('names a rate limit, the wait the provider asked for, and the next attempt', () => {
    expect(formatProviderRetry(retryFrame())).toBe(
      'The model provider is rate limiting requests (429). Retrying in 7s, as the provider asked (attempt 2 of 3).'
    );
  });

  it('names a server error and a transport failure', () => {
    expect(formatProviderRetry(retryFrame({ statusCode: 503, fromRetryAfter: false, delayMs: 2000 }))).toBe(
      'The model provider returned an error (503). Retrying in 2s (attempt 2 of 3).'
    );
    expect(formatProviderRetry(retryFrame({ statusCode: null, fromRetryAfter: false, attempt: 2 }))).toBe(
      'The model provider could not be reached. Retrying in 7s (attempt 3 of 3).'
    );
  });
});
