import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSubAgentPanel } from '@/composables/useSubAgentPanel';
// Import the composable's own source text to assert it never couples to useChat.
import panelSource from '@/composables/useSubAgentPanel.ts?raw';
import { MessageType } from '@/types';
import type { SubAgentSummary } from '@/api/subAgentsApi';

// The sub-agent panel is a presentation-only surface: list a conversation's children, focus one to
// view its transcript (persisted history + live stream), send input to it. These tests pin that it
// reuses the shared merge-key/display logic (no duplication) and never couples to useChat.

const subAgentsMocks = vi.hoisted(() => ({
  listSubAgents: vi.fn(),
}));
vi.mock('@/api/subAgentsApi', () => ({
  listSubAgents: subAgentsMocks.listSubAgents,
}));

const wsSubMocks = vi.hoisted(() => ({
  connectSubAgent: vi.fn(),
}));
vi.mock('@/api/subAgentWsClient', () => ({
  connectSubAgent: wsSubMocks.connectSubAgent,
}));

const wsMocks = vi.hoisted(() => ({
  sendWebSocketMessage: vi.fn(),
  closeWebSocketConnection: vi.fn(),
}));
vi.mock('@/api/wsClient', () => ({
  sendWebSocketMessage: wsMocks.sendWebSocketMessage,
  closeWebSocketConnection: wsMocks.closeWebSocketConnection,
}));

const convMocks = vi.hoisted(() => ({
  loadConversationMessages: vi.fn(),
}));
vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
}));

// Captured connectSubAgent invocations (parent/agent + live callbacks), mirroring useChatResume.
interface Captured {
  parentThreadId: string;
  agentId: string;
  callbacks: { onMessage: (m: any) => void; onDone: () => void; onError: (e: string) => void };
  connection: { socket: { readyState: number }; connectionId: string; threadId: string; isConnected: boolean };
}

let captured: Captured[];

function summary(agentId: string, overrides: Partial<SubAgentSummary> = {}): SubAgentSummary {
  return {
    agentId,
    name: `Agent ${agentId}`,
    template: 'research',
    task: 'do work',
    status: 'running',
    threadId: `subagent-${agentId}`,
    lastActivityUtc: null,
    ...overrides,
  };
}

beforeEach(() => {
  captured = [];
  subAgentsMocks.listSubAgents.mockReset();
  wsSubMocks.connectSubAgent.mockReset();
  wsMocks.sendWebSocketMessage.mockReset();
  wsMocks.closeWebSocketConnection.mockReset();
  convMocks.loadConversationMessages.mockReset();

  convMocks.loadConversationMessages.mockResolvedValue([]);
  wsSubMocks.connectSubAgent.mockImplementation(async (parentThreadId: string, agentId: string, callbacks: any) => {
    const connection = {
      socket: { readyState: WebSocket.OPEN },
      connectionId: `sa-${captured.length + 1}`,
      threadId: `subagent-${agentId}`,
      isConnected: true,
    };
    captured.push({ parentThreadId, agentId, callbacks, connection });
    return connection;
  });
});

afterEach(() => {
  vi.useRealTimers();
});

describe('useSubAgentPanel — listing & polling', () => {
  it('refreshChildren populates children from listSubAgents', async () => {
    const kids = [summary('a1'), summary('a2')];
    subAgentsMocks.listSubAgents.mockResolvedValue(kids);

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledWith('parent-1');
    expect(panel.children.value).toEqual(kids);
  });

  it('refreshChildren is a no-op when there is no parent thread', async () => {
    const panel = useSubAgentPanel(() => null);
    await panel.refreshChildren();
    expect(subAgentsMocks.listSubAgents).not.toHaveBeenCalled();
  });

  it('startPolling refreshes immediately and every 3s; stopPolling stops it', async () => {
    vi.useFakeTimers();
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);

    const panel = useSubAgentPanel(() => 'parent-1');
    panel.startPolling();

    // Immediate poll on start.
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(3000);
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(2);

    vi.advanceTimersByTime(3000);
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(3);

    panel.stopPolling();
    vi.advanceTimersByTime(9000);
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(3);
  });
});

// Wire-shape builders (camelCase, as delivered by wsClient.normalizeKeys). Live tool_call /
// tool_call_result / text arrive WITHOUT a runId; only run_assignment carries it.
const GEN = 'gen-child-1';
const RUN = 'run-child-1';

