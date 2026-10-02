import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';
import type { PersistedMessage } from '@/api/conversationsApi';

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
  getConversationUsage: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: conversationsMocks.loadConversationMessages,
  getConversationUsage: conversationsMocks.getConversationUsage,
}));

function stored(
  id: string,
  seq: number,
  runId: string,
  role: 'user' | 'assistant',
  text: string,
  messageOrderIdx = 0
): PersistedMessage {
  return {
    id,
    seq,
    threadId: 'thread-1',
    runId,
    generationId: `gen-${role}-${runId}`,
    messageOrderIdx,
    timestamp: seq,
    messageType: 'text',
    // The wire shape: the row's Role is the server enum's name, while the message JSON inside it is
    // lowercase. Fixtures that used lowercase here hid a live-path bug that only matched lowercase.
    role: role === 'user' ? 'User' : 'Assistant',
    messageJson: JSON.stringify({ $type: MessageType.Text, role, text }),
  };
}

// Fork actions name STORED message ids, which the client used to drop. They ride on separate fields
// (`persistedId`, `seq`) so the display/merge id scheme is unchanged.
describe('useChat stored message identity (fork support)', () => {
  beforeEach(() => {
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => ({
      socket: { readyState: WebSocket.OPEN },
      connectionId: 'ws-1',
      threadId: options.threadId,
      isConnected: true,
    }));
    conversationsMocks.loadConversationMessages.mockReset();
    conversationsMocks.getConversationUsage.mockReset();
    conversationsMocks.getConversationUsage.mockResolvedValue(null);
  });

  it('keeps the stored id and seq on rehydrated messages without changing their display id', async () => {
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      stored('pm-u1', 1, 'run-1', 'user', 'question'),
      stored('pm-a1', 2, 'run-1', 'assistant', 'answer'),
    ]);
    const chat = useChat({ provisionThreadId: async () => 'thread-1' });

    await chat.loadMessagesFromBackend('thread-1');

    const [user, answer] = chat.displayItems.value;
    expect([user.persistedId, user.seq, answer.persistedId, answer.seq]).toEqual(['pm-u1', 1, 'pm-a1', 2]);
    expect(user.id).not.toBe('pm-u1');
    expect(answer.id).not.toBe('pm-a1');
  });

  it('teaches a live user message its stored id once its run completes (2 runs, matched by run + order)', async () => {
    const chat = useChat({ provisionThreadId: async () => 'thread-1' });
    await chat.sendMessage('first');
    const socket = wsMocks.createWebSocketConnection.mock.calls[0][0];
    const runAssignment = (runId: string, inputId: string) => ({
      $type: MessageType.RunAssignment,
      Assignment: { runId, generationId: `gen-${runId}`, inputIds: [inputId] },
    });
    const runCompleted = (runId: string) => ({
      $type: MessageType.RunCompleted,
      role: 'assistant',
      completedRunId: runId,
      isError: false,
    });

    socket.onMessage(runAssignment('run-1', 'input-1'));
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      stored('pm-u1', 1, 'run-1', 'user', 'first'),
      stored('pm-a1', 2, 'run-1', 'assistant', 'reply'),
    ]);
    socket.onMessage(runCompleted('run-1'));
    await vi.waitFor(() =>
      expect(chat.displayItems.value.find((i) => i.id === 'input-1')?.persistedId).toBe('pm-u1')
    );

    await chat.sendMessage('second');
    socket.onMessage(runAssignment('run-2', 'input-2'));
    conversationsMocks.loadConversationMessages.mockResolvedValue([
      stored('pm-u1', 1, 'run-1', 'user', 'first'),
      stored('pm-a1', 2, 'run-1', 'assistant', 'reply'),
      stored('pm-u2', 3, 'run-2', 'user', 'second'),
    ]);
    socket.onMessage(runCompleted('run-2'));
    await vi.waitFor(() =>
      expect(chat.displayItems.value.find((i) => i.id === 'input-2')?.persistedId).toBe('pm-u2')
    );
    expect(chat.displayItems.value.find((i) => i.id === 'input-1')?.persistedId).toBe('pm-u1');
  });
});
