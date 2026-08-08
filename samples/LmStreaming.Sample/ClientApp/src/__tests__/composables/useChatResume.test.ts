import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useChat } from '@/composables/useChat';
import { MessageType } from '@/types';

// Resume bug: an ongoing (streaming) conversation stops streaming if you switch to another
// conversation and come back, or refresh. Switching tears down the WebSocket and returning only
// reloads persisted REST history (everything marked completed) — it never re-subscribes to the
// still-running backend run. These tests pin the fix: on return, when the backend reports an
// in-flight run, the client re-opens the WebSocket (subscribe-only) and resumes the live stream.

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
  getRunState: vi.fn(),
  getConversationUsage: vi.fn(),
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
  getRunState: convMocks.getRunState,
  getConversationUsage: convMocks.getConversationUsage,
}));

function textUpdate(text: string) {
  return {
    $type: 'text_update',
    text,
    role: 'assistant',
    runId: 'run-1',
    generationId: 'gen-1',
    messageOrderIdx: 0,
  };
}

describe('useChat — resume in-flight stream after switch/refresh', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

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

  it('reconnects (subscribe-only) and resumes when the conversation has an in-flight run', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // Start a streaming run on the first connection and stream a partial delta.
    await chat.sendMessage('hi');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    captured[0].onMessage(textUpdate('Hel'));

    // Switch away (disconnect), then switch back: load history (empty) + attempt resume.
    await chat.disconnectWebSocket();
    await chat.loadMessagesFromBackend('thread-1');

    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: true,
      currentRunId: 'run-1',
    });
    await chat.resumeStreamIfActive('thread-1');

    // A second connection is opened to RESUME, and it is subscribe-only (no new chat message sent).
    expect(convMocks.getRunState).toHaveBeenCalledWith('thread-1');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
    expect(wsMocks.sendWebSocketMessage).toHaveBeenCalledTimes(1); // only the original send

    // Backend replays the in-flight run on the resumed connection, then completes.
    captured[1].onMessage(textUpdate('Hel'));
    captured[1].onMessage(textUpdate('lo'));
    captured[1].onDone();

    expect(chat.isLoading.value).toBe(false);
    const bubbles = chat.displayItems.value.filter((i) => i.type === 'assistant-message');
    expect((bubbles[0] as { content?: { text?: string } }).content?.text).toBe('Hello');
  });

  it('does NOT reconnect when no run is in progress', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    await chat.sendMessage('hi');
    captured[0].onMessage(textUpdate('done'));
    await chat.disconnectWebSocket();
    await chat.loadMessagesFromBackend('thread-1');

    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: false,
      currentRunId: null,
    });
    await chat.resumeStreamIfActive('thread-1');

    // No active run ⇒ no reconnect.
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
  });

  it('does not query run-state or reconnect when already streaming the same thread', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    await chat.sendMessage('hi');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);

    // Already connected to thread-1 — resume must be a no-op.
    await chat.resumeStreamIfActive('thread-1');
    expect(convMocks.getRunState).not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
  });

  it('does not resume under the SSE transport', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    chat.setTransport('sse');

    await chat.resumeStreamIfActive('thread-1');

    expect(convMocks.getRunState).not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection).not.toHaveBeenCalled();
  });

  it('resets isLoading when the resume connection fails to open', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: true,
      currentRunId: 'run-1',
    });
    wsMocks.createWebSocketConnection.mockRejectedValueOnce(new Error('ws open failed'));

    await chat.resumeStreamIfActive('thread-1');

    // A failed resume must not leave the UI stuck "streaming" forever.
    expect(chat.isLoading.value).toBe(false);
  });

  it('aborts resume if the active thread changed during the run-state check', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-A');

    // Hold getRunState open so we can simulate a conversation switch mid-await.
    let resolveRunState!: (v: unknown) => void;
    convMocks.getRunState.mockImplementation(
      () => new Promise((resolve) => { resolveRunState = resolve; })
    );

    const pending = chat.resumeStreamIfActive('thread-A');
    // Wait until the run-state request is actually in flight (after the dynamic import), then
    // simulate the user switching to a different conversation before it resolves.
    await vi.waitFor(() => expect(convMocks.getRunState).toHaveBeenCalledTimes(1));
    chat.setThreadId('thread-B');
    resolveRunState({ threadId: 'thread-A', isInProgress: true, currentRunId: 'run-1' });
    await pending;

    // The stream for thread-A must NOT be bound to the now-current thread-B state.
    expect(wsMocks.createWebSocketConnection).not.toHaveBeenCalled();
    expect(chat.isLoading.value).toBe(false);
  });
});

// A tool call that is still RUNNING (issued, result not yet produced) when the user switches away
// and comes back. The persisted REST history loaded on return carries the UNRESOLVED tool call; the
// resumed WebSocket then replays the run including the tool call AND its result. The resolved pill
// must show (the result must land in the toolResults map) AND there must be exactly one pill for the
// call (the REST-rehydrated tool call and the WS-replayed tool call must merge, not duplicate).
describe('useChat — resume resolves an in-flight tool call after switch-back', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

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

  // The real wire: ONLY run_assignment carries the runId; tool_call / text / tool_call_result are
  // streamed WITHOUT a runId field (verified against recorded WS traffic). The persisted record,
  // however, stores the producing run on its top-level `runId` column, so on switch-back the loader
  // stamps the rehydrated message with the real run GUID — diverging from the live (runId-less) copy.
  const ID = { runId: '39018c20a1b540a2b51627f3835e75b9', generationId: 'gen-1' };

  // Live wire shape — NO runId (matches recorded WS frames).
  function toolCall() {
    return {
      $type: MessageType.ToolCall,
      role: 'assistant',
      tool_call_id: 'call_1',
      function_name: 'Read',
      function_args: '{"path":"x"}',
      generationId: ID.generationId,
      messageOrderIdx: 1,
    };
  }

  function toolResult() {
    return {
      $type: MessageType.ToolCallResult,
      role: 'tool',
      tool_call_id: 'call_1',
      result: 'file contents here',
      generationId: ID.generationId,
      messageOrderIdx: 2,
    };
  }

  function runAssignment() {
    return {
      $type: MessageType.RunAssignment,
      Assignment: { runId: ID.runId, generationId: ID.generationId, inputIds: [] },
    };
  }

  function runCompleted() {
    return {
      $type: MessageType.RunCompleted,
      completedRunId: ID.runId,
      hasPendingMessages: false,
    };
  }

  // PersistedMessage rows the loader rehydrates on switch-back: the user turn + the UNRESOLVED
  // tool call (its result was not yet persisted when the user switched away).
  function persistedHistory() {
    return [
      {
        id: 'p-user',
        threadId: 'thread-1',
        runId: ID.runId,
        generationId: ID.generationId,
        messageOrderIdx: 0,
        timestamp: 1000,
        messageType: 'text',
        role: 'user',
        messageJson: JSON.stringify({ $type: MessageType.Text, role: 'user', text: 'read a file' }),
      },
      {
        id: 'p-call_1',
        threadId: 'thread-1',
        runId: ID.runId,
        generationId: ID.generationId,
        messageOrderIdx: 1,
        timestamp: 1001,
        messageType: 'tool_call',
        role: 'assistant',
        messageJson: JSON.stringify(toolCall()),
      },
    ];
  }

  function call1Pills(chat: ReturnType<typeof useChat>) {
    return chat.displayItems.value
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
      .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === 'call_1'));
  }

  it('resolves the tool pill and keeps it single after a switch-away/back resume', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // 1) Start the run on connection 1; the tool call is issued but NOT yet resolved.
    await chat.sendMessage('read a file');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    captured[0].onMessage(runAssignment());
    captured[0].onMessage(toolCall());
    expect(chat.getResultForToolCall('call_1')).toBeNull();

    // 2) Switch away: tear down the socket.
    await chat.disconnectWebSocket();

    // 3) Switch back: load persisted history (result NOT yet persisted → tool call unresolved).
    convMocks.loadConversationMessages.mockResolvedValue(persistedHistory());
    await chat.loadMessagesFromBackend('thread-1');
    expect(chat.getResultForToolCall('call_1'), 'rehydrated history has no result yet').toBeNull();
    expect(call1Pills(chat), 'exactly one pill after rehydrate').toHaveLength(1);

    // 4) Resume: backend reports the run is still in flight.
    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: true,
      currentRunId: ID.runId,
    });
    await chat.resumeStreamIfActive('thread-1');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);

    // 5) The resumed connection replays the run: assignment, the tool call, THEN its result, done.
    captured[1].onMessage(runAssignment());
    captured[1].onMessage(toolCall());
    captured[1].onMessage(toolResult());
    captured[1].onMessage(runCompleted());
    captured[1].onDone();

    // The pill must resolve (result in the toolResults map) and there must be exactly ONE pill.
    expect(chat.getResultForToolCall('call_1'), 'tool result must resolve the pill after resume').not.toBeNull();
    expect(call1Pills(chat), 'the replayed tool call must merge with the rehydrated one, not duplicate').toHaveLength(1);
  });

  // User-reported repro: a turn with MANY tool calls (10-15), streamed while the user switches away
  // mid-run and comes back. The concurrent tool calls in one turn share runId/generationId/
  // messageOrderIdx and differ only by tool_call_id (the real Anthropic shape). Each must resolve and
  // render as exactly ONE pill after resume — without the runId stamp every one of them duplicates
  // (rehydrated real-runId key vs replayed 'default' key), so the user sees a doubled, never-settling
  // tool count.
  it('resolves all 12 tool pills and keeps each single after a many-tool switch-back', async () => {
    const N = 12;
    const ids = Array.from({ length: N }, (_, i) => `call_${i + 1}`);
    const ORDER = 1; // concurrent tools in one turn share messageOrderIdx, differ by tool_call_id

    const liveToolCall = (id: string) => ({
      $type: MessageType.ToolCall,
      role: 'assistant',
      tool_call_id: id,
      function_name: 'Read',
      function_args: '{"path":"x"}',
      generationId: ID.generationId,
      messageOrderIdx: ORDER,
    });
    const liveResult = (id: string) => ({
      $type: MessageType.ToolCallResult,
      role: 'tool',
      tool_call_id: id,
      result: `result ${id}`,
      generationId: ID.generationId,
      messageOrderIdx: 2,
    });
    const persisted = ids.map((id, i) => ({
      id: `p-${id}`,
      threadId: 'thread-1',
      runId: ID.runId,
      generationId: ID.generationId,
      messageOrderIdx: ORDER,
      timestamp: 1001 + i,
      messageType: 'tool_call',
      role: 'assistant',
      messageJson: JSON.stringify(liveToolCall(id)),
    }));

    const pillsFor = (chat: ReturnType<typeof useChat>, id: string) =>
      chat.displayItems.value
        .filter((i) => i.type === 'pill')
        .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
        .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === id));

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // 1) Run starts; 12 tool calls issued, none resolved yet (user switches away mid-stream).
    await chat.sendMessage('use many tools');
    captured[0].onMessage(runAssignment());
    ids.forEach((id) => captured[0].onMessage(liveToolCall(id)));

    // 2) Switch away.
    await chat.disconnectWebSocket();

    // 3) Switch back: history has 12 UNRESOLVED tool calls (results not yet persisted).
    convMocks.loadConversationMessages.mockResolvedValue(persisted);
    await chat.loadMessagesFromBackend('thread-1');
    ids.forEach((id) =>
      expect(pillsFor(chat, id), `one pill for ${id} after rehydrate`).toHaveLength(1),
    );

    // 4) Resume the in-flight run.
    convMocks.getRunState.mockResolvedValue({
      threadId: 'thread-1',
      isInProgress: true,
      currentRunId: ID.runId,
    });
    await chat.resumeStreamIfActive('thread-1');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);

    // 5) Replay: assignment, the 12 tool calls, then their 12 results, completed.
    captured[1].onMessage(runAssignment());
    ids.forEach((id) => captured[1].onMessage(liveToolCall(id)));
    ids.forEach((id) => captured[1].onMessage(liveResult(id)));
    captured[1].onMessage(runCompleted());
    captured[1].onDone();

    // Every tool resolves, and each renders as exactly ONE pill (no duplicate from the resume).
    for (const id of ids) {
      expect(chat.getResultForToolCall(id), `${id} must resolve after resume`).not.toBeNull();
      expect(pillsFor(chat, id), `exactly one pill for ${id} (no resume duplicate)`).toHaveLength(1);
    }
  });
});

