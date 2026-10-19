import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  listBackupSnapshots,
  purgeBackupWorkspace,
  restoreBackupSnapshot,
  SandboxApiError,
} from '@/api/sandboxApi';
import { setOperatorSecret } from '@/api/operatorSecret';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function secretSent(call: unknown[]): string | null {
  return new Headers((call[1] as RequestInit | undefined)?.headers).get('X-Operator-Secret');
}

describe('sandboxApi operator secret', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  it('sends the operator secret on restore and purge, never on a read', async () => {
    setOperatorSecret('s3cret');
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json({}));

    await listBackupSnapshots('t1');
    await restoreBackupSnapshot('t1', 'snap-1');
    await purgeBackupWorkspace('t1', 'ws');

    expect(fetchSpy.mock.calls.map(secretSent)).toEqual([null, 's3cret', 's3cret']);
    expect(new Headers(fetchSpy.mock.calls[1][1]!.headers).get('Content-Type')).toBe('application/json');
  });

  it('sends no header when no secret was entered', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json({}));

    await restoreBackupSnapshot('t1', 'snap-1');

    expect(secretSent(fetchSpy.mock.calls[0])).toBeNull();
  });

  it('explains an operator refusal instead of the bare status', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
      json({ error: 'forbidden', code: 'operator_auth_failed' }, 403)
    );

    const error = await purgeBackupWorkspace('t1', 'ws').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(SandboxApiError);
    expect((error as SandboxApiError).message).toContain('Needs the operator secret');
    expect((error as SandboxApiError).errorCode).toBe('operator_auth_failed');
  });
});
