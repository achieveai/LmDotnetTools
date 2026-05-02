/**
 * Single provider entry returned by GET /api/providers.
 */
export interface ProviderDescriptor {
  id: string;
  displayName: string;
  available: boolean;
}

/**
 * Response body for GET /api/providers.
 */
export interface ProvidersResponse {
  providers: ProviderDescriptor[];
  default: string;
}
