import { afterEach, describe, expect, it, vi } from 'vitest';
import { queueRender, RenderCancelledError, viewportDistance } from '@/utils/renderQueue';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** Drain only the microtask queue: a macrotask boundary (the queue's yield) is NOT crossed. */
async function microtasks(turns = 6) {
  for (let turn = 0; turn < turns; turn += 1) await Promise.resolve();
}

/** Cross one macrotask boundary, which is what the queue's yield waits on. */
function eventLoopTurn() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('renderQueue', () => {
  it('runs one render at a time and starts the first one without waiting for a timer', async () => {
    const started: string[] = [];
    const first = deferred<string>();

    const a = queueRender(() => {
      started.push('a');
      return first.promise;
    });
    const b = queueRender(() => {
      started.push('b');
      return Promise.resolve('b');
    });

    // An idle queue starts immediately: a lone diagram must not pay for a scheduling hop.
    expect(started).toEqual(['a']);

    first.resolve('a');
    await eventLoopTurn();
    await eventLoopTurn();
    expect(started).toEqual(['a', 'b']);
    await expect(a.promise).resolves.toBe('a');
    await expect(b.promise).resolves.toBe('b');
  });

  it('yields to the event loop between two renders instead of chaining them in one task', async () => {
    const started: string[] = [];
    const first = deferred<string>();

    queueRender(() => {
      started.push('a');
      return first.promise;
    });
    const b = queueRender(() => {
      started.push('b');
      return Promise.resolve('b');
    });

    first.resolve('a');
    await microtasks();
    // The first render has finished, yet the second has not begun: the queue is parked on a real
    // event-loop turn, which is what lets a click or a keystroke be delivered between diagrams.
    expect(started).toEqual(['a']);

    await eventLoopTurn();
    expect(started).toEqual(['a', 'b']);
    await b.promise;
  });

  it('uses scheduler.yield when the platform provides it', async () => {
    const schedulerYield = vi.fn(() => Promise.resolve());
    vi.stubGlobal('scheduler', { yield: schedulerYield });
    const started: string[] = [];
    const first = deferred<string>();

    queueRender(() => {
      started.push('a');
      return first.promise;
    });
    const b = queueRender(() => {
      started.push('b');
      return Promise.resolve('b');
    });

    first.resolve('a');
    await microtasks();
    expect(schedulerYield).toHaveBeenCalledTimes(1);
    expect(started).toEqual(['a', 'b']);
    await b.promise;
  });

  it('takes the job closest to the viewport first, re-reading priority at dequeue time', async () => {
    const started: string[] = [];
    const blocker = deferred<string>();
    queueRender(() => {
      started.push('blocker');
      return blocker.promise;
    });

    let farMoved = 900;
    const queue = (name: string, priority: () => number) =>
      queueRender(() => {
        started.push(name);
        return Promise.resolve(name);
      }, priority);

    // Insertion order is near, middle, far -- deliberately different from the order they must run
    // in, so a queue that ignored priority and ran FIFO would be caught here.
    queue('near', () => 10);
    queue('middle', () => 400);
    queue('far', () => farMoved);

    // The reader scrolls while the queue is busy, so "far" is now the closest of the three. A
    // priority captured at enqueue time would still run it last.
    farMoved = 0;
    blocker.resolve('blocker');
    for (let turn = 0; turn < 5; turn += 1) await eventLoopTurn();

    expect(started).toEqual(['blocker', 'far', 'near', 'middle']);
  });

  it('never runs a render cancelled before its turn', async () => {
    const started: string[] = [];
    const blocker = deferred<string>();
    queueRender(() => {
      started.push('blocker');
      return blocker.promise;
    });
    const skipped = queueRender(() => {
      started.push('skipped');
      return Promise.resolve('skipped');
    });
    const kept = queueRender(() => {
      started.push('kept');
      return Promise.resolve('kept');
    });

    skipped.cancel();
    await expect(skipped.promise).rejects.toBeInstanceOf(RenderCancelledError);

    blocker.resolve('blocker');
    for (let turn = 0; turn < 4; turn += 1) await eventLoopTurn();

    expect(started).toEqual(['blocker', 'kept']);
    await kept.promise;
  });

  it('releases the queue when a render already in flight is cancelled', async () => {
    const started: string[] = [];
    const abandoned = deferred<string>();
    const running = queueRender(() => {
      started.push('running');
      return abandoned.promise;
    });
    const next = queueRender(() => {
      started.push('next');
      return Promise.resolve('next');
    });

    running.cancel();
    await expect(running.promise).rejects.toBeInstanceOf(RenderCancelledError);
    for (let turn = 0; turn < 3; turn += 1) await eventLoopTurn();

    // The abandoned job never settles; the queue must not be held behind a result nobody wants.
    expect(started).toEqual(['running', 'next']);
    await expect(next.promise).resolves.toBe('next');
    abandoned.resolve('too late');
  });

  it('propagates a render failure to its own caller only', async () => {
    const failing = queueRender(() => Promise.reject(new Error('boom')));
    const after = queueRender(() => Promise.resolve('after'));

    await expect(failing.promise).rejects.toThrow('boom');
    for (let turn = 0; turn < 3; turn += 1) await eventLoopTurn();
    await expect(after.promise).resolves.toBe('after');
  });
});

describe('viewportDistance', () => {
  const element = (top: number, bottom: number) =>
    ({ getBoundingClientRect: () => ({ top, bottom }) }) as unknown as Element;

  it('is zero while any part of the element is on screen', () => {
    vi.stubGlobal('innerHeight', 800);
    expect(viewportDistance(element(-50, 120))).toBe(0);
    expect(viewportDistance(element(700, 1200))).toBe(0);
  });

  it('grows with the gap above or below the viewport', () => {
    vi.stubGlobal('innerHeight', 800);
    expect(viewportDistance(element(-500, -300))).toBe(300);
    expect(viewportDistance(element(1400, 1700))).toBe(600);
  });

  it('treats a missing element as on screen rather than infinitely far away', () => {
    expect(viewportDistance(null)).toBe(0);
  });
});
