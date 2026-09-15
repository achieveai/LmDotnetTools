import { computed, onScopeDispose, ref, watch } from 'vue';
import { getConversationContext, requestCompaction, supportsManualCompaction } from '@/api/contextApi';
import type { ConversationContextReport } from '@/types/context';
import type { CompactionStatusMessage, CompactionStatusPhase, CompactionStatusTrigger } from '@/types/messages';
import { logger } from '@/utils';

const log = logger.forComponent('useManualCompaction');

/**
 * Where the open conversation's compaction is, as the Compact now control shows it:
 *  - `idle` — nothing running; the control is available.
 *  - `requesting` — the POST is in flight.
 *  - `queued` / `running` — the host accepted it (or a frame says a compaction started).
 *  - `applied` — a checkpoint committed; shown briefly, then back to `idle`.
 *  - `refused` — the host declined (409/403/404 or a refused frame); the message says why.
 *  - `failed` — the request or the compaction failed; the control is available again.
 */
export type ManualCompactionPhase = 'idle' | 'requesting' | 'queued' | 'running' | 'applied' | 'refused' | 'failed';

/** What the context panel needs to render the Compact now control and the compaction status line. */
export interface CompactionControlView {
  /** The host accepts manual requests; false hides the button but not an automatic compaction's status. */
  supported: boolean;
  phase: ManualCompactionPhase;
  /** A request or compaction is under way; the control is disabled. */
  busy: boolean;
  /** Human status line; empty while idle. */
  statusText: string;
}

const REFUSAL_MESSAGES: Record<string, string> = {
  compaction_off: 'Compaction is turned off for this conversation.',
  provider_owned_session: 'This provider manages its own context, so it cannot be compacted here.',
  already_pending: 'A compaction is already queued for this conversation.',
  in_progress: 'A compaction is already running for this conversation.',
  nothing_to_compact: 'Nothing to compact yet.',
  no_safe_boundary: "Can't compact yet: work in progress. Try again when the run pauses.",
};

/** A human sentence for a refusal reason; an unknown reason is still shown, never swallowed. */
export function compactionRefusalMessage(reason: string | null | undefined): string {
  if (!reason || reason === 'unknown') return 'Compaction was refused.';
  return REFUSAL_MESSAGES[reason] ?? `Compaction was refused (${reason.replace(/_/g, ' ')}).`;
}

/** A human sentence for a failure reason. */
function compactionFailureMessage(reason: string | null | undefined): string {
  if (reason === 'cancelled') return 'Compaction cancelled.';
  return `Compaction failed${reason ? ` (${reason.replace(/_/g, ' ')})` : ''}.`;
}

/** The longest error message a log line keeps. */
const MAX_LOGGED_ERROR_CHARS = 200;

/**
 * A caught value as log fields. The logger serializes with JSON.stringify, which turns a native Error into `{}`,
 * so the name and a bounded message are copied out.
 */
export function describeError(e: unknown): { name: string; message: string } {
  if (e instanceof Error) return { name: e.name, message: e.message.slice(0, MAX_LOGGED_ERROR_CHARS) };
  return { name: typeof e, message: String(e).slice(0, MAX_LOGGED_ERROR_CHARS) };
}

const BUSY: ReadonlySet<ManualCompactionPhase> = new Set(['requesting', 'queued', 'running']);

/** How far one request has got. A frame or answer ranked at or below what was seen is stale. */
const RANK: Record<CompactionStatusPhase, number> = {
  requested: 1,
  running: 2,
  applied: 3,
  refused: 3,
  failed: 3,
};

/**
 * State for the context panel's Compact now control (manual compaction) and for the "Compacting…"
 * line automatic compactions show too.
 *
 * A plain factory like `useContextReport`, fed by getters so the caller keeps the reactive sources:
 *  - `getThreadId` — the open conversation. A change resets everything: state is per thread, and a
 *    response or frame for the previous thread must not paint the new one (the switch bug family).
 *  - `getLatestStatus` — the newest `compaction_status` frame `useChat` saw.
 *  - `refreshReport` — called when a compaction commits, or when the control stops waiting, so the
 *    report re-reads the new state. When the control stops waiting it passes the report it just read, so
 *    the panel need not fetch it again.
 *  - `options.getConnectionEpoch` — changes whenever a WebSocket is (re)installed. A reconnect also
 *    re-reads the host capability while the control is hidden.
 *
 * Frames are authoritative for progress and only move forward per request id, with one exception: a
 * `requested` frame after that request's `running` frame is a requeue (the host's loop was disposed
 * mid-run and queued the same request again), so the control goes back to `queued`. Frames arrive in
 * order on one socket; a 202 does not, so `running` known only from the 202 never makes a later
 * `requested` a requeue. Nothing reopens a request that ended. The POST answer only moves
 * `requesting` forward, so a frame that beat the 202 is never regressed by it.
 *
 * Frames are transient, so a terminal one can be lost (a reconnect, a stopped run, a page with no
 * socket). While busy, a reconnect or `watchdogMs` without a frame re-reads the report. If the root
 * row is neither `InFlight` nor names a `pendingManualCompaction`, the control goes idle. A host without
 * that field cannot show a queued request that has not started, so a click then may meet 409
 * `already_pending`, which the refusal line explains.
 */
