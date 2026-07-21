import type { EgressKeyView, EgressKeyRequest } from '@/types/egressAuth';

/**
 * Fetches all pre-defined egress keys (masked) from the backend.
 */
export async function listEgressKeys(): Promise<EgressKeyView[]> {
  const response = await fetch('/api/auth/egress-keys');
  if (!response.ok) {
    throw new Error(`Failed to fetch egress keys: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Creates (id null) or updates (id set) an egress key. Surfaces the server's
 * `error` body on a non-ok response.
 */
export async function upsertEgressKey(req: EgressKeyRequest): Promise<EgressKeyView> {
  const response = await fetch('/api/auth/egress-keys', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to save egress key: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Deletes an egress key by id. Surfaces the server's `error` body on a non-ok
 * response (e.g. 404 for an unknown id).
 */
export async function deleteEgressKey(id: string): Promise<void> {
  const response = await fetch(`/api/auth/egress-keys/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || `Failed to delete egress key: ${response.statusText}`);
  }
}
