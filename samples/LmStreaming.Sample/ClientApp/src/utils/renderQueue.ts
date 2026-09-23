/*
 * One shared queue for expensive, client-side figure rendering (Mermaid/PlantUML diagrams and
 * SMILES structures).
 *
 * Mermaid lays out a diagram synchronously on the main thread; a medium flowchart costs tens to
 * hundreds of milliseconds. A message — or a previewed Markdown file — with a dozen of them used to
 * start every render as soon as its component mounted, so the whole page froze for seconds before
 * the first paint, and nothing the user clicked or typed was processed until the last diagram was
 * done.
 *
 * Two rules fix that, and both live here rather than in the viewers:
 *   1. one render at a time, so N diagrams never multiply into N concurrent layout passes;
 *   2. a real yield to the event loop between renders, so the browser gets a turn to paint and to
 *      deliver input between two diagrams instead of after all of them.
 *
 * Jobs are taken lowest-priority-value first, re-read at dequeue time, so the viewers can order by
 * distance from the viewport and a scroll that happens while the queue is busy is respected.
 * `renderDiagram` keeps its own engine-level serialization; this queue is about the main thread,
 * not about engine state.
 */

/** One viewport of lead time: a diagram starts rendering shortly before it can be seen. */
const APPROACH_MARGIN = '100% 0px';

export class RenderCancelledError extends Error {
  constructor() {
    super('The render was cancelled.');
    this.name = 'RenderCancelledError';
  }
}

export interface QueuedRender<T> {
  /** Resolves with the job's value, or rejects with `RenderCancelledError` once cancelled. */
  readonly promise: Promise<T>;
  /** Drop the job if it has not started yet; abandon its result if it already has. */
  cancel(): void;
}

interface Entry {
  priority: () => number;
  /** Settles when the job finishes or is cancelled, whichever comes first. */
  begin: () => Promise<void>;
}

const pending: Entry[] = [];
let draining = false;

/**
 * Hand the main thread back. `scheduler.yield()` keeps our place ahead of unrelated work where it
 * exists (Chromium 129+); elsewhere a zero timer is the portable macrotask boundary.
 */
export function yieldToEventLoop(): Promise<void> {
  const scheduler = (globalThis as { scheduler?: { yield?: () => Promise<unknown> } }).scheduler;
  if (typeof scheduler?.yield === 'function') {
    return scheduler.yield().then(
      () => undefined,
      () => undefined
    );
  }
  return new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

function takeNext(): Entry | undefined {
  if (!pending.length) return undefined;
  let bestIndex = 0;
  let bestPriority = pending[0].priority();
  for (let index = 1; index < pending.length; index += 1) {
    const priority = pending[index].priority();
    // Strictly lower only, so equal priorities keep their insertion order.
    if (priority < bestPriority) {
      bestIndex = index;
      bestPriority = priority;
    }
  }
  return pending.splice(bestIndex, 1)[0];
}

async function drain(): Promise<void> {
  draining = true;
  try {
    for (let entry = takeNext(); entry; entry = takeNext()) {
      await entry.begin();
      if (pending.length) await yieldToEventLoop();
    }
  } finally {
    draining = false;
  }
}

/**
 * Queue one render. `run` is invoked when the job's turn comes — never before, so a job cancelled
 * while it waits costs nothing at all.
 *
 * `priority` is evaluated every time the queue looks for the next job: lower runs first.
 */
export function queueRender<T>(run: () => Promise<T>, priority: () => number = () => 0): QueuedRender<T> {
  let settle!: (value: T) => void;
  let fail!: (reason: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => {
    settle = resolve;
    fail = reject;
  });

  let release!: () => void;
  const finished = new Promise<void>((resolve) => {
    release = resolve;
  });

  const entry: Entry = {
    priority,
    begin() {
      try {
        run().then(
          (value) => {
            settle(value);
            release();
          },
          (reason) => {
            fail(reason);
            release();
          }
        );
      } catch (reason) {
        fail(reason);
        release();
      }
      return finished;
    },
  };

  pending.push(entry);
  // Starts synchronously when the queue is idle, so a lone diagram is not delayed by a timer.
  if (!draining) void drain();

  return {
    promise,
    cancel(): void {
      const index = pending.indexOf(entry);
      if (index >= 0) pending.splice(index, 1);
      fail(new RenderCancelledError());
      // If the job was already running, its work is now abandoned: let the queue move on rather
      // than hold every remaining diagram behind a result nobody will look at.
      release();
    },
  };
}

/**
 * Distance in CSS pixels between `element` and the viewport, 0 while any part of it is visible.
 * Used as the queue priority so the diagrams the reader is looking at are drawn first.
 */
export function viewportDistance(element: Element | null | undefined): number {
  if (!element || typeof element.getBoundingClientRect !== 'function') return 0;
  const rect = element.getBoundingClientRect();
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
  if (rect.bottom < 0) return -rect.bottom;
  if (rect.top > viewportHeight) return rect.top - viewportHeight;
  return 0;
}

/**
 * Call `onApproach` once, when `element` comes within a viewport of being visible — and never
 * again, so scrolling a diagram out of view and back does not re-render it.
 *
 * Without `IntersectionObserver` (jsdom, very old browsers) the element counts as approached
 * immediately: the lazy pipeline is an optimization, never a precondition for showing a diagram.
 */
export function observeApproach(element: Element, onApproach: () => void): () => void {
  if (typeof IntersectionObserver !== 'function') {
    onApproach();
    return () => undefined;
  }
  const observer = new IntersectionObserver(
    (entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return;
      observer.disconnect();
      onApproach();
    },
    { rootMargin: APPROACH_MARGIN }
  );
  observer.observe(element);
  return () => observer.disconnect();
}
