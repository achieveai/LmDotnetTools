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
}));

vi.mock('@/api/conversationsApi', () => ({
  loadConversationMessages: convMocks.loadConversationMessages,
  getRunState: convMocks.getRunState,
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
