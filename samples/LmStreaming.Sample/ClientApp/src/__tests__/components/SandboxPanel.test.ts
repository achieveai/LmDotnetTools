import { describe, it, expect, vi, beforeEach } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { SandboxPanelInfo, BackupRestoreResult, BackupSnapshot } from '@/api/sandboxApi';

const api = vi.hoisted(() => ({
  getSandboxInfo: vi.fn(),
  listBackupSnapshots: vi.fn(),
  getBackupCosts: vi.fn(),
  restoreBackupSnapshot: vi.fn(),
  purgeBackupWorkspace: vi.fn(),
}));
vi.mock('@/api/sandboxApi', () => api);

import SandboxPanel from '@/components/SandboxPanel.vue';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

function infoFor(thread: string): SandboxPanelInfo {
  return {
    workspaceId: `ws-${thread}`,
    gatewayWorkspace: `gw-${thread}`,
    session: { sessionId: `s-${thread}`, image: null, imageDigest: null, imageWarnings: [], home: null },
  };
}

const snapshot: BackupSnapshot = {
  id: 'snap-a-0001',
  time: '2026-10-09T22:00:18.437828115+00:00[UTC]',
  complete: true,
  partial: false,
  orphan: false,
  tags: [],
  files: 1,
  bytes: 10,
};

describe('SandboxPanel', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
  });

  it('a thread switch while a load is pending loads the new thread, and the old answer never shows', async () => {
    const pendingA = deferred<SandboxPanelInfo>();
    api.getSandboxInfo.mockImplementation((thread: string) =>
      thread === 'a' ? pendingA.promise : Promise.resolve(infoFor(thread)),
    );
    const wrapper = mount(SandboxPanel, { props: { threadId: 'a' } });

    await wrapper.setProps({ threadId: 'b' });
    await flushPromises();
    pendingA.resolve(infoFor('a'));
    await flushPromises();

    expect(api.getSandboxInfo.mock.calls.map(([thread]) => thread)).toEqual(['a', 'b']);
    expect(wrapper.find('[data-testid="sandbox-info-workspace"]').text()).toBe('gw-b');
  });

  it('a restore that finishes after a thread switch publishes no notice or refresh into the new thread', async () => {
    api.getSandboxInfo.mockImplementation((thread: string) => Promise.resolve(infoFor(thread)));
    api.listBackupSnapshots.mockResolvedValue([snapshot]);
    const pendingRestore = deferred<BackupRestoreResult>();
    api.restoreBackupSnapshot.mockReturnValue(pendingRestore.promise);
    const wrapper = mount(SandboxPanel, { props: { threadId: 'a' } });
    await flushPromises();
    await wrapper.find('[data-testid="sandbox-snapshots-load"]').trigger('click');
    await flushPromises();

    await wrapper.find(`[data-testid="sandbox-snapshot-restore-${snapshot.id}"]`).trigger('click');
    await wrapper.setProps({ threadId: 'b' });
    await flushPromises();
    pendingRestore.resolve({ workspace: 'gw-a', restoredSnapshotId: snapshot.id, orphanSnapshotId: null });
    await flushPromises();

    expect(api.restoreBackupSnapshot).toHaveBeenCalledWith('a', snapshot.id, expect.anything());
    const restoreSignal = api.restoreBackupSnapshot.mock.calls[0][2] as AbortSignal;
    expect(restoreSignal.aborted).toBe(false);
    expect(wrapper.find('[data-testid="sandbox-panel-notice"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="sandbox-snapshots"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="sandbox-info-workspace"]').text()).toBe('gw-b');
    // No refresh for a thread no longer shown, and none of A's operation lands on B; B's controls are usable.
    expect(api.getSandboxInfo.mock.calls.map(([thread]) => thread)).toEqual(['a', 'b']);
    expect(api.listBackupSnapshots.mock.calls.map(([thread]) => thread)).toEqual(['a']);
    expect(wrapper.find('[data-testid="sandbox-refresh"]').attributes('disabled')).toBeUndefined();
  });

  it('an error from the previous thread is not shown on the new one', async () => {
    const pendingA = deferred<SandboxPanelInfo>();
    api.getSandboxInfo.mockImplementation((thread: string) =>
      thread === 'a' ? pendingA.promise : Promise.resolve(infoFor(thread)),
    );
    let failPurge!: (reason: Error) => void;
    api.purgeBackupWorkspace.mockReturnValue(new Promise((_, reject) => (failPurge = reject)));
    const wrapper = mount(SandboxPanel, { props: { threadId: 'a' } });
    pendingA.resolve(infoFor('a'));
    await flushPromises();
    await wrapper.find('[data-testid="sandbox-purge-confirm"]').setValue('gw-a');

    await wrapper.find('[data-testid="sandbox-purge"]').trigger('click');
    await wrapper.setProps({ threadId: 'b' });
    await flushPromises();
    failPurge(new Error('purge of gw-a failed'));
    await flushPromises();

    expect(api.purgeBackupWorkspace).toHaveBeenCalledWith('a', 'gw-a', expect.anything());
    expect(wrapper.find('[data-testid="sandbox-panel-error"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="sandbox-info-workspace"]').text()).toBe('gw-b');
  });
});
