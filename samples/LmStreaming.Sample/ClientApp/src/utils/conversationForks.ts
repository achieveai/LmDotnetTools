import type { ConversationSummary, DisplayItem } from '@/types';
import type { BranchPoint } from '@/api/conversationsApi';

/**
 * Provider ids whose model reads the CLI's own session rather than our stored messages. Forking is
 * unsupported for them in v1 (the server answers 409 `cli_provider_unsupported`), so the client hides
 * both fork buttons.
 *
 * `GET /api/providers` carries no provider-kind field, so this is an EXACT-id list mirroring the
 * server's CLI catalog entries (`ProviderRegistry.CatalogEntries`: claude, codex, copilot and their
 * mocks). The discovered Copilot API models are NOT CLI-backed and are not listed. Replace this with
 * a server-sent provider kind once `GET /api/providers` carries one.
 */
export const CLI_PROVIDER_IDS: ReadonlySet<string> = new Set([
  'claude',
  'codex',
  'copilot',
  'claude-mock',
  'codex-mock',
  'copilot-mock',
]);

/** Whether a conversation bound to `providerId` is backed by a CLI session (no fork in v1). */
export function isCliBackedProvider(providerId: string | null | undefined): boolean {
  if (!providerId) return false;
  return CLI_PROVIDER_IDS.has(providerId.toLowerCase());
}

/** One top-level sidebar row and the forks nested under it. */
export interface SidebarEntry {
  conversation: ConversationSummary;
  forks: ConversationSummary[];
}

function isFork(row: ConversationSummary): boolean {
  return !!row.rootThreadId && row.rootThreadId !== row.threadId;
}

/**
 * Nests forks one level under their original, keeping list order.
 *
 * Grouping is by `rootThreadId`, so a fork of a fork sits beside its parent under the same original.
 * A fork whose original is not in `rows` (another page, or unreadable) stays a top-level row. A deleted
 * original is kept only while at least one of its forks is loaded: on its own it is not openable and
 * says nothing.
 */
export function nestForks(rows: readonly ConversationSummary[]): SidebarEntry[] {
  const loaded = new Set(rows.map((row) => row.threadId));
  const entries: SidebarEntry[] = [];
  const byId = new Map<string, SidebarEntry>();

  for (const row of rows) {
    if (isFork(row) && loaded.has(row.rootThreadId!)) continue;
    const entry: SidebarEntry = { conversation: row, forks: [] };
    entries.push(entry);
    byId.set(row.threadId, entry);
  }
  for (const row of rows) {
    if (!isFork(row) || !loaded.has(row.rootThreadId!)) continue;
    const parent = byId.get(row.rootThreadId!);
    if (parent) {
      parent.forks.push(row);
    } else {
      // The original is itself nested somewhere (a malformed chain). Never drop a row.
      entries.push({ conversation: row, forks: [] });
    }
  }
  return entries.filter((entry) => !entry.conversation.deleted || entry.forks.length > 0);
}

/**
 * The provenance text of a fork row: `from msg N` when it forked the original directly, otherwise
 * `from "<parent title>" · msg N`, naming the fork it came from.
 */
export function forkOriginLabel(
  fork: ConversationSummary,
  rows: readonly ConversationSummary[]
): string {
  const origin = fork.forkedFrom;
  const msg = origin?.seq != null ? `msg ${origin.seq}` : null;
  if (!origin || origin.threadId === fork.rootThreadId) {
    return msg ? `from ${msg}` : 'fork';
  }
  const parent = rows.find((row) => row.threadId === origin.threadId);
  const from = parent?.title ? `from "${parent.title}"` : 'from a fork';
  return msg ? `${from} · ${msg}` : from;
}

/**
 * Which display item each branch point renders after.
 *
 * An exact stored-id match wins. Otherwise the point goes after the last item whose `seq` is at or
 * before the point's `afterSeq`: the fork point is often a row the transcript does not draw (a tool
 * result, a usage record), and the bubble that ends there is the one the user sees.
 */
export function resolveBranchAnchors(
  items: readonly DisplayItem[],
  points: readonly BranchPoint[]
): Map<string, BranchPoint[]> {
  const anchors = new Map<string, BranchPoint[]>();
  for (const point of points) {
    let anchorId = items.find((item) => item.persistedId === point.afterMessageId)?.id ?? null;
    if (anchorId === null) {
      for (const item of items) {
        if (item.seq != null && item.seq <= point.afterSeq) anchorId = item.id;
      }
    }
    if (anchorId === null) continue;
    const list = anchors.get(anchorId) ?? [];
    list.push(point);
    anchors.set(anchorId, list);
  }
  return anchors;
}
