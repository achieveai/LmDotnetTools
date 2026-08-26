import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';
import { getMergeKey } from '@/composables/messageMergeKey';
import type { AgentMessage } from '@/types';

import persistedAgentFx from '../fixtures/synthetic/agentmessage.persisted.json';

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

/**
 * #435: `useChat` mints no thread id of its own any more — it asks the SERVER, through the hook
 * `ChatLayout` wires to `useConversations.createNewConversation` (`POST /api/conversations`).
 * These cases all start on a brand-new conversation, so they supply that hook.
 */
const provisionThreadId = async () => 'thread-provisioned';

/**
 * A live AgentMessage as it arrives on the WebSocket: `$type` present, snake_case structured fields
 * beside the camelCase identity fields, and — the trap — `role: 'user'`.
 */
function agentMessage(overrides: Record<string, unknown> = {}) {
  return {
    $type: MessageType.Agent,
    role: 'user',
    text: '<agent-message message-id="am-1" from="reviewer" from-agent-id="agent-2" type="Question">\nWhich repo?\n</agent-message>',
    message_id: 'am-1',
    agent_message_type: 'Question',
    from_agent_id: 'agent-2',
    from_name: 'reviewer',
    body: 'Which repo?',
    runId: 'run-1',
    generationId: 'agentmsg:1',
    threadId: 'thread-1',
    messageOrderIdx: 0,
    ...overrides,
  };
}

describe('useChat AgentMessage (agent-to-agent pill)', () => {
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

  // handleMessage must not drop an AgentMessage as "unknown message type", and displayItems must
  // route it through the notification branch that PRECEDES the `role === 'user'` catch-all.
  it('routes a live AgentMessage to a pill, not a user bubble', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    await chat.sendMessage('hello there');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];
    expect(options).toBeDefined();

    options.onMessage({
      $type: MessageType.RunAssignment,
      role: 'assistant',
      Assignment: { runId: 'run-1', inputIds: ['input-1'], generationId: 'gen-1' },
    });
    options.onMessage(agentMessage());

    const items = chat.displayItems.value;
    const notifications = items.filter((i) => i.type === 'notification');
    const users = items.filter((i) => i.type === 'user-message');

    expect(notifications, 'the agent message is added, not dropped').toHaveLength(1);
    expect(users, 'the human’s own message is the only user bubble').toHaveLength(1);
    expect((users[0] as { content: { text?: string } }).content.text).toBe('hello there');

    const data = (notifications[0] as {
      notification: {
        notifyKind: string;
        label?: string | null;
        detail?: string | null;
        sourceToolCallId?: string | null;
        agentMessageType?: string | null;
      };
    }).notification;
    expect(data.notifyKind).toBe('agent-message');
    expect(data.label, 'the pill names the sender').toBe('reviewer');
    expect(data.sourceToolCallId, 'the sender id drives the agent colour').toBe('agent-2');
    expect(data.agentMessageType).toBe('Question');
    expect(data.detail, 'the body is shown, not the XML envelope').toBe('Which repo?');
  });

  // Several agents can speak into one run at the same messageOrderIdx. Only message_id separates
  // them, so without it in the merge key the later message would overwrite the earlier pill.
  it('keeps two agent messages in one run distinct, in arrival order', async () => {
    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    await chat.sendMessage('go');
    const options = wsMocks.createWebSocketConnection.mock.calls[0]?.[0];

    options.onMessage(agentMessage({ message_id: 'am-1', from_name: 'first' }));
    options.onMessage(
      agentMessage({ message_id: 'am-2', from_name: 'second', agent_message_type: 'Response' })
    );

    const notifications = chat.displayItems.value.filter((i) => i.type === 'notification');
    expect(notifications).toHaveLength(2);
    expect(
      notifications.map((i) => (i as { notification: { label?: string | null } }).notification.label)
    ).toEqual(['first', 'second']);
  });

  it('never collides an agent message with a notification of the same identity', () => {
    const shared = { runId: 'run-1', generationId: 'g-1', messageOrderIdx: 0 };
    const agent = { ...agentMessage(shared) } as unknown as AgentMessage;
    const notify = {
      $type: MessageType.Notify,
      role: 'user',
      text: 'x',
      notify_kind: 'subagent-completion',
      ...shared,
    } as never;

    expect(getMergeKey(agent)).not.toBe(getMergeKey(notify));
  });

  // Reload path: the persisted row's own `role` is 'user' too, so the historical transcript is where
  // a role-first consumer would silently attribute an agent's words to the human.
  it('renders a persisted AgentMessage as a pill after reload', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([persistedAgentFx]);

    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    await chat.loadMessagesFromBackend('thread-root');

    const items = chat.displayItems.value;
    const notifications = items.filter((i) => i.type === 'notification');
    const users = items.filter((i) => i.type === 'user-message');

    expect(notifications, 'the reloaded agent message renders').toHaveLength(1);
    expect(users, 'a reloaded agent message must not become a user bubble').toHaveLength(0);

    const data = (notifications[0] as {
      notification: { notifyKind: string; label?: string | null; agentMessageType?: string | null };
    }).notification;
    expect(data.notifyKind).toBe('agent-message');
    expect(data.label).toBe('reviewer');
    expect(data.agentMessageType).toBe('Question');
  });

  // A conversation persisted BEFORE #244 has no agent messages at all, and must reload byte-for-byte
  // as it always did — the new branch may not claim anything that isn't an AgentMessage.
  it('leaves a pre-#244 transcript rendering exactly as before', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'u1',
        threadId: 'thread-old',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 0,
        timestamp: 1000,
        messageType: 'text',
        role: 'user',
        messageJson: JSON.stringify({ $type: MessageType.Text, role: 'user', text: 'old question' }),
      },
      {
        id: 'a1',
        threadId: 'thread-old',
        runId: 'run-1',
        generationId: 'gen-1',
        messageOrderIdx: 1,
        timestamp: 1001,
        messageType: 'text',
        role: 'assistant',
        messageJson: JSON.stringify({
          $type: MessageType.Text,
          role: 'assistant',
          text: 'old answer',
        }),
      },
    ]);

    const chat = useChat({ getModeId: () => 'default', provisionThreadId });
    await chat.loadMessagesFromBackend('thread-old');

    const items = chat.displayItems.value;
    expect(items.filter((i) => i.type === 'notification')).toHaveLength(0);
    expect(items.filter((i) => i.type === 'user-message')).toHaveLength(1);
    expect(items.filter((i) => i.type === 'assistant-message')).toHaveLength(1);
  });
});
