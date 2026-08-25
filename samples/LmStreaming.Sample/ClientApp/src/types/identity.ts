/** What `GET /api/identity/config` tells the client before it can sign in. */
export interface IdentityConfig {
  /** Whether the deployment requires an authenticated principal on `/api` routes. */
  enforce: boolean;
  /** Entra app registration the SPA authenticates against, or null when unconfigured. */
  clientId: string | null;
  /** Entra authority the SPA acquires tokens from, or null when unconfigured. */
  authority: string | null;
  /** Scopes the SPA must request for its access token. */
  scopes: string[];
}

/**
 * A refusal the server answers with when the token is genuine but the organisation behind it may
 * not be here. Distinguished from "not signed in" because signing in again cannot fix either one.
 */
export type IdentityRefusalCode = 'tenant_not_provisioned' | 'tenant_suspended';

/** Header the server repeats the refusal code in, so a client need not read the body. */
export const IDENTITY_REFUSAL_HEADER = 'x-identity-refusal';

/** Every refusal code the client knows how to explain. */
export const IDENTITY_REFUSAL_CODES: readonly IdentityRefusalCode[] = [
  'tenant_not_provisioned',
  'tenant_suspended',
];

/** Narrows an arbitrary header value to a refusal code this client can render a screen for. */
export function asRefusalCode(value: string | null | undefined): IdentityRefusalCode | null {
  return IDENTITY_REFUSAL_CODES.includes(value as IdentityRefusalCode)
    ? (value as IdentityRefusalCode)
    : null;
}

/**
 * What the app shell renders.
 *
 * `disabled` and `signed-in` both mean "show the app". They are kept apart because they are
 * reached by completely different routes — one skipped authentication, the other completed it —
 * and collapsing them would make a failure to sign in indistinguishable from a deployment that
 * never asked for one.
 *
 * `expired` is likewise kept apart from `rejected`. Both arrive on a session that WAS working, but
 * the remedies are opposites: signing in again fixes an expiry and is precisely the thing that
 * must not be offered on a refusal.
 */
export type IdentityStatus =
  | 'loading'
  | 'disabled'
  | 'signing-in'
  | 'signed-in'
  | 'rejected'
  | 'expired'
  | 'error';
