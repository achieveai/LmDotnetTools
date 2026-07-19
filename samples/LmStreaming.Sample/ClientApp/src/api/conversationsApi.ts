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
 * In-memory run state for a conversation. `isInProgress` is true while a run is still streaming
 * on the backend (the pooled agent keeps running after the client disconnects), which lets a
 * client returning to the conversation decide whether to re-open the WebSocket and resume.
 */
export interface ConversationRunState {
  threadId: string;
  isInProgress: boolean;
  currentRunId?: string | null;
}

/**
 * Fetches whether a conversation currently has an in-flight run, so a client returning to it
 * (switch-back or refresh) can resume the live stream instead of showing a frozen partial.
 */
export async function getRunState(threadId: string): Promise<ConversationRunState> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}/run-state`);
  if (!response.ok) {
    throw new Error(`Failed to fetch run state: ${response.statusText}`);
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

/**
 * Request to reserve a new conversation thread ahead of the first message — locks its
 * workspace/provider/mode as metadata without starting a live agent/sandbox session.
 */
export interface ProvisionConversationRequest {
  workspaceId: string;
  providerId: string;
  modeId: string;
  authWebhookUrl?: string | null;
}

export interface ProvisionConversationResponse {
  threadId: string;
}

export interface SendMessageRequest {
  text: string;
}

/**
 * Carries only the input id the caller polls status by — no run id, since an injected send may
 * fold into a run already in flight.
 */
export interface SendMessageResponse {
  inputId: string;
  queued: boolean;
}

/**
 * The 6 top-level states a headless REST caller can observe for a run (mirrors the backend's
 * `ConversationRunStatus` enum, serialized as its plain member name).
 */
export type ConversationRunStatusValue =
  | 'NotStarted'
  | 'InProgress'
  | 'Completed'
  | 'Errored'
  | 'Interrupted'
  | 'Cancelled';

/**
 * Resolved status of a conversation run, polled by either `runId` or `inputId`.
 */
export interface ConversationStatusResponse {
  threadId: string;
  runId?: string | null;
  status: ConversationRunStatusValue;
  response?: unknown;
}

/**
 * Thrown when a conversation REST call returns a non-ok response whose body carries a `code`
 * field (e.g. `unknown_thread`, `unknown_inputId`, `unknown_runId`, `queue_full`,
 * `provider_unavailable`). Callers that need to distinguish error cases (e.g. an unknown thread
 * vs. an unknown inputId while polling status) should branch on `code` rather than parsing
 * `message`; `code` is `undefined` if the body wasn't the expected shape.
 */
export class ConversationApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string | undefined
  ) {
    super(message);
    this.name = 'ConversationApiError';
  }
}

async function toConversationApiError(response: Response, fallbackMessage: string): Promise<ConversationApiError> {
  let code: string | undefined;
  let error: string | undefined;
  try {
    const body = await response.json();
    code = typeof body?.code === 'string' ? body.code : undefined;
    error = typeof body?.error === 'string' ? body.error : undefined;
  } catch {
    // Non-JSON error body: fall through with code left undefined.
  }
  return new ConversationApiError(error ?? fallbackMessage, response.status, code);
}

/**
 * Reserves a new conversation thread, locking its workspace/provider/mode without starting a
 * live agent/sandbox session — enables headless REST callers to provision ahead of time.
 */
export async function provisionConversation(
  request: ProvisionConversationRequest
): Promise<ProvisionConversationResponse> {
  const response = await fetch('/api/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to provision conversation: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Queues a message onto a previously-provisioned thread. Non-blocking: resolves as soon as the
 * input is durably recorded as accepted, before it is necessarily drained into a run — poll
 * {@link getConversationStatus} by the returned `inputId` to learn when/how it resolved.
 */
export async function sendConversationMessage(
  threadId: string,
  request: SendMessageRequest
): Promise<SendMessageResponse> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to send message: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Polls a run's resolved status by exactly one of `runId` or `inputId`. A 404 whose body's
 * `code` is `unknown_thread` means the thread itself doesn't exist; `unknown_inputId` /
 * `unknown_runId` means the thread exists but that id was never accepted/assigned on it — two
 * distinct not-found cases callers must not conflate (an unrecognized id on a real conversation
 * is not the same as a stale/deleted conversation).
 */
export async function getConversationStatus(
  threadId: string,
  by: { runId: string } | { inputId: string }
): Promise<ConversationStatusResponse> {
  const query =
    'runId' in by ? `runId=${encodeURIComponent(by.runId)}` : `inputId=${encodeURIComponent(by.inputId)}`;
  const response = await fetch(`/api/conversations/${encodeURIComponent(threadId)}/status?${query}`);
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to fetch conversation status: ${response.statusText}`);
  }
  return response.json();
}
