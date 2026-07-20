/**
 * Kind of a pre-defined egress auth key. Mirrors the backend `EgressKeyKind`:
 *  - `custom-headers`      — one or more literal request headers (name/value).
 *  - `refresh-token`       — OAuth2 refresh-token grant.
 *  - `client-credentials`  — OAuth2 client-credentials grant.
 */
export type EgressKeyKind = 'custom-headers' | 'refresh-token' | 'client-credentials';

/**
 * A single literal request header for a `custom-headers` key. On UPDATE, a blank
 * `value` is preserved server-side (the stored secret is kept) — see EgressKeyRequest.
 */
export interface EgressHeaderPair {
  name: string;
  value: string;
}

/**
 * Masked read view returned by `GET /api/auth/egress-keys` (and by upsert). Secret
 * material is never included — only presence flags (`hasClientSecret` /
 * `hasRefreshToken`) and header names (`headerNames`, values omitted).
 */
export interface EgressKeyView {
  id: string;
  host: string;
  kind: EgressKeyKind;
  headerName: string;
  headerNames: string[];
  hasClientSecret: boolean;
  hasRefreshToken: boolean;
  scopes: string[];
}

/**
 * Request body for `POST /api/auth/egress-keys`. `id` null = create, set = update.
 *
 * On UPDATE, secret fields left blank/empty are PRESERVED server-side (the stored
 * value is kept): `clientSecret`, `refreshToken`, and any custom header `value`. The
 * edit form therefore leaves secret inputs blank to keep the current stored secret.
 */
export interface EgressKeyRequest {
  id?: string | null;
  host: string;
  kind: EgressKeyKind;
  headers?: EgressHeaderPair[] | null;
  headerName?: string | null;
  tokenEndpoint?: string | null;
  clientId?: string | null;
  clientSecret?: string | null;
  refreshToken?: string | null;
  scopes?: string[] | null;
}
