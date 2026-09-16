import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { effectScope, nextTick, ref } from 'vue';
import { useManualCompaction, compactionRefusalMessage, describeError } from '@/composables/useManualCompaction';
import { MessageType, type CompactionStatusMessage } from '@/types/messages';
import { logger } from '@/utils';

const mocks = vi.hoisted(() => ({
  requestCompaction: vi.fn(),
  supportsManualCompaction: vi.fn(),
  getConversationContext: vi.fn(),
}));

vi.mock('@/api/contextApi', () => ({
  requestCompaction: mocks.requestCompaction,
  supportsManualCompaction: mocks.supportsManualCompaction,
  getConversationContext: mocks.getConversationContext,
}));

/**
 * A context report whose root row carries this compaction state. `pending` omitted leaves the field
 * out, as a host that predates it sends.
 */
function report(state: string, pending?: { requestId: string; requestedAtUtc: string } | null) {
  const compaction = pending === undefined ? { state } : { state, pendingManualCompaction: pending };
  return { agents: [{ agentId: 'root', threadId: 't1', compaction }] };
}

function frame(overrides: Partial<CompactionStatusMessage> = {}): CompactionStatusMessage {
  return {
    $type: MessageType.CompactionStatus,
    threadId: 't1',
    agentId: 'root',
    requestId: 'req-1',
    trigger: 'manual',
    phase: 'running',
    ...overrides,
  } as CompactionStatusMessage;
}

/** Resolves once every queued microtask (the mocked fetch promises) has run. */
async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) await Promise.resolve();
  await nextTick();
}

function setup(opts: { threadId?: string | null; supported?: boolean } = {}) {
  // A test that scripted the capability probe itself keeps its script.
  if (opts.supported !== undefined || !mocks.supportsManualCompaction.getMockImplementation()) {
    mocks.supportsManualCompaction.mockResolvedValue(opts.supported === false ? 'unsupported' : 'supported');
  }
  const threadId = ref<string | null>(opts.threadId === undefined ? 't1' : opts.threadId);
  const latest = ref<CompactionStatusMessage | null>(null);
  const epoch = ref(0);
  const onApplied = vi.fn();
  const scope = effectScope();
  const store = scope.run(() =>
    useManualCompaction(
      () => threadId.value,
      () => latest.value,
      onApplied,
      { successMs: 1000, watchdogMs: 30000, getConnectionEpoch: () => epoch.value }
    )
  )!;
  return { store, threadId, latest, epoch, onApplied, scope };
}

/** Puts a manual request in `queued` via a 202 for `req-1`. */
async function queued(store: ReturnType<typeof setup>['store']): Promise<void> {
  mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
  await store.request();
  expect(store.phase.value).toBe('queued');
}

