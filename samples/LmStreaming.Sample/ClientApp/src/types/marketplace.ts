/**
 * Types for the marketplace catalog served by `GET /api/marketplaces` (the backend proxy over the
 * sandbox gateway's `/api/v1/marketplaces/preview`). Mirrors the gateway's `CatalogResponse`.
 */

export interface CatalogSkill {
  name: string;
  description: string;
  plugin: string;
  marketplace: string;
  /** Sandbox-relative path, e.g. `/marketplaces/<alias>/<plugin>/skills/<name>/`. */
  path: string;
}

export interface CatalogAgent {
  name: string;
  description: string;
  plugin: string;
  marketplace: string;
  /** Sandbox-relative path to the agent markdown, e.g. `.../agents/<name>.md`. */
  path: string;
}

export interface CatalogPlugin {
  name: string;
  /** Plugins without a declared version report `null`, not an empty string. */
  version: string | null;
  description: string;
  skills: CatalogSkill[];
  agents: CatalogAgent[];
}

export interface CatalogMarketplace {
  alias: string;
  /** Non-null when the gateway failed to load this marketplace; `plugins` is then empty. */
  error: string | null;
  plugins: CatalogPlugin[];
}

export interface MarketplaceCatalog {
  /** Aliases the gateway actually resolved for the request. */
  selected: string[];
  marketplaces: CatalogMarketplace[];
}