// BUG 1: switching FROM an in-flight conversation TO an idle one must return the Send/Stop control to
// idle. The switch tears down the socket and reloads the target's history, but the streaming flag
// (isLoading) was only ever raised (to true, by resume) and never lowered on switch — so an idle target
// kept showing the red Stop button forever. handleSelectConversation calls, in order:
// disconnectWebSocket → clearMessages → loadMessagesFromBackend → resumeStreamIfActive; this exercises
// that same sequence at the composable level.
describe('useChat — streaming flag resets when switching to an idle conversation (BUG 1)', () => {
  let captured: any[];

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

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

  it('clears isLoading after switching from a streaming conversation to an idle one', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-A');

    // Conversation A is actively streaming.
    await chat.sendMessage('hi');
    captured[0].onMessage({
      $type: 'text_update', text: 'partial', role: 'assistant',
      runId: 'run-A', generationId: 'gen-A', messageOrderIdx: 0,
    });
    expect(chat.isLoading.value, 'A is streaming').toBe(true);

    // Switch to idle conversation B — mirror ChatLayout.handleSelectConversation exactly.
    await chat.disconnectWebSocket();
    await chat.clearMessages();
    await chat.loadMessagesFromBackend('thread-B'); // history empty (idle)
    convMocks.getRunState.mockResolvedValue({ threadId: 'thread-B', isInProgress: false, currentRunId: null });
    await chat.resumeStreamIfActive('thread-B');

    // B has no in-flight run ⇒ the UI must be idle (Send, not Stop).
    expect(chat.isLoading.value, 'switching to an idle conversation must clear the streaming flag').toBe(false);
    expect(chat.isSending.value, 'and the sending flag too').toBe(false);
  });

  // Regression for the BUG 1 fix: the flag must NOT be lowered inside clearMessages. Doing so flashed
  // a transient "idle" during the awaited history load when switching BACK into a still-streaming
  // conversation — which raced the stream-idle wait into reading the transcript before the resumed
  // final text arrived (the StreamingResumeToolPills E2E failure). markStreamLoading() raises the flag
  // BEFORE the load so a resuming target stays continuously "streaming"; markStreamIdle() lowers it
  // only once the run state is known to be idle.
  it('keeps isLoading true throughout a switch-back into a still-streaming conversation', async () => {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-A');
    await chat.sendMessage('hi');
    expect(chat.isLoading.value, 'A is streaming').toBe(true);

    // Switch AWAY to a fresh idle chat — mirror handleNewChat (clearMessages + markStreamIdle).
    await chat.disconnectWebSocket();
    await chat.clearMessages();
    chat.markStreamIdle();
    expect(chat.isLoading.value, 'the new idle chat shows Send').toBe(false);

    // Switch BACK to A, which is STILL streaming — mirror handleSelectConversation.
    await chat.clearMessages();
    chat.setThreadId('thread-A');
    chat.markStreamLoading();
    // The flag must already be raised before the awaited load, so there is no idle window to observe.
    expect(chat.isLoading.value, 'loading a possibly-active conversation shows Stop, not Send').toBe(true);
    await chat.loadMessagesFromBackend('thread-A');
    convMocks.getRunState.mockResolvedValue({ threadId: 'thread-A', isInProgress: true, currentRunId: 'run-A' });
    await chat.resumeStreamIfActive('thread-A');

    // A is in-flight ⇒ the control stays "Stop" (isLoading true) with no transient flip to idle.
    expect(chat.isLoading.value, 'a resuming conversation stays streaming').toBe(true);
  });
});

// BUG 2: a multi-turn run mixing reasoning + tool calls + text, switched away MID-run and returned to,
// scrambles/duplicates the thinking & text. The content turn epoch (which disambiguates turns that share
// generationId+messageOrderIdx) is stateful and is reset at the start of loadMessagesFromBackend but NOT
// again before resumeStreamIfActive replays the SAME already-emitted messages — so replayed reasoning/text
// re-key with a higher turn epoch than their rehydrated twins, fail to merge, and pile up at the bottom.
// Tool calls are immune (their key carries tool_call_id), matching the "tools fine, thinking/text messed
// up" symptom. Proven against backend [Client] merge-key logs (run 592af00a: t1/t2/t3 → replayed t3/t4/t5).
describe('useChat — multi-turn mixed order/dedup on resume after switch-back (BUG 2)', () => {
  let captured: any[];
  const RUN = 'run-1';
  const GEN = { t1: 'gen-1', t2: 'gen-2', t3: 'gen-3' };

  // Live wire shapes — NO runId (only run_assignment carries it; handleMessage stamps currentRunId).
  const reasoningMsg = (gen: string, moi: number, r: string) =>
    ({ $type: MessageType.Reasoning, role: 'assistant', reasoning: r, visibility: 1, generationId: gen, messageOrderIdx: moi });
  const textMsg = (gen: string, moi: number, t: string) =>
    ({ $type: MessageType.Text, role: 'assistant', text: t, generationId: gen, messageOrderIdx: moi });
  const toolCallMsg = (gen: string, moi: number, id: string) =>
    ({ $type: MessageType.ToolCall, role: 'assistant', tool_call_id: id, function_name: 'get_weather', function_args: '{}', generationId: gen, messageOrderIdx: moi });
  const runAssignment = () => ({ $type: MessageType.RunAssignment, Assignment: { runId: RUN, generationId: GEN.t1, inputIds: [] } });
  const runCompleted = () => ({ $type: MessageType.RunCompleted, completedRunId: RUN, hasPendingMessages: false });

  // Persisted rows carry runId + identity (loadMessagesFromBackend stamps parsedMessage.runId ??= pm.runId).
  const persist = (id: string, ts: number, msg: Record<string, unknown>, gen: string, moi: number) => ({
    id, threadId: 'thread-1', runId: RUN, generationId: gen, messageOrderIdx: moi,
    timestamp: ts, messageType: String(msg.$type), role: String(msg.role), messageJson: JSON.stringify(msg),
  });

  // History persisted before the switch: turn1 (R1 + 2 parallel tools) + turn2 (R2 + tool + text A2).
  const history = () => [
    persist('p1', 1000, reasoningMsg(GEN.t1, 0, 'R1'), GEN.t1, 0),
    persist('p2', 1001, toolCallMsg(GEN.t1, 1, 'call_1'), GEN.t1, 1),
    persist('p3', 1002, toolCallMsg(GEN.t1, 1, 'call_2'), GEN.t1, 1),
    persist('p4', 1003, reasoningMsg(GEN.t2, 0, 'R2'), GEN.t2, 0),
    persist('p5', 1004, toolCallMsg(GEN.t2, 1, 'call_3'), GEN.t2, 1),
    persist('p6', 1005, textMsg(GEN.t2, 2, 'A2'), GEN.t2, 2),
  ];

  const reasoningsOf = (chat: ReturnType<typeof useChat>) =>
    chat.displayItems.value
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ $type?: string; reasoning?: string }> }).items)
      .filter((m) => m.$type === MessageType.Reasoning)
      .map((m) => m.reasoning ?? '');
  const textsOf = (chat: ReturnType<typeof useChat>) =>
    chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text ?? '');
  const pillsForId = (chat: ReturnType<typeof useChat>, id: string) =>
    chat.displayItems.value
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
      .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === id));

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured.push(options);
      return {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: options.threadId,
        isConnected: true,
      };
    });
  });

  it('does not duplicate or reorder multi-turn reasoning/text when resuming after switch-back', async () => {
    convMocks.loadConversationMessages.mockResolvedValue(history());
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // Switch-back: rehydrate persisted history (turns 1-2).
    await chat.loadMessagesFromBackend('thread-1');
    expect(reasoningsOf(chat), 'reload: two distinct turn reasonings').toEqual(['R1', 'R2']);
    expect(textsOf(chat), 'reload: one turn-2 text').toEqual(['A2']);

    // Resume the in-flight run.
    convMocks.getRunState.mockResolvedValue({ threadId: 'thread-1', isInProgress: true, currentRunId: RUN });
    await chat.resumeStreamIfActive('thread-1');
    const opts = captured[captured.length - 1];

    // Backend replays the run from the start (assignment + turns 1-2), then streams live turn 3, completes.
    opts.onMessage(runAssignment());
    opts.onMessage(reasoningMsg(GEN.t1, 0, 'R1'));
    opts.onMessage(toolCallMsg(GEN.t1, 1, 'call_1'));
    opts.onMessage(toolCallMsg(GEN.t1, 1, 'call_2'));
    opts.onMessage(reasoningMsg(GEN.t2, 0, 'R2'));
    opts.onMessage(toolCallMsg(GEN.t2, 1, 'call_3'));
    opts.onMessage(textMsg(GEN.t2, 2, 'A2'));
    // Live turn 3 (fresh per-turn generationId).
    opts.onMessage(reasoningMsg(GEN.t3, 0, 'R3'));
    opts.onMessage(textMsg(GEN.t3, 1, 'A3'));
    opts.onMessage(runCompleted());
    opts.onDone();

    // Replayed turns 1-2 must MERGE with their rehydrated twins (no duplicates), and turn 3 appends —
    // yielding the correct chronological set, not a scrambled/duplicated pile.
    expect(reasoningsOf(chat), 'no duplicate reasoning after resume; three turns in order').toEqual(['R1', 'R2', 'R3']);
    expect(textsOf(chat), 'no duplicate text after resume; two texts in order').toEqual(['A2', 'A3']);
    for (const id of ['call_1', 'call_2', 'call_3']) {
      expect(pillsForId(chat, id), `exactly one pill for ${id}`).toHaveLength(1);
    }
  });
});

