import { apiFetch } from '@/api/http';
import { operatorRefusalMessage, withOperatorSecret } from '@/api/operatorSecret';

/** One git marketplace this app registered with the sandbox gateway (gateway ADR 0041). */
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

/** Throws with the gateway's error code (e.g. `ref_not_found`) so the UI can say why. */
async function throwIfFailed(response: Response, action: string): Promise<void> {
  if (response.ok) return;
  let body: { error?: unknown; code?: unknown } | null = null;
  try {
    body = await response.json();
  } catch {
    // Unreadable body: the status alone is all there is.
  }
  const refusal = operatorRefusalMessage(body?.code);
  if (refusal) throw new Error(`Could not ${action}: ${refusal}`);
  const code = typeof body?.error === 'string' ? body.error : null;
  throw new Error(`Could not ${action} (${response.status}${code ? ` ${code}` : ''})`);
}
