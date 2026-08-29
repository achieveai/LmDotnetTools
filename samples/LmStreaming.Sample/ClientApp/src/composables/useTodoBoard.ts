import { computed, onScopeDispose, ref, watch } from 'vue';
import { getConversationTodos } from '@/api/todosApi';
import type { TodoTask } from '@/types/todo';
import type { ConversationTodoMessage } from '@/types/messages';
import {
  countTodoTasks,
  findActiveTaskId,
  flattenTodoTasks,
  normalizeTodoTasks,
} from '@/utils/todoBoard';
import { logger } from '@/utils';

const log = logger.forComponent('useTodoBoard');

/**
 * Store for the ToDo board panel (#583, PR 3).
 *
 * Mirrors `useSubAgentPanel`: a plain factory, instantiated ONCE in `ChatLayout` and passed down as
 * props — not a module singleton, so two mounted layouts (or two tests) never share a board.
 *
 * Two inputs, both getters so the caller keeps ownership of the reactive source:
 *  - `getThreadId` — which conversation's board to show. Changing it resets and re-hydrates.
 *  - `getLatestFrame` — the newest `conversation_todo` push frame seen by `useChat`. This composable
 *    deliberately does NOT import `useChat`; the frame is handed in, exactly as the sub-agent panel
 *    is handed its parent thread id, so the board can be tested without the chat machinery.
 *
 * Frames SET the board, they never accumulate into it — same rule as the conversation-usage banner.
 * The server sends a whole snapshot on every change, so the newest frame is the whole truth.
 *
 * Until PRs 1-2 merge there is no endpoint and no frame. That is not an error state: the board is
 * simply absent, `hasBoard` is false, and `ChatLayout` mounts nothing.
 */
export function useTodoBoard(
  getThreadId: () => string | null,
  getLatestFrame: () => ConversationTodoMessage | null
) {
  const tasks = ref<TodoTask[]>([]);
  const isLoading = ref(false);

  const rows = computed(() => flattenTodoTasks(tasks.value));
  const counts = computed(() => countTodoTasks(tasks.value));
  const activeTaskId = computed(() => findActiveTaskId(tasks.value));

  /**
   * Whether there is a board worth showing at all. `ChatLayout` gates the panel on this so a
   * conversation that never touched the task tools — every CLI-backed provider, and every ordinary
   * chat — shows no panel rather than an empty one taking up the right edge.
   */
  const hasBoard = computed(() => tasks.value.length > 0);

  /**
   * Two counters, because "a newer hydrate started" and "a newer write landed" are different
   * questions with different answers.
   *
   * `hydrateSeq` decides which in-flight REST read owns the board and the loading flag; only the
   * newest may write, and only the newest may clear `isLoading` (otherwise a superseded read
   * switches the spinner off while its replacement is still running).
   *
   * `writeEpoch` advances on EVERY write, frames included. A REST read that started before a live
   * frame arrived is stale by the time it resolves, and must not clobber the newer frame — but it
   * still owns the loading flag it set, so it clears that and drops only its data.
   */
  let hydrateSeq = 0;
  let writeEpoch = 0;

  /** Drops the board without fetching — used on conversation switch and on scope teardown. */
  function reset(): void {
    hydrateSeq++;
    writeEpoch++;
    tasks.value = [];
    isLoading.value = false;
  }

  /**
   * Applies one live push frame.
   *
   * A frame that names a DIFFERENT thread is ignored. The frame ref handed in by `useChat` keeps
   * holding the last frame after the user switches conversations; without this guard, re-entering a
   * conversation could paint another one's board.
   */
  function applyFrame(frame: ConversationTodoMessage): void {
    const threadId = getThreadId();
    if (frame.threadId && threadId && frame.threadId !== threadId) {
      log.debug('Ignoring a todo frame addressed to another conversation', {
        frameThreadId: frame.threadId,
        threadId,
      });
      return;
    }
    writeEpoch++;
    tasks.value = normalizeTodoTasks(frame.tasks);
  }

  /** Loads the persisted board over REST — page load, reconnect, and conversation switch. */
  async function hydrate(): Promise<void> {
    const threadId = getThreadId();
    if (!threadId) {
      reset();
      return;
    }

    const seq = ++hydrateSeq;
    const epochAtStart = ++writeEpoch;
    isLoading.value = true;
    try {
      const snapshot = await getConversationTodos(threadId);
      if (seq !== hydrateSeq) return; // a newer hydrate owns the board AND the loading flag
      if (epochAtStart === writeEpoch) {
        tasks.value = snapshot ? normalizeTodoTasks(snapshot.tasks) : [];
      }
      // else: a live frame landed while this read was in flight. It is strictly newer, so it keeps
      // the board; this read still clears the loading flag it set, in the finally below.
    } catch (e) {
      if (seq !== hydrateSeq) return;
      // Debug, not error: a build without PR 1's endpoint reaches here on every conversation, and
      // an accessory panel must not fill the console for a backend that has not shipped yet.
      log.debug('Could not load the todo board; rendering it as absent', { threadId, error: e });
      if (epochAtStart === writeEpoch) {
        tasks.value = [];
      }
    } finally {
      if (seq === hydrateSeq) isLoading.value = false;
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

  // `failSilently` so unit tests may call this composable outside an active effect scope without
  // tripping a dev warning; advancing the counters makes any in-flight hydrate land as a no-op.
  onScopeDispose(() => {
    stopThreadWatch();
    stopFrameWatch();
    hydrateSeq++;
    writeEpoch++;
  }, true);

  return {
    tasks,
    rows,
    counts,
    activeTaskId,
    hasBoard,
    isLoading,
    hydrate,
    applyFrame,
    reset,
  };
}
