/**
 * Wire types for the named conversation-sharing routes (`/api/conversations/{threadId}/shares`).
 *
 * Mirrors `LmStreaming.Sample/Models/ConversationShareDtos.cs`, which serializes camelCase.
 */

/**
 * What a grant confers. The server refuses anything else with `invalid_role` rather than
 * defaulting it, so this stays a closed union rather than `string`.
 */
export type ShareRole = 'viewer' | 'editor';

/** One grant on a conversation, as returned by `GET`/`POST .../shares`. */
export interface ConversationShare {
  threadId: string;
  /** The namespaced `{tenant-id}:{object-id}` of the person the grant names — never a UPN. */
  subjectId: string;
  role: ShareRole;
  /** Who created the grant. */
  grantedBy: string;
  grantedAtUnixMs: number;
  /** When the grant lapses, Unix milliseconds, or null when it does not expire. */
  expiresAtUnixMs?: number | null;
}

/** Body of `POST .../shares`. */
export interface ConversationShareRequest {
  subjectId: string;
  role: ShareRole;
  expiresAtUnixMs?: number | null;
}
