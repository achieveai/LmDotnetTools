import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
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

// A live NotifyMessage as it arrives on the WebSocket (normalized through IMessageJsonConverter →
// $type present, snake_case structured fields, camelCase identity fields, Role.User).
function notify(overrides: Record<string, unknown> = {}) {
  return {
    $type: MessageType.Notify,
    role: 'user',
    text: '<notification kind="subagent-completion" in-response-to="Spawn:call_7" label="build-fixer">\ndone\n</notification>',
    notify_kind: 'subagent-completion',
    source_tool_name: 'Spawn',
    source_tool_call_id: 'call_7',
    label: 'build-fixer',
    detail: 'done',
    runId: 'run-1',
    generationId: 'notify:0',
    threadId: 'thread-1',
    messageOrderIdx: 0,
    ...overrides,
  };
}

// A persisted NotifyMessage row as the conversation store serves it (messageJson carries $type via
// the controller's IMessageJsonConverter normalization; identity fields also live on the row).
function persistedNotify(id: string, timestamp: number, generationId: string, label: string) {
  return {
    id,
    threadId: 'thread-x',
    runId: 'run-1',
    generationId,
    messageOrderIdx: 0,
    timestamp,
    messageType: 'notify',
    role: 'user',
    messageJson: JSON.stringify({
      $type: MessageType.Notify,
      role: 'user',
      text: `<notification kind="subagent-completion" label="${label}">\ndone\n</notification>`,
      notify_kind: 'subagent-completion',
      source_tool_name: 'Spawn',
      label,
      detail: 'done',
      generationId,
    }),
  };
}

describe('useChat NotifyMessage (out-of-band notification pill)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    conversationsMocks.loadConversationMessages.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: 1 },
      connectionId: `ws-${Date.now()}`,
      threadId: options.threadId,
      isConnected: true,
    }));
  });

  // handleMessage must NOT drop a NotifyMessage as "unknown message type", and displayItems must
  // route it through the notification branch that PRECEDES the `role === 'user'` catch-all — so it
  // renders as a pill, never a user bubble, even though it maps to Role.User.
  it('routes a NotifyMessage to a notification item, not a user bubble', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('hello there');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    // Activate the pending user message so a genuine user bubble coexists with the notification.
    options.onMessage({
      $type: MessageType.RunAssignment,
      role: 'assistant',
      Assignment: { runId: 'run-1', inputIds: ['input-1'], generationId: 'gen-1' },
    });

    options.onMessage(notify({ generationId: 'notify:1' }));

    const items = chat.displayItems.value;
    const notifications = items.filter((i) => i.type === 'notification');
    const users = items.filter((i) => i.type === 'user-message');

    expect(notifications, 'the notification is added, not dropped').toHaveLength(1);
    expect(users, 'the real user message is unaffected').toHaveLength(1);
    // The notification content never leaked into a user bubble.
    expect((users[0] as { content: { text?: string } }).content.text).toBe('hello there');
    expect(
      (notifications[0] as { notification: { notifyKind: string } }).notification.notifyKind
    ).toBe('subagent-completion');
  });

  // Two notifications in one run share runId + messageOrderIdx (both 0) and differ only by the
  // distinct 'notify:<guid>' generationId. They must render as TWO distinct pills in arrival order —
  // no collision onto a single merge key.
  it('renders two notifications in one run as two distinct items, in order (live)', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    await chat.sendMessage('go');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    options.onMessage(notify({ generationId: 'notify:1', label: 'first' }));
    options.onMessage(notify({ generationId: 'notify:2', label: 'second' }));

    const notifications = chat.displayItems.value.filter((i) => i.type === 'notification');
    expect(notifications).toHaveLength(2);
    expect(
      notifications.map((i) => (i as { notification: { label?: string | null } }).notification.label)
    ).toEqual(['first', 'second']);
  });

  it('renders two notifications in one run as two distinct items, in order (after reload)', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      persistedNotify('n1', 1000, 'notify:1', 'first'),
      persistedNotify('n2', 1001, 'notify:2', 'second'),
    ]);

    const chat = useChat({ getModeId: () => 'default' });
    await chat.loadMessagesFromBackend('thread-x');

    const items = chat.displayItems.value;
    const notifications = items.filter((i) => i.type === 'notification');
    const users = items.filter((i) => i.type === 'user-message');

    expect(notifications, 'both reloaded notifications survive').toHaveLength(2);
    expect(users, 'a reloaded notify (Role.User) must not render as a user bubble').toHaveLength(0);
    expect(
      notifications.map((i) => (i as { notification: { label?: string | null } }).notification.label)
    ).toEqual(['first', 'second']);
  });

  // Back-compat: a conversation persisted BEFORE the migration carried the context file as a
  // Role.User TextMessage with a flattened `context_discovery` marker. Reloading it must render one
  // unified context pill through the SAME notification branch — no duplicate, no user bubble.
  it('renders a legacy context_discovery TextMessage as a single context pill (no user bubble)', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'c1',
        threadId: 'thread-x',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 0,
        timestamp: 1000,
        messageType: 'text',
        role: 'user',
        messageJson: JSON.stringify({
          $type: MessageType.Text,
          role: 'user',
          text: '<context-discovery path="CLAUDE.md">…body…</context-discovery>',
          context_discovery: { path: 'CLAUDE.md', truncated: true },
        }),
      },
    ]);

    const chat = useChat({ getModeId: () => 'default' });
    await chat.loadMessagesFromBackend('thread-x');

    const items = chat.displayItems.value;
    const notifications = items.filter((i) => i.type === 'notification');
    const users = items.filter((i) => i.type === 'user-message');

    expect(notifications, 'exactly one unified context pill').toHaveLength(1);
    expect(users, 'the legacy context message must not render as a user bubble').toHaveLength(0);
    const data = (notifications[0] as {
      notification: { notifyKind: string; contextPath?: string | null; contextTruncated?: boolean };
    }).notification;
    expect(data.notifyKind).toBe('context-discovery');
    expect(data.contextPath).toBe('CLAUDE.md');
    expect(data.contextTruncated).toBe(true);
  });
});