describe('useManualCompaction', () => {
  beforeEach(() => {
    mocks.requestCompaction.mockReset();
    mocks.supportsManualCompaction.mockReset();
    mocks.getConversationContext.mockReset();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  describe('capability', () => {
    it('is supported only when the host advertises manualCompaction', async () => {
      const on = setup({ supported: true });
      await flush();
      expect(on.store.supported.value).toBe(true);

      const off = setup({ supported: false });
      await flush();
      expect(off.store.supported.value).toBe(false);
    });

    // #774 F-008: a failed or malformed probe is transient; the control must come back without a reload.
    it('retries an unavailable probe and shows the control once the host answers', async () => {
      vi.useFakeTimers();
      mocks.supportsManualCompaction.mockResolvedValueOnce('unavailable').mockResolvedValue('supported');
      const { store } = setup();
      await flush();
      expect(store.supported.value).toBe(false);
      expect(store.view.value.supported).toBe(false);

      vi.advanceTimersByTime(2000);
      await flush();

      expect(mocks.supportsManualCompaction).toHaveBeenCalledTimes(2);
      expect(store.view.value.supported).toBe(true);
    });

    it('stops retrying after the bounded attempts, and a reconnect probes again', async () => {
      vi.useFakeTimers();
      mocks.supportsManualCompaction.mockResolvedValue('unavailable');
      const { store, epoch } = setup();
      await flush();
      for (const delay of [2000, 10000, 30000, 60000]) {
        vi.advanceTimersByTime(delay);
        await flush();
      }
      expect(mocks.supportsManualCompaction).toHaveBeenCalledTimes(4); // the first read and three retries
      expect(store.supported.value).toBe(false);

      mocks.supportsManualCompaction.mockResolvedValue('supported');
      epoch.value++;
      await flush();

      expect(mocks.supportsManualCompaction).toHaveBeenCalledTimes(5);
      expect(store.view.value.supported).toBe(true);
    });

    it('does not retry an unsupported answer, and does not probe again once supported', async () => {
      vi.useFakeTimers();
      const off = setup({ supported: false });
      await flush();
      vi.advanceTimersByTime(60000);
      await flush();
      expect(mocks.supportsManualCompaction).toHaveBeenCalledTimes(1);
      off.scope.stop();

      mocks.supportsManualCompaction.mockClear();
      const on = setup({ supported: true });
      await flush();
      on.epoch.value++;
      await flush();
      expect(mocks.supportsManualCompaction).toHaveBeenCalledTimes(1);
      expect(on.store.supported.value).toBe(true);
    });

    it('does not request when unsupported', async () => {
      const { store } = setup({ supported: false });
      await flush();

      await store.request('focus');

      expect(mocks.requestCompaction).not.toHaveBeenCalled();
      expect(store.phase.value).toBe('idle');
    });
  });

  describe('diagnostics', () => {
    // #774 F-007: the logger serializes with JSON.stringify, which turns a native Error into `{}`.
    it('logs a failed request as a bounded name and message, not a bare Error', async () => {
      const logSpy = vi.spyOn(
        logger as unknown as { _logWithComponent: (...a: unknown[]) => void },
        '_logWithComponent'
      );
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockRejectedValue(new TypeError(`Failed to fetch ${'x'.repeat(500)}`));

      await store.request();

      const entry = logSpy.mock.calls.find((c) => c[1] === 'Manual compaction request failed');
      expect(entry).toBeTruthy();
      const error = (JSON.parse(JSON.stringify(entry![2])) as { error: { name: string; message: string } }).error;
      expect(error.name).toBe('TypeError');
      expect(error.message.startsWith('Failed to fetch')).toBe(true);
      expect(error.message.length).toBe(200);
      logSpy.mockRestore();
    });

    it('describes a non-Error throw by its type', () => {
      expect(describeError('nope')).toEqual({ name: 'string', message: 'nope' });
    });
  });

  describe('request mapping', () => {
    it('goes idle → requesting → queued on a 202 and passes the focus through', async () => {
      const { store } = setup();
      await flush();
      let resolve!: (v: unknown) => void;
      mocks.requestCompaction.mockReturnValue(new Promise((r) => (resolve = r)));

      const pending = store.request('keep decisions');
      expect(store.phase.value).toBe('requesting');
      expect(store.isBusy.value).toBe(true);
      expect(mocks.requestCompaction).toHaveBeenCalledWith('t1', 'keep decisions');

      resolve({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
      await pending;

      expect(store.phase.value).toBe('queued');
      expect(store.isBusy.value).toBe(true);
      expect(store.statusText.value).toBe('Compacting…');
    });

    it('maps a running 202 to running', async () => {
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'running' });

      await store.request();

      expect(store.phase.value).toBe('running');
    });

    it.each([
      'compaction_off',
      'provider_owned_session',
      'already_pending',
      'in_progress',
      'nothing_to_compact',
      'no_safe_boundary',
    ])('maps 409 %s to refused with a human message', async (reason) => {
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'refused', reason });

      await store.request();

      expect(store.phase.value).toBe('refused');
      expect(store.statusText.value).toBe(compactionRefusalMessage(reason));
      expect(store.statusText.value).not.toContain('_');
      expect(store.isBusy.value).toBe(false);
    });

    it('tells the user a no_safe_boundary refusal is temporary, unlike nothing_to_compact', async () => {
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'refused', reason: 'no_safe_boundary' });

      await store.request();

      expect(store.statusText.value).toBe("Can't compact yet: work in progress. Try again when the run pauses.");
      expect(store.statusText.value).not.toBe(compactionRefusalMessage('nothing_to_compact'));
    });

    it('maps 403 and 404 to refused with their own messages', async () => {
      const { store } = setup();
      await flush();

      mocks.requestCompaction.mockResolvedValue({ kind: 'forbidden' });
      await store.request();
      expect(store.phase.value).toBe('refused');
      expect(store.statusText.value).toMatch(/permission/i);

      mocks.requestCompaction.mockResolvedValue({ kind: 'not-found' });
      await store.request();
      expect(store.phase.value).toBe('refused');
      expect(store.statusText.value).toMatch(/not found/i);
    });

    it('maps a thrown request to failed and allows a retry', async () => {
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockRejectedValue(new Error('boom'));

      await store.request();

      expect(store.phase.value).toBe('failed');
      expect(store.statusText.value).toMatch(/failed/i);
      expect(store.isBusy.value).toBe(false);
    });

    it('ignores a second request while one is in progress', async () => {
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'queued' });

      await store.request();
      await store.request();

      expect(mocks.requestCompaction).toHaveBeenCalledTimes(1);
    });
  });

  describe('frames', () => {
    it('drives each manual phase and refreshes the report on applied, then settles to idle', async () => {
      vi.useFakeTimers();
      const { store, latest, onApplied } = setup();
      await flush();

      latest.value = frame({ phase: 'requested' });
      await nextTick();
      expect(store.phase.value).toBe('queued');

      latest.value = frame({ phase: 'running' });
      await nextTick();
      expect(store.phase.value).toBe('running');
      expect(store.statusText.value).toBe('Compacting…');

      latest.value = frame({ phase: 'applied', checkpointId: 'cp-1' });
      await nextTick();
      expect(store.phase.value).toBe('applied');
      expect(store.isBusy.value).toBe(false);
      expect(onApplied).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(1000);
      expect(store.phase.value).toBe('idle');
    });

    it('maps a manual refused frame to its reason message', async () => {
      const { store, latest } = setup();
      await flush();

      latest.value = frame({ phase: 'refused', reason: 'nothing_to_compact' });
      await nextTick();

      expect(store.phase.value).toBe('refused');
      expect(store.statusText.value).toBe(compactionRefusalMessage('nothing_to_compact'));
    });

    it('maps a manual failed frame to failed, re-enabling the control', async () => {
      const { store, latest } = setup();
      await flush();

      latest.value = frame({ phase: 'failed', reason: 'summarizer_error' });
      await nextTick();

      expect(store.phase.value).toBe('failed');
      expect(store.isBusy.value).toBe(false);
      expect(store.statusText.value).toMatch(/failed/i);
    });

    it('shows Compacting… for an automatic compaction and clears silently when it does not apply', async () => {
      const { store, latest, onApplied } = setup({ supported: false });
      await flush();

      latest.value = frame({ trigger: 'preemptive', requestId: null, phase: 'running' });
      await nextTick();
      expect(store.phase.value).toBe('running');
      expect(store.trigger.value).toBe('preemptive');
      expect(store.statusText.value).toBe('Compacting…');

      latest.value = frame({ trigger: 'preemptive', requestId: null, phase: 'refused', reason: 'cooldown' });
      await nextTick();
      expect(store.phase.value).toBe('idle');
      expect(onApplied).not.toHaveBeenCalled();
    });

    it('refreshes the report when an automatic compaction applies', async () => {
      const { store, latest, onApplied } = setup();
      await flush();

      latest.value = frame({ trigger: 'reactive', requestId: null, phase: 'applied' });
      await nextTick();

      expect(onApplied).toHaveBeenCalledTimes(1);
      expect(store.phase.value).toBe('applied');
    });

    it('does not let an automatic terminal frame clobber a queued manual request', async () => {
      const { store, latest } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
      await store.request();

      latest.value = frame({ trigger: 'preemptive', requestId: null, phase: 'refused', reason: 'cooldown' });
      await nextTick();

      expect(store.phase.value).toBe('queued');
      expect(store.trigger.value).toBe('manual');
    });

    it('does not regress a frame-advanced phase when the 202 arrives late', async () => {
      const { store, latest } = setup();
      await flush();
      let resolve!: (v: unknown) => void;
      mocks.requestCompaction.mockReturnValue(new Promise((r) => (resolve = r)));

      const pending = store.request();
      latest.value = frame({ phase: 'running' });
      await nextTick();
      resolve({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
      await pending;

      expect(store.phase.value).toBe('running');
    });

    it('drops frames for another thread, without a thread id, or from a sub-agent', async () => {
      const { store, latest } = setup();
      await flush();

      latest.value = frame({ threadId: 'other' });
      await nextTick();
      latest.value = frame({ threadId: null });
      await nextTick();
      latest.value = frame({ agentId: 'child-1' });
      await nextTick();

      expect(store.phase.value).toBe('idle');
    });
  });

  describe('phases only move forward for one request', () => {
    it('takes requested after a running frame as a requeue, and follows the retry to applied', async () => {
      const { store, latest, onApplied } = setup();
      await flush();
      const step = async (phase: CompactionStatusMessage['phase']) => {
        latest.value = frame({ phase, ...(phase === 'applied' ? { checkpointId: 'cp-1' } : {}) });
        await nextTick();
        return { phase: store.phase.value, busy: store.isBusy.value, text: store.statusText.value, trigger: store.trigger.value };
      };

      expect(await step('requested')).toEqual({ phase: 'queued', busy: true, text: 'Compacting…', trigger: 'manual' });
      expect(await step('running')).toEqual({ phase: 'running', busy: true, text: 'Compacting…', trigger: 'manual' });
      // The loop was disposed mid-run and the host requeued the same request.
      expect(await step('requested')).toEqual({ phase: 'queued', busy: true, text: 'Compacting…', trigger: 'manual' });
      expect(await step('running')).toEqual({ phase: 'running', busy: true, text: 'Compacting…', trigger: 'manual' });
      expect(await step('applied')).toEqual({ phase: 'applied', busy: false, text: 'Conversation compacted.', trigger: 'manual' });
      expect(onApplied).toHaveBeenCalledTimes(1);
    });

    it('takes a requeue after a running 202 once the running frame itself arrived', async () => {
      const { store, latest } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'running' });
      await store.request();

      latest.value = frame({ phase: 'running' });
      await nextTick();
      latest.value = frame({ phase: 'requested' });
      await nextTick();

      expect(store.phase.value).toBe('queued');
      expect(store.isBusy.value).toBe(true);
    });

    it.each(['applied', 'failed', 'refused'] as const)('keeps a request that ended %s when a stale requested frame follows', async (end) => {
      const { store, latest } = setup();
      await flush();

      latest.value = frame({ phase: 'running' });
      await nextTick();
      latest.value = frame({ phase: end, reason: end === 'applied' ? null : 'cancelled' });
      await nextTick();
      latest.value = frame({ phase: 'requested' });
      await nextTick();

      expect(store.phase.value).toBe(end);
      expect(store.isBusy.value).toBe(false);
    });

    // The 202 and the frames travel on different channels, so a requested frame sent before the run
    // started can land after a 202 that already said running. Only a running frame proves the order.
    it('ignores a late requested frame after a running 202 for the same request', async () => {
      const { store, latest } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'running' });
      await store.request();

      latest.value = frame({ phase: 'requested' });
      await nextTick();

      expect(store.phase.value).toBe('running');
    });

    it('ignores progress frames after the request ended, and a repeated terminal frame', async () => {
      const { store, latest, onApplied } = setup();
      await flush();

      latest.value = frame({ phase: 'applied', checkpointId: 'cp-1' });
      await nextTick();
      latest.value = frame({ phase: 'running' });
      await nextTick();
      expect(store.phase.value).toBe('applied');
      expect(store.isBusy.value).toBe(false);

      latest.value = frame({ phase: 'applied', checkpointId: 'cp-1' });
      await nextTick();
      expect(onApplied).toHaveBeenCalledTimes(1);
    });

    it('starts fresh for a different request id', async () => {
      const { store, latest } = setup();
      await flush();

      latest.value = frame({ phase: 'failed', reason: 'summary_call_failed' });
      await nextTick();
      latest.value = frame({ requestId: 'req-2', phase: 'requested' });
      await nextTick();

      expect(store.phase.value).toBe('queued');
    });

    it('forgets the request on a conversation switch', async () => {
      const { store, latest, threadId } = setup();
      await flush();
      latest.value = frame({ phase: 'applied' });
      await nextTick();

      threadId.value = 't2';
      await nextTick();
      threadId.value = 't1';
      await nextTick();
      latest.value = frame({ phase: 'running' });
      await nextTick();

      expect(store.phase.value).toBe('running');
    });
  });

  describe('lost terminal frame', () => {
    it('re-reads the report after the watchdog and goes idle when nothing is in flight', async () => {
      vi.useFakeTimers();
      const { store, onApplied } = setup();
      await flush();
      await queued(store);
      const active = report('Active');
      mocks.getConversationContext.mockResolvedValue(active);

      vi.advanceTimersByTime(29999);
      expect(mocks.getConversationContext).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      await flush();

      expect(mocks.getConversationContext).toHaveBeenCalledTimes(1);
      expect(mocks.getConversationContext).toHaveBeenCalledWith('t1');
      expect(store.phase.value).toBe('idle');
      expect(store.isBusy.value).toBe(false);
      // #774 F-014: the report it just read is handed over, so the panel does not fetch it a second time.
      expect(onApplied).toHaveBeenCalledTimes(1);
      expect(onApplied).toHaveBeenCalledWith(active);
    });

    it('stays busy and checks again while the report says InFlight', async () => {
      vi.useFakeTimers();
      const { store } = setup();
      await flush();
      await queued(store);
      mocks.getConversationContext.mockResolvedValueOnce(report('InFlight')).mockResolvedValueOnce(report('None'));

      vi.advanceTimersByTime(30000);
      await flush();
      expect(store.phase.value).toBe('queued');

      vi.advanceTimersByTime(30000);
      await flush();
      expect(mocks.getConversationContext).toHaveBeenCalledTimes(2);
      expect(store.phase.value).toBe('idle');
    });

    it('stays busy while the report names a pending request that has not started', async () => {
      vi.useFakeTimers();
      const { store } = setup();
      await flush();
      await queued(store);
      mocks.getConversationContext
        .mockResolvedValueOnce(
          report('Active', { requestId: 'req-1', requestedAtUtc: '2026-09-15T04:00:00Z' })
        )
        .mockResolvedValueOnce(report('Active', null));

      vi.advanceTimersByTime(30000);
      await flush();
      expect(store.phase.value).toBe('queued');

      vi.advanceTimersByTime(30000);
      await flush();
      expect(store.phase.value).toBe('idle');
    });

    it('restarts the watchdog on every frame', async () => {
      vi.useFakeTimers();
      const { store, latest } = setup();
      await flush();
      await queued(store);
      mocks.getConversationContext.mockResolvedValue(report('None'));

      vi.advanceTimersByTime(20000);
      latest.value = frame({ phase: 'running' });
      await nextTick();
      vi.advanceTimersByTime(20000);
      await flush();
      expect(mocks.getConversationContext).not.toHaveBeenCalled();

      vi.advanceTimersByTime(10000);
      await flush();
      expect(mocks.getConversationContext).toHaveBeenCalledTimes(1);
    });

    it('keeps waiting while the POST itself has not answered', async () => {
      vi.useFakeTimers();
      const { store } = setup();
      await flush();
      mocks.requestCompaction.mockReturnValue(new Promise(() => {}));
      void store.request();

      vi.advanceTimersByTime(30000);
      await flush();

      expect(mocks.getConversationContext).not.toHaveBeenCalled();
      expect(store.phase.value).toBe('requesting');
    });

    it('keeps waiting when the report cannot be read', async () => {
      vi.useFakeTimers();
      const { store } = setup();
      await flush();
      await queued(store);
      mocks.getConversationContext.mockRejectedValueOnce(new Error('boom')).mockResolvedValueOnce(report('None'));

      vi.advanceTimersByTime(30000);
      await flush();
      expect(store.phase.value).toBe('queued');

      vi.advanceTimersByTime(30000);
      await flush();
      expect(store.phase.value).toBe('idle');
    });

    it('re-reads the report on a reconnect while busy', async () => {
      const { store, epoch } = setup();
      await flush();
      await queued(store);
      mocks.getConversationContext.mockResolvedValue(report('None'));

      epoch.value++;
      await flush();

      expect(mocks.getConversationContext).toHaveBeenCalledWith('t1');
      expect(store.phase.value).toBe('idle');
    });

    it('does not read the report on a reconnect while idle', async () => {
      const { epoch } = setup();
      await flush();

      epoch.value++;
      await flush();

      expect(mocks.getConversationContext).not.toHaveBeenCalled();
    });

    it('lets a frame that lands during the read win', async () => {
      const { store, epoch, latest } = setup();
      await flush();
      await queued(store);
      let resolve!: (v: unknown) => void;
      mocks.getConversationContext.mockReturnValue(new Promise((r) => (resolve = r)));

      epoch.value++;
      await nextTick();
      latest.value = frame({ phase: 'running' });
      await nextTick();
      resolve(report('None'));
      await flush();

      expect(store.phase.value).toBe('running');
    });

    it('drops a read that lands after a conversation switch', async () => {
      const { store, epoch, threadId, onApplied } = setup();
      await flush();
      await queued(store);
      let resolve!: (v: unknown) => void;
      mocks.getConversationContext.mockReturnValue(new Promise((r) => (resolve = r)));

      epoch.value++;
      await nextTick();
      threadId.value = 't2';
      await nextTick();
      resolve(report('None'));
      await flush();

      expect(store.phase.value).toBe('idle');
      expect(onApplied).not.toHaveBeenCalled();
    });

    it('stops the watchdog when the scope is disposed', async () => {
      vi.useFakeTimers();
      const { store, scope } = setup();
      await flush();
      await queued(store);

      scope.stop();
      vi.advanceTimersByTime(30000);
      await flush();

      expect(mocks.getConversationContext).not.toHaveBeenCalled();
    });
  });

  describe('cancellation', () => {
    it('maps a manual failed frame with reason cancelled to "Compaction cancelled."', async () => {
      const { store, latest } = setup();
      await flush();
      await queued(store);

      latest.value = frame({ phase: 'failed', reason: 'cancelled' });
      await nextTick();

      expect(store.phase.value).toBe('failed');
      expect(store.isBusy.value).toBe(false);
      expect(store.statusText.value).toBe('Compaction cancelled.');
    });
  });

  describe('conversation switch', () => {
    it('resets state when the thread changes', async () => {
      const { store, threadId } = setup();
      await flush();
      mocks.requestCompaction.mockResolvedValue({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
      await store.request();
      expect(store.phase.value).toBe('queued');

      threadId.value = 't2';
      await nextTick();

      expect(store.phase.value).toBe('idle');
      expect(store.statusText.value).toBe('');
    });

    it('drops a response that lands after the user switched away', async () => {
      const { store, threadId } = setup();
      await flush();
      let resolve!: (v: unknown) => void;
      mocks.requestCompaction.mockReturnValue(new Promise((r) => (resolve = r)));

      const pending = store.request();
      threadId.value = 't2';
      await nextTick();
      resolve({ kind: 'refused', reason: 'nothing_to_compact' });
      await pending;

      expect(store.phase.value).toBe('idle');
    });

    it('does not request without an open conversation', async () => {
      const { store } = setup({ threadId: null });
      await flush();

      await store.request();

      expect(mocks.requestCompaction).not.toHaveBeenCalled();
    });
  });
});
