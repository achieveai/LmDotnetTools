import { ref } from 'vue';
import type { EgressKeyView, EgressKeyRequest } from '@/types/egressAuth';
import {
  listEgressKeys,
  upsertEgressKey as apiUpsertEgressKey,
  deleteEgressKey as apiDeleteEgressKey,
} from '@/api/egressAuthApi';

// Module-singleton reactive state: every consumer (the modal, the header button,
// a future auth banner) shares the same key list + loading/error + dialog request.
const egressKeys = ref<EgressKeyView[]>([]);
const isLoading = ref(false);
const error = ref<string | null>(null);

/**
 * Programmatic open request for the egress-auth dialog. A future auth banner can
 * call `openEgressDialog(host)` to pop the editor prefilled to a host without
 * threading props through ChatLayout.
 */
export interface EgressDialogRequest {
  open: boolean;
  prefillHost?: string;
}

export const egressDialogRequest = ref<EgressDialogRequest>({ open: false });

/**
 * Opens the egress-auth dialog. When `prefillHost` is supplied the editor starts in
 * create mode with the host prefilled.
 */
export function openEgressDialog(prefillHost?: string): void {
  egressDialogRequest.value = { open: true, prefillHost };
}

/**
 * Clears the programmatic open request (called when the dialog closes).
 */
export function closeEgressDialog(): void {
  egressDialogRequest.value = { open: false };
}

/**
 * Composable exposing the pre-defined egress-key catalog and CRUD actions. State is
 * module-singleton (see above) so it survives re-mounts of the modal.
 */
export function useEgressAuth() {
  /**
   * Loads the egress-key catalog (masked). Never throws — surfaces failures via
   * `error` so the modal can render them.
   */
  async function loadEgressKeys(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      egressKeys.value = await listEgressKeys();
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load egress keys';
      console.error('Failed to load egress keys:', e);
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * Creates or updates an egress key, then reloads the catalog. Re-throws so the
   * caller (the editor) can surface the server validation `error` inline.
   */
  async function saveEgressKey(req: EgressKeyRequest): Promise<EgressKeyView> {
    try {
      const saved = await apiUpsertEgressKey(req);
      await loadEgressKeys();
      return saved;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to save egress key';
      console.error('Failed to save egress key:', e);
      throw e;
    }
  }

  /**
   * Deletes an egress key, then reloads the catalog. Re-throws so the caller can
   * surface the failure.
   */
  async function removeEgressKey(id: string): Promise<void> {
    try {
      await apiDeleteEgressKey(id);
      await loadEgressKeys();
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to delete egress key';
      console.error('Failed to delete egress key:', e);
      throw e;
    }
  }

  return {
    egressKeys,
    isLoading,
    error,
    loadEgressKeys,
    saveEgressKey,
    removeEgressKey,
    egressDialogRequest,
    openEgressDialog,
    closeEgressDialog,
  };
}
