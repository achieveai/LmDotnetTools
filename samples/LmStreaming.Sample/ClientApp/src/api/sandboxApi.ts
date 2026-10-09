import { apiFetch } from '@/api/http';

/** What the conversation's live sandbox was created from, as the gateway acknowledged it. */
export interface SandboxSessionInfo {
  sessionId: string;
  image: string | null;
  imageDigest: string | null;
  imageWarnings: string[];
  home: string | null;
}

export interface SandboxPanelInfo {
  workspaceId: string;
  /** The gateway workspace name backups are kept under. */
  gatewayWorkspace: string;
  /** `null` when the conversation has no live sandbox in this process. */
  session: SandboxSessionInfo | null;
}

export interface BackupSnapshot {
  id: string;
  time: string;
  complete: boolean;
  partial: boolean;
  orphan: boolean;
  tags: string[];
  files: number | null;
  bytes: number | null;
}

export interface BackupRestoreResult {
  workspace: string;
  restoredSnapshotId: string | null;
  orphanSnapshotId: string | null;
}

export interface BackupPurgeResult {
  workspace: string;
  snapshotsDeleted: number;
  sandboxesDeleted: string[];
}

export interface BackupCostGroup {
  group: string | null;
  runs: number;
  failedRuns: number;
  estimatedCostUsd: number;
  storageUsdPerMonth: number;
  bytesUp: number;
  bytesDown: number;
  lastRunAt: string | null;
}

export interface BackupCosts {
  appId: string;
  totalEstimatedCostUsd: number;
  totalStorageUsdPerMonthAdded: number;
  groups: BackupCostGroup[];
}

/** A failed sandbox call, carrying the gateway's machine-readable reason when it gave one. */
export class SandboxApiError extends Error {
  constructor(message: string, readonly status: number, readonly errorCode: string | null) {
    super(message);
    this.name = 'SandboxApiError';
  }
}

function sandboxUrl(threadId: string, path = ''): string {
  return `/api/conversations/${encodeURIComponent(threadId)}/sandbox${path}`;
}

async function call<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(url, init);
  if (!response.ok) {
    let message = `Sandbox request failed (${response.status})`;
    let errorCode: string | null = null;
    try {
      const body = await response.json() as { error?: unknown; errorCode?: unknown };
      if (typeof body.error === 'string') message = body.error;
      if (typeof body.errorCode === 'string') errorCode = body.errorCode;
    } catch {
      // Not JSON (e.g. a bare status): keep the status message.
    }
    throw new SandboxApiError(message, response.status, errorCode);
  }
  return await response.json() as T;
}

function postJson(body: unknown, signal?: AbortSignal): RequestInit {
  return { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal };
}

export function getSandboxInfo(threadId: string, signal?: AbortSignal): Promise<SandboxPanelInfo> {
  return call(sandboxUrl(threadId), { signal });
}

export function listBackupSnapshots(threadId: string, signal?: AbortSignal): Promise<BackupSnapshot[]> {
  return call(sandboxUrl(threadId, '/backup/snapshots'), { signal });
}

export function restoreBackupSnapshot(
  threadId: string, snapshotId: string, signal?: AbortSignal,
): Promise<BackupRestoreResult> {
  return call(sandboxUrl(threadId, '/backup/restore'), postJson({ snapshotId }, signal));
}

export function getBackupCosts(threadId: string, signal?: AbortSignal): Promise<BackupCosts> {
  return call(sandboxUrl(threadId, '/backup/costs'), { signal });
}

export function purgeBackupWorkspace(
  threadId: string, confirmWorkspace: string, signal?: AbortSignal,
): Promise<BackupPurgeResult> {
  return call(sandboxUrl(threadId, '/backup/purge'), postJson({ confirmWorkspace }, signal));
}
