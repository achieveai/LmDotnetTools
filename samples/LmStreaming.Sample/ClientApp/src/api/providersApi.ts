import type { ProvidersResponse, SwitchProviderResponse } from '@/types/providers';

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

/**
 * Switches a conversation's provider. Allowed only while the conversation is idle: the backend
 * answers 409 while a run streams and 503 when the target provider is unavailable.
 */
export async function switchConversationProvider(
  threadId: string,
  providerId: string
): Promise<SwitchProviderResponse> {
  const response = await fetch(
    `/api/conversations/${encodeURIComponent(threadId)}/provider`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ providerId }),
    }
  );
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to switch provider: ${response.statusText}`);
  }
  return response.json();
}
