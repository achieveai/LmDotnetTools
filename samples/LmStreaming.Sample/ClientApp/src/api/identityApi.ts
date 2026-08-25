import type { IdentityConfig } from '@/types/identity';

/**
 * Reads the deployment's identity configuration.
 *
 * Uses plain `fetch`, not `apiFetch`: this is the one call that necessarily happens BEFORE a token
 * could exist, since its answer is what tells the client whether and where to get one.
 */
export async function getIdentityConfig(): Promise<IdentityConfig> {
  const response = await fetch('/api/identity/config');
  if (!response.ok) {
    throw new Error(`Failed to read identity config: ${response.statusText}`);
  }
  return response.json();
}
