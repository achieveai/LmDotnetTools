import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { effectScope } from 'vue';
import { useSubAgentPanel } from '@/composables/useSubAgentPanel';
import { LIVE_BUFFER_MAX } from '@/composables/useSubAgentPanel';
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
  callbacks: {
    onMessage: (m: any) => void;
    onDone: () => void;
    onError: (e: string) => void;
    onClose?: (info: { wasClean: boolean; code: number; reason: string }) => void;
  };
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

describe('useSubAgentPanel — hardening', () => {
  it('focusChild concurrent-guard: a later focus wins; the stale focus closes its late connection without clobbering state', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1'), summary('a2')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    // a1's connection opens LAZILY (deferred) so a2 can supersede a1 while a1 is still awaiting connect.
    const a1Conn = { socket: { readyState: WebSocket.OPEN }, connectionId: 'sa-a1', threadId: 'subagent-a1', isConnected: true };
    const a2Conn = { socket: { readyState: WebSocket.OPEN }, connectionId: 'sa-a2', threadId: 'subagent-a2', isConnected: true };
    let openA1: (() => void) | undefined;
    wsSubMocks.connectSubAgent.mockImplementation((parentThreadId: string, agentId: string, callbacks: any) => {
      if (agentId === 'a1') {
        return new Promise((resolve) => {
          openA1 = () => {
            captured.push({ parentThreadId, agentId, callbacks, connection: a1Conn });
            resolve(a1Conn);
          };
        });
      }
      captured.push({ parentThreadId, agentId, callbacks, connection: a2Conn });
      return Promise.resolve(a2Conn);
    });

    // Start focusing a1 — it parks awaiting connectSubAgent's deferred promise.
    const p1 = panel.focusChild('a1');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    expect(openA1, 'a1 focus should be parked inside connectSubAgent').toBeTypeOf('function');

    // a2 fully focuses and wins while a1 is still parked.
    await panel.focusChild('a2');
    expect(panel.focusedAgentId.value).toBe('a2');

    // a1's socket finally opens; the superseded focus must CLOSE it and not adopt it.
    openA1!();
    await p1;

    expect(panel.focusedAgentId.value).toBe('a2');
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(a1Conn);
    // Sending routes to a2's connection, proving a1's late connection never clobbered focusedConnection.
    panel.sendToFocusedChild('to a2');
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledWith(a2Conn, 'to a2');
  });

  it('focusChild same-agent concurrent-guard: a superseding re-focus of the SAME agent closes the stale connection', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    // Two connections for the SAME agent; the first opens lazily so the second can supersede it.
    const firstConn = { socket: { readyState: WebSocket.OPEN }, connectionId: 'sa-a1-1', threadId: 'subagent-a1', isConnected: true };
    const secondConn = { socket: { readyState: WebSocket.OPEN }, connectionId: 'sa-a1-2', threadId: 'subagent-a1', isConnected: true };
    let openFirst: (() => void) | undefined;
    let call = 0;
    wsSubMocks.connectSubAgent.mockImplementation((parentThreadId: string, agentId: string, callbacks: any) => {
      call += 1;
      if (call === 1) {
        return new Promise((resolve) => {
          openFirst = () => {
            captured.push({ parentThreadId, agentId, callbacks, connection: firstConn });
            resolve(firstConn);
          };
        });
      }
      captured.push({ parentThreadId, agentId, callbacks, connection: secondConn });
      return Promise.resolve(secondConn);
    });

    // First focus of a1 parks awaiting connect.
    const p1 = panel.focusChild('a1');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    expect(openFirst, 'first a1 focus should be parked inside connectSubAgent').toBeTypeOf('function');

    // A SECOND focus of the SAME agent supersedes the first via the monotonic token.
    await panel.focusChild('a1');
    expect(panel.focusedAgentId.value).toBe('a1');

    // The first (stale) connection finally opens; it must be CLOSED, not adopted.
    openFirst!();
    await p1;

    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(firstConn);
    // Sending routes to the SECOND connection, proving the stale one never clobbered focusedConnection.
    panel.sendToFocusedChild('hi');
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledWith(secondConn, 'hi');
  });

  it('onClose (unexpected): stops streaming, resumes once, and a second close is terminal (no loop)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    await panel.focusChild('a1');
    expect(captured).toHaveLength(1);
    expect(panel.isFocusedStreaming.value).toBe(true);

    // An UNEXPECTED (clean) server close — the backpressure-drop path fires neither onDone nor onError.
    captured[0].callbacks.onClose!({ wasClean: true, code: 1000, reason: '' });
    // The spinner must stop immediately (view can't hang).
    expect(panel.isFocusedStreaming.value).toBe(false);

    // One automatic resume: a fresh connection is opened for the same agent.
    for (let i = 0; i < 6; i++) await Promise.resolve();
    expect(captured, 'exactly one resume reconnect').toHaveLength(2);
    expect(captured[1].agentId).toBe('a1');

    // Sends route to the resumed connection, not the dead one.
    panel.sendToFocusedChild('after resume');
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledWith(captured[1].connection, 'after resume');

    // A SECOND unexpected close must NOT loop — resume budget is spent, so it ends terminal.
    wsMocks.sendWebSocketMessage.mockClear();
    captured[1].connection.socket.readyState = WebSocket.CLOSED;
    captured[1].callbacks.onClose!({ wasClean: true, code: 1000, reason: '' });
    expect(panel.isFocusedStreaming.value).toBe(false);
    for (let i = 0; i < 6; i++) await Promise.resolve();
    expect(captured, 'no third reconnect after the one-shot budget is spent').toHaveLength(2);

    // The now-dead socket rejects sends instead of throwing on a closed socket.
    panel.sendToFocusedChild('to dead socket');
    expect(wsMocks.sendWebSocketMessage).not.toHaveBeenCalled();
  });

  it('onClose after an intentional unfocus does not auto-resume', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    await panel.focusChild('a1');
    expect(captured).toHaveLength(1);

    await panel.unfocusChild();
    // A late onClose from the socket WE closed must be ignored (no resume).
    captured[0].callbacks.onClose!({ wasClean: true, code: 1000, reason: '' });
    for (let i = 0; i < 6; i++) await Promise.resolve();
    expect(captured, 'an intentional unfocus must not trigger a reconnect').toHaveLength(1);
    expect(panel.focusedAgentId.value).toBeNull();
  });

  it('onScopeDispose stops polling and unfocuses the child when the host scope is disposed', async () => {
    vi.useFakeTimers();
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);

    const scope = effectScope();
    let panel!: ReturnType<typeof useSubAgentPanel>;
    scope.run(() => {
      panel = useSubAgentPanel(() => 'parent-1');
    });

    panel.startPolling();
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(1);

    await panel.refreshChildren();
    await panel.focusChild('a1');
    expect(panel.focusedAgentId.value).toBe('a1');

    // Disposing the host scope must tear down the interval AND the focused child connection.
    scope.stop();

    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
    expect(panel.focusedAgentId.value).toBeNull();

    const callsBefore = subAgentsMocks.listSubAgents.mock.calls.length;
    vi.advanceTimersByTime(9000);
    expect(subAgentsMocks.listSubAgents).toHaveBeenCalledTimes(callsBefore);
  });
});

