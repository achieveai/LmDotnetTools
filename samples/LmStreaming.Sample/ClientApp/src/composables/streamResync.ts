import { logger } from '@/utils';

const log = logger.forComponent('streamResync');

/** Attempts allowed per (threadId, epoch) before recovery gives up and reports once. */
export const DEFAULT_MAX_RESYNC_ATTEMPTS = 3;

/**
 * The recovery steps the coordinator drives, injected so the coordinator itself owns only the
 * single-flight / bounded-retry / stale-epoch policy and can be unit-tested without Vue, a socket,
 * or the REST API.
 *
 * The coordinator invokes them in a STRICT order — `discardDroppedStream` → `loadHistory` →
 * `resubscribe` — because each step depends on the previous one having settled:
 * canonical history must be rehydrated before anything re-subscribes (otherwise replayed frames
 * race the rehydrate), and nothing may re-subscribe before authoritative run state says the run is
 * still alive (otherwise a finished run gets a pointless socket and a permanent spinner).
 */
export interface StreamResyncSteps {
  /**
   * Is `(threadId, epoch)` still the conversation the user is looking at? Re-checked at every
   * await boundary so a conversation switch mid-recovery abandons the work instead of
   * resurrecting a stream the user has moved on from.
   */
  isCurrent: (threadId: string, epoch: number) => boolean;
  /**
   * Release everything the dead socket left behind: the SOCKET itself (an open connection keeps
   * pushing frames from a stream we have declared dead, and overlaps its replacement) and the
   * UNFINALIZED streaming accumulators (a half-received text/reasoning delta). Canonical
   * display/history is NOT touched — it is re-established by `loadHistory` and merged by stable
   * message identity. May be async, and the caller may already have STARTED the close before
   * requesting recovery — it must, in fact: `request` rejects stale and over-budget calls without
   * running any step, so a socket released only here would leak on those paths. Either way the close
   * must have COMPLETED by the time this settles, because `resubscribe` opens the replacement next.
   */
  discardDroppedStream: () => void | Promise<void>;
  /** Rehydrate canonical history over REST. */
  loadHistory: (threadId: string) => Promise<void>;
  /**
   * Consult AUTHORITATIVE run state and, only if the run is still in flight, open a
   * subscribe-only socket. Must settle the UI to idle when the run turned out to be finished, so
   * recovery never leaves a permanent loading state.
   */
  resubscribe: (threadId: string) => Promise<void>;
  /** Surface exactly ONE actionable error when recovery is abandoned, and settle the UI to idle. */
  reportFailure: (message: string) => void;
}

export interface StreamResyncCoordinator {
  /**
   * Request recovery of `threadId` at conversation `epoch`. Single-flight: never two operations at
   * once. Resolves when the operation that answers this request finishes (or immediately, when the
   * request is rejected as stale or over budget).
   *
   * `dropId` identifies the PHYSICAL drop being reported. Repeat signals for one drop (an explicit
   * `stream_recovery` frame and the close that follows it) share an id and coalesce into the
   * operation already running. A DIFFERENT id arriving mid-operation is a different drop — most
   * importantly the replacement socket that operation just opened dying at birth, which must become
   * its own bounded attempt AFTER its creator finishes instead of being silently absorbed by it.
   * Omit it only where "coalesce with whatever is running" is genuinely what you mean.
   */
  request: (threadId: string, epoch: number, reason: string, dropId?: string) => Promise<void>;
  /**
   * Everything in flight is now irrelevant and the attempt budget starts over — the run completed
   * normally, the user switched conversations, or the stream was cancelled.
   */
  invalidate: () => void;
  /**
   * A new run started in the SAME conversation: give it a full attempt budget without abandoning a
   * recovery already under way. Deliberately separate from `invalidate()` — the budget and the
   * in-flight operation are different concerns, and a send must not kill a rehydrate in progress.
   */
  resetAttempts: () => void;
}

/**
 * Single-flight, REST-first stream resynchronization.
 *
 * A dropped stream can be signalled several ways at once (an explicit `stream_recovery` frame AND
 * the close that follows it AND, for an abnormal drop, a transport error), and each recovery opens
 * a socket that may itself drop. Handling that with recursive callbacks produces overlapping
 * rehydrates and, against a flapping backend, an unbounded reconnect storm. This coordinator makes
 * the policy explicit and testable instead:
 *
 * - ONE in-flight operation per `(threadId, epoch)` — repeat signals for the same drop (`dropId`)
 *   coalesce, while a DIFFERENT drop reported mid-recovery (typically the replacement socket dying
 *   at birth) is queued as one rerun behind it rather than absorbed into it or run beside it.
 * - A bounded attempt budget per key; on exhaustion the user is told ONCE and the UI settles idle.
 * - Progress resets the budget (`invalidate()` on a completed run), so a long, occasionally-flaky
 *   conversation is not permanently penalised for earlier drops.
 * - Stale requests (an old conversation's close landing after a switch) are rejected outright.
 */
