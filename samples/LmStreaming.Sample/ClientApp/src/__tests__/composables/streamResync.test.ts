import { describe, expect, it, vi } from 'vitest';
import {
  createStreamResyncCoordinator,
  DEFAULT_MAX_RESYNC_ATTEMPTS,
  type StreamResyncSteps,
} from '@/composables/streamResync';

// The coordinator owns the RECOVERY POLICY — single flight, strict step order, bounded attempts and
// stale-epoch rejection — while the steps themselves are the existing conversation-restore path.
// useChatResume.test.ts proves the socket wiring end to end; these tests pin the policy in isolation,
// where a flapping backend and a mid-recovery conversation switch are trivial to reproduce.

type Steps = { [K in keyof StreamResyncSteps]: ReturnType<typeof vi.fn> } & StreamResyncSteps;

function makeSteps(overrides: Partial<StreamResyncSteps> = {}): Steps {
  return {
    isCurrent: vi.fn(() => true),
    discardDroppedStream: vi.fn(),
    loadHistory: vi.fn(async () => {}),
    resubscribe: vi.fn(async () => {}),
    reportFailure: vi.fn(),
    ...overrides,
  } as Steps;
}

/** A step whose completion the test controls, for observing the window while work is in flight. */
function deferred(): { promise: Promise<void>; resolve: () => void; reject: (e: unknown) => void } {
  let resolve!: () => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = () => res();
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe('createStreamResyncCoordinator', () => {
  it('discards partials, rehydrates from REST, then resubscribes — in that order', async () => {
    const steps = makeSteps();
    const coordinator = createStreamResyncCoordinator(steps);

    await coordinator.request('thread-1', 0, 'slow_consumer');

    expect(steps.discardDroppedStream).toHaveBeenCalledTimes(1);
    expect(steps.loadHistory).toHaveBeenCalledWith('thread-1');
    expect(steps.resubscribe).toHaveBeenCalledWith('thread-1');
    expect(steps.discardDroppedStream.mock.invocationCallOrder[0]).toBeLessThan(
      steps.loadHistory.mock.invocationCallOrder[0],
    );
    expect(steps.loadHistory.mock.invocationCallOrder[0]).toBeLessThan(
      steps.resubscribe.mock.invocationCallOrder[0],
    );
    expect(steps.reportFailure).not.toHaveBeenCalled();
  });

  it('coalesces concurrent requests for the same thread/epoch into one operation', async () => {
    const gate = deferred();
    const steps = makeSteps({ loadHistory: vi.fn(() => gate.promise) });
    const coordinator = createStreamResyncCoordinator(steps);

    const first = coordinator.request('thread-1', 0, 'stream_recovery');
    const second = coordinator.request('thread-1', 0, 'resync_required');
    const third = coordinator.request('thread-1', 0, 'closed_1006');
    gate.resolve();
    await Promise.all([first, second, third]);

    expect(steps.loadHistory, 'three signals, one drop, one rehydrate').toHaveBeenCalledTimes(1);
    expect(steps.resubscribe).toHaveBeenCalledTimes(1);
  });

  it('starts a fresh operation for a drop that happens after the previous one settled', async () => {
    const steps = makeSteps();
    const coordinator = createStreamResyncCoordinator(steps);

    await coordinator.request('thread-1', 0, 'closed_1000');
    await coordinator.request('thread-1', 0, 'closed_1000');

    expect(steps.loadHistory, 'the in-flight slot is released once the operation finishes').toHaveBeenCalledTimes(2);
  });

  it('rejects a request for a conversation that is no longer current', async () => {
    const steps = makeSteps({ isCurrent: vi.fn(() => false) });
    const coordinator = createStreamResyncCoordinator(steps);

    await coordinator.request('thread-1', 0, 'closed_1000');

    expect(steps.discardDroppedStream, 'a stale close must not disturb the current conversation').not.toHaveBeenCalled();
    expect(steps.loadHistory).not.toHaveBeenCalled();
    expect(steps.reportFailure).not.toHaveBeenCalled();
  });

  it('abandons an in-flight operation when it is invalidated mid-recovery', async () => {
    const gate = deferred();
    const steps = makeSteps({ loadHistory: vi.fn(() => gate.promise) });
    const coordinator = createStreamResyncCoordinator(steps);

    const pending = coordinator.request('thread-1', 0, 'closed_1000');
    coordinator.invalidate(); // e.g. the user switched conversations while REST was in flight
    gate.resolve();
    await pending;

    expect(steps.resubscribe, 'never resubscribe a conversation the user left').not.toHaveBeenCalled();
    expect(steps.reportFailure).not.toHaveBeenCalled();
  });

  it('stops after the bounded attempt count and reports exactly once', async () => {
    const steps = makeSteps();
    const coordinator = createStreamResyncCoordinator(steps);

    for (let i = 0; i < DEFAULT_MAX_RESYNC_ATTEMPTS + 3; i++) {
      await coordinator.request('thread-1', 0, 'closed_1006');
    }

    expect(steps.loadHistory, 'a flapping backend cannot spin the client').toHaveBeenCalledTimes(
      DEFAULT_MAX_RESYNC_ATTEMPTS,
    );
    expect(steps.reportFailure, 'one actionable error, not one per drop').toHaveBeenCalledTimes(1);
    expect(steps.reportFailure.mock.calls[0][0]).toContain(String(DEFAULT_MAX_RESYNC_ATTEMPTS));
  });

  it('gives each conversation its own attempt budget', async () => {
    const steps = makeSteps();
    const coordinator = createStreamResyncCoordinator(steps, { maxAttempts: 1 });

    await coordinator.request('thread-1', 0, 'closed_1006');
    await coordinator.request('thread-1', 0, 'closed_1006'); // over budget for thread-1
    await coordinator.request('thread-2', 1, 'closed_1006'); // a different conversation starts fresh

    expect(steps.loadHistory).toHaveBeenCalledTimes(2);
    expect(steps.loadHistory.mock.calls.map((c) => c[0])).toEqual(['thread-1', 'thread-2']);
  });

  it('restores the attempt budget once the run makes progress', async () => {
    const steps = makeSteps();
    const coordinator = createStreamResyncCoordinator(steps, { maxAttempts: 1 });

    await coordinator.request('thread-1', 0, 'closed_1006');
    coordinator.invalidate(); // the recovered stream reached `done`
    await coordinator.request('thread-1', 0, 'closed_1006');

    expect(steps.loadHistory, 'an occasionally-flaky conversation is not permanently penalised').toHaveBeenCalledTimes(2);
    expect(steps.reportFailure).not.toHaveBeenCalled();
  });

  it('reports one actionable failure when a recovery step throws, and stays usable', async () => {
    const steps = makeSteps({
      loadHistory: vi.fn().mockRejectedValueOnce(new Error('history fetch failed')),
    });
    const coordinator = createStreamResyncCoordinator(steps);

    await coordinator.request('thread-1', 0, 'closed_1006');
    expect(steps.reportFailure).toHaveBeenCalledTimes(1);
    expect(steps.reportFailure.mock.calls[0][0]).toContain('history fetch failed');
    expect(steps.resubscribe).not.toHaveBeenCalled();

    // The in-flight slot must be released in `finally`, or a throw would wedge recovery forever.
    await coordinator.request('thread-1', 0, 'closed_1006');
    expect(steps.resubscribe).toHaveBeenCalledTimes(1);
  });

  // A NEW RUN in the same conversation is a fresh start for the budget, but it is NOT a reason to
  // abandon a recovery already under way — the two are different concerns, so they are different
  // calls. Folding this into invalidate() would silently kill an in-flight rehydrate on every send.
  it('restores the attempt budget without abandoning the operation already in flight', async () => {
    const gate = deferred();
    const steps = makeSteps({ loadHistory: vi.fn(() => gate.promise) });
    const coordinator = createStreamResyncCoordinator(steps, { maxAttempts: 1 });

    const pending = coordinator.request('thread-1', 0, 'closed_1006');
    coordinator.resetAttempts();
    gate.resolve();
    await pending;

    expect(steps.resubscribe, 'the recovery already under way still completes').toHaveBeenCalledTimes(1);

    await coordinator.request('thread-1', 0, 'closed_1006');
    expect(steps.loadHistory, 'the new run gets its own budget').toHaveBeenCalledTimes(2);
    expect(steps.reportFailure).not.toHaveBeenCalled();
  });

  // Single-flight must never coalesce a LIVE request into an operation that has been invalidated: the
  // abandoned one returns before resubscribing, so the caller would get silence instead of recovery.
  it('does not coalesce a fresh request into an operation that was already invalidated', async () => {
    const gate = deferred();
    const steps = makeSteps({ loadHistory: vi.fn(() => gate.promise) });
    const coordinator = createStreamResyncCoordinator(steps);

    const abandoned = coordinator.request('thread-1', 0, 'closed_1006');
    coordinator.invalidate();
    const fresh = coordinator.request('thread-1', 0, 'closed_1006');
    gate.resolve();
    await Promise.all([abandoned, fresh]);

    expect(steps.loadHistory, 'the abandoned operation cannot stand in for the new one').toHaveBeenCalledTimes(2);
    expect(steps.resubscribe, 'only the still-valid operation resubscribes').toHaveBeenCalledTimes(1);
  });

  // A rerun QUEUED behind an in-flight operation is work that has not started yet, so `invalidate()`
  // (Stop, or the run completing) must cancel it like anything else in flight. `isCurrent` cannot
  // catch it — cancelling a run does not change the conversation — and `invalidate()` also refills
  // the attempt budget, so an unguarded rerun rehydrates and resubscribes a run the user just ended.
  it('drops a rerun queued behind an operation that was then invalidated', async () => {
    const gate = deferred();
    const steps = makeSteps({ loadHistory: vi.fn(() => gate.promise) });
    const coordinator = createStreamResyncCoordinator(steps);

    const creator = coordinator.request('thread-1', 0, 'closed_1006', 'socket-1');
    // A different physical drop while the first recovery runs: queued as one rerun behind it.
    const rerun = coordinator.request('thread-1', 0, 'closed_1006', 'socket-2');
    coordinator.invalidate();
    gate.resolve();
    await Promise.all([creator, rerun]);

    expect(steps.loadHistory, 'the queued rerun never starts').toHaveBeenCalledTimes(1);
    expect(steps.resubscribe, 'nothing is resubscribed once everything in flight is unwanted').not.toHaveBeenCalled();
    expect(steps.reportFailure, 'an abandoned rerun is not a failure').not.toHaveBeenCalled();
  });
});
