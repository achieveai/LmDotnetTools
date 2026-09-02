import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ref, nextTick } from 'vue';
import { useContextReport } from '@/composables/useContextReport';
import type { ContextPressureMessage } from '@/types/messages';
import type { AgentContextRow, ConversationContextReport } from '@/types/context';
import composableSource from '@/composables/useContextReport.ts?raw';

const mocks = vi.hoisted(() => ({
  getConversationContext: vi.fn(),
}));

vi.mock('@/api/contextApi', () => ({
  getConversationContext: mocks.getConversationContext,
}));

function agent(overrides: Partial<AgentContextRow> = {}): AgentContextRow {
  return {
    agentId: 'root',
    threadId: 't1',
    parentAgentId: null,
    executionKind: 'Primary',
    observation: {
      thread_id: 't1',
      agent_id: 'root',
      run_id: 'run-1',
      generation_id: 'gen-2',
      generation_ordinal: 2,
      observed_at_utc: '2026-09-02T10:00:02Z',
      effective_model_id: 'model-x',
      estimated_input_tokens: 4_000,
      measured_input_tokens: 5_000,
      provenance: 'Measured',
      window_tokens: 200_000,
      reserve_tokens: 8_000,
      prompt_caching_enabled: true,
      rows_in_view: 10,
    },
    freshness: 'Stale',
    cacheTemperature: 'Hot',
    compaction: { state: 'None' },
    usage: {
      executionId: 't1',
      inputTokens: 100,
      outputTokens: 40,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0,
      totalTokens: 140,
      estimatedPublicCostMicros: 700,
      providerReportedCostMicros: null,
      preferredCostMicros: 700,
      costProvenance: 'PublicEstimate',
      estimatedCostCompleteness: 'Complete',
      attemptCount: 1,
    },
    ...overrides,
  };
}

function report(agents: AgentContextRow[] = [agent()]): ConversationContextReport {
  return {
    rootThreadId: 't1',
    schemaVersion: 1,
    generatedAtUtc: '2026-09-02T10:00:05Z',
    agents,
    total: {
      inputTokens: 100,
      outputTokens: 40,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0,
      totalTokens: 140,
      preferredCostMicros: 700,
      costProvenance: 'PublicEstimate',
      costCompleteness: 'Complete',
      usageCompleteness: 'Complete',
    },
  };
}

function frame(overrides: Partial<ContextPressureMessage> = {}): ContextPressureMessage {
  return {
    $type: 'context_pressure',
    role: 'assistant',
    threadId: 't1',
    agentId: 'root',
    generationOrdinal: 3,
    observedAtUtc: '2026-09-02T10:00:03Z',
    effectiveModelId: 'model-x',
    estimatedInputTokens: 6_000,
    measuredInputTokens: null,
    provenance: 'Estimated',
    windowTokens: 200_000,
    reserveTokens: 8_000,
    utilization: 6_000 / 192_000,
    rowsInView: 12,
    ...overrides,
  } as ContextPressureMessage;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const flush = () => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
  vi.clearAllMocks();
  mocks.getConversationContext.mockResolvedValue(null);
});

describe('useContextReport — architecture', () => {
  it('never imports useChat: the panel must be testable without the chat machinery', () => {
    expect(composableSource).not.toContain("from './useChat'");
    expect(composableSource).not.toContain("from '@/composables/useChat'");
  });
});

