/**
 * Out-of-band deferred-auth frames pushed by the backend over the chat WebSocket.
 *
 * When a sandboxed agent's outbound request needs an OAuth token the user hasn't provided yet,
 * the backend HOLDS the gateway's webhook call and broadcasts `auth_required`; the client shows
 * a banner whose button opens the same-origin sign-in page (`signinUrl`). The hold resolves with
 * exactly one terminal frame that dismisses the prompt: `auth_completed` (a token landed) or
 * `auth_denied` (the hold timed out, sign-in failed, or deferral was disabled).
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

export interface AuthDeniedEvent {
  $type: 'auth_denied';
  providerId: string;
  reason?: string;
}

export type AuthEvent = AuthRequiredEvent | AuthCompletedEvent | AuthDeniedEvent;

/**
 * The provider-id namespace IS the kind discriminator: a pre-defined egress key (issue #210) uses a
 * `predefined-<id>` provider id (see the backend PredefinedKeyRegistry), whereas an OAuth provider is
 * a bare id like `github`. A failing pre-defined key routes the banner CTA to the Egress Auth dialog
 * (to re-enter the credential) instead of opening an OAuth sign-in page.
 */
export function isPredefinedKeyProvider(providerId: string): boolean {
  return providerId.startsWith('predefined-');
}

export function isAuthEventPayload(data: string): boolean {
  return (
    data.includes('"$type":"auth_required"') ||
    data.includes('"$type":"auth_completed"') ||
    data.includes('"$type":"auth_denied"')
  );
}
