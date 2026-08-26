import type { ConversationShare, ConversationShareRequest } from '@/types/shares';
import { apiFetch } from '@/api/http';
import { ConversationApiError, toConversationApiError } from '@/api/conversationsApi';

/**
 * Client for the named conversation-sharing routes shipped in #302.
 *
 * Every refusal is raised as a {@link ConversationApiError} carrying the server's `code`, because
 * the codes are the whole contract here: `unknown_thread`, `grantee_may_not_reshare`,
 * `admin_may_not_reshare`, `app_cannot_share`, `publication_supersedes_sharing`,
 * `tenant_member_read_only`, `invalid_role`, `invalid_subject`, `sharing_unavailable`. A caller
 * that only saw the message could not tell "sharing is off on this host" from "you were shared
 * this conversation and may not re-share it", and those want opposite things said to the user.
 */

/** Path of the share collection for one conversation. */
function sharesUrl(threadId: string): string {
  return `/api/conversations/${encodeURIComponent(threadId)}/shares`;
}

/**
 * Lists the grants on a conversation. Requires only `Read`, so a grantee can read the roster they
 * are on — which is why the read and the mutations refuse for different reasons.
 */
export async function listShares(threadId: string): Promise<ConversationShare[]> {
  const response = await apiFetch(sharesUrl(threadId));
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to list shares: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Shares a conversation with one named subject. Idempotent server-side: re-sharing with a
 * different role replaces the grant rather than adding a second one.
 */
export async function addShare(
  threadId: string,
  request: ConversationShareRequest
): Promise<ConversationShare> {
  const response = await apiFetch(sharesUrl(threadId), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to share conversation: ${response.statusText}`);
  }
  return response.json();
}

/**
 * Revokes one subject's grant.
 *
 * The server answers `204` whether or not a row was removed — a `404` for "there was no grant"
 * would let anyone entitled to revoke enumerate who a conversation is shared with. So there is no
 * "not found" outcome to report here, only success or a refusal of the `Share` action itself.
 */
export async function removeShare(threadId: string, subjectId: string): Promise<void> {
  const response = await apiFetch(`${sharesUrl(threadId)}/${encodeURIComponent(subjectId)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    throw await toConversationApiError(response, `Failed to revoke share: ${response.statusText}`);
  }
}

export { ConversationApiError };