describe('useContextReport — hydrate (the authoritative endpoint)', () => {
  it('loads rows and the total, and reports ready', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const store = useContextReport(
      () => 't1',
      () => null,
      () => 0
    );

    await store.hydrate();

    expect(mocks.getConversationContext).toHaveBeenCalledWith('t1');
    expect(store.status.value).toBe('ready');
    expect(store.rows.value.map((r) => r.agentId)).toEqual(['root']);
    expect(store.rows.value[0].capacity).toMatchObject({ kind: 'known', used: 5_000 });
    expect(store.total.value?.cost).toEqual({
      kind: 'value',
      micros: 700,
      provenance: 'PublicEstimate',
      completeness: 'Complete',
    });
    expect(store.generatedAtUtc.value).toBe('2026-09-02T10:00:05Z');
    expect(store.hasReport.value).toBe(true);
  });

  it('is UNAVAILABLE — not empty, not zero — when the endpoint answers 404 or 403 (api maps both to null)', async () => {
    mocks.getConversationContext.mockResolvedValue(null);
    const store = useContextReport(
      () => 't1',
      () => null,
      () => 0
    );

    await store.hydrate();

    expect(store.status.value).toBe('unavailable');
    expect(store.rows.value).toEqual([]);
    expect(store.total.value).toBeNull();
    expect(store.hasReport.value).toBe(false);
  });

  it('degrades a thrown fetch to the same unavailable state rather than an error banner', async () => {
    mocks.getConversationContext.mockRejectedValue(new Error('Failed to fetch context: 500'));
    const store = useContextReport(
      () => 't1',
      () => null,
      () => 0
    );

    await expect(store.hydrate()).resolves.toBeUndefined();
    expect(store.status.value).toBe('unavailable');
    expect(store.isLoading.value).toBe(false);
  });

  it('does not call the endpoint with no thread id, and stays idle', async () => {
    const store = useContextReport(
      () => null,
      () => null,
      () => 0
    );
    await store.hydrate();
    expect(mocks.getConversationContext).not.toHaveBeenCalled();
    expect(store.status.value).toBe('idle');
  });

  it('holds the loading flag only while in flight, and keeps the previous rows visible meanwhile', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const store = useContextReport(
      () => 't1',
      () => null,
      () => 0
    );
    await store.hydrate();

    const gate = deferred<ConversationContextReport | null>();
    mocks.getConversationContext.mockReturnValue(gate.promise);
    const inFlight = store.hydrate();
    expect(store.isLoading.value).toBe(true);
    expect(store.rows.value).toHaveLength(1); // no flicker to empty on a refresh

    gate.resolve(report());
    await inFlight;
    expect(store.isLoading.value).toBe(false);
  });

  it('lets only the newest hydrate write when two overlap', async () => {
    const first = deferred<ConversationContextReport | null>();
    const second = deferred<ConversationContextReport | null>();
    mocks.getConversationContext.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const store = useContextReport(
      () => 't1',
      () => null,
      () => 0
    );

    const h1 = store.hydrate();
    const h2 = store.hydrate();
    second.resolve(report([agent({ agentId: 'root' }), agent({ agentId: 'a1', threadId: 'sub-1' })]));
    await h2;
    first.resolve(report());
    await h1;

    expect(store.rows.value.map((r) => r.agentId)).toEqual(['root', 'a1']);
    expect(store.isLoading.value).toBe(false);
  });
});