function runAssignment() {
  return { $type: MessageType.RunAssignment, Assignment: { runId: RUN, generationId: GEN, inputIds: [] } };
}
function runCompleted() {
  return { $type: MessageType.RunCompleted, completedRunId: RUN, hasPendingMessages: false };
}
function textUpdate(text: string) {
  return { $type: MessageType.TextUpdate, text, role: 'assistant', generationId: GEN, messageOrderIdx: 0 };
}
function reasoning(text: string) {
  return { $type: MessageType.Reasoning, reasoning: text, visibility: 'Plain', role: 'assistant', generationId: GEN, messageOrderIdx: 1 };
}
function toolCall(id: string) {
  return { $type: MessageType.ToolCall, role: 'assistant', tool_call_id: id, function_name: 'Read', function_args: '{"path":"x"}', generationId: GEN, messageOrderIdx: 2 };
}
function toolResult(id: string, result: string) {
  return { $type: MessageType.ToolCallResult, role: 'tool', tool_call_id: id, result, generationId: GEN, messageOrderIdx: 3 };
}

function assistantText(items: ReturnType<typeof useSubAgentPanel>['focusedDisplayItems']['value']) {
  return items
    .filter((i) => i.type === 'assistant-message')
    .map((i) => (i as { content?: { text?: string } }).content?.text);
}
function toolPills(items: ReturnType<typeof useSubAgentPanel>['focusedDisplayItems']['value'], id: string) {
  return items
    .filter((i) => i.type === 'pill')
    .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
    .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === id));
}

describe('useSubAgentPanel — focus & transcript', () => {
  async function focusFirst(panel: ReturnType<typeof useSubAgentPanel>, agentId = 'a1') {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary(agentId)]);
    await panel.refreshChildren();
    await panel.focusChild(agentId);
  }

  it('loads persisted history then opens a sub-agent stream; focusedDisplayItems reflects history', async () => {
    convMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'p-user', threadId: 'subagent-a1', runId: RUN, generationId: GEN, messageOrderIdx: 0,
        timestamp: 1000, messageType: 'text', role: 'user',
        messageJson: JSON.stringify({ $type: MessageType.Text, role: 'user', text: 'go' }),
      },
      {
        id: 'p-asst', threadId: 'subagent-a1', runId: RUN, generationId: GEN, messageOrderIdx: 1,
        timestamp: 1001, messageType: 'text', role: 'assistant',
        messageJson: JSON.stringify({ $type: MessageType.Text, role: 'assistant', text: 'done!' }),
      },
    ]);

    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);

    expect(convMocks.loadConversationMessages).toHaveBeenCalledWith('subagent-a1');
    expect(wsSubMocks.connectSubAgent).toHaveBeenCalledWith('parent-1', 'a1', expect.any(Object));
    expect(panel.focusedAgentId.value).toBe('a1');
    expect(panel.isFocusedStreaming.value).toBe(true);

    const items = panel.focusedDisplayItems.value;
    expect(items.find((i) => i.type === 'user-message')).toBeTruthy();
    expect(assistantText(items)).toContain('done!');
  });

  it('focusing a second child closes the previous connection first', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1'), summary('a2')]);
    await panel.refreshChildren();

    await panel.focusChild('a1');
    expect(wsSubMocks.connectSubAgent).toHaveBeenCalledTimes(1);
    expect(wsMocks.closeWebSocketConnection).not.toHaveBeenCalled();

    await panel.focusChild('a2');
    // The first connection must be closed before the second opens.
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledTimes(1);
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
    expect(wsSubMocks.connectSubAgent).toHaveBeenCalledTimes(2);
    expect(panel.focusedAgentId.value).toBe('a2');
  });

  it('builds and merges the full modality bar from the live stream', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);

    const cb = captured[0].callbacks;
    cb.onMessage(runAssignment());
    cb.onMessage(textUpdate('Hel'));
    cb.onMessage(textUpdate('lo'));
    cb.onMessage(reasoning('thinking hard'));
    cb.onMessage(toolCall('call_1'));
    cb.onMessage(toolResult('call_1', 'file contents'));

    const items = panel.focusedDisplayItems.value;
    expect(assistantText(items)).toContain('Hello');
    // Reasoning renders inside a pill.
    const reasoningItems = items
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ reasoning?: string }> }).items)
      .filter((m) => m.reasoning === 'thinking hard');
    expect(reasoningItems).toHaveLength(1);
    // Exactly one tool pill for call_1, and its result is captured.
    expect(toolPills(items, 'call_1')).toHaveLength(1);
    expect(panel.getResultForToolCall('call_1')?.result).toBe('file contents');
  });

  it('runId-less live tool call merges with its rehydrated twin (switch-back, no duplicate)', async () => {
    // Persisted history has the UNRESOLVED tool call, stamped with the run's real id.
    convMocks.loadConversationMessages.mockResolvedValue([
      {
        id: 'p-call', threadId: 'subagent-a1', runId: RUN, generationId: GEN, messageOrderIdx: 2,
        timestamp: 1001, messageType: 'tool_call', role: 'assistant',
        messageJson: JSON.stringify(toolCall('call_1')),
      },
    ]);

    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);

    // One pill after rehydrate, not yet resolved.
    expect(toolPills(panel.focusedDisplayItems.value, 'call_1')).toHaveLength(1);
    expect(panel.getResultForToolCall('call_1')).toBeNull();

    // Live stream replays: assignment (carries runId) → the SAME (runId-less) tool call → its result.
    const cb = captured[0].callbacks;
    cb.onMessage(runAssignment());
    cb.onMessage(toolCall('call_1'));
    cb.onMessage(toolResult('call_1', 'resolved'));

    // The replayed runId-less call must MERGE with the rehydrated twin, not duplicate.
    expect(toolPills(panel.focusedDisplayItems.value, 'call_1')).toHaveLength(1);
    expect(panel.getResultForToolCall('call_1')?.result).toBe('resolved');
  });

  it('multi-turn text stays distinct via the server per-turn generationId', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);

    const cb = captured[0].callbacks;
    cb.onMessage(runAssignment());
    // Turn 1 uses the run's generationId; turn 2 gets a fresh one (server-minted).
    cb.onMessage({ $type: MessageType.TextUpdate, text: 'turn one', role: 'assistant', generationId: GEN, messageOrderIdx: 0 });
    cb.onMessage({ $type: MessageType.ToolCall, role: 'assistant', tool_call_id: 't', function_name: 'Read', function_args: '{}', generationId: GEN, messageOrderIdx: 1 });
    cb.onMessage({ $type: MessageType.TextUpdate, text: 'turn two', role: 'assistant', generationId: 'gen-child-2', messageOrderIdx: 0 });

    const texts = assistantText(panel.focusedDisplayItems.value);
    expect(texts).toContain('turn one');
    expect(texts).toContain('turn two');
  });

  it('isFocusedStreaming goes false when the stream completes', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);
    expect(panel.isFocusedStreaming.value).toBe(true);

    captured[0].callbacks.onMessage(runAssignment());
    captured[0].callbacks.onMessage(runCompleted());
    captured[0].callbacks.onDone();
    expect(panel.isFocusedStreaming.value).toBe(false);
  });
});

