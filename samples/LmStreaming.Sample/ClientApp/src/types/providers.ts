/**
 * Single provider entry returned by GET /api/providers.
 */
export interface ProviderDescriptor {
  id: string;
  displayName: string;
  available: boolean;
  /**
   * Optional caveat from the server when a provider is technically available but has a
   * documented limitation that prevents end-to-end use today (typically links a follow-up
   * issue). Rendered next to the entry in the dropdown.
   */
  knownLimitation?: string | null;
}

/**
 * Response body for GET /api/providers.
 */
export interface ProvidersResponse {
  providers: ProviderDescriptor[];
  default: string;
}
