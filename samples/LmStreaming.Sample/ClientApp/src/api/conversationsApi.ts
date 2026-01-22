import type { ConversationSummary, ConversationMetadataUpdate } from '@/types/conversations';

/**
 * Persisted message from backend storage.
 */
export interface PersistedMessage {
  id: string;
  threadId: string;
  runId: string;
  parentRunId?: string | null;
  generationId?: string | null;
  messageOrderIdx?: number | null;
  timestamp: number;
  messageType: string;
  role: string;
  fromAgent?: string | null;
  messageJson: string;
}

/**
 * Fetches the list of conversations from the backend.
 */
export async function listConversations(
  limit = 50,
  offset = 0
): Promise<ConversationSummary[]> {
  const response = await fetch(`/api/conversations?limit=${limit}&offset=${offset}`);
  if (!response.ok) {
    throw new Error(`Failed to fetch conversations: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Loads all messages for a conversation from the backend.
 */
export async function loadConversationMessages(
  threadId: string
): Promise<PersistedMessage[]> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}/messages`);
  if (!response.ok) {
    throw new Error(`Failed to load messages: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Updates conversation metadata (title, preview).
 */
export async function updateConversationMetadata(
  threadId: string,
  update: ConversationMetadataUpdate
): Promise<void> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}/metadata`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(update),
  });
  if (!response.ok) {
    throw new Error(`Failed to update metadata: ${response.statusText}`);
  }
}

/**
 * Deletes a conversation.
 */
export async function deleteConversation(threadId: string): Promise<void> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    throw new Error(`Failed to delete conversation: ${response.statusText}`);
  }
}
