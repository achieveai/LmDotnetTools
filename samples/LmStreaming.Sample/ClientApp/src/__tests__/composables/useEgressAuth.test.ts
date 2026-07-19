import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { EgressKeyRequest, EgressKeyView } from '@/types/egressAuth';

const apiMocks = vi.hoisted(() => ({
  listEgressKeys: vi.fn(),
  upsertEgressKey: vi.fn(),
  deleteEgressKey: vi.fn(),
}));

vi.mock('@/api/egressAuthApi', () => ({
  listEgressKeys: apiMocks.listEgressKeys,
  upsertEgressKey: apiMocks.upsertEgressKey,
  deleteEgressKey: apiMocks.deleteEgressKey,
}));

import {
  useEgressAuth,
  egressDialogRequest,
  openEgressDialog,
  closeEgressDialog,
} from '@/composables/useEgressAuth';

const keyA: EgressKeyView = {
  id: 'a',
  host: 'a.example.com',
  kind: 'custom-headers',
  headerName: 'Authorization',
  headerNames: ['Authorization'],
  hasClientSecret: false,
  hasRefreshToken: false,
  scopes: [],
};

const keyB: EgressKeyView = {
  id: 'b',
  host: 'b.example.com',
  kind: 'refresh-token',
  headerName: 'Authorization',
  headerNames: [],
  hasClientSecret: true,
  hasRefreshToken: true,
  scopes: ['read', 'write'],
};

describe('useEgressAuth', () => {
  beforeEach(() => {
    apiMocks.listEgressKeys.mockReset();
    apiMocks.upsertEgressKey.mockReset();
    apiMocks.deleteEgressKey.mockReset();
    // Reset module-singleton state between tests.
    const { egressKeys, error } = useEgressAuth();
    egressKeys.value = [];
    error.value = null;
    closeEgressDialog();
  });

  afterEach(() => vi.restoreAllMocks());

  it('loadEgressKeys populates the reactive list and toggles loading', async () => {
    apiMocks.listEgressKeys.mockResolvedValueOnce([keyA, keyB]);
    const { egressKeys, isLoading, loadEgressKeys } = useEgressAuth();

    const promise = loadEgressKeys();
    expect(isLoading.value).toBe(true);
    await promise;

    expect(isLoading.value).toBe(false);
    expect(egressKeys.value).toEqual([keyA, keyB]);
  });

  it('loadEgressKeys captures the error message without throwing', async () => {
    apiMocks.listEgressKeys.mockRejectedValueOnce(new Error('boom'));
    const { error, loadEgressKeys } = useEgressAuth();

    await loadEgressKeys();

    expect(error.value).toBe('boom');
  });

  it('saveEgressKey upserts then reloads the catalog', async () => {
    apiMocks.upsertEgressKey.mockResolvedValueOnce(keyA);
    apiMocks.listEgressKeys.mockResolvedValueOnce([keyA]);
    const { egressKeys, saveEgressKey } = useEgressAuth();

    const req: EgressKeyRequest = { id: null, host: 'a.example.com', kind: 'custom-headers' };
    const saved = await saveEgressKey(req);

    expect(apiMocks.upsertEgressKey).toHaveBeenCalledWith(req);
    expect(apiMocks.listEgressKeys).toHaveBeenCalledTimes(1);
    expect(saved).toEqual(keyA);
    expect(egressKeys.value).toEqual([keyA]);
  });

  it('saveEgressKey re-throws and sets error on failure', async () => {
    apiMocks.upsertEgressKey.mockRejectedValueOnce(new Error('host is required'));
    const { error, saveEgressKey } = useEgressAuth();

    await expect(
      saveEgressKey({ id: null, host: '', kind: 'custom-headers' })
    ).rejects.toThrow(/host is required/);
    expect(error.value).toBe('host is required');
    expect(apiMocks.listEgressKeys).not.toHaveBeenCalled();
  });

  it('removeEgressKey deletes then reloads the catalog', async () => {
    apiMocks.deleteEgressKey.mockResolvedValueOnce(undefined);
    apiMocks.listEgressKeys.mockResolvedValueOnce([]);
    const { egressKeys, removeEgressKey } = useEgressAuth();

    await removeEgressKey('a');

    expect(apiMocks.deleteEgressKey).toHaveBeenCalledWith('a');
    expect(apiMocks.listEgressKeys).toHaveBeenCalledTimes(1);
    expect(egressKeys.value).toEqual([]);
  });

  it('removeEgressKey re-throws and sets error on failure', async () => {
    apiMocks.deleteEgressKey.mockRejectedValueOnce(new Error('not found'));
    const { error, removeEgressKey } = useEgressAuth();

    await expect(removeEgressKey('missing')).rejects.toThrow(/not found/);
    expect(error.value).toBe('not found');
  });

  it('openEgressDialog sets the dialog request with the prefill host', () => {
    openEgressDialog('api.example.com');

    expect(egressDialogRequest.value.open).toBe(true);
    expect(egressDialogRequest.value.prefillHost).toBe('api.example.com');
  });

  it('openEgressDialog with no host opens with an undefined prefill', () => {
    openEgressDialog();

    expect(egressDialogRequest.value.open).toBe(true);
    expect(egressDialogRequest.value.prefillHost).toBeUndefined();
  });

  it('closeEgressDialog clears the dialog request', () => {
    openEgressDialog('api.example.com');
    closeEgressDialog();

    expect(egressDialogRequest.value.open).toBe(false);
    expect(egressDialogRequest.value.prefillHost).toBeUndefined();
  });
});
