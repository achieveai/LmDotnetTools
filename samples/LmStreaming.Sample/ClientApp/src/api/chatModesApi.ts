import type {
  ChatMode,
  ChatModeCreateUpdate,
  ToolDefinition,
  SwitchModeResponse,
} from '@/types/chatMode';
import { apiFetch } from '@/api/http';

// Re-exported, not redeclared: see `@/api/envErrors` for why there is exactly one of these.
export { InvalidEnvError } from '@/api/envErrors';
import { InvalidEnvError } from '@/api/envErrors';

/** Best-effort parse of a JSON error body; returns `{}` when unreadable. */
async function readBody(response: Response): Promise<Record<string, unknown>> {
  try {
    return (await response.json()) as Record<string, unknown>;
  } catch {
    return {};
  }
}

function stringOf(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function stringListOf(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is string => typeof entry === 'string');
}

/**
 * Maps a structured chat-mode failure to its typed error: 400 `invalid_env` →
 * {@link InvalidEnvError}, carrying the offending keys in the message so any caller that renders
 * `error.message` (this is all of them today — see ModeEditor's exposed `showFormError`) shows them.
 * Anything else falls back to the server's `error` text, preserving the pre-existing behaviour.
 */
async function classifyFailure(response: Response, operation: string): Promise<Error> {
  const body = await readBody(response);
  const code = stringOf(body?.code);

  if (response.status === 400 && code === 'invalid_env') {
    const keys = stringListOf(body?.keys);
    const base = stringOf(body?.error) || 'One or more environment variable names are invalid.';
    return new InvalidEnvError(
      keys.length > 0 ? `${base} (${keys.join(', ')})` : base,
      keys,
      stringOf(body?.layer)
    );
  }
  return new Error(stringOf(body?.error) || `Failed to ${operation}: ${response.statusText}`);
}

/**
 * Fetches all chat modes from the backend.
 */
export async function listChatModes(): Promise<ChatMode[]> {
  const response = await apiFetch('/api/chat-modes');
  if (!response.ok) {
    throw new Error(`Failed to fetch chat modes: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Fetches a specific chat mode by ID.
 */
export async function getChatMode(modeId: string): Promise<ChatMode | null> {
  const response = await apiFetch(`/api/chat-modes/${encodeURIComponent(modeId)}`);
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
  const response = await apiFetch('/api/chat-modes', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(mode),
  });
  if (!response.ok) {
    throw await classifyFailure(response, 'create chat mode');
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
  const response = await apiFetch(`/api/chat-modes/${encodeURIComponent(modeId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(mode),
  });
  if (!response.ok) {
    throw await classifyFailure(response, 'update chat mode');
  }
  return response.json();
}

/**
 * Deletes a user-defined chat mode.
 */
export async function deleteChatMode(modeId: string): Promise<void> {
  const response = await apiFetch(`/api/chat-modes/${encodeURIComponent(modeId)}`, {
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
  const response = await apiFetch(`/api/chat-modes/${encodeURIComponent(modeId)}/copies`, {
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
  const response = await apiFetch('/api/tools');
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
  const response = await apiFetch(
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