describe('useSubAgentPanel — lifecycle invalidation & handoff (PR #209)', () => {
  // Finding #2 — conversation isolation: switching the parent conversation must invalidate any
  // focused child (close its socket, clear the transcript) before the NEW parent's list is applied,
  // and a late list response for a superseded parent must be dropped.
  it('parent change invalidates the focused child: closes the old socket, clears focus, loads the new list', async () => {
    let parent = 'parent-A';
    subAgentsMocks.listSubAgents.mockImplementation(async (p: string) =>
      p === 'parent-A' ? [summary('a1')] : [summary('b1')]);

    const panel = useSubAgentPanel(() => parent);
    await panel.refreshChildren();
    await panel.focusChild('a1');
    expect(panel.focusedAgentId.value).toBe('a1');
    expect(captured).toHaveLength(1);

    // User switches the main conversation to parent B, then a poll refreshes.
    parent = 'parent-B';
    await panel.refreshChildren();

    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
    expect(panel.focusedAgentId.value).toBeNull();
    expect(panel.focusedDisplayItems.value).toEqual([]);
    expect(panel.children.value).toEqual([summary('b1')]);
  });

  it('discards a late list response for a superseded parent (#2)', async () => {
    let parent = 'parent-A';
    let resolveA: (() => void) | undefined;
    subAgentsMocks.listSubAgents.mockImplementation((p: string) => {
      if (p === 'parent-A') {
        return new Promise<SubAgentSummary[]>((r) => {
          resolveA = () => r([summary('a-late')]);
        });
      }
      return Promise.resolve([summary('b1')]);
    });

    const panel = useSubAgentPanel(() => parent);
    // Kick off an in-flight refresh for A (parked inside listSubAgents).
    const pA = panel.refreshChildren();
    // Switch to B and refresh: invalidates (advances the parent epoch) and loads B.
    parent = 'parent-B';
    await panel.refreshChildren();
    expect(panel.children.value).toEqual([summary('b1')]);

    // The late A response resolves AFTER B was applied → it must be discarded.
    resolveA!();
    await pA;
    expect(panel.children.value).toEqual([summary('b1')]);
  });

  // Finding #3 — async lifecycle race: an unfocus (Back/unmount) during a pending focus must
  // supersede it so the late socket is closed, never adopted.
  it('a pending focus superseded by unfocus closes its late socket instead of adopting it (#3)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    let resolveHistory: (() => void) | undefined;
    convMocks.loadConversationMessages.mockImplementation(
      () => new Promise((r) => { resolveHistory = () => r([]); })
    );

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    // Focus parks mid history-load (the socket is already opened by the connect-first handoff).
    const p = panel.focusChild('a1');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    expect(captured).toHaveLength(1);

    // Unfocus (Back/unmount) supersedes the pending focus.
    await panel.unfocusChild();

    // History finally resolves; the superseded focus must CLOSE the socket, not adopt it.
    resolveHistory!();
    await p;

    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
    expect(panel.focusedAgentId.value).toBeNull();
    panel.sendToFocusedChild('nope');
    expect(wsMocks.sendWebSocketMessage).not.toHaveBeenCalled();
  });

  // Finding #8 — failure handling: HTTP/connect failures must be caught (no unhandled rejection),
  // surface an error, and leave no half-focused child.
  it('surfaces an error and does not throw when listSubAgents rejects; a later poll recovers (#8)', async () => {
    subAgentsMocks.listSubAgents.mockRejectedValueOnce(new Error('boom'));
    const panel = useSubAgentPanel(() => 'parent-1');
    await expect(panel.refreshChildren()).resolves.toBeUndefined();
    expect(panel.error.value).toBeTruthy();

    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    await panel.refreshChildren();
    expect(panel.error.value).toBeNull();
    expect(panel.children.value).toEqual([summary('a1')]);
  });

  it('clears focus state and surfaces an error when history load fails, closing the opened socket (#8)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    convMocks.loadConversationMessages.mockRejectedValueOnce(new Error('load fail'));
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');

    expect(panel.focusedAgentId.value).toBeNull();
    expect(panel.isFocusedStreaming.value).toBe(false);
    expect(panel.error.value).toBeTruthy();
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledWith(captured[0].connection);
  });

  it('clears focus state and surfaces an error when the connect fails (#8)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    wsSubMocks.connectSubAgent.mockRejectedValueOnce(new Error('connect fail'));
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');

    expect(panel.focusedAgentId.value).toBeNull();
    expect(panel.isFocusedStreaming.value).toBe(false);
    expect(panel.error.value).toBeTruthy();
  });

  // Finding #7 — live/history handoff: open the subscription FIRST, then load history, then merge
  // buffered live messages on top so nothing emitted in the gap is lost or duplicated.
  it('opens the live subscription BEFORE loading history (#7 handoff order)', async () => {
    const order: string[] = [];
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    convMocks.loadConversationMessages.mockImplementation(async () => {
      order.push('history');
      return [];
    });
    wsSubMocks.connectSubAgent.mockImplementation(
      async (parentThreadId: string, agentId: string, callbacks: any) => {
        order.push('connect');
        const connection = {
          socket: { readyState: WebSocket.OPEN },
          connectionId: `sa-${captured.length + 1}`,
          threadId: `subagent-${agentId}`,
          isConnected: true,
        };
        captured.push({ parentThreadId, agentId, callbacks, connection });
        return connection;
      }
    );

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');

    expect(order).toEqual(['connect', 'history']);
    expect(panel.focusedAgentId.value).toBe('a1');
    expect(panel.isFocusedStreaming.value).toBe(true);
  });

  it('buffers a live message that arrives during history load and applies it exactly once (#7)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    let resolveHistory: (() => void) | undefined;
    convMocks.loadConversationMessages.mockImplementation(
      () =>
        new Promise((r) => {
          resolveHistory = () =>
            r([
              {
                id: 'p-call', threadId: 'subagent-a1', runId: RUN, generationId: GEN, messageOrderIdx: 2,
                timestamp: 1001, messageType: 'tool_call', role: 'assistant',
                messageJson: JSON.stringify(toolCall('call_1')),
              },
            ]);
        })
    );

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    const p = panel.focusChild('a1');
    // Socket is open (connect-first); history is still pending → a live tool call arrives in the gap.
    for (let i = 0; i < 5; i++) await Promise.resolve();
    expect(captured).toHaveLength(1);
    captured[0].callbacks.onMessage(runAssignment());
    captured[0].callbacks.onMessage(toolCall('call_1'));
    captured[0].callbacks.onMessage(toolResult('call_1', 'resolved'));

    // History resolves with the SAME tool call persisted (twin, real runId).
    resolveHistory!();
    await p;

    // The gap message is present exactly once (merged with its rehydrated twin), and resolved.
    expect(toolPills(panel.focusedDisplayItems.value, 'call_1')).toHaveLength(1);
    expect(panel.getResultForToolCall('call_1')?.result).toBe('resolved');
  });
});

