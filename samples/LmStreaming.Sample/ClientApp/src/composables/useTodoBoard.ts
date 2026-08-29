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
   * BOTH thread ids must be present AND equal. An absent id on either side is not permission to
   * paint, because both absences are reachable and both would silently disable the guard:
   *
   *  - `getThreadId()` is null mid-switch — `useChat.clearMessages` nulls its `threadId` at the
   *    start of every conversation switch and deliberately does NOT clear `conversationTodo` — and
   *    on a fresh New Chat. A frame landing in that window would mount another board.
   *  - `ConversationTodoMessage.threadId` is optional on the wire, so a PR 2 that omits it would
   *    turn this guard off for every frame at once rather than for one.
   *
   * The frame ref keeps holding the last frame across a switch, so "no active conversation" must
   * mean "paint nothing", not "paint whatever was last seen".
   */
  function applyFrame(frame: ConversationTodoMessage): void {
    const threadId = getThreadId();
    if (!frame.threadId || !threadId || frame.threadId !== threadId) {
      log.debug('Dropping a todo frame that does not name the open conversation', {
        frameThreadId: frame.threadId ?? null,
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
      // Only a genuine 5xx or a network error reaches here: `getConversationTodos` already maps a
      // 404 and a non-JSON body to `null` without throwing, so the two expected "no board" cases
      // never take this path. That makes this rare rather than routine.
      //
      // It is still `debug` rather than `error` because the panel is an accessory — a board that
      // cannot load must degrade to absent, not to an error banner over the chat. This does not
      // hide the fault: the app's logger runs at `minLevel: 'debug'` with `consoleOutput: true`, so
      // the line reaches the console AND is batched to the server log endpoint. It is recorded
      // quietly, not swallowed.
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
