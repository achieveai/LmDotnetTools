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

/** The server's limit on a manual compaction focus prompt; longer input is cut, never refused. */
export const MAX_COMPACTION_FOCUS_LENGTH = 2000;

/**
 * Why the host refused a manual compaction (409 `reason`). Typed as `string` on the result so a
 * reason a newer server adds still reaches the user as text rather than breaking the mapping.
 */
export type CompactionRefusalReason =
  | 'compaction_off'
  | 'provider_owned_session'
  | 'already_pending'
  | 'in_progress'
  | 'nothing_to_compact'
  // History exists, but no cut is legal right now (open tool calls, a protected run): retry later.
  | 'no_safe_boundary';

/**
 * Outcome of asking the host to compact a conversation now. Every expected answer is a value, so the
 * caller maps each to a UI state; only an unexpected status (5xx, network) throws.
 */
export type CompactionRequestResult =
  | { kind: 'accepted'; requestId: string; status: 'queued' | 'running' }
  | { kind: 'refused'; reason: CompactionRefusalReason | (string & {}) }
  | { kind: 'forbidden' }
  | { kind: 'not-found' };

/**
 * Asks the host to compact the conversation now (`POST /api/conversations/{id}/compaction`).
 *
 * Compaction is automatic (context thresholds) or user-triggered through this call; the model can
 * never trigger it. `focus` optionally steers what the summary keeps: it is trimmed, capped at
 * {@link MAX_COMPACTION_FOCUS_LENGTH}, and omitted from the body when blank.
 */
export async function requestCompaction(threadId: string, focus?: string | null): Promise<CompactionRequestResult> {
  const trimmed = (focus ?? '').trim().slice(0, MAX_COMPACTION_FOCUS_LENGTH);
  const response = await apiFetch(`/api/conversations/${encodeURIComponent(threadId)}/compaction`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(trimmed ? { focus: trimmed } : {}),
  });

  if (response.status === 202) {
    const body = (await response.json()) as { requestId: string; status: 'queued' | 'running' };
    return { kind: 'accepted', requestId: body.requestId, status: body.status };
  }
  if (response.status === 409) {
    let reason = 'unknown';
    try {
      const body = await response.json();
      // `reason` is the route's contract; `code` is the refusal field every other conversation route uses.
      const value = body?.reason ?? body?.code;
      if (typeof value === 'string' && value.length > 0) reason = value;
    } catch {
      // Unreadable body: still a refusal, just an unexplained one.
    }
    return { kind: 'refused', reason };
  }
  if (response.status === 403) return { kind: 'forbidden' };
  if (response.status === 404) return { kind: 'not-found' };
  throw new Error(`Failed to request compaction: ${response.status} ${response.statusText}`);
}

/**
 * Whether the host accepts manual compaction requests (`manualCompaction` on
 * `GET /api/conversations/capabilities`). Absent, false, unreadable or failed all mean "no": the
 * control stays hidden rather than offering a button the server would refuse.
 */
export async function supportsManualCompaction(): Promise<boolean> {
  try {
    const response = await apiFetch('/api/conversations/capabilities');
    if (!response.ok) return false;
    const body = await response.json();
    return body?.manualCompaction === true;
  } catch {
    return false;
  }
}
