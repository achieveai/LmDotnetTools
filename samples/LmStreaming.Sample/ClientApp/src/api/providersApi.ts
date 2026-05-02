import type { ProvidersResponse } from '@/types/providers';

/**
 * Fetches the list of providers available in the current process and the default
 * provider id. The default is used when the user does not explicitly pick one.
 */
export async function listProviders(): Promise<ProvidersResponse> {
  const response = await fetch('/api/providers');
  if (!response.ok) {
    throw new Error(`Failed to fetch providers: ${response.statusText}`);
  }
  return response.json();
}
