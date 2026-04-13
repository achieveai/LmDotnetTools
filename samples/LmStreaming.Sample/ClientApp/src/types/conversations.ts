/**
 * Summary of a conversation for display in the sidebar.
 */
export interface ConversationSummary {
  threadId: string;
  title: string;
  preview?: string;
  lastUpdated: number;
}

/**
 * Request body for updating conversation metadata.
 */
export interface ConversationMetadataUpdate {
  title?: string;
  preview?: string;
}
