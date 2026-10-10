/**
 * The operator secret some sandbox routes need: git marketplace register/sync/remove and workspace
 * restore/purge change things for every user of the app, so the host gates them behind
 * `LMSTREAMING_IDENTITY_OPERATOR_SECRET`. The operator types it once per tab; it is kept in
 * sessionStorage so it never outlives the tab.
 */

const STORAGE_KEY = 'lmstreaming.operatorSecret';
const HEADER_NAME = 'X-Operator-Secret';

/** The secret this tab presents, or '' when none was entered (or storage is blocked). */
export function getOperatorSecret(): string {
  try {
    return sessionStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

/** Remembers `secret` for this tab; '' forgets it. */
export function setOperatorSecret(secret: string): void {
  try {
    if (secret) sessionStorage.setItem(STORAGE_KEY, secret);
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage blocked: nothing is kept, so the next call goes without the header and the host
    // answers 403, which the UI explains.
  }
}

/** `init` with the operator secret header added when one is set. */
export function withOperatorSecret(init: RequestInit = {}): RequestInit {
  const secret = getOperatorSecret();
  if (!secret) return init;
  const headers = new Headers(init.headers);
  headers.set(HEADER_NAME, secret);
  return { ...init, headers };
}

/** What to tell the user when the host refused an operator route, or null when `code` is not such a refusal. */
export function operatorRefusalMessage(code: unknown): string | null {
  if (code === 'operator_auth_failed') return 'Needs the operator secret: enter it above (it is wrong or missing).';
  if (code === 'operator_secret_not_configured')
    return 'Operator actions are off: set LMSTREAMING_IDENTITY_OPERATOR_SECRET on the host.';
  return null;
}