export function createStreamResyncCoordinator(
  steps: StreamResyncSteps,
  options: { maxAttempts?: number } = {}
): StreamResyncCoordinator {
  const maxAttempts = options.maxAttempts ?? DEFAULT_MAX_RESYNC_ATTEMPTS;
  const keyOf = (threadId: string, epoch: number) => `${threadId}::${epoch}`;

  let inFlight: Promise<void> | null = null;
  let inFlightKey = '';
  // The validity token the in-flight operation was started under, so an INVALIDATED operation can
  // still own its slot (its own `finally` releases it) without new requests coalescing into it.
  let inFlightToken = -1;
  // The physical drop the in-flight operation is recovering from. Anything else reported while it
  // runs is a NEW drop rather than a repeat signal for this one.
  let inFlightDropId: string | undefined;
  // At most ONE rerun is queued behind the in-flight operation. Queuing it (rather than recursing
  // from the step that discovered the new drop) is what keeps single-flight true: the rerun goes
  // through `request` like any other, re-checking staleness and spending one attempt of the budget.
  let pendingRerun: Promise<void> | null = null;
  // Attempt budget, scoped to one key so switching conversations starts fresh.
  let attemptKey = '';
  let attempts = 0;
  let exhaustedKey = '';
  // Bumped by invalidate(); an operation started under an older token abandons itself.
  let validity = 0;

  function resetAttempts(): void {
    attemptKey = '';
    attempts = 0;
    exhaustedKey = '';
  }

  async function run(threadId: string, epoch: number, token: number): Promise<void> {
    const stillWanted = () => token === validity && steps.isCurrent(threadId, epoch);
    try {
      await steps.discardDroppedStream();
      await steps.loadHistory(threadId);
      if (!stillWanted()) return;
      await steps.resubscribe(threadId);
    } catch (err) {
      // A recovery abandoned because the user moved on is not a failure worth reporting.
      if (!stillWanted()) return;
      const detail = err instanceof Error ? err.message : String(err);
      log.error('Stream resync failed', { threadId, epoch, error: detail });
      steps.reportFailure(`Could not restore the conversation stream: ${detail}`);
    }
  }

  async function request(threadId: string, epoch: number, reason: string, dropId?: string): Promise<void> {
    if (!steps.isCurrent(threadId, epoch)) {
      log.debug('Ignoring resync for a conversation that is no longer current', { threadId, epoch, reason });
      return;
    }

    const key = keyOf(threadId, epoch);
    // Only coalesce into an operation that is still WANTED: an invalidated one returns before
    // resubscribing, so folding a live request into it would silently skip that request's recovery.
    if (inFlight && inFlightKey === key && inFlightToken === validity) {
      if (dropId === undefined || dropId === inFlightDropId) {
        log.debug('Coalescing resync into the in-flight operation', { threadId, epoch, reason });
        return inFlight;
      }
      // A DIFFERENT drop. The operation in flight cannot recover it — it is already past the steps
      // that would have — and starting one now would break single-flight. Queue exactly one rerun;
      // further new drops fold into that same rerun, and the attempt budget still bounds the chain.
      if (pendingRerun) {
        log.debug('Folding a new drop into the rerun already queued', { threadId, epoch, reason, dropId });
        return pendingRerun;
      }
      log.debug('Queuing a rerun for a drop that happened during recovery', { threadId, epoch, reason, dropId });
      // The rerun is work that has NOT started, so it is cancellable like anything else in flight:
      // it carries the validity token it was queued under and abandons itself if `invalidate()` has
      // since fired. `isCurrent` cannot stand in for this — cancelling a run (Stop) or completing it
      // leaves the conversation exactly as it was — and `invalidate()` refills the attempt budget,
      // so an unguarded rerun would rehydrate and resubscribe a run the user just ended.
      const queuedToken = validity;
      const queued = inFlight.then(() => {
        // Release the slot before rerunning so a drop during the RERUN can queue behind it in turn.
        if (pendingRerun === queued) pendingRerun = null;
        if (queuedToken !== validity) {
          log.debug('Discarding a queued rerun that was invalidated before it started', { threadId, epoch, dropId });
          return;
        }
        return request(threadId, epoch, reason, dropId);
      });
      pendingRerun = queued;
      return queued;
    }

    if (attemptKey !== key) {
      attemptKey = key;
      attempts = 0;
    }
    if (attempts >= maxAttempts) {
      // Report once per key: a flapping backend must not machine-gun error banners.
      if (exhaustedKey !== key) {
        exhaustedKey = key;
        log.error('Giving up on stream resync', { threadId, epoch, reason, attempts });
        steps.reportFailure(
          `Lost the connection to this run and could not restore it after ${maxAttempts} attempts. Reload the conversation to continue.`
        );
      }
      return;
    }

    attempts += 1;
    log.info('Resynchronizing dropped stream', { threadId, epoch, reason, attempt: attempts });

    const token = validity;
    const operation = run(threadId, epoch, token).finally(() => {
      // Only clear the slot if it is still ours — invalidate() or a newer request may own it now.
      if (inFlight === operation) {
        inFlight = null;
        inFlightKey = '';
      }
    });
    inFlight = operation;
    inFlightKey = key;
    inFlightToken = token;
    inFlightDropId = dropId;
    return operation;
  }

  return {
    request,

    invalidate(): void {
      // The in-flight operation stays TRACKED so its own `finally` still releases the slot; bumping
      // the token is what makes it unwanted and stops anything new coalescing into it.
      validity += 1;
      resetAttempts();
    },

    resetAttempts,
  };
}
