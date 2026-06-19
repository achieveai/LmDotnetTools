/**
 * Mock for the sandbox gateway's marketplace catalog endpoint
 * (`GET /api/v1/marketplaces/preview`). The JSON beside this module is a real, captured
 * gateway response trimmed to a representative subset (3 marketplaces; plugins covering
 * skills+agents, skills-only, and a null `version`) so UI unit tests and Playwright specs
 * can render the catalog deterministically without a live gateway.
 *
 * To refresh the capture:
 *   curl -s http://localhost:3000/api/v1/marketplaces/preview > full.json
 *   # then carve a subset (see git history of this fixture for the jq recipe).
 *
 * The TS shapes mirror the gateway's `CatalogResponse` so a future typed catalog client can
 * lift them into `src/types/` unchanged.
 */
import type { MarketplaceCatalog } from '@/types/marketplace';
import rawCatalog from './marketplace-preview.subset.json';

export type { MarketplaceCatalog } from '@/types/marketplace';

/** The captured-and-trimmed catalog, typed against the gateway contract. */
export const marketplacePreviewSubset: MarketplaceCatalog = rawCatalog as MarketplaceCatalog;

/** Pre-serialized body for Playwright `route.fulfill({ body })` or any string-based mock. */
export const marketplacePreviewJson: string = JSON.stringify(marketplacePreviewSubset);

/**
 * The gateway's 400 envelope for an unknown marketplace alias (shared by the create and preview
 * endpoints). Use it to exercise the catalog error path in UI tests.
 */
export const marketplaceUnknownAliasError = {
  error: 'unknown marketplace alias(es): nope',
  code: 400,
  unknown: ['nope'],
  available: ['ClaudePlugins', 'claude-plugins-official', 'superpowers'],
} as const;

/**
 * Installs a `fetch` stub that answers any request with the captured catalog (or a caller-supplied
 * one / status). Returns the spy so a test can assert call args, and a `restore()` to undo it.
 * Generic by design — it does not assume a particular proxy URL.
 *
 * @example
 *   const { restore } = installMarketplacePreviewFetchMock();           // 200 + subset
 *   const { restore } = installMarketplacePreviewFetchMock({ status: 503 }); // gateway offline
 */
export function installMarketplacePreviewFetchMock(
  options: { catalog?: MarketplaceCatalog; status?: number; body?: unknown } = {}
): { fetchSpy: ReturnType<typeof vi.fn>; restore: () => void } {
  const { catalog = marketplacePreviewSubset, status = 200, body } = options;
  const payload = body ?? catalog;
  const original = globalThis.fetch;
  const fetchSpy = vi.fn(async () =>
    new Response(JSON.stringify(payload), {
      status,
      headers: { 'Content-Type': 'application/json' },
    })
  );
  globalThis.fetch = fetchSpy as unknown as typeof fetch;
  return { fetchSpy, restore: () => (globalThis.fetch = original) };
}
