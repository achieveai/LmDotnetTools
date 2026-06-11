/**
 * Out-of-band deferred-auth frames pushed by the backend over the chat WebSocket.
 *
 * When a sandboxed agent's outbound request needs an OAuth token the user hasn't provided yet,
 * the backend HOLDS the gateway's webhook call and broadcasts `auth_required`; the client shows
 * a banner whose button opens the same-origin sign-in page (`signinUrl`). Once the sign-in
 * completes (the held webhook resolved with a token), `auth_completed` dismisses the prompt.
 */
export interface AuthRequiredEvent {
  $type: 'auth_required';
  providerId: string;
  /** Same-origin sign-in page to open, e.g. "/auth/github". */
  signinUrl: string;
  reason?: string;
}

export interface AuthCompletedEvent {
  $type: 'auth_completed';
  providerId: string;
}

export type AuthEvent = AuthRequiredEvent | AuthCompletedEvent;

export function isAuthEventPayload(data: string): boolean {
  return data.includes('"$type":"auth_required"') || data.includes('"$type":"auth_completed"');
}
