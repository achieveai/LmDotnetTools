import { apiFetch } from '@/api/http';
import type { TodoBoardSnapshot } from '@/types/todo';

/**
 * Fetches the conversation's ToDo board (#583), or `null` when there is no board to show.
 *
 * `null` covers three cases the panel must treat identically, because to the reader they are the
 * same thing — nothing to look at:
 *
 *  - **404** — nothing recorded for this thread, OR a server predating PR 1 that has no `/todos`
 *    route at all. The panel is being built ahead of its backend, so the second case is the normal
 *    one until PR 1 merges, and it must be silent rather than an error.
 *  - **A non-JSON body** — a dev server that falls back to `index.html` for an unknown `/api` path
 *    answers 200 with HTML. Letting the `SyntaxError` escape would surface a parse failure in the
 *    chat view for what is, again, just an absent endpoint.
 *  - CLI-backed providers (codex/claude/copilot), which never register a `TaskManager`.
 *
 * Any OTHER non-ok status still throws: a 500 from a route that does exist is a real fault, and
 * silently blanking the board would hide it. The composable decides how loudly to react.
 */
export async function getConversationTodos(threadId: string): Promise<TodoBoardSnapshot | null> {
  const response = await apiFetch(`/api/conversations/${encodeURIComponent(threadId)}/todos`);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Failed to fetch todos: ${response.statusText}`);
  }
  try {
    return (await response.json()) as TodoBoardSnapshot;
  } catch {
    return null;
  }
}
