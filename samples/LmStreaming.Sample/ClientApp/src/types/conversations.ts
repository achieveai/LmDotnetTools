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
}

/**
 * Request body for updating conversation metadata.
 */
export interface ConversationMetadataUpdate {
  title?: string;
  preview?: string;
}
