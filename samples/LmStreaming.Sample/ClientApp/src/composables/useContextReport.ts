import { computed, onScopeDispose, ref, watch } from 'vue';
import { getConversationContext } from '@/api/contextApi';
import type { ContextPressureMessage } from '@/types/messages';
import {
  applyPressureFrame,
  viewFromReport,
  type ContextRowView,
  type ContextTotalView,
} from '@/utils/contextReport';
import { logger } from '@/utils';

const log = logger.forComponent('useContextReport');

/**
 * What the panel knows about the endpoint, as distinct from what it knows about the rows:
 *  - `idle` — no conversation open; nothing was asked.
 *  - `loading` — a read is in flight (previous rows stay visible so a refresh does not flicker).
 *  - `ready` — the endpoint answered with a report.
 *  - `unavailable` — the endpoint answered 404 or 403, or the read failed. ONE state for all three,
 *    on purpose: the panel must not reveal whether a refused thread exists (#685 AC).
 */
export type ContextReportStatus = 'idle' | 'loading' | 'ready' | 'unavailable';

/**
 * Store for the context/cost panel (#685).
 *
 * Mirrors `useTodoBoard`: a plain factory, instantiated once in `ChatLayout` and passed down as
 * props; three getters so the caller keeps ownership of the reactive sources:
 *  - `getThreadId` — which conversation to show. Changing it resets and re-hydrates.
 *  - `getLatestFrame` — the newest `context_pressure` frame seen by `useChat`. Handed in, never
 *    imported, so the panel is testable without the chat machinery.
 *  - `getRefreshKey` — any value whose change means "the endpoint may know more now": the run
 *    going idle (usage rows are persisted at run completion) or the sub-agent roster changing.
 *
 * Authority (spec 679 §7.3): the endpoint is authoritative; frames are transient enrichments.
 * Frames only ever UPGRADE a row's observation (a lower generation ordinal is dropped), and they
 * carry no usage, so the total is always the endpoint's. After every hydrate the newest frame is
 * re-applied on top, so live and reload converge on the same values whichever lands last.
 */
export function useContextReport(
  getThreadId: () => string | null,
  getLatestFrame: () => ContextPressureMessage | null,
  getRefreshKey: () => unknown
) {
  const rows = ref<ContextRowView[]>([]);
  const total = ref<ContextTotalView | null>(null);
  const generatedAtUtc = ref<string | null>(null);
  const status = ref<ContextReportStatus>('idle');

  const isLoading = computed(() => status.value === 'loading');
  /** Whether there is anything to render: a report, or at least one provisional live row. */
  const hasReport = computed(() => status.value === 'ready' || rows.value.length > 0);

  /** Which in-flight read owns the store; only the newest may write or clear the loading state. */
  let hydrateSeq = 0;

  function reset(): void {
    hydrateSeq++;
    rows.value = [];
    total.value = null;
    generatedAtUtc.value = null;
    status.value = 'idle';
  }

  /**
   * Applies one live frame. BOTH thread ids must be present AND equal — the same rule as the todo
   * board, for the same reasons (`useChat.clearMessages` nulls its thread id mid-switch; the frame
   * ref keeps holding the last frame across a switch; `threadId` is optional on the wire).
   */
  function applyFrame(frame: ContextPressureMessage): void {
    const threadId = getThreadId();
    if (!frame.threadId || !threadId || frame.threadId !== threadId) {
      log.debug('Dropping a context_pressure frame that does not name the open conversation', {
        frameThreadId: frame.threadId ?? null,
        threadId,
      });
      return;
    }
    const next = applyPressureFrame(rows.value, frame);
    if (next !== rows.value) rows.value = next;
  }

  /** Loads the authoritative report — page load, reconnect, conversation switch, refresh key. */
  async function hydrate(): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      reset();
      return;
    }

    const seq = ++hydrateSeq;
    status.value = 'loading';
    try {
      const report = await getConversationContext(threadId);
      if (seq !== hydrateSeq) return; // a newer hydrate owns the store
      if (report === null) {
        rows.value = [];
        total.value = null;
        generatedAtUtc.value = null;
        status.value = 'unavailable';
        return;
      }
      const view = viewFromReport(report);
      const latest = getLatestFrame();
      // Re-apply the newest frame: if it is newer than the report's observation it stays on top; if
      // the report is newer (a reload after the run), the ordinal guard drops it. Either way the two
      // paths converge on one answer.
      rows.value = latest ? applyPressureFrame(view.rows, latest) : view.rows;
      total.value = view.total;
      generatedAtUtc.value = view.generatedAtUtc;
      status.value = 'ready';
    } catch (e) {
      if (seq !== hydrateSeq) return;
      // Only a genuine 5xx or a network error reaches here (404/403/non-JSON are already `null`).
      // `debug`, not `error`: the panel is an accessory and must degrade to "unavailable", never to
      // an error banner over the chat; the app logger forwards debug lines to the console and the
      // server log endpoint, so it is recorded, not swallowed.
      log.debug('Could not load the context report; rendering it as unavailable', {
        threadId,
        error: e,
      });
      rows.value = [];
      total.value = null;
      generatedAtUtc.value = null;
      status.value = 'unavailable';
    }
  }

  const stopThreadWatch = watch(
    () => getThreadId(),
    () => {
      reset();
      void hydrate();
    }
  );

  const stopFrameWatch = watch(
    () => getLatestFrame(),
    (frame) => {
      if (frame) applyFrame(frame);
    }
  );

  const stopRefreshWatch = watch(
    () => getRefreshKey(),
    () => {
      if (getThreadId()) void hydrate();
    }
  );

  onScopeDispose(() => {
    stopThreadWatch();
    stopFrameWatch();
    stopRefreshWatch();
    hydrateSeq++;
  }, true);

  return {
    rows,
    total,
    generatedAtUtc,
    status,
    isLoading,
    hasReport,
    hydrate,
    applyFrame,
    reset,
  };
}