describe('useContextReport — live context_pressure frames (transient enrichment)', () => {
  it('applies a newer frame for the open conversation on top of the endpoint row, marking it fresh', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const latest = ref<ContextPressureMessage | null>(null);
    const store = useContextReport(
      () => 't1',
      () => latest.value,
      () => 0
    );
    await store.hydrate();
    expect(store.rows.value[0].freshness).toBe('Stale');

    latest.value = frame();
    await nextTick();

    expect(store.rows.value[0].capacity).toMatchObject({
      kind: 'known',
      used: 6_000,
      provenance: 'Estimated',
      utilization: 6_000 / 192_000,
    });
    expect(store.rows.value[0].freshness).toBe('Fresh');
    expect(store.rows.value[0].generationOrdinal).toBe(3);
    // The total is the endpoint's; a pressure frame carries no usage and must not fabricate one.
    expect(store.total.value?.tokens).toMatchObject({ kind: 'value', total: 140 });
  });

  it('paints a provisional row from a frame that lands before the endpoint has answered', async () => {
    const latest = ref<ContextPressureMessage | null>(null);
    const store = useContextReport(
      () => 't1',
      () => latest.value,
      () => 0
    );

    latest.value = frame();
    await nextTick();

    expect(store.rows.value).toHaveLength(1);
    expect(store.rows.value[0].provisional).toBe(true);
    expect(store.rows.value[0].cost).toEqual({ kind: 'none' });
    expect(store.hasReport.value).toBe(true);
  });

  it('drops a frame that names another conversation, or arrives with no open conversation', async () => {
    const threadId = ref<string | null>('t1');
    const latest = ref<ContextPressureMessage | null>(null);
    const store = useContextReport(
      () => threadId.value,
      () => latest.value,
      () => 0
    );

    latest.value = frame({ threadId: 'other' });
    await nextTick();
    expect(store.rows.value).toEqual([]);

    latest.value = frame({ threadId: null });
    await nextTick();
    expect(store.rows.value).toEqual([]);

    threadId.value = null;
    await nextTick();
    latest.value = frame();
    await nextTick();
    expect(store.rows.value).toEqual([]);
  });

  it('converges: a hydrate that lands AFTER a newer frame keeps the frame on top (higher ordinal wins)', async () => {
    const gate = deferred<ConversationContextReport | null>();
    mocks.getConversationContext.mockReturnValue(gate.promise);
    const latest = ref<ContextPressureMessage | null>(null);
    const store = useContextReport(
      () => 't1',
      () => latest.value,
      () => 0
    );

    const inFlight = store.hydrate();
    latest.value = frame({ generationOrdinal: 3 });
    await nextTick();

    gate.resolve(report()); // the report's observation is ordinal 2
    await inFlight;

    expect(store.rows.value[0].generationOrdinal).toBe(3);
    expect(store.rows.value[0].capacity).toMatchObject({ used: 6_000 });
    expect(store.rows.value[0].provisional).toBe(false); // the endpoint's usage/total now back it
    expect(store.rows.value[0].cost).toMatchObject({ kind: 'value', micros: 700 });
  });

  it('converges: a reload whose report is newer than the last frame shows the endpoint values', async () => {
    const latest = ref<ContextPressureMessage | null>(frame({ generationOrdinal: 3 }));
    const store = useContextReport(
      () => 't1',
      () => latest.value,
      () => 0
    );
    await nextTick();
    const newer = agent();
    newer.observation = { ...newer.observation!, generation_ordinal: 4, measured_input_tokens: 9_000 };
    mocks.getConversationContext.mockResolvedValue(report([newer]));

    await store.hydrate();

    expect(store.rows.value[0].generationOrdinal).toBe(4);
    expect(store.rows.value[0].capacity).toMatchObject({ used: 9_000, provenance: 'Measured' });
    expect(store.rows.value[0].freshness).toBe('Stale'); // the endpoint's word, not the frame's
  });

  it('ignores an older frame (lower ordinal) so a late socket delivery cannot roll the view back', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const latest = ref<ContextPressureMessage | null>(null);
    const store = useContextReport(
      () => 't1',
      () => latest.value,
      () => 0
    );
    await store.hydrate();

    latest.value = frame({ generationOrdinal: 1, estimatedInputTokens: 1 });
    await nextTick();

    expect(store.rows.value[0].capacity).toMatchObject({ used: 5_000 });
    expect(store.rows.value[0].generationOrdinal).toBe(2);
  });
});

describe('useContextReport — when to re-read the endpoint', () => {
  it('resets and re-hydrates when the conversation changes', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const threadId = ref<string | null>('t1');
    const store = useContextReport(
      () => threadId.value,
      () => null,
      () => 0
    );
    await store.hydrate();
    expect(store.rows.value).toHaveLength(1);

    mocks.getConversationContext.mockResolvedValue(null);
    threadId.value = 't2';
    await nextTick();
    await flush();

    expect(mocks.getConversationContext).toHaveBeenLastCalledWith('t2');
    expect(store.status.value).toBe('unavailable');
    expect(store.rows.value).toEqual([]);
  });

  it('re-reads (without dropping rows) when the refresh key changes — run idle, roster change', async () => {
    mocks.getConversationContext.mockResolvedValue(report());
    const key = ref(0);
    const store = useContextReport(
      () => 't1',
      () => null,
      () => key.value
    );
    await store.hydrate();
    expect(mocks.getConversationContext).toHaveBeenCalledTimes(1);

    mocks.getConversationContext.mockResolvedValue(
      report([agent(), agent({ agentId: 'a1', threadId: 'sub-1', parentAgentId: 'root', executionKind: 'SubAgent' })])
    );
    key.value = 1;
    await nextTick();
    await flush();

    expect(mocks.getConversationContext).toHaveBeenCalledTimes(2);
    expect(store.rows.value.map((r) => r.agentId)).toEqual(['root', 'a1']);
  });

  it('does not re-read on a key change with no open conversation', async () => {
    const key = ref(0);
    useContextReport(
      () => null,
      () => null,
      () => key.value
    );
    key.value = 1;
    await nextTick();
    await flush();
    expect(mocks.getConversationContext).not.toHaveBeenCalled();
  });
});
