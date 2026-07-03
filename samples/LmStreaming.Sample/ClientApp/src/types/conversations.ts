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
}

/**
 * Request body for updating conversation metadata.
 */
export interface ConversationMetadataUpdate {
  title?: string;
  preview?: string;
}