// TASK 4: automatic, single-flight, REST-first resynchronization after a DROPPED stream.
//
// The server may deliberately drop a slow/stuck consumer: it emits a `stream_recovery` frame and then
// closes the socket CLEANLY (`NormalClosure`, reason `resync_required`) WITHOUT ever sending `done`.
// A clean close means `wasClean === true`, so the transport reports no error — the drop is invisible to
// the client, `isLoading` never falls, and the run appears frozen forever. Any close while authoritative
// run state is still active must recover, whether or not the recovery reason was announced.
//
// The recovery is a coordinator (see `composables/streamResync.ts`), NOT recursive callbacks: ONE
// in-flight operation per (threadId, epoch), strict order REST load → authoritative run state →
// subscribe-only socket, a bounded attempt budget, and stale-epoch rejection — so a flapping backend
// can never spin the client, and a switched-away conversation can never be resurrected by a late close.
describe('useChat — automatic resync when the stream drops before done (TASK 4)', () => {
  let captured: any[];
  const RUN = 'run-1';
  const GEN = 'gen-1';

  // The client's attempt budget per (threadId, epoch). Hard-coded rather than imported so this file
  // still parses (and fails on ASSERTIONS, not on a missing module) before the coordinator exists.
  const MAX_RESYNC_ATTEMPTS = 3;

  const runAssignment = () => ({
    $type: MessageType.RunAssignment,
    Assignment: { runId: RUN, generationId: GEN, inputIds: [] },
  });
  const text = (t: string, moi = 0) =>
    ({ $type: 'text_update', text: t, role: 'assistant', runId: RUN, generationId: GEN, messageOrderIdx: moi });

  /** The server's deliberate drop: clean close, reason `resync_required`, and no preceding `done`. */
  const recoveryClose = () => ({ wasClean: true, code: 1000, reason: 'resync_required' });

  const inProgress = () => ({ threadId: 'thread-1', isInProgress: true, currentRunId: RUN });
  const finished = () => ({ threadId: 'thread-1', isInProgress: false, currentRunId: null });

  /**
   * Drain the coordinator's awaited steps (two dynamic imports + two REST calls + socket open).
   * Required before asserting a NEGATIVE ("no resync happened"), where there is nothing to wait for.
   */
  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) await new Promise((resolve) => setTimeout(resolve, 0));
  }

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();
    convMocks.getConversationUsage.mockReset();

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
    convMocks.getRunState.mockResolvedValue(inProgress());
    convMocks.getConversationUsage.mockResolvedValue(null);
  });

  /** Start a live run on connection 1 and stream a partial delta (the state the drop interrupts). */
  async function startStreaming(getModeId: () => string = () => 'default') {
    const chat = useChat({ getModeId });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    captured[0].onMessage(runAssignment());
    captured[0].onMessage(text('Hel'));
    expect(chat.isLoading.value, 'the run is live before the drop').toBe(true);
    return chat;
  }

  it('rehydrates and resubscribes when the socket closes before done', async () => {
    const chat = await startStreaming();

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    // Strict order: canonical REST history FIRST, then AUTHORITATIVE run state, then the socket.
    // Subscribing before the run-state check would re-attach to a run that already finished; loading
    // history after subscribing would race replayed frames against the rehydrate.
    const restAt = convMocks.loadConversationMessages.mock.invocationCallOrder[0];
    const runStateAt = convMocks.getRunState.mock.invocationCallOrder[0];
    const socketAt = wsMocks.createWebSocketConnection.mock.invocationCallOrder[1];
    expect(restAt, 'REST rehydrate precedes the run-state check').toBeLessThan(runStateAt);
    expect(runStateAt, 'run state precedes the replacement socket').toBeLessThan(socketAt);

    // Subscribe-only: the resync must never re-send the user's prompt (that would duplicate the turn).
    expect(wsMocks.sendWebSocketMessage, 'only the original send').toHaveBeenCalledTimes(1);
    expect(captured[1].threadId).toBe('thread-1');

    // A recovered stream is still streaming, and a successful recovery is not an error banner.
    expect(chat.isLoading.value, 'the UI stays in the streaming state across the recovery').toBe(true);
    expect(chat.error.value, 'a successful recovery surfaces no error').toBeNull();

    // The replacement connection carries the run to completion.
    captured[1].onMessage(runAssignment());
    captured[1].onMessage(text('Hello'));
    captured[1].onDone();
    expect(chat.isLoading.value).toBe(false);
  });

  // The rehydrate a DROP recovery runs is the same destructive reload a conversation SWITCH runs, and
  // that reload empties the pending queue. For a switch that is right — the queue belongs to the
  // conversation being left. For a drop it is not: the conversation is still on screen, at the same
  // epoch, and the prompts queued against it are still going to be sent. (The in-place
  // `replay_truncated` fill already reasoned this out; the slow-consumer/transport drop is the same
  // situation and must reach the same conclusion.) The rehydrate is HELD OPEN across the queueing so
  // the queue is observed surviving the reload itself, not merely re-created after it.
  it('keeps the prompts queued during the run when a normal drop recovery rehydrates', async () => {
    const chat = await startStreaming();

    // A second prompt queued while the run is still live, on the socket that is about to drop.
    await chat.sendMessage('queued while streaming');
    expect(
      chat.pendingMessages.value.map((p) => p.content.text),
      'both prompts are queued against the live run'
    ).toEqual(['hi', 'queued while streaming']);

    let resolveHistory!: (v: unknown) => void;
    convMocks.loadConversationMessages.mockImplementation(
      () => new Promise((resolve) => { resolveHistory = resolve; })
    );

    // A NORMAL drop — the clean `resync_required` close, NOT a `replay_truncated` advisory — so this
    // pins the socket-replacing recovery path rather than the in-place fill.
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));

    // Let the destructive reload land while the recovery is still in flight.
    resolveHistory([]);
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));
    await settle();

    expect(
      chat.pendingMessages.value.map((p) => p.content.text),
      'recovering the conversation on screen must not discard the prompts queued against it'
    ).toEqual(['hi', 'queued while streaming']);
    expect(
      wsMocks.createWebSocketConnection,
      'one drop, one replacement socket — preserving the queue must not re-send or re-open anything'
    ).toHaveBeenCalledTimes(2);
    expect(wsMocks.sendWebSocketMessage, 'only the two real sends; the recovery is subscribe-only').toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value, 'the run is still live across the recovery').toBe(true);
    expect(chat.error.value).toBeNull();
  });

  // The counterpart guard: the DEFAULT caller of the reload is a conversation switch, and it must go
  // on clearing. Preserving the queue is a property of the RECOVERY wiring, not a new default — a
  // "fix" that flipped the default instead would leave one conversation's queue attached to another.
  it('still empties the queue on the default (conversation-switch) history load', async () => {
    const chat = await startStreaming();
    expect(chat.pendingMessages.value).toHaveLength(1);

    await chat.loadMessagesFromBackend('thread-2');

    expect(
      chat.pendingMessages.value,
      'a switch leaves the queue behind with the conversation it belonged to'
    ).toEqual([]);
  });

  it('resyncs once on an explicit stream_recovery frame, not twice with the close that follows', async () => {
    const chat = await startStreaming();

    // The server announces the recovery, THEN closes — one logical drop, so exactly one resync.
    captured[0].onStreamRecovery?.({
      reason: 'slow_consumer',
      threadId: 'thread-1',
      runId: RUN,
      generationId: GEN,
    });
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    captured[0].onClose(recoveryClose());
    await settle();

    expect(convMocks.loadConversationMessages, 'one rehydrate for one drop').toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection, 'no second replacement socket').toHaveBeenCalledTimes(2);
    expect(chat.error.value).toBeNull();
  });

  // `replay_truncated` is a DIFFERENT signal from the slow-consumer drop above: the run's buffered
  // PREFIX is gone, but this same socket still carries the live tail. Treating it as a drop would
  // reconnect onto the same still-truncated buffer and be advised again for the rest of the run — a
  // reconnect storm. So: refetch authoritative history IN PLACE and keep the socket.
  it('fills the hole in place on replay_truncated instead of dropping the socket', async () => {
    const chat = await startStreaming();
    const socketsBefore = wsMocks.createWebSocketConnection.mock.calls.length;

    captured[0].onStreamRecovery?.({
      reason: 'replay_truncated',
      threadId: 'thread-1',
      runId: RUN,
      generationId: GEN,
    });
    await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));
    await settle();

    expect(wsMocks.createWebSocketConnection, 'no replacement socket is opened').toHaveBeenCalledTimes(socketsBefore);
    expect(wsMocks.closeWebSocketConnection, 'the advised socket is never torn down').not.toHaveBeenCalled();
    expect(convMocks.getRunState, 'no drop happened, so no run-state probe is needed').not.toHaveBeenCalled();
    expect(chat.isLoading.value, 'the run is still live across the advisory').toBe(true);
    expect(chat.error.value, 'a non-terminal advisory is not a user-facing failure').toBeNull();

    // The SAME socket goes on to deliver the live tail, which renders and completes the run.
    captured[0].onMessage(text('LIVE'));
    captured[0].onDone();
    const renderedTexts = chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text ?? '');
    expect(renderedTexts, 'the live tail after the advisory reaches the UI').toEqual(['LIVE']);
    expect(chat.isLoading.value).toBe(false);
  });

  // ...but that refetch is ASYNCHRONOUS and DESTRUCTIVE, and the socket is deliberately kept OPEN
  // across it. Everything the live tail delivers while the REST round-trip is in flight lands in the
  // message index and is then WIPED by the reload — and persisted history cannot contain those
  // frames, because they are precisely the tail the server has not persisted yet. The same reload
  // empties the pending queue, discarding prompts the user typed while the run streams even though
  // the conversation they belong to is still the one on screen.
  describe('while the truncated-replay refetch is in flight', () => {
    const reasoning = (moi: number, r: string) =>
      ({ $type: MessageType.Reasoning, role: 'assistant', reasoning: r, visibility: 1, generationId: GEN, messageOrderIdx: moi });
    const toolCall = (id: string, moi: number) =>
      ({ $type: MessageType.ToolCall, role: 'assistant', tool_call_id: id, function_name: 'Read', function_args: '{}', generationId: GEN, messageOrderIdx: moi });
    const toolResult = (id: string, moi: number) =>
      ({ $type: MessageType.ToolCallResult, role: 'tool', tool_call_id: id, result: `result ${id}`, generationId: GEN, messageOrderIdx: moi });
    const persist = (id: string, ts: number, msg: Record<string, unknown>, moi: number) => ({
      id, threadId: 'thread-1', runId: RUN, generationId: GEN, messageOrderIdx: moi,
      timestamp: ts, messageType: String(msg.$type), role: String(msg.role), messageJson: JSON.stringify(msg),
    });

    const advise = (socket: any) =>
      socket.onStreamRecovery?.({ reason: 'replay_truncated', threadId: 'thread-1', runId: RUN, generationId: GEN });

    const textsOf = (chat: ReturnType<typeof useChat>) =>
      chat.displayItems.value
        .filter((i) => i.type === 'assistant-message')
        .map((i) => (i as { content: { text?: string } }).content.text ?? '');
    const reasoningsOf = (chat: ReturnType<typeof useChat>) =>
      chat.displayItems.value
        .filter((i) => i.type === 'pill')
        .flatMap((i) => (i as { items: Array<{ $type?: string; reasoning?: string }> }).items)
        .filter((m) => m.$type === MessageType.Reasoning)
        .map((m) => m.reasoning ?? '');
    const pillsFor = (chat: ReturnType<typeof useChat>, id: string) =>
      chat.displayItems.value
        .filter((i) => i.type === 'pill')
        .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
        .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === id));

    /** Start a run, advise `replay_truncated`, and hold the REST rehydrate open so the tail can race it. */
    async function adviseWithHistoryHeld() {
      const chat = await startStreaming();
      let resolveHistory!: (v: unknown) => void;
      convMocks.loadConversationMessages.mockImplementation(
        () => new Promise((resolve) => { resolveHistory = resolve; })
      );
      advise(captured[0]);
      await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));
      return { chat, resolveHistory: (v: unknown) => resolveHistory(v) };
    }

    // The finding, exactly: a delta (plus every other modality — the resume path has shipped a green
    // suite that only ever fed text before) and a queued prompt, all delivered DURING the fetch.
    it('keeps the live tail and the queued prompts the reload would otherwise wipe', async () => {
      const { chat, resolveHistory } = await adviseWithHistoryHeld();

      // The same socket goes on streaming while the refetch is in flight, and the user queues a turn.
      captured[0].onMessage(text('TAIL', 1));
      captured[0].onMessage(toolCall('call_9', 2));
      captured[0].onMessage(toolResult('call_9', 3));
      await chat.sendMessage('queued while streaming');

      // Authoritative history holds only the recovered PREFIX — never the tail above.
      resolveHistory([persist('p1', 1000, reasoning(0, 'R1'), 0)]);
      await settle();

      expect(
        chat.pendingMessages.value.map((p) => p.content.text),
        'an in-place fill of the CURRENT conversation must not discard its queued prompts'
      ).toEqual(['hi', 'queued while streaming']);
      expect(reasoningsOf(chat), 'the recovered prefix is rehydrated').toEqual(['R1']);
      expect(textsOf(chat), 'the delta that arrived during the fetch survives the reload').toEqual(['TAIL']);
      expect(pillsFor(chat, 'call_9'), 'exactly one pill for the tool call that raced the reload').toHaveLength(1);
      expect(chat.getResultForToolCall('call_9'), 'its result survives too').not.toBeNull();
      expect(chat.isLoading.value, 'the run is still live').toBe(true);

      captured[0].onDone();
      expect(chat.isLoading.value).toBe(false);
    });

    // The server advises per resumed subscription, so the same hole can be announced repeatedly.
    // A second refetch would restart the destructive reload and drop whatever the first is holding.
    it('coalesces repeated advisories on one socket into a single refetch', async () => {
      const { chat, resolveHistory } = await adviseWithHistoryHeld();

      advise(captured[0]);
      advise(captured[0]);
      await settle();
      expect(convMocks.loadConversationMessages, 'one hole, one refetch').toHaveBeenCalledTimes(1);

      captured[0].onMessage(text('TAIL', 1));
      resolveHistory([]);
      await settle();
      expect(textsOf(chat), 'the tail buffered across the repeats still lands once').toEqual(['TAIL']);

      // Once it has settled, a LATER advisory is a new hole and is honoured.
      advise(captured[0]);
      await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(2));
    });

    // Buffering must not become a way to resurrect an abandoned conversation: the reload already
    // discards its own result on a stale epoch, and the frames held beside it must go the same way.
    it('discards the buffered tail when the user leaves the conversation mid-refetch', async () => {
      const { chat, resolveHistory } = await adviseWithHistoryHeld();

      captured[0].onMessage(text('TAIL', 1));
      await chat.clearMessages(); // the switch away: bumps the conversation epoch
      resolveHistory([]);
      await settle();

      expect(textsOf(chat), 'nothing from the abandoned conversation paints onto the new one').toEqual([]);
    });

    // `done` says "everything before me is rendered". Letting it through while the tail it completes
    // is still buffered marks the transcript finished and then appends a block nothing ever settles.
    it('settles the run only after the buffered tail has been replayed', async () => {
      const { chat, resolveHistory } = await adviseWithHistoryHeld();

      captured[0].onMessage(text('TAIL', 1));
      captured[0].onDone();
      expect(chat.isLoading.value, 'the run is not finished while its own tail is still held').toBe(true);

      resolveHistory([]);
      await settle();
      expect(textsOf(chat)).toEqual(['TAIL']);
      expect(chat.isLoading.value, 'and it settles once the tail has landed').toBe(false);
    });

    // A failed refetch performs no wipe, so the tail it was holding is still valid — losing it would
    // turn a transient REST hiccup into a permanently frozen transcript.
    it('still replays the buffered tail when the refetch fails', async () => {
      const chat = await startStreaming();
      let rejectHistory!: (e: unknown) => void;
      convMocks.loadConversationMessages.mockImplementation(
        () => new Promise((_resolve, reject) => { rejectHistory = reject; })
      );
      advise(captured[0]);
      await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));

      captured[0].onMessage(text('lo', 0));
      rejectHistory(new Error('history unavailable'));
      await settle();

      expect(textsOf(chat), 'the tail lands on the history we already had').toEqual(['Hello']);
      expect(chat.error.value, 'a failed in-place fill is not a user-facing run failure').toBeNull();
    });
  });

  it('coalesces repeated close callbacks into one resync', async () => {
    await startStreaming();

    captured[0].onClose(recoveryClose());
    captured[0].onClose(recoveryClose());
    captured[0].onClose({ wasClean: false, code: 1006, reason: '' });
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));
    await settle();

    expect(convMocks.loadConversationMessages, 'single-flight: one rehydrate').toHaveBeenCalledTimes(1);
    expect(convMocks.getRunState, 'single-flight: one run-state check').toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection, 'single-flight: one replacement socket').toHaveBeenCalledTimes(2);
  });

  it('ignores a late close from an obsolete conversation epoch', async () => {
    const chat = await startStreaming();

    // The user switches to another conversation; the old socket's close lands afterwards.
    chat.setThreadId('thread-2');
    captured[0].onClose(recoveryClose());
    await settle();

    expect(convMocks.loadConversationMessages, 'no rehydrate of the abandoned conversation').not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection, 'no socket resurrected for thread-1').toHaveBeenCalledTimes(1);
  });

  it('clears loading when the run completed before run-state check', async () => {
    const chat = await startStreaming();
    convMocks.getRunState.mockResolvedValue(finished());

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(chat.isLoading.value).toBe(false));

    // Authoritative run state says "finished", so there is nothing to subscribe to — and, critically,
    // no permanent loading state.
    expect(wsMocks.createWebSocketConnection, 'no pointless socket for a finished run').toHaveBeenCalledTimes(1);
    expect(chat.isSending.value).toBe(false);
    expect(chat.error.value, 'a completed run is not an error').toBeNull();
  });

  it('does not resync a close the client itself initiated', async () => {
    const chat = await startStreaming();

    await chat.disconnectWebSocket();
    captured[0].onClose({ wasClean: true, code: 1000, reason: 'Client closing' });
    await settle();

    expect(convMocks.loadConversationMessages, 'a deliberate teardown is not a drop').not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
  });

  it('does not resync after the run finished normally with done', async () => {
    const chat = await startStreaming();

    captured[0].onDone();
    captured[0].onClose({ wasClean: true, code: 1000, reason: '' });
    await settle();

    expect(convMocks.loadConversationMessages, 'a completed run needs no recovery').not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    expect(chat.isLoading.value).toBe(false);
  });

  it('stops after the bounded attempt count and surfaces one actionable error', async () => {
    const chat = await startStreaming();

    // Every replacement socket drops again immediately — the pathological flapping backend.
    for (let attempt = 0; attempt <= MAX_RESYNC_ATTEMPTS; attempt++) {
      captured[captured.length - 1].onClose(recoveryClose());
      await settle();
    }

    expect(wsMocks.createWebSocketConnection, 'initial socket + a bounded number of retries').toHaveBeenCalledTimes(
      1 + MAX_RESYNC_ATTEMPTS,
    );
    expect(chat.error.value, 'the user is told once, instead of the client spinning silently').toBeTruthy();
    expect(chat.isLoading.value, 'a give-up must not leave a permanent loading state').toBe(false);
    expect(chat.isSending.value).toBe(false);
  });

  // Modality coverage. The resume path has burned us before with a fully-green suite that only ever fed
  // text (see samples/LmStreaming.Sample/CLAUDE.md — "a passing suite that never exercises the failing
  // modality is false confidence"). The drop+resync path replays EVERY kind, and finalized tool_call /
  // tool_call_result arrive WITHOUT a runId, so their merge keys must still line up with the
  // REST-rehydrated (real-runId) twins after the recovery.
  it('resyncs reasoning, tool calls, tool results and text without duplicating any of them', async () => {
    const reasoning = (moi: number, r: string) =>
      ({ $type: MessageType.Reasoning, role: 'assistant', reasoning: r, visibility: 1, generationId: GEN, messageOrderIdx: moi });
    const toolCall = (id: string, moi: number) =>
      ({ $type: MessageType.ToolCall, role: 'assistant', tool_call_id: id, function_name: 'Read', function_args: '{}', generationId: GEN, messageOrderIdx: moi });
    const toolResult = (id: string, moi: number) =>
      ({ $type: MessageType.ToolCallResult, role: 'tool', tool_call_id: id, result: `result ${id}`, generationId: GEN, messageOrderIdx: moi });
    const finalText = (t: string, moi: number) =>
      ({ $type: MessageType.Text, role: 'assistant', text: t, generationId: GEN, messageOrderIdx: moi });
    const persist = (id: string, ts: number, msg: Record<string, unknown>, moi: number) => ({
      id, threadId: 'thread-1', runId: RUN, generationId: GEN, messageOrderIdx: moi,
      timestamp: ts, messageType: String(msg.$type), role: String(msg.role), messageJson: JSON.stringify(msg),
    });

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('read a file');

    // Live before the drop: reasoning + an UNRESOLVED tool call.
    captured[0].onMessage(runAssignment());
    captured[0].onMessage(reasoning(0, 'R1'));
    captured[0].onMessage(toolCall('call_1', 1));
    expect(chat.getResultForToolCall('call_1')).toBeNull();

    // What the server had persisted by the time it dropped us (the result had not landed yet).
    convMocks.loadConversationMessages.mockResolvedValue([
      persist('p1', 1000, reasoning(0, 'R1'), 0),
      persist('p2', 1001, toolCall('call_1', 1), 1),
    ]);

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    // The replacement connection replays the whole run and then finishes it.
    captured[1].onMessage(runAssignment());
    captured[1].onMessage(reasoning(0, 'R1'));
    captured[1].onMessage(toolCall('call_1', 1));
    captured[1].onMessage(toolResult('call_1', 2));
    captured[1].onMessage(finalText('A1', 3));
    captured[1].onDone();

    const reasonings = chat.displayItems.value
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ $type?: string; reasoning?: string }> }).items)
      .filter((m) => m.$type === MessageType.Reasoning)
      .map((m) => m.reasoning ?? '');
    const pills = chat.displayItems.value
      .filter((i) => i.type === 'pill')
      .flatMap((i) => (i as { items: Array<{ tool_calls?: Array<{ tool_call_id?: string }> }> }).items)
      .filter((m) => m.tool_calls?.some((tc) => tc.tool_call_id === 'call_1'));
    const texts = chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text ?? '');

    expect(reasonings, 'one reasoning block across the drop').toEqual(['R1']);
    expect(pills, 'exactly one pill for call_1 across the drop').toHaveLength(1);
    expect(chat.getResultForToolCall('call_1'), 'the tool result lands after the recovery').not.toBeNull();
    expect(texts, 'one final answer, not a duplicate').toEqual(['A1']);
    expect(chat.isLoading.value).toBe(false);
  });

  // -------------------------------------------------------------------------------------------
  // Round-1 review findings. Each is a way the recovery above silently does NOT happen — or happens
  // destructively — against the REAL callback ordering and lifecycle. The tests above only ever
  // invoked `onClose` on an idealised drop, so none of these paths were exercised.
  // -------------------------------------------------------------------------------------------

  // C1. An ABNORMAL drop (1006) is the common field case, and wsClient fires `onError` FIRST and
  // `onClose` immediately after, synchronously (`socket.onclose`: `if (!wasClean) onError(...)` then
  // `onClose(...)`). If the error handler's cleanup latches "the client closed this socket", the close
  // that follows is misread as a deliberate teardown and the run is abandoned mid-flight — the exact
  // frozen-spinner bug this task exists to fix, reached through the other door.
  it('recovers from an abnormal close even though the transport reports an error first', async () => {
    const chat = await startStreaming();

    captured[0].onError('WebSocket closed unexpectedly: Unknown reason');
    captured[0].onClose({ wasClean: false, code: 1006, reason: '' });

    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    // Same REST-first contract as the clean drop: rehydrate, then authoritative run state, then socket.
    const restAt = convMocks.loadConversationMessages.mock.invocationCallOrder[0];
    const runStateAt = convMocks.getRunState.mock.invocationCallOrder[0];
    const socketAt = wsMocks.createWebSocketConnection.mock.invocationCallOrder[1];
    expect(restAt, 'REST rehydrate precedes the run-state check').toBeLessThan(runStateAt);
    expect(runStateAt, 'run state precedes the replacement socket').toBeLessThan(socketAt);

    // ONE recovery for one drop, even though two callbacks announced it.
    expect(convMocks.loadConversationMessages, 'one rehydrate for one drop').toHaveBeenCalledTimes(1);
    expect(wsMocks.sendWebSocketMessage, 'subscribe-only: only the original send').toHaveBeenCalledTimes(1);

    // The transient "closed unexpectedly" banner is superseded by the recovery, not left on screen.
    expect(chat.error.value, 'a drop we are recovering from is not a user-facing failure').toBeNull();
    expect(chat.isLoading.value, 'the UI stays in the streaming state across the recovery').toBe(true);
  });

  // C1, the other half. A server ERROR FRAME is TERMINAL for the run and carries a structured `code`
  // (`subagent_unavailable`, …). Letting transport errors recover must not also resurrect these, nor
  // wipe the banner that tells the user what actually went wrong.
  it('does not resync — and keeps the banner — after a terminal server error frame', async () => {
    const chat = await startStreaming();

    captured[0].onError('Sub-agent is unavailable', 'subagent_unavailable');
    captured[0].onClose({ wasClean: true, code: 1000, reason: '' });
    await settle();

    expect(convMocks.loadConversationMessages, 'an application error is not a drop').not.toHaveBeenCalled();
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    expect(chat.error.value, 'the user keeps the actionable error').toBe('Sub-agent is unavailable');
    expect(chat.isLoading.value).toBe(false);
  });

  // C2. The coordinator can abandon an operation mid-flight, but the REST load it already started
  // keeps running AND stays side-effectful: `loadMessagesFromBackend` clears the message index and
  // reassigns `threadId` AFTER its await. A slow thread-1 rehydrate landing after the user opened
  // thread-2 therefore wipes thread-2's transcript and silently reselects the conversation they left.
  it('does not let a slow rehydrate of the previous conversation overwrite the one the user switched to', async () => {
    const persistedText = (id: string, thread: string, body: string) => {
      const msg = { $type: MessageType.Text, role: 'assistant', text: body, generationId: GEN, messageOrderIdx: 0 };
      return {
        id, threadId: thread, runId: RUN, generationId: GEN, messageOrderIdx: 0,
        timestamp: 1000, messageType: String(msg.$type), role: 'assistant', messageJson: JSON.stringify(msg),
      };
    };

    const chat = await startStreaming();

    // thread-1's rehydrate hangs until this test releases it; thread-2's resolves immediately.
    let releaseThread1!: (rows: unknown[]) => void;
    const thread1Load = new Promise<unknown[]>((resolve) => { releaseThread1 = resolve; });
    convMocks.loadConversationMessages.mockImplementation((id: string) =>
      id === 'thread-1' ? thread1Load : Promise.resolve([persistedText('p2', 'thread-2', 'TWO')]),
    );

    // The drop starts a recovery whose REST load is now in flight.
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledWith('thread-1'));

    // The user opens a different conversation while that load is still outstanding.
    chat.setThreadId('thread-2');
    await chat.loadMessagesFromBackend('thread-2');
    expect(chat.threadId.value, 'the switch itself works').toBe('thread-2');

    // …and only now does thread-1's history arrive.
    releaseThread1([persistedText('p1', 'thread-1', 'ONE')]);
    await settle();

    expect(chat.threadId.value, 'a stale rehydrate must not reselect the conversation the user left').toBe('thread-2');
    const texts = chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text ?? '');
    expect(texts, "thread-2's transcript survives intact").toEqual(['TWO']);
    expect(wsMocks.createWebSocketConnection, 'no stale resubscribe for the abandoned conversation').toHaveBeenCalledTimes(1);
  });

  // I1. The attempt budget is scoped to (thread, epoch), and a new user send changes neither. Once a
  // conversation has burned its budget, every later run in it would inherit the exhausted state and
  // never recover — one bad patch of connectivity permanently disarming recovery for that thread.
  it('restores the attempt budget when the user starts a new run in the same conversation', async () => {
    const chat = await startStreaming();

    for (let attempt = 1; attempt <= MAX_RESYNC_ATTEMPTS; attempt++) {
      captured[attempt - 1].onClose(recoveryClose());
      await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(attempt + 1));
    }
    captured[MAX_RESYNC_ATTEMPTS].onClose(recoveryClose());
    await vi.waitFor(() => expect(chat.error.value, 'recovery gave up and said so').toBeTruthy());
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1 + MAX_RESYNC_ATTEMPTS);

    // A new run in the SAME conversation is a fresh start, not a continuation of the old failure.
    await chat.sendMessage('try again');
    expect(chat.error.value, 'a new send clears the stale give-up banner').toBeNull();
    const socketsBeforeDrop = wsMocks.createWebSocketConnection.mock.calls.length;

    captured[captured.length - 1].onClose(recoveryClose());
    await vi.waitFor(() =>
      expect(wsMocks.createWebSocketConnection, 'the new run gets its own recovery attempts').toHaveBeenCalledTimes(
        socketsBeforeDrop + 1,
      ),
    );
  });

  // I2. An explicit `stream_recovery` frame arrives while the socket is still OPEN. Detaching our
  // reference without closing it leaks that socket and lets the server keep pushing frames from the
  // stream we just declared dead — straight into the replacement connection's rehydrated state.
  it('closes the dropped socket before opening its replacement', async () => {
    await startStreaming();
    const droppedConnection = await wsMocks.createWebSocketConnection.mock.results[0].value;

    captured[0].onStreamRecovery?.({ reason: 'slow_consumer', threadId: 'thread-1', runId: RUN, generationId: GEN });
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    expect(wsMocks.closeWebSocketConnection, 'the dead socket is released, not leaked').toHaveBeenCalledWith(
      droppedConnection,
    );
    expect(
      wsMocks.closeWebSocketConnection.mock.invocationCallOrder[0],
      'the replacement never overlaps the socket it replaces',
    ).toBeLessThan(wsMocks.createWebSocketConnection.mock.invocationCallOrder[1]);

    // The close event that follows the frame is still the same logical drop.
    captured[0].onClose(recoveryClose());
    await settle();
    expect(convMocks.loadConversationMessages, 'frame + close coalesce into one recovery').toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2);
  });

  // -------------------------------------------------------------------------------------------
  // Round-2 review findings. Each is a place where the recovery above is defeated by the fact that a
  // CONNECTION outlives the RUN it was opened for, or by state that is global while the thing that
  // mutates it is per-socket / per-conversation.
  // -------------------------------------------------------------------------------------------

  // C-N1. The socket is full-duplex and outlives its run: turn 2 is sent on the very same connection
  // (`sendMessageViaWebSocket`'s reuse branch). `doneReceived` / `resyncRequested` / `terminalError`
  // are facts about the run that ENDED, so a reused socket still carrying them makes every later drop
  // read as "this run already finished" — recovery silently never happens for any turn but the first.
  it('recovers from a drop on the second turn of a reused socket', async () => {
    const chat = await startStreaming();

    // Turn 1 completes normally; the connection stays open for the next turn.
    captured[0].onDone();
    expect(chat.isLoading.value, 'turn 1 finished').toBe(false);

    // Turn 2 goes out on the SAME socket — no new connection is created.
    await chat.sendMessage('and again');
    expect(wsMocks.createWebSocketConnection, 'turn 2 reuses the open socket').toHaveBeenCalledTimes(1);
    expect(wsMocks.sendWebSocketMessage, 'two prompts, one connection').toHaveBeenCalledTimes(2);
    captured[0].onMessage(runAssignment());
    captured[0].onMessage(text('Hel'));

    // …and turn 2 is the one that drops.
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() =>
      expect(wsMocks.createWebSocketConnection, 'a later turn is recovered too').toHaveBeenCalledTimes(2),
    );

    // Same REST-first contract as turn 1.
    const restAt = convMocks.loadConversationMessages.mock.invocationCallOrder[0];
    const runStateAt = convMocks.getRunState.mock.invocationCallOrder[0];
    const socketAt = wsMocks.createWebSocketConnection.mock.invocationCallOrder[1];
    expect(restAt, 'REST rehydrate precedes the run-state check').toBeLessThan(runStateAt);
    expect(runStateAt, 'run state precedes the replacement socket').toBeLessThan(socketAt);
    expect(wsMocks.sendWebSocketMessage, 'subscribe-only: neither prompt is re-sent').toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value, 'the recovered turn is still streaming').toBe(true);
    expect(chat.error.value, 'a successful recovery surfaces no error').toBeNull();
  });

  // I-N1. A recovery replaces socket A with socket B, but A's transport can still report its own
  // death afterwards. Handled globally, that late error tears down the LIVE replacement
  // (`closeActiveConnection` closes whatever is current) and paints a banner over a healthy run.
  it('ignores a late transport error from the socket its recovery already replaced', async () => {
    const chat = await startStreaming();

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));
    const replacement = await wsMocks.createWebSocketConnection.mock.results[1].value;
    const closesBefore = wsMocks.closeWebSocketConnection.mock.calls.length;

    // Socket A's error lands only now — after B is live and streaming.
    captured[0].onError('WebSocket closed unexpectedly: Unknown reason');
    await settle();

    expect(
      wsMocks.closeWebSocketConnection.mock.calls.map((call) => call[0]),
      'the live replacement is not torn down by its predecessor',
    ).not.toContain(replacement);
    expect(wsMocks.closeWebSocketConnection, 'and nothing else is closed either').toHaveBeenCalledTimes(closesBefore);
    expect(wsMocks.createWebSocketConnection, 'no extra recovery is started').toHaveBeenCalledTimes(2);
    expect(chat.error.value, "a dead socket's error is not the live run's banner").toBeNull();
    expect(chat.isLoading.value, 'the replacement keeps streaming').toBe(true);
  });

  // I-N2. `loadMessagesFromBackend` re-checks the conversation after the MESSAGES round-trip, then
  // makes a SECOND one for the usage aggregate and applies it unconditionally. The banner is global,
  // so a switch during that second await repaints it with the totals of the conversation just left.
  it('does not apply the previous conversation usage totals after a switch', async () => {
    const aggregate = (totalTokens: number) => ({
      rootConversationId: 'thread-x',
      schemaVersion: 1,
      foldedRevision: 1,
      completeness: 'Complete',
      perModel: [
        {
          modelId: 'model-a',
          inputTokens: totalTokens,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheWriteTokens: 0,
          reasoningTokens: 0,
          totalTokens,
          attemptCount: 1,
        },
      ],
      totalTokens,
      currency: 'USD',
    });

    // thread-1's usage request hangs until this test releases it; thread-2's resolves immediately.
    let releaseThread1Usage!: (value: unknown) => void;
    const thread1Usage = new Promise<unknown>((resolve) => {
      releaseThread1Usage = resolve;
    });
    convMocks.getConversationUsage.mockImplementation((id: string) =>
      id === 'thread-1' ? thread1Usage : Promise.resolve(aggregate(222)),
    );

    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');

    // thread-1's MESSAGES arrive (so the existing epoch check passes); its usage is still outstanding.
    const staleLoad = chat.loadMessagesFromBackend('thread-1');
    await vi.waitFor(() => expect(convMocks.getConversationUsage).toHaveBeenCalledWith('thread-1'));

    // The user opens another conversation while that usage request is in flight.
    chat.setThreadId('thread-2');
    await chat.loadMessagesFromBackend('thread-2');
    expect(chat.cumulativeUsage.value.totalTokens, "thread-2's own totals are on the banner").toBe(222);

    releaseThread1Usage(aggregate(999_999));
    await staleLoad;
    await settle();

    expect(
      chat.cumulativeUsage.value.totalTokens,
      'a usage response for the conversation the user left must not repaint the banner',
    ).toBe(222);
  });

  // I-N3. `requestStreamResync` detaches the still-OPEN socket before asking the coordinator, and the
  // coordinator rejects stale and over-budget requests without running a single step. Nobody then
  // closes the detached socket: it stays open, pushing frames from a stream we have already given up
  // on — the exact leak the "close before reopen" fix was meant to prevent, on the rejected path.
  it('releases the dropped socket even when recovery has no attempts left', async () => {
    const chat = await startStreaming();

    for (let attempt = 1; attempt <= MAX_RESYNC_ATTEMPTS; attempt++) {
      captured[attempt - 1].onClose(recoveryClose());
      await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(attempt + 1));
    }
    const abandoned = await wsMocks.createWebSocketConnection.mock.results[MAX_RESYNC_ATTEMPTS].value;
    const closesBefore = wsMocks.closeWebSocketConnection.mock.calls.length;

    // The server announces a drop on a socket that is still OPEN, with the budget already spent.
    captured[MAX_RESYNC_ATTEMPTS].onStreamRecovery?.({
      reason: 'slow_consumer',
      threadId: 'thread-1',
      runId: RUN,
      generationId: GEN,
    });
    await vi.waitFor(() => expect(chat.error.value, 'recovery gave up and said so').toBeTruthy());
    await settle();

    expect(wsMocks.closeWebSocketConnection, 'a socket we gave up on is still released').toHaveBeenCalledWith(
      abandoned,
    );
    expect(wsMocks.closeWebSocketConnection, 'released exactly once').toHaveBeenCalledTimes(closesBefore + 1);
    expect(
      wsMocks.createWebSocketConnection,
      'no replacement for a recovery that never started',
    ).toHaveBeenCalledTimes(1 + MAX_RESYNC_ATTEMPTS);
    expect(chat.isLoading.value, 'giving up settles the UI to idle').toBe(false);

    // The close that follows the frame is the same logical drop: no second release, no second banner.
    captured[MAX_RESYNC_ATTEMPTS].onClose(recoveryClose());
    await settle();
    expect(wsMocks.closeWebSocketConnection).toHaveBeenCalledTimes(closesBefore + 1);
    expect(chat.error.value, 'exactly one actionable failure').toContain(`${MAX_RESYNC_ATTEMPTS} attempts`);
  });

  // -------------------------------------------------------------------------------------------
  // Round-3 review findings. Both are TIMING defeats: a run boundary the client can only infer from
  // its own send, and a replacement socket that dies before the operation which created it finishes.
  // -------------------------------------------------------------------------------------------

  // C1. Full duplex means turn 2 is sent (and queued by the server) while turn 1 is still streaming,
  // so turn 1's `done` lands on the SHARED socket AFTER the send that starts turn 2. Resetting the
  // run-scoped flags at send time is therefore not enough — the late `done` re-arms `doneReceived`
  // and the queued turn's drop reads as "this run already finished". Only the server's
  // `run_assignment` authoritatively says "a new run starts here".
  it('recovers a queued turn whose predecessor completes after the send', async () => {
    const RUN2 = 'run-2';
    const GEN2 = 'gen-2';
    const nextRunAssignment = () => ({
      $type: MessageType.RunAssignment,
      Assignment: { runId: RUN2, generationId: GEN2, inputIds: [] },
    });
    const nextText = (t: string) =>
      ({ $type: 'text_update', text: t, role: 'assistant', runId: RUN2, generationId: GEN2, messageOrderIdx: 0 });

    const chat = await startStreaming();

    // Turn 2 goes out while turn 1 is still streaming — same connection, no new socket.
    await chat.sendMessage('and again');
    expect(wsMocks.createWebSocketConnection, 'the queued turn reuses the open socket').toHaveBeenCalledTimes(1);
    expect(wsMocks.sendWebSocketMessage, 'two prompts, one connection').toHaveBeenCalledTimes(2);

    // Only NOW does turn 1 finish. This `done` belongs to the run that ENDED, not to the queued one.
    captured[0].onDone();

    // The server starts the queued run and streams it…
    captured[0].onMessage(nextRunAssignment());
    captured[0].onMessage(nextText('Hel'));

    // …and THAT is the run that drops.
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() =>
      expect(wsMocks.createWebSocketConnection, 'the queued turn is recovered too').toHaveBeenCalledTimes(2),
    );
    await settle();

    expect(convMocks.loadConversationMessages, 'exactly one recovery for one drop').toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection, 'exactly one replacement socket').toHaveBeenCalledTimes(2);
    expect(wsMocks.sendWebSocketMessage, 'subscribe-only: neither prompt is re-sent').toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value, 'the recovered turn is still streaming').toBe(true);
    expect(chat.error.value, 'a successful recovery surfaces no error').toBeNull();
  });

  // I2. The replacement socket can be dead on arrival: the server drops it before
  // `createWebSocketConnection` even hands back the reference, so its close lands while the operation
  // that created it is STILL in flight. Coalescing that into the creator makes it run no step at all —
  // the stillborn socket stays installed as the active connection, the next attempt short-circuits on
  // resumeStreamIfActive's "already connected" guard, and the spinner never comes down.
  it('recovers again when the replacement socket dies during its own open', async () => {
    const chat = await startStreaming();

    wsMocks.createWebSocketConnection.mockImplementationOnce(async (options: any) => {
      captured.push(options);
      const connection = {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: options.threadId,
        isConnected: true,
      };
      // Dead on arrival — announced before the caller can hold a reference to it.
      options.onClose(recoveryClose());
      return connection;
    });

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() =>
      expect(wsMocks.createWebSocketConnection, 'a bounded NEXT attempt follows the stillborn one').toHaveBeenCalledTimes(3),
    );
    await settle();

    const stillborn = await wsMocks.createWebSocketConnection.mock.results[1].value;
    expect(
      wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]),
      'the stillborn replacement is released, not left installed as the live connection',
    ).toContain(stillborn);
    expect(
      wsMocks.closeWebSocketConnection.mock.invocationCallOrder.at(-1),
      'single-flight: the next attempt never overlaps the socket it replaces',
    ).toBeLessThan(wsMocks.createWebSocketConnection.mock.invocationCallOrder[2]);
    expect(convMocks.loadConversationMessages, 'one rehydrate per attempt, never two in parallel').toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value, 'the run is streaming on the new socket, not frozen').toBe(true);
    expect(chat.error.value, 'attempts remained, so nothing was given up on').toBeNull();
  });

  // -------------------------------------------------------------------------------------------
  // Round-4 review findings. All three are OWNERSHIP defeats: work queued behind an operation
  // outlives the decision that cancelled it, an open that no longer owns the connection installs it
  // anyway, and a socket we already declared dead still speaks for the whole composable.
  // -------------------------------------------------------------------------------------------

  // C-R4. Stop/cancel invalidates the coordinator, but the rerun queued behind an in-flight
  // operation is a plain `.then` on that operation: after the creator settles it re-enters
  // `request`, where `isCurrent` is still TRUE (cancelling does not change the conversation) and the
  // attempt budget has just been reset. A run the user explicitly ended therefore rehydrates itself
  // over REST, opens a fresh socket, and puts the spinner back up.
  it('abandons a rerun queued for a drop when the user stops the run', async () => {
    const chat = await startStreaming();

    // The replacement socket is stillborn (which is what queues the rerun) AND its open is held, so
    // Stop lands strictly between "rerun queued" and "the operation it is queued behind settles".
    let releaseReplacement!: () => void;
    wsMocks.createWebSocketConnection.mockImplementationOnce(async (options: any) => {
      captured.push(options);
      const connection = {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: options.threadId,
        isConnected: true,
      };
      options.onClose(recoveryClose());
      await new Promise<void>((resolve) => {
        releaseReplacement = resolve;
      });
      return connection;
    });

    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(2));

    await chat.cancelStream();
    expect(chat.isLoading.value, 'Stop settles the UI immediately').toBe(false);

    releaseReplacement();
    await settle();

    expect(convMocks.loadConversationMessages, 'no rehydrate for a run the user ended').toHaveBeenCalledTimes(1);
    expect(wsMocks.createWebSocketConnection, 'no socket resurrected after Stop').toHaveBeenCalledTimes(2);
    expect(chat.isLoading.value, 'Stop stays stopped').toBe(false);
    expect(chat.error.value, 'cancelling is not a failure').toBeNull();

    // Abandoning the rerun must not abandon the socket: the stillborn replacement is still released.
    const stillborn = await wsMocks.createWebSocketConnection.mock.results[1].value;
    expect(
      wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]),
      'the stillborn socket is released, not leaked by the cancel',
    ).toContain(stillborn);
  });

  // I-R4a. `openStreamConnection` takes ownership SYNCHRONOUSLY (`activeSocketState`) but installs
  // its connection only when `createWebSocketConnection` resolves. Two overlapping opens — a
  // recovery reopening the stream while the user sends — therefore install in RESOLUTION order, not
  // ownership order: whichever resolves last overwrites `wsConnection`, the other socket is never
  // closed (it keeps delivering frames into the same callbacks), and the send path puts the user's
  // prompt on whatever happens to be installed at that instant.
  /**
   * Drive a recovery's reopen and a user send opening a socket at the same time, releasing the two
   * `createWebSocketConnection` promises in the given order. The send starts SECOND, so it is the
   * open that owns the connection; which promise resolves first must not change that.
   */
  async function raceRecoveryAgainstSend(release: 'send-first' | 'recovery-first') {
    const chat = await startStreaming();

    const gates: Array<() => void> = [];
    wsMocks.createWebSocketConnection.mockImplementation(async (options: any) => {
      captured.push(options);
      const connection = {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: options.threadId,
        isConnected: true,
      };
      await new Promise<void>((resolve) => gates.push(resolve));
      return connection;
    });

    // The stream drops, and the recovery gets as far as opening its replacement (socket B)…
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(gates.length, 'the recovery is opening its replacement').toBe(1));

    // …then, while that open is still in flight, the user sends — opening socket C (the owner).
    const sending = chat.sendMessage('and again');
    await vi.waitFor(() => expect(gates.length, 'the send is opening its own socket').toBe(2));

    for (const index of release === 'send-first' ? [1, 0] : [0, 1]) {
      gates[index]();
      await settle();
    }
    await sending;

    return {
      chat,
      recoverySocket: await wsMocks.createWebSocketConnection.mock.results[1].value,
      sendSocket: await wsMocks.createWebSocketConnection.mock.results[2].value,
    };
  }

  /** One live connection, one released loser, and a prompt that went to the live one. */
  async function expectOnlyTheOwningOpenInstalled(
    chat: ReturnType<typeof useChat>,
    recoverySocket: unknown,
    sendSocket: unknown,
  ) {
    const closed = wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]);
    expect(closed, 'the superseded open releases the socket it created').toContain(recoverySocket);
    expect(closed, 'the connection that owns the thread stays open').not.toContain(sendSocket);

    const sentOn = wsMocks.sendWebSocketMessage.mock.calls.map((call: unknown[]) => call[0]);
    expect(sentOn, 'the prompt goes out on the connection the send owns').toContain(sendSocket);
    expect(sentOn, 'never on a socket the composable is not tracking').not.toContain(recoverySocket);

    // A superseded socket is not a dropped one: its close must not manufacture a second recovery
    // (nor a duplicate live subscription) for a stream that is being served perfectly well.
    const rehydrates = convMocks.loadConversationMessages.mock.calls.length;
    captured[1].onClose(recoveryClose());
    await settle();
    expect(convMocks.loadConversationMessages, 'a superseded socket dying is not a drop').toHaveBeenCalledTimes(
      rehydrates,
    );
    expect(wsMocks.createWebSocketConnection, 'three sockets in total: original, recovery, send').toHaveBeenCalledTimes(3);
    expect(chat.error.value, 'the race is invisible to the user').toBeNull();
  }

  it('installs only the owning connection when a send races a recovery reopen', async () => {
    const { chat, recoverySocket, sendSocket } = await raceRecoveryAgainstSend('send-first');
    await expectOnlyTheOwningOpenInstalled(chat, recoverySocket, sendSocket);
  });

  it('installs only the owning connection even when the superseded open resolves last', async () => {
    const { chat, recoverySocket, sendSocket } = await raceRecoveryAgainstSend('recovery-first');
    await expectOnlyTheOwningOpenInstalled(chat, recoverySocket, sendSocket);
  });

  // I-R4b. `onDone`'s SHARED `invalidate()` is guarded by "am I the live socket", but a socket that
  // has already reported a drop stays `activeSocketState` until its replacement is installed — and
  // the server announces a recovery while that socket is still OPEN. A `done` for the turn that just
  // ENDED (full duplex: turn 1 completes after turn 2 was queued) can therefore land mid-recovery
  // and abandon it. AUTHORITATIVE run state, not a frame from a socket we already declared dead,
  // decides whether there is still something to resubscribe to.
  it('does not let a late done from the dropped socket abandon its own recovery', async () => {
    const chat = await startStreaming();

    let releaseHistory!: () => void;
    convMocks.loadConversationMessages.mockImplementationOnce(async () => {
      await new Promise<void>((resolve) => {
        releaseHistory = resolve;
      });
      return [];
    });

    captured[0].onStreamRecovery?.({ reason: 'slow_consumer', threadId: 'thread-1', runId: RUN, generationId: GEN });
    await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));

    // The dropped socket's last frame lands while its own recovery is rehydrating.
    captured[0].onDone();
    releaseHistory();

    await vi.waitFor(() =>
      expect(wsMocks.createWebSocketConnection, 'the recovery still resubscribes').toHaveBeenCalledTimes(2),
    );
    await settle();
    expect(chat.isLoading.value, 'the run the server still reports in progress keeps streaming').toBe(true);
    expect(chat.error.value, 'nothing failed').toBeNull();
  });

  // -------------------------------------------------------------------------------------------
  // Round-5 finding. Round 4 made the OWNING open the one that installs — correct, but a send that
  // loses that race is not merely a discarded socket: it is the user's prompt, already on screen as
  // a pending message. Failing it outright is the worse outcome when the winner is serving exactly
  // the route the send asked for; failing it is the only SAFE outcome when it is not.
  // -------------------------------------------------------------------------------------------

  /**
   * The adversarial ordering: a recovery's reopen starts AFTER a user send's open — so it wins
   * ownership — but for the SAME thread. Gating the recovery's REST rehydrate is what puts the send
   * in the middle: the drop detaches the socket, the user sends into that gap, and the resubscribe
   * lands last. Both opens are then released in the order that hurts most (loser first, while the
   * winner is still in flight and there is no installed connection to be found).
   */
  async function raceSendBehindRecoveryReopen(
    options: { winnerFails?: boolean; beforeReopen?: () => void; getModeId?: () => string } = {},
  ) {
    const chat = await startStreaming(options.getModeId);

    let releaseHistory!: () => void;
    convMocks.loadConversationMessages.mockImplementationOnce(async () => {
      await new Promise<void>((resolve) => {
        releaseHistory = resolve;
      });
      return [];
    });

    const gates: Array<(fail?: boolean) => void> = [];
    wsMocks.createWebSocketConnection.mockImplementation(async (opts: any) => {
      captured.push(opts);
      const connection = {
        socket: { readyState: WebSocket.OPEN },
        connectionId: `ws-${captured.length}`,
        threadId: opts.threadId,
        isConnected: true,
      };
      await new Promise<void>((resolve, reject) =>
        gates.push((fail) => (fail ? reject(new Error('the replacement socket never opened')) : resolve())),
      );
      return connection;
    });

    // The stream drops and the recovery stalls on its (gated) REST rehydrate.
    captured[0].onClose(recoveryClose());
    await vi.waitFor(() => expect(convMocks.loadConversationMessages).toHaveBeenCalledTimes(1));

    // The user sends into that gap: this open (socket B) starts FIRST…
    const sending = chat.sendMessage('second prompt');
    await vi.waitFor(() => expect(gates.length, 'the send opened its own socket').toBe(1));

    options.beforeReopen?.();

    // …and the recovery's resubscribe (socket C) starts SECOND, so it is the one that owns it.
    releaseHistory();
    await vi.waitFor(() => expect(gates.length, 'the recovery reopen started after the send').toBe(2));

    // The send's socket resolves into a race it has already lost, BEFORE the winner is installed.
    gates[0]();
    await settle();
    gates[1](options.winnerFails);
    await settle();
    await sending;

    return {
      chat,
      sendSocket: await wsMocks.createWebSocketConnection.mock.results[1].value,
      winner: options.winnerFails ? null : await wsMocks.createWebSocketConnection.mock.results[2].value,
    };
  }

  it('sends a superseded prompt on the connection that won the race', async () => {
    const { chat, sendSocket, winner } = await raceSendBehindRecoveryReopen();

    const sends = wsMocks.sendWebSocketMessage.mock.calls;
    expect(sends, 'the first prompt and the raced one — each sent exactly once').toHaveLength(2);
    expect(sends[1], 'the raced prompt goes out on the connection that owns the thread').toEqual([
      winner,
      'second prompt',
    ]);

    const closed = wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]);
    expect(closed, 'the send still releases the socket it lost with').toContain(sendSocket);
    expect(closed, 'the winner stays open to carry the prompt').not.toContain(winner);

    expect(chat.error.value, 'a race the client resolved itself is not a user-facing failure').toBeNull();
    expect(chat.isSending.value, 'the send finished').toBe(false);
    expect(chat.isLoading.value, 'the prompt is streaming').toBe(true);
  });

  it('keeps the prompt actionable when the race leaves no connection to hand it to', async () => {
    const { chat, sendSocket } = await raceSendBehindRecoveryReopen({ winnerFails: true });

    expect(wsMocks.sendWebSocketMessage, 'never a prompt on a socket nobody owns').toHaveBeenCalledTimes(1);
    expect(
      wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]),
      'the superseded socket is released either way',
    ).toContain(sendSocket);
    expect(chat.error.value, 'the user is told, not left watching a prompt that never went out').toBeTruthy();
    expect(chat.isSending.value, 'the send settled').toBe(false);
    // The chosen fallback is "actionable error + settled prompt": the recovery's own rehydrate
    // clears the pending queue, so the optimistic message must not be left sitting in a queue that
    // would later be flushed onto a socket for a run this prompt never started.
    expect(
      chat.pendingMessages.value.map((msg: { content: { text?: string } }) => msg.content.text),
      'nothing is left half-queued to be sent behind the user’s back',
    ).not.toContain('second prompt');
  });

  it('never hands a superseded prompt to a winner opened for a different mode', async () => {
    let mode = 'default';
    const { chat, sendSocket } = await raceSendBehindRecoveryReopen({
      getModeId: () => mode,
      beforeReopen: () => {
        mode = 'plan';
      },
    });

    expect(captured[2].modeId, 'the winning connection really is a different route').toBe('plan');
    expect(wsMocks.sendWebSocketMessage, 'a prompt is never sent across modes').toHaveBeenCalledTimes(1);
    expect(
      wsMocks.closeWebSocketConnection.mock.calls.map((call: unknown[]) => call[0]),
      'the send releases its own socket rather than reusing a mismatched one',
    ).toContain(sendSocket);
    expect(chat.error.value, 'refusing is visible, never silent').toBeTruthy();
  });
});