export function useManualCompaction(
  getThreadId: () => string | null,
  getLatestStatus: () => CompactionStatusMessage | null,
  refreshReport: (report?: ConversationContextReport) => void,
  options: {
    successMs?: number;
    watchdogMs?: number;
    probeRetryMs?: readonly number[];
    getConnectionEpoch?: () => unknown;
  } = {}
) {
  const successMs = options.successMs ?? 4000;
  const watchdogMs = options.watchdogMs ?? 30000;
  const probeRetryMs = options.probeRetryMs ?? [2000, 10000, 30000];

  const supported = ref(false);
  const phase = ref<ManualCompactionPhase>('idle');
  const trigger = ref<CompactionStatusTrigger | null>(null);
  const message = ref('');

  const isBusy = computed(() => BUSY.has(phase.value));
  const statusText = computed(() => {
    switch (phase.value) {
      case 'requesting':
        return 'Requesting compaction…';
      case 'queued':
      case 'running':
        return 'Compacting…';
      case 'applied':
        return 'Conversation compacted.';
      case 'refused':
      case 'failed':
        return message.value;
      default:
        return '';
    }
  });

  /** Bumped on every reset; an awaited response from an older generation is dropped. */
  let generation = 0;
  /** Bumped on every phase change; a report read that started before one is stale. */
  let phaseSeq = 0;
  /** The newest request id seen, how far it got, and whether a `running` frame (not just a 202) said so. */
  let seen: { requestId: string; rank: number; runningFrame?: boolean } | null = null;
  let settleTimer: ReturnType<typeof setTimeout> | null = null;
  let watchdogTimer: ReturnType<typeof setTimeout> | null = null;
  /** Bumped on every capability probe and on dispose; an older probe's answer is dropped. */
  let probeSeq = 0;
  let probeTimer: ReturnType<typeof setTimeout> | null = null;

  function clearProbeTimer(): void {
    if (probeTimer !== null) {
      clearTimeout(probeTimer);
      probeTimer = null;
    }
  }

  function clearSettleTimer(): void {
    if (settleTimer !== null) {
      clearTimeout(settleTimer);
      settleTimer = null;
    }
  }

  function clearWatchdog(): void {
    if (watchdogTimer !== null) {
      clearTimeout(watchdogTimer);
      watchdogTimer = null;
    }
  }

  function armWatchdog(): void {
    clearWatchdog();
    watchdogTimer = setTimeout(() => {
      watchdogTimer = null;
      void reconcile();
    }, watchdogMs);
  }

  function setPhase(next: ManualCompactionPhase, nextTrigger: CompactionStatusTrigger | null, text = ''): void {
    clearSettleTimer();
    phaseSeq++;
    phase.value = next;
    trigger.value = nextTrigger;
    message.value = text;
    if (BUSY.has(next)) armWatchdog();
    else clearWatchdog();
    if (next === 'applied') {
      settleTimer = setTimeout(() => {
        settleTimer = null;
        if (phase.value === 'applied') setPhase('idle', null);
      }, successMs);
    }
  }

  /**
   * Records that `requestId` reached `rank`. False when it had already got that far, so the caller
   * drops the stale frame or answer.
   */
  function advance(requestId: string | null | undefined, rank: number): boolean {
    if (!requestId) return true;
    if (seen?.requestId === requestId && rank <= seen.rank) return false;
    seen = { requestId, rank };
    return true;
  }

  function reset(): void {
    generation++;
    seen = null;
    setPhase('idle', null);
  }

  /**
   * Checks the report when a terminal frame may have been lost. Leaves busy only when the root row
   * is not `InFlight`, names no pending request, and nothing changed meanwhile. The POST still
   * awaiting its answer counts as a pending request too, so `requesting` keeps waiting.
   */
  async function reconcile(): Promise<void> {
    const threadId = getThreadId();
    if (!threadId || !isBusy.value) return;
    if (phase.value === 'requesting') {
      armWatchdog();
      return;
    }

    const gen = generation;
    const seq = phaseSeq;
    try {
      const report = await getConversationContext(threadId);
      if (gen !== generation || seq !== phaseSeq) return; // a switch or a frame got there first
      const root = report?.agents?.find((a) => a.agentId === 'root');
      if (!report || root?.compaction?.state === 'InFlight' || root?.compaction?.pendingManualCompaction?.requestId) {
        armWatchdog();
        return;
      }
      log.debug('No compaction in flight after a frame gap; releasing the Compact now control', { threadId });
      setPhase('idle', null);
      refreshReport(report);
    } catch (e) {
      if (gen !== generation || seq !== phaseSeq) return;
      log.debug('Could not re-read the context report after a frame gap; still waiting', {
        threadId,
        error: describeError(e),
      });
      armWatchdog();
    }
  }

  async function request(focus?: string | null): Promise<void> {
    const threadId = getThreadId();
    if (!supported.value || !threadId || isBusy.value) return;

    const gen = generation;
    setPhase('requesting', 'manual');
    try {
      const result = await requestCompaction(threadId, focus);
      if (gen !== generation) return; // the user switched conversations meanwhile
      switch (result.kind) {
        case 'accepted':
          // A frame may already have moved us past `requesting`; the 202 never moves state backwards.
          if (phase.value === 'requesting' && advance(result.requestId, result.status === 'queued' ? RANK.requested : RANK.running)) {
            setPhase(result.status, 'manual');
          }
          break;
        case 'refused':
          setPhase('refused', 'manual', compactionRefusalMessage(result.reason));
          break;
        case 'forbidden':
          setPhase('refused', 'manual', 'You do not have permission to compact this conversation.');
          break;
        case 'not-found':
          setPhase('refused', 'manual', 'This conversation was not found.');
          break;
      }
    } catch (e) {
      if (gen !== generation) return;
      log.debug('Manual compaction request failed', { threadId, error: describeError(e) });
      setPhase('failed', 'manual', 'The compaction request failed. Try again.');
    }
  }

  function applyFrame(frame: CompactionStatusMessage): void {
    const threadId = getThreadId();
    // Same fail-closed rule as the pressure frame: both ids present and equal. Only the root agent's
    // compaction drives this control; a sub-agent's compaction is not the conversation the user sees.
    if (!frame.threadId || !threadId || frame.threadId !== threadId || (frame.agentId && frame.agentId !== 'root')) {
      log.debug('Dropping a compaction_status frame that does not name the open conversation', {
        frameThreadId: frame.threadId ?? null,
        agentId: frame.agentId ?? null,
        threadId,
      });
      return;
    }

    const requeue =
      frame.phase === 'requested' &&
      !!frame.requestId &&
      seen?.requestId === frame.requestId &&
      seen.rank === RANK.running &&
      seen.runningFrame === true;
    const advanced = requeue || advance(frame.requestId, RANK[frame.phase]);
    if (requeue) {
      log.debug('A running compaction request was requeued', { requestId: frame.requestId });
      seen = { requestId: frame.requestId!, rank: RANK.requested };
    }
    // Noted even for a running frame that is not news (a 202 said running first), so a requeue after it is recognised.
    if (frame.phase === 'running' && frame.requestId && seen?.requestId === frame.requestId) seen.runningFrame = true;
    if (!advanced) {
      log.debug('Dropping a compaction_status frame older than what this request already reached', {
        requestId: frame.requestId,
        phase: frame.phase,
      });
      return;
    }

    const manual = frame.trigger === 'manual';
    // An automatic attempt ending must not clobber a manual request the user is waiting on.
    const trackingManual = trigger.value === 'manual' && isBusy.value;

    switch (frame.phase) {
      case 'requested':
        if (manual || !trackingManual) setPhase('queued', frame.trigger);
        break;
      case 'running':
        if (manual || !trackingManual) setPhase('running', frame.trigger);
        break;
      case 'applied':
        refreshReport();
        if (manual || !trackingManual) setPhase('applied', frame.trigger);
        break;
      case 'refused':
        if (manual) setPhase('refused', 'manual', compactionRefusalMessage(frame.reason));
        else if (!trackingManual) setPhase('idle', null);
        break;
      case 'failed':
        if (manual) {
          setPhase('failed', 'manual', compactionFailureMessage(frame.reason));
        } else if (!trackingManual) {
          setPhase('idle', null);
        }
        break;
    }
  }

  /**
   * Reads the host capability. An `unavailable` answer (network, 5xx, malformed body) is retried after each
   * of `probeRetryMs`, then again on the next reconnect, so a blip never hides the control for the session.
   */
  async function probe(attempt: number): Promise<void> {
    clearProbeTimer();
    const seq = ++probeSeq;
    const capability = await supportsManualCompaction();
    if (seq !== probeSeq) return; // disposed, or a newer probe started
    supported.value = capability === 'supported';
    if (capability === 'unavailable' && attempt < probeRetryMs.length) {
      probeTimer = setTimeout(() => {
        probeTimer = null;
        void probe(attempt + 1);
      }, probeRetryMs[attempt]);
    }
  }

  void probe(0);

  const stopThreadWatch = watch(() => getThreadId(), reset);
  const stopFrameWatch = watch(
    () => getLatestStatus(),
    (frame) => {
      if (frame) applyFrame(frame);
    }
  );

  const stopConnectionWatch = options.getConnectionEpoch
    ? watch(options.getConnectionEpoch, () => {
        if (isBusy.value) void reconcile();
        if (!supported.value) void probe(0);
      })
    : () => {};

  onScopeDispose(() => {
    stopThreadWatch();
    stopFrameWatch();
    stopConnectionWatch();
    clearSettleTimer();
    clearWatchdog();
    clearProbeTimer();
    generation++;
    probeSeq++;
  }, true);

  const view = computed<CompactionControlView>(() => ({
    supported: supported.value,
    phase: phase.value,
    busy: isBusy.value,
    statusText: statusText.value,
  }));

  return { supported, phase, trigger, isBusy, statusText, view, request, applyFrame, reset };
}
