/**
 * Who a conversation is visible to, mirroring the server's stored `ThreadMetadata.Visibility`.
 *
 * A closed union rather than `string`: the server hand-maps its enum to exactly these three names
 * (`ConversationSummary.ToWireVisibility`), and it throws rather than reporting an unknown state as
 * `private`, so a fourth value never reaches the client silently.
 */
export type ConversationVisibility = 'private' | 'shared' | 'tenant-published';

/**
 * Summary of a conversation for display in the sidebar.
 */
export interface ConversationSummary {
  threadId: string;
  title: string;
  preview?: string;
  lastUpdated: number;
  /**
   * Provider id this thread is locked to. Set on first agent creation; null for legacy
   * threads predating the per-conversation provider feature.
   */
  provider?: string | null;
  /**
   * Workspace id this thread is locked to. Set on first agent creation; null for
   * legacy threads predating the per-conversation workspace feature.
   */
  workspace?: string | null;
  /**
   * Chat mode id this thread is bound to. Seeded on first agent creation and updated on a
   * deliberate mode switch. Lets the client restore the conversation's bound mode after a refresh
   * instead of falling back to the default. Null for legacy threads predating mode persistence.
   */
  mode?: string | null;
  /**
   * Who this conversation is visible to. The server flips it as the first grant is added and the
   * last one is revoked, so it is what a share control reflects.
   *
   * Optional because a host predating the field sends nothing, and silence is not `private`:
   * reading it that way would state a fact about who can see the conversation that nothing on the
   * wire supports. Render nothing instead.
   */
  visibility?: ConversationVisibility;
  /**
   * Whether the VIEWER who fetched this listing may change who the conversation is shared with —
   * the server's own answer, from the authorizer call the share routes are gated on (#482).
   *
   * Viewer-scoped, so it is only ever true of the request that fetched it: two people listing the
   * same conversation get different values, and this must not be cached or handed to a second
   * reader. {@link visibility} is the opposite, a stored property of the conversation, and cannot
   * substitute — an owner and a grantee of one shared conversation both read `shared`.
   *
   * Optional because a host predating the field sends nothing, and silence is not a refusal: the
   * share control keeps offering the mutation and lets the server's answer decide. The server always
   * writes the field when it has one, so an explicit `false` is a real "no".
   */
  canShare?: boolean;
}

/**
 * Request body for updating conversation metadata.
 */
export interface ConversationMetadataUpdate {
  title?: string;
  preview?: string;
}

/**
 * Order the sidebar asks the backend for, and the order it keeps the list in locally.
 *
 * - `lastUsed` — most recently used first, and it *live re-sorts*: touching a conversation during
 *   the session moves it back to the top.
 * - `created` — newest-created first, a stable order that does not shuffle while the user works.
 *
 * Sent verbatim as the `sort` query parameter, so these values are the wire contract.
 */
export type ConversationSortMode = 'lastUsed' | 'created';

/** The sort mode used when nothing has been chosen (or the stored choice is unreadable). */
export const DEFAULT_CONVERSATION_SORT_MODE: ConversationSortMode = 'lastUsed';

/** Every selectable sort mode, in the order the picker lists them, with its user-facing label. */
export const CONVERSATION_SORT_MODES: ReadonlyArray<{
  id: ConversationSortMode;
  label: string;
}> = [
  { id: 'lastUsed', label: 'Last used' },
  { id: 'created', label: 'Recently created' },
];
