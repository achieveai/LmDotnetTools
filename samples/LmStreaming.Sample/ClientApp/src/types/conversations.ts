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
}

/**
 * Request body for updating conversation metadata.
 */
export interface ConversationMetadataUpdate {
  title?: string;
  preview?: string;
}
