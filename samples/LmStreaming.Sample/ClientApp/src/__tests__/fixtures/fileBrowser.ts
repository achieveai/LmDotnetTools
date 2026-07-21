/**
 * Test doubles for the File Browser REST contract
 * (`/api/conversations/{threadId}/files`). Mirrors the fixture style of `marketplacePreview.ts`:
 * a representative listing plus small helpers to build `Response`s and a `fetch` spy.
 */
import type { DirectoryListing, NoSessionState, PreviewResult } from '@/types/fileBrowser';

/** A representative listing: a directory, a file, a symlink, and a lossy-named entry. */
export const sampleListing: DirectoryListing = {
  workspaceId: 'ws-1',
  path: '',
  entries: [
    { name: 'src', type: 'directory', size: null, nameLossy: false },
    { name: 'readme.md', type: 'file', size: 1234, nameLossy: false },
    { name: 'link', type: 'symlink', size: null, nameLossy: false },
    { name: 'lossy.dat', type: 'file', size: 10, nameLossy: true },
  ],
  moreCount: 0,
};

export const noSessionState: NoSessionState = {
  state: 'no_session_yet',
  workspaceId: null,
};

export const textPreview: PreviewResult = {
  previewable: true,
  text: 'line one\nline two',
  lineCount: 2,
};

export const binaryPreview: PreviewResult = {
  previewable: false,
  reason: 'binary',
};

/** Builds a JSON `Response` with the given status. */
export function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Builds a 204 No Content `Response`. */
export function noContentResponse(): Response {
  return new Response(null, { status: 204 });
}
