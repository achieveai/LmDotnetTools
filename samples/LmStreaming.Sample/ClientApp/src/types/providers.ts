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
  /**
   * Optional partition label rendered as a non-selectable section header in the dropdown
   * (e.g. "Copilot · Anthropic", "Copilot · OpenAI"). Entries without a group are rendered
   * as a flat list ahead of the grouped sections.
   */
  group?: string | null;
}

/**
 * Response body for GET /api/providers.
 */
export interface ProvidersResponse {
  providers: ProviderDescriptor[];
  default: string;
}

/**
 * Response body for POST /api/conversations/{threadId}/provider.
 */
export interface SwitchProviderResponse {
  providerId: string;
}
