import { apiFetch } from '@/api/http';
import type { ConversationContextReport } from '@/types/context';

/**
 * Fetches the conversation's context/cost report (#681 → #685), or `null` when there is nothing
 * the caller may see.
 *
 * `null` deliberately covers BOTH a 404 (unknown thread, or a server predating the route) and a 403
 * (a thread the caller may not read). The panel renders the two identically — one "unavailable"
 * state — because distinguishing them would itself be metadata about a conversation the caller was
 * refused (#685 AC: per-thread authorization failures reveal no context/cost data). A non-JSON body
 * (a dev server falling back to `index.html` for an unknown `/api` path) is treated the same way.
 *
 * Any OTHER non-ok status still throws, so a 500 from a route that exists stays distinguishable from
 * an absent one HERE; the composable then degrades to the same unavailable state and keeps the
 * difference in a log line.
 */
export async function getConversationContext(
  threadId: string
): Promise<ConversationContextReport | null> {
  const response = await apiFetch(`/api/conversations/${encodeURIComponent(threadId)}/context`);
  if (response.status === 404 || response.status === 403) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Failed to fetch context: ${response.statusText}`);
  }
  try {
    return (await response.json()) as ConversationContextReport;
  } catch {
    return null;
  }
}
