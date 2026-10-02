import { ref, type Ref } from 'vue';
import type { ConversationSummary } from '@/types/conversations';
import {
  forkConversation,
  loadBranches,
  ConversationApiError,
  type BranchPoint,
  type ForkAnchor,
} from '@/api/conversationsApi';
import { logger } from '@/utils';

const log = logger.forComponent('useConversationFork');

/** What the fork flow needs from the layout that owns the conversation list and the composer. */
export interface ConversationForkDeps {
  /** The conversation on screen, which is the one forked. */
  currentThreadId: Ref<string | null>;
  /** The loaded sidebar rows; the fork's bindings are copied from its source row. */
  conversations: Ref<ConversationSummary[]>;
  addOrUpdateConversation: (summary: ConversationSummary) => void;
  /** Opens a conversation exactly as selecting it in the sidebar does. */
  openConversation: (threadId: string) => Promise<void>;
  /** Puts text in the composer, for "Edit in fork". */
  setComposerText: (text: string) => void;
}

/** User-facing text for the server's fork refusals. */
function describeForkError(e: unknown): string {
  if (e instanceof ConversationApiError) {
    switch (e.code) {
      case 'turn_in_progress':
        return 'Cannot fork while a reply is still running.';
      case 'pending_delayed_result':
        return 'Cannot fork yet: a tool result is still pending in this part of the conversation.';
      case 'cli_provider_unsupported':
        return 'Forking is not supported for this provider.';
      default:
        return e.message || 'Failed to fork the conversation.';
    }
  }
  return e instanceof Error ? e.message : 'Failed to fork the conversation.';
}

/**
 * Fork a conversation and open the fork; and hold the branch points of the conversation on screen.
 *
 * On success the fork is added to the sidebar with its source's bindings (the server copies them too),
 * then opened through the same path as a sidebar click, so it loads, resumes and restores bindings
 * like any other conversation. "Edit in fork" then puts the old user text in the composer.
 */
export function useConversationFork(deps: ConversationForkDeps) {
  const isForking = ref(false);
  const forkError = ref<string | null>(null);
  const branchPoints = ref<BranchPoint[]>([]);

  async function fork(anchor: ForkAnchor): Promise<void> {
    const sourceId = deps.currentThreadId.value;
    if (!sourceId || isForking.value) return;
    isForking.value = true;
    forkError.value = null;
    try {
      const created = await forkConversation(sourceId, anchor);
      const source = deps.conversations.value.find((row) => row.threadId === sourceId);
      deps.addOrUpdateConversation({
        threadId: created.threadId,
        title: created.title,
        lastUpdated: Date.now(),
        provider: source?.provider ?? null,
        workspace: source?.workspace ?? null,
        mode: source?.mode ?? null,
        forkedFrom: created.forkedFrom,
        rootThreadId: created.rootThreadId,
        deleted: false,
      });
      await deps.openConversation(created.threadId);
      if (created.prefillText) {
        deps.setComposerText(created.prefillText);
      }
    } catch (e) {
      forkError.value = describeForkError(e);
      log.warn('Fork failed', { threadId: sourceId, error: String(e) });
    } finally {
      isForking.value = false;
    }
  }

  /**
   * Reads the branch points of `threadId`. Best-effort: a failure (an older host without the route,
   * a transient error) shows no switcher rather than an error. A response for a conversation the user
   * has already left is dropped.
   */
  async function refreshBranches(threadId: string | null): Promise<void> {
    branchPoints.value = [];
    if (!threadId) return;
    try {
      const { points } = await loadBranches(threadId);
      if (deps.currentThreadId.value === threadId) {
        branchPoints.value = points ?? [];
      }
    } catch (e) {
      log.debug('Could not load branches', { threadId, error: String(e) });
    }
  }

  return { fork, isForking, forkError, branchPoints, refreshBranches };
}
