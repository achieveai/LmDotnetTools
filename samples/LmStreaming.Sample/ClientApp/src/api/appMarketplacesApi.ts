import { apiFetch } from '@/api/http';
import { operatorRefusalMessage, withOperatorSecret } from '@/api/operatorSecret';

/** One git marketplace this app registered with the sandbox gateway (gateway ADR 0045). */
export interface AppMarketplace {
  appId: string;
  alias: string;
  url: string;
  repoKey: string;
  /** `latest` or the pinned tag, branch or commit. */
  ref: string;
  /** The commit new sandboxes mount. */
  resolvedSha: string;
  createdAt: string;
  updatedAt: string;
  /** The last failed background sync; null after a good one. */
  lastError: string | null;
}

const BASE = '/api/marketplaces/app';

/** Lists this app's registered git marketplaces. */
export async function listAppMarketplaces(): Promise<AppMarketplace[]> {
  const response = await apiFetch(BASE);
  await throwIfFailed(response, 'list git marketplaces');
  return response.json();
}

/** Registers (or re-points) `alias`. A blank `ref` means `latest`. The gateway clones first, so this can take a while. */
export async function registerAppMarketplace(
  alias: string,
  url: string,
  ref?: string
): Promise<AppMarketplace> {
  const response = await apiFetch(
    `${BASE}/${encodeURIComponent(alias)}`,
    withOperatorSecret({
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url, ref: ref?.trim() ? ref.trim() : null }),
    })
  );
  await throwIfFailed(response, `register '${alias}'`);
  return response.json();
}

/** Fetches `alias` now; returns the refreshed registration. */
export async function syncAppMarketplace(alias: string): Promise<AppMarketplace> {
  const response = await apiFetch(
    `${BASE}/${encodeURIComponent(alias)}/sync`,
    withOperatorSecret({ method: 'POST' })
  );
  await throwIfFailed(response, `sync '${alias}'`);
  return response.json();
}

/** Unregisters `alias`. */
export async function removeAppMarketplace(alias: string): Promise<void> {
  const response = await apiFetch(
    `${BASE}/${encodeURIComponent(alias)}`,
    withOperatorSecret({ method: 'DELETE' })
  );
  await throwIfFailed(response, `remove '${alias}'`);
}

/**
 * What each gateway refusal means. The SDK never relays the gateway's free-text message (it can echo
 * request input), so the server's `detail` only repeats the code; these say what to fix instead.
 */
const GATEWAY_HINTS: Record<string, string> = {
  marketplaces_disabled:
    'git marketplaces are off. Set SandboxGateway:MarketplaceStorePath (or MARKETPLACE_STORE_PATH on an ' +
    'external gateway) and restart.',
  invalid_alias: 'an alias is 1-64 characters of A-Z, a-z, 0-9, - and _.',
  invalid_url: 'use an https:// git repository URL.',
  host_not_allowed: 'the gateway only clones from its MARKETPLACE_ALLOWED_HOSTS (default github.com).',
  invalid_ref: 'not a valid tag, branch or commit name.',
  ref_not_found: 'no such tag, branch or commit on the remote.',
  alias_taken: 'an operator marketplace (PluginsDirs) already uses this alias.',
  unsafe_checkout: 'the repository contains a symbolic link, which the gateway refuses.',
  no_default_branch: 'the remote has no default branch for latest to follow. Pin a ref.',
  git_failed: 'the clone or fetch failed or timed out (network, or a private repository).',
};

/** Throws with the gateway's error code (e.g. `ref_not_found`) and what it means, so the UI can say why. */
async function throwIfFailed(response: Response, action: string): Promise<void> {
  if (response.ok) return;
  let body: { error?: unknown; code?: unknown; detail?: unknown } | null = null;
  try {
    body = await response.json();
  } catch {
    // Unreadable body: the status alone is all there is.
  }
  const refusal = operatorRefusalMessage(body?.code);
  if (refusal) throw new Error(`Could not ${action}: ${refusal}`);
  const code = typeof body?.error === 'string' ? body.error : null;
  // Own keys only: a code such as `constructor` must not resolve to an Object.prototype member.
  const hint = code && Object.prototype.hasOwnProperty.call(GATEWAY_HINTS, code) ? GATEWAY_HINTS[code] : null;
  const detail = hint || (typeof body?.detail === 'string' ? body.detail : '');
  throw new Error(`Could not ${action} (${response.status}${code ? ` ${code}` : ''})${detail ? `: ${detail}` : ''}`);
}