// `generation_abandoned`: the provider stream was cut mid-reply, the agent loop threw that generation
// away, and it is retrying the SAME turn under a NEW generation id — on the SAME, still-open socket
// (this is NOT a disconnect; the run never stopped). Before this frame was wired the client silently
// swallowed it, so the abandoned generation's half-written bubble stayed on screen forever and the
// retry rendered as a SECOND assistant bubble beside it — the user saw the answer twice, once
// truncated.
//
// The contract is narrow on purpose: drop exactly the abandoned generation's UNFINALIZED blocks.
// Anything already delivered whole is canonical and stays, whichever generation produced it.
describe('useChat — generation_abandoned drops the abandoned partial and lets the retry render once', () => {
  let captured: any[];
  const RUN = 'run-1';

  // Live wire shapes for one thread/run, distinguished only by generationId.
  const textUpdateFor = (gen: string, text: string, moi = 0) =>
    ({ $type: 'text_update', text, role: 'assistant', runId: RUN, generationId: gen, messageOrderIdx: moi });
  const finalTextFor = (gen: string, text: string, moi = 0) =>
    ({ $type: MessageType.Text, role: 'assistant', text, runId: RUN, generationId: gen, messageOrderIdx: moi });

  const textsOf = (chat: ReturnType<typeof useChat>) =>
    chat.displayItems.value
      .filter((i) => i.type === 'assistant-message')
      .map((i) => (i as { content: { text?: string } }).content.text ?? '');

  beforeEach(() => {
    captured = [];
    wsMocks.createWebSocketConnection.mockReset();
    wsMocks.sendWebSocketMessage.mockReset();
    wsMocks.closeWebSocketConnection.mockReset();
    convMocks.loadConversationMessages.mockReset();
    convMocks.getRunState.mockReset();

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

  async function startStreaming() {
    const chat = useChat({ getModeId: () => 'default' });
    chat.setThreadId('thread-1');
    await chat.sendMessage('hi');
    expect(wsMocks.createWebSocketConnection).toHaveBeenCalledTimes(1);
    return chat;
  }

  // The user-visible symptom in one shot, with NO intermediate assertions to short-circuit it: run
  // the whole abandon-and-retry sequence, then count bubbles. Unwired, this reports the truncated
  // gen-A partial AND the gen-B retry side by side — the "answer rendered twice, once cut off"
  // report. The step-by-step test below localizes which step broke.
  it('renders the retried answer as ONE assistant block, not two', async () => {
    const chat = await startStreaming();

    captured[0].onMessage(textUpdateFor('gen-A', 'The retried '));
    captured[0].onMessage(textUpdateFor('gen-A', 'answer is cut o'));
    captured[0].onGenerationAbandoned?.({ threadId: 'thread-1', runId: RUN, generationId: 'gen-A' });
    captured[0].onMessage(finalTextFor('gen-B', 'The retried answer is cut off no longer.'));

    expect(textsOf(chat), 'the abandoned partial must not survive beside its retry').toEqual([
      'The retried answer is cut off no longer.',
    ]);
  });

  it('removes the abandoned partial, keeps finalized blocks, and renders the retry exactly once', async () => {
    const chat = await startStreaming();

    // An EARLIER generation in this same run already produced a whole, finalized answer. It is
    // canonical and must survive the abandon untouched.
    captured[0].onMessage(finalTextFor('gen-EARLIER', 'Earlier finished answer.', 0));

    // Generation A then streams a reply that is about to be cut off mid-sentence.
    captured[0].onMessage(textUpdateFor('gen-A', 'The retried '));
    captured[0].onMessage(textUpdateFor('gen-A', 'answer is cut o'));
    expect(textsOf(chat), 'the in-flight partial renders while gen-A is alive').toEqual([
      'Earlier finished answer.',
      'The retried answer is cut o',
    ]);

    // The stream drops; the server abandons gen-A and will retry under a new id. Optional-call on
    // purpose: if the callback is not wired through to useChat this is a silent no-op, and the
    // duplicate-bubble assertion below is what catches it.
    captured[0].onGenerationAbandoned?.({ threadId: 'thread-1', runId: RUN, generationId: 'gen-A' });

    expect(textsOf(chat), 'the abandoned partial is gone; the finalized block stays').toEqual([
      'Earlier finished answer.',
    ]);

    // Generation B delivers the retried reply in full.
    captured[0].onMessage(finalTextFor('gen-B', 'The retried answer is cut off no longer.'));

    expect(
      textsOf(chat),
      'exactly ONE assistant block for the retried content, beside the preserved finalized one',
    ).toEqual(['Earlier finished answer.', 'The retried answer is cut off no longer.']);

    // Replay/duplicate delivery of the same control frame must change nothing.
    const before = textsOf(chat);
    captured[0].onGenerationAbandoned?.({ threadId: 'thread-1', runId: RUN, generationId: 'gen-A' });
    expect(textsOf(chat), 'a replayed abandon frame is idempotent').toEqual(before);

    captured[0].onDone();
    expect(chat.isLoading.value).toBe(false);
  });

  // Pins the `isStreaming` half of the predicate SEPARATELY from the generationId half. Dropping
  // every block of the abandoned generation (rather than only its unfinalized ones) would delete
  // content the server already delivered whole under that same generation — e.g. the text finalized
  // before the cut, in the turn that is being retried.
  it('keeps a FINALIZED block of the abandoned generation itself', async () => {
    const chat = await startStreaming();

    // gen-A finalized one block, then started a second that never completed.
    captured[0].onMessage(finalTextFor('gen-A', 'Finalized under gen-A.', 0));
    captured[0].onMessage(textUpdateFor('gen-A', 'Unfinished under gen-A', 1));
    expect(textsOf(chat)).toEqual(['Finalized under gen-A.', 'Unfinished under gen-A']);

    captured[0].onGenerationAbandoned?.({ threadId: 'thread-1', runId: RUN, generationId: 'gen-A' });

    expect(
      textsOf(chat),
      'only the unfinalized block of gen-A is dropped; its finalized block is canonical',
    ).toEqual(['Finalized under gen-A.']);
  });

  // Pins `finalize(generationId)` — the merger accumulators are keyed `${genId}::t${turnSeq}`, so
  // clearing them requires passing the abandoned id. Calling the no-arg `finalize()` (which clears
  // only the 'default' key) or omitting the call leaves gen-A's accumulated prefix alive, and a
  // stray in-flight delta that lands after the abandon resurrects the whole truncated answer.
  it('clears the merger accumulator for the abandoned generation', async () => {
    const chat = await startStreaming();

    captured[0].onMessage(textUpdateFor('gen-A', 'stale prefix that must not come back: '));
    captured[0].onGenerationAbandoned?.({ threadId: 'thread-1', runId: RUN, generationId: 'gen-A' });
    expect(textsOf(chat), 'partial dropped').toEqual([]);

    // A delta already in flight when the abandon was published still arrives.
    captured[0].onMessage(textUpdateFor('gen-A', 'stray'));

    expect(
      textsOf(chat),
      'a late gen-A delta must start from empty, not from the abandoned accumulation',
    ).toEqual(['stray']);
  });

  // The callback only reaches useChat if it is present in the createWebSocketConnection options
  // literal. Pinned directly so removing the wiring names itself, rather than only surfacing as a
  // confusing bubble count above.
  it('wires onGenerationAbandoned onto the chat WebSocket connection', async () => {
    await startStreaming();
    expect(typeof captured[0].onGenerationAbandoned).toBe('function');
  });
});