describe('useSubAgentPanel — send & unfocus', () => {
  async function focusFirst(panel: ReturnType<typeof useSubAgentPanel>, agentId = 'a1') {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary(agentId)]);
    await panel.refreshChildren();
    await panel.focusChild(agentId);
  }

  it('sendToFocusedChild forwards text over the open connection', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);

    panel.sendToFocusedChild('hello child');
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledWith(captured[0].connection, 'hello child');
  });

  it('sendToFocusedChild is a no-op with no focused child', () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    panel.sendToFocusedChild('nobody home');
    expect(wsMocks.sendWebSocketMessage).not.toHaveBeenCalled();
  });

  it('unfocusChild closes the connection and clears focused state', async () => {
    const panel = useSubAgentPanel(() => 'parent-1');
    await focusFirst(panel);
    captured[0].callbacks.onMessage(runAssignment());
    captured[0].callbacks.onMessage(textUpdate('hi'));
    expect(panel.focusedDisplayItems.value.length).toBeGreaterThan(0);

    await panel.unfocusChild();

    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
    expect(panel.focusedAgentId.value).toBeNull();
    expect(panel.focusedDisplayItems.value).toEqual([]);
    expect(panel.isFocusedStreaming.value).toBe(false);
    expect(panel.getResultForToolCall('call_1')).toBeNull();
  });
});

describe('useSubAgentPanel — decoupling', () => {
  it('does not import useChat (no coupling to the parent chat state)', () => {
    // The composable references useChat by name in explanatory comments, but must never import it:
    // it keeps its OWN index/merger/tool-results so focusing a child cannot perturb the parent chat.
    expect(panelSource).not.toMatch(/from\s+['"][^'"]*useChat['"]/);
    expect(panelSource).not.toMatch(/import\(['"][^'"]*useChat['"]\)/);
  });
});