describe('useSubAgentPanel — re-review hardening (PR #209 findings A/B/C)', () => {
  function persistedToolCall(id: string) {
    return {
      id: `p-${id}`, threadId: 'subagent-a1', runId: RUN, generationId: GEN, messageOrderIdx: 2,
      timestamp: 1001, messageType: 'tool_call', role: 'assistant',
      messageJson: JSON.stringify(toolCall(id)),
    };
  }

  // FINDING A — a terminal socket close DURING the pending history-load await must not let the focus
  // adopt the now-dead connection. The dangerous case is the SECOND (terminal) close: the one-shot
  // auto-resume budget is already spent, so onClose does NOT bump focusSeq — the plain focus-token
  // guard still matches and (pre-fix) the focus would adopt a CLOSED socket, freezing the spinner.
  it('does not adopt a connection that closed terminally during history load (budget spent) (#A)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    // First focus loads instantly; the resume focus parks on a deferred history load.
    let resolveResumeHistory: (() => void) | undefined;
    let historyCall = 0;
    convMocks.loadConversationMessages.mockImplementation(() => {
      historyCall += 1;
      if (historyCall === 1) return Promise.resolve([]);
      return new Promise((r) => { resolveResumeHistory = () => r([]); });
    });

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');
    expect(captured).toHaveLength(1);
    expect(panel.isFocusedStreaming.value).toBe(true);

    // First unexpected close: spends the one-shot resume → opens a SECOND connection (captured[1]),
    // which parks on the deferred history load with its socket open.
    captured[0].callbacks.onClose!({ wasClean: true, code: 1000, reason: '' });
    for (let i = 0; i < 8; i++) await Promise.resolve();
    expect(captured).toHaveLength(2);

    // The resume connection now closes TERMINALLY while its history is still loading. Budget is spent,
    // so no third resume fires and focusSeq is NOT advanced.
    captured[1].connection.socket.readyState = WebSocket.CLOSED;
    captured[1].callbacks.onClose!({ wasClean: true, code: 1000, reason: '' });
    expect(panel.isFocusedStreaming.value).toBe(false);

    // History finally resolves for the resume focus. The dead socket must NOT be adopted.
    resolveResumeHistory!();
    for (let i = 0; i < 8; i++) await Promise.resolve();

    // No third reconnect, spinner stays off, and sends never route to the dead socket.
    expect(captured, 'no extra resume after the budget is spent').toHaveLength(2);
    expect(panel.isFocusedStreaming.value).toBe(false);
    wsMocks.sendWebSocketMessage.mockClear();
    panel.sendToFocusedChild('to dead socket');
    expect(wsMocks.sendWebSocketMessage).not.toHaveBeenCalled();
  });

  // FINDING B — the subscribe-first live buffer must be bounded. When it overflows while history is
  // still loading, the composable abandons the buffered-merge and reloads history AFTER the connection
  // is established, so a message that arrived in the gap (and is persisted) is reconciled — appearing
  // exactly once — instead of being lost or growing the buffer without bound.
  it('bounds the live buffer and reloads history on overflow so a gap message appears exactly once (#B)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    let resolveFirstHistory: (() => void) | undefined;
    let historyCall = 0;
    convMocks.loadConversationMessages.mockImplementation(() => {
      historyCall += 1;
      // First load parks (buffering window); the reconcile reload returns the persisted gap message.
      if (historyCall === 1) return new Promise((r) => { resolveFirstHistory = () => r([]); });
      return Promise.resolve([persistedToolCall('call_1')]);
    });

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    const p = panel.focusChild('a1');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    expect(captured).toHaveLength(1);

    const cb = captured[0].callbacks;
    cb.onMessage(runAssignment());
    // Overflow the bounded buffer while history is still loading.
    for (let i = 0; i < LIVE_BUFFER_MAX + 5; i++) {
      cb.onMessage(textUpdate(`chunk-${i}`));
    }
    // The gap tool call also arrives live; it is dropped by the overflow but is persisted server-side.
    cb.onMessage(toolCall('call_1'));

    resolveFirstHistory!();
    await p;

    // A reconcile reload happened (2 history loads total) and the gap message appears exactly once.
    expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(2);
    expect(toolPills(panel.focusedDisplayItems.value, 'call_1')).toHaveLength(1);
    expect(panel.isFocusedStreaming.value).toBe(true);
  });

  it('does not reload history when the live buffer stays within bounds (#B)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    let resolveHistory: (() => void) | undefined;
    convMocks.loadConversationMessages.mockImplementation(
      () => new Promise((r) => { resolveHistory = () => r([]); })
    );

    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    const p = panel.focusChild('a1');
    for (let i = 0; i < 5; i++) await Promise.resolve();
    const cb = captured[0].callbacks;
    cb.onMessage(runAssignment());
    // Well under the bound — no reload should be triggered.
    cb.onMessage(textUpdate('just a little'));

    resolveHistory!();
    await p;

    expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1);
    expect(assistantText(panel.focusedDisplayItems.value)).toContain('just a little');
  });

  // FINDING C — a stream error (relay_failed / subagent_unavailable / other) must be copied into the
  // public `error` ref so the panel's error banner surfaces feedback, while streaming stops.
  it('surfaces a relay_failed stream error in the public error ref and stops streaming (#C)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');
    expect(panel.isFocusedStreaming.value).toBe(true);
    expect(panel.error.value).toBeNull();

    // wsClient turns a {"$type":"error","code":"relay_failed",...} frame into onError(message).
    captured[0].callbacks.onError("Failed to relay the message to sub-agent 'a1'. Please retry.");

    expect(panel.isFocusedStreaming.value).toBe(false);
    expect(panel.error.value).toBeTruthy();
    expect(panel.error.value).toContain('relay');
  });

  it('surfaces a subagent_unavailable stream error in the public error ref (#C)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();
    await panel.focusChild('a1');

    captured[0].callbacks.onError("Sub-agent 'a1' is not available.");

    expect(panel.isFocusedStreaming.value).toBe(false);
    expect(panel.error.value).toContain('not available');
  });

  it('a stale focus stream error does not clobber a newer focus (#C generation-guard)', async () => {
    subAgentsMocks.listSubAgents.mockResolvedValue([summary('a1'), summary('a2')]);
    const panel = useSubAgentPanel(() => 'parent-1');
    await panel.refreshChildren();

    await panel.focusChild('a1');
    const staleCb = captured[0].callbacks;
    await panel.focusChild('a2');
    expect(panel.error.value).toBeNull();

    // A late error from the superseded a1 focus must be ignored.
    staleCb.onError("Failed to relay the message to sub-agent 'a1'. Please retry.");
    expect(panel.error.value).toBeNull();
    expect(panel.isFocusedStreaming.value).toBe(true);
  });
});
