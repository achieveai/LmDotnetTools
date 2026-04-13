import type {
  ChatMode,
  ChatModeCreateUpdate,
  ToolDefinition,
  SwitchModeResponse,
} from '@/types/chatMode';

/**
 * Fetches all chat modes from the backend.
 */
export async function listChatModes(): Promise<ChatMode[]> {
  const response = await fetch('/api/chat-modes');
  if (!response.ok) {
    throw new Error(`Failed to fetch chat modes: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Fetches a specific chat mode by ID.
 */
export async function getChatMode(modeId: string): Promise<ChatMode | null> {
  const response = await fetch(`/api/chat-modes/${encodeURIComponent(modeId)}`);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Failed to fetch chat mode: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Creates a new user-defined chat mode.
 */
export async function createChatMode(mode: ChatModeCreateUpdate): Promise<ChatMode> {
  const response = await fetch('/api/chat-modes', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(mode),
  });
  if (!response.ok) {
    throw new Error(`Failed to create chat mode: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Updates an existing user-defined chat mode.
 */
export async function updateChatMode(
  modeId: string,
  mode: ChatModeCreateUpdate
): Promise<ChatMode> {
  const response = await fetch(`/api/chat-modes/${encodeURIComponent(modeId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(mode),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to update chat mode: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Deletes a user-defined chat mode.
 */
export async function deleteChatMode(modeId: string): Promise<void> {
  const response = await fetch(`/api/chat-modes/${encodeURIComponent(modeId)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to delete chat mode: ${response.statusText}`);
  }
}

/**
 * Copies a chat mode to create a new user-defined mode.
 */
export async function copyChatMode(modeId: string, newName: string): Promise<ChatMode> {
  const response = await fetch(`/api/chat-modes/${encodeURIComponent(modeId)}/copies`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ newName }),
  });
  if (!response.ok) {
    throw new Error(`Failed to copy chat mode: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Fetches all available tools.
 */
export async function listTools(): Promise<ToolDefinition[]> {
  const response = await fetch('/api/tools');
  if (!response.ok) {
    throw new Error(`Failed to fetch tools: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Switches the mode for a conversation.
 */
export async function switchConversationMode(
  threadId: string,
  modeId: string
): Promise<SwitchModeResponse> {
  const response = await fetch(
    `/api/conversations/${encodeURIComponent(threadId)}/mode`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ modeId }),
    }
  );
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to switch mode: ${response.statusText}`);
  }
  return response.json();
}
