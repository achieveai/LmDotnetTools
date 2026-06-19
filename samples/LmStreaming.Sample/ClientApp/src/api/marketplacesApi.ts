import type { MarketplaceCatalog } from '@/types/marketplace';

/** Raised when the backend reports the sandbox gateway is offline (HTTP 503). */
export class MarketplaceGatewayUnavailableError extends Error {
  constructor(message = 'The sandbox gateway is unavailable.') {
    super(message);
    this.name = 'MarketplaceGatewayUnavailableError';
  }
}

/**
 * Fetches the marketplace catalog (available plugins/skills/agents) from the backend proxy.
 *
 * @param marketplaces Optional subset of marketplace aliases to request; when omitted the gateway
 *   applies its own default set.
 * @throws {MarketplaceGatewayUnavailableError} when the gateway is offline (503) — callers should
 *   render an "offline" state rather than treating it as a hard failure.
 */
export async function listMarketplaces(marketplaces?: string[]): Promise<MarketplaceCatalog> {
  const query =
    marketplaces && marketplaces.length > 0
      ? `?marketplaces=${encodeURIComponent(marketplaces.join(','))}`
      : '';
  const response = await fetch(`/api/marketplaces${query}`);

  if (response.status === 503) {
    throw new MarketplaceGatewayUnavailableError();
  }
  if (!response.ok) {
    // Surface the status code (statusText is often empty under HTTP/2) and the server's structured
    // `detail` when present, so the error state is actionable rather than a bare message.
    const detail = await readErrorDetail(response);
    throw new Error(
      `Failed to fetch marketplaces (${response.status})${detail ? `: ${detail}` : ''}`
    );
  }
  return response.json();
}

/** Best-effort extraction of the backend's `{ error, detail }` body; returns null if unreadable. */
async function readErrorDetail(response: Response): Promise<string | null> {
  try {
    const body = await response.json();
    return body?.detail ?? body?.error ?? null;
  } catch {
    return null;
  }
}
