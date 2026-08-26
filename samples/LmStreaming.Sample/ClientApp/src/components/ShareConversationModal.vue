<script setup lang="ts">
/**
 * The share control for one conversation: the roster, plus adding and revoking a grant.
 *
 * **Why the mutation controls are offered before we know they are allowed.** No client-visible DTO
 * on these routes carries the conversation's visibility or an owner-vs-grantee flag, so there is
 * nothing to compute a permission from up front. Reading the roster needs only `Read`, which a
 * grantee has; changing it needs `Share`, which only the owner has. The only honest sequence is
 * therefore: offer, attempt, and render the server's refusal — then withdraw the control, so a
 * grantee is not left with a button that will fail every time they press it.
 *
 * `unknown_thread` is deliberately NOT reported as an error. The server answers a refused read and
 * a never-minted id with the same 404 body, so a message that read "you do not have permission"
 * would turn this modal into the existence oracle that 404 exists to close.
 */
import { computed, onMounted, ref, watch } from 'vue';
import BaseModal from './BaseModal.vue';
import type { ConversationShare, ShareRole } from '@/types/shares';
import { listShares, addShare, removeShare, ConversationApiError } from '@/api/sharesApi';

const props = defineProps<{ threadId: string }>();

defineEmits<{ close: [] }>();

/**
 * What each refusal code means to a person. Written per code rather than falling back to the
 * server's `error` string because the wire text is written for an operator reading a log, and
 * because three of these ("you were shared this", "you are an admin", "you are an app") are the
 * same 403 with the same phrasing and different consequences.
 */
const REFUSAL_MESSAGES: Record<string, string> = {
  unknown_thread: 'This conversation is not available.',
  grantee_may_not_reshare:
    'This conversation was shared with you. Only its owner can change who it is shared with.',
  admin_may_not_reshare:
    'An administrator cannot share a conversation on its owner’s behalf. Only its owner can.',
  app_cannot_share:
    'An application identity cannot share a conversation. Sign in as a person to share it.',
  publication_supersedes_sharing:
    'This conversation is published to the whole tenant, which replaces sharing it with named people.',
  tenant_member_read_only:
    'You can read this conversation, but not change who it is shared with.',
  invalid_role: 'Choose either viewer or editor.',
  invalid_subject:
    'Enter the subject as the {tenant-id}:{object-id} pair of the person to share with.',
  sharing_unavailable: 'Sharing is not enabled on this host.',
  authentication_required: 'Sign in to manage sharing.',
};

/**
 * Codes that say this caller will never be able to change this conversation's roster. The control
 * is withdrawn rather than disabled: a disabled button still claims the action is theirs.
 */
const WITHDRAW_CODES = new Set([
  'grantee_may_not_reshare',
  'admin_may_not_reshare',
  'app_cannot_share',
  'publication_supersedes_sharing',
  'tenant_member_read_only',
]);

/**
 * Codes that say sharing is off for everyone on this host, not that this caller lacks the right.
 * The control stays on screen and disabled, because the answer is about the deployment and the
 * user should be able to see what would be available if it were enabled.
 */
const DISABLE_CODES = new Set(['sharing_unavailable']);

const shares = ref<ConversationShare[]>([]);
const isLoading = ref(false);
const busy = ref(false);
const refusal = ref<string | null>(null);
/** True once the thread answered 404: nothing to list, nothing to offer, nothing to imply. */
const unavailable = ref(false);
/** Latched by {@link WITHDRAW_CODES}: the caller may read the roster but never change it. */
const readOnly = ref(false);
/** Latched by {@link DISABLE_CODES}: sharing is off host-wide. */
const sharingOff = ref(false);

const subjectId = ref('');
const role = ref<ShareRole>('viewer');

const canOfferMutation = computed(() => !unavailable.value && !readOnly.value);
const addDisabled = computed(
  () => busy.value || sharingOff.value || subjectId.value.trim().length === 0
);

/** Turns any thrown failure into the sentence shown, and latches what it implies about rights. */
function report(error: unknown, fallback: string): void {
  const code = error instanceof ConversationApiError ? error.code : undefined;

  if (code === 'unknown_thread') {
    unavailable.value = true;
    shares.value = [];
    refusal.value = REFUSAL_MESSAGES.unknown_thread;
    return;
  }

  if (code !== undefined && WITHDRAW_CODES.has(code)) {
    readOnly.value = true;
  }
  if (code !== undefined && DISABLE_CODES.has(code)) {
    sharingOff.value = true;
  }

  refusal.value =
    (code !== undefined ? REFUSAL_MESSAGES[code] : undefined) ??
    (error instanceof Error ? error.message : fallback);
}

/**
 * Which read is the current one. The Add control is live while the initial GET is still in flight
 * (that is deliberate — see the header comment: there is nothing to compute a permission from, so
 * the control is offered and the server's answer decides), which means a grant can be added and the
 * roster re-read before the first read has answered. Without this counter whichever read resolved
 * LAST won, and that is routinely the initial one — answering from before the grant existed, so the
 * row the user just created disappears from the list.
 */
let loadGeneration = 0;

/**
 * Which conversation this modal is about. Bumped by the `props.threadId` watcher below.
 *
 * `loadGeneration` cannot serve here: it distinguishes one READ from a later read, and the two reads
 * either side of a switch are both legitimate. What the mutations need to know is different — not
 * "was my read superseded" but "is the conversation I was started for still the one on screen" —
 * because everything they write after their await (the cleared input, the refusal, `readOnly`,
 * `busy`) is a statement about THAT conversation and about the caller's rights on it.
 */
let threadGeneration = 0;

async function load(): Promise<void> {
  const generation = ++loadGeneration;
  isLoading.value = true;
  try {
    const fetched = await listShares(props.threadId);
    if (generation !== loadGeneration) return;
    shares.value = fetched;
  } catch (error) {
    // A superseded read's refusal is just as stale as its roster — reporting it would latch a
    // verdict about a request nobody is waiting on any more.
    if (generation !== loadGeneration) return;
    report(error, 'Could not load who this conversation is shared with.');
  } finally {
    if (generation === loadGeneration) {
      isLoading.value = false;
    }
  }
}

async function handleAdd(): Promise<void> {
  const subject = subjectId.value.trim();
  if (subject.length === 0) {
    return;
  }
  const generation = threadGeneration;
  busy.value = true;
  refusal.value = null;
  try {
    await addShare(props.threadId, { subjectId: subject, role: role.value });
    // The grant was made, on the thread it was made for. Clearing the input and re-reading are both
    // about THAT thread, and the watcher has already re-read the one now on screen — so a stale
    // continuation would only wipe a subject the user has since typed and race that read.
    if (generation !== threadGeneration) return;
    subjectId.value = '';
    await load();
  } catch (error) {
    if (generation !== threadGeneration) return;
    report(error, 'Could not share this conversation.');
  } finally {
    // `busy` belongs to whichever mutation is current; the watcher lowers it for the new thread.
    if (generation === threadGeneration) {
      busy.value = false;
    }
  }
}

async function handleRemove(subject: string): Promise<void> {
  const generation = threadGeneration;
  busy.value = true;
  refusal.value = null;
  try {
    await removeShare(props.threadId, subject);
    if (generation !== threadGeneration) return;
    await load();
  } catch (error) {
    if (generation !== threadGeneration) return;
    report(error, 'Could not revoke this share.');
  } finally {
    if (generation === threadGeneration) {
      busy.value = false;
    }
  }
}

/** Renders an expiry for a human; grants without one simply do not expire. */
function formatExpiry(unixMs: number | null | undefined): string {
  return unixMs === null || unixMs === undefined
    ? 'no expiry'
    : `expires ${new Date(unixMs).toLocaleString()}`;
}

onMounted(load);

/**
 * Every latched flag here is a verdict about ONE conversation: `unavailable` is that thread's 404,
 * `readOnly` is "you were shared THIS one", `sharingOff` alone is about the deployment. Carrying any
 * of the first two into a different conversation would withhold a control the caller may well own
 * there, or claim the new thread does not exist. The generation counter in `load()` covers the other
 * half: a read still in flight for the old thread must not land on the new one's roster.
 */
watch(
  () => props.threadId,
  () => {
    threadGeneration += 1;
    shares.value = [];
    refusal.value = null;
    unavailable.value = false;
    readOnly.value = false;
    subjectId.value = '';
    // No mutation is in flight for the conversation just switched TO. Anything still open belongs to
    // the one left behind and will no longer lower this — which is the point: without resetting it
    // here, an abandoned mutation would leave the new conversation's controls disabled for good.
    busy.value = false;
    void load();
  }
);
</script>

<template>
  <BaseModal title="Share conversation" data-test-id="share-conversation-modal" @close="$emit('close')">
    <div class="share-body">
      <p v-if="refusal" class="share-refusal" data-testid="share-refusal">{{ refusal }}</p>

      <template v-if="!unavailable">
        <p v-if="isLoading" class="share-loading" data-testid="share-loading">Loading&hellip;</p>

        <ul v-else class="share-list" data-testid="share-list">
          <li
            v-for="share in shares"
            :key="share.subjectId"
            class="share-row"
            :data-testid="`share-row-${share.subjectId}`"
          >
            <span class="share-subject">{{ share.subjectId }}</span>
            <span class="share-role" :data-testid="`share-role-${share.subjectId}`">{{ share.role }}</span>
            <span class="share-meta">
              shared by {{ share.grantedBy }} &middot; {{ formatExpiry(share.expiresAtUnixMs) }}
            </span>
            <button
              v-if="canOfferMutation"
              class="share-revoke-btn"
              :data-testid="`share-remove-${share.subjectId}`"
              :disabled="busy || sharingOff"
              @click="handleRemove(share.subjectId)"
            >
              Revoke
            </button>
          </li>
          <li v-if="shares.length === 0" class="share-empty" data-testid="share-empty">
            This conversation is not shared with anyone.
          </li>
        </ul>

        <div v-if="canOfferMutation" class="share-add-form" data-testid="share-add-form">
          <input
            v-model="subjectId"
            class="share-subject-input"
            data-testid="share-subject-input"
            placeholder="tenant-id:object-id"
            aria-label="Subject id to share with"
          />
          <select
            v-model="role"
            class="share-role-select"
            data-testid="share-role-select"
            aria-label="Role"
          >
            <option value="viewer">viewer</option>
            <option value="editor">editor</option>
          </select>
          <button
            class="share-add-btn"
            data-testid="share-add-button"
            :disabled="addDisabled"
            @click="handleAdd"
          >
            Share
          </button>
        </div>
      </template>
    </div>
  </BaseModal>
</template>

<style scoped>
.share-body {
  padding: 16px 20px 20px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.share-refusal {
  margin: 0;
  padding: 10px 12px;
  border-radius: 6px;
  background: #fff4e5;
  border: 1px solid #ffd9a0;
  color: #8a5300;
  font-size: 14px;
}

.share-loading,
.share-empty {
  margin: 0;
  color: #666;
  font-size: 14px;
}

.share-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.share-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border: 1px solid #eee;
  border-radius: 6px;
  font-size: 14px;
}

.share-subject {
  font-family: monospace;
  flex: 1;
  overflow-wrap: anywhere;
}

.share-role {
  padding: 2px 8px;
  border-radius: 10px;
  background: #eef3fd;
  color: #2057bd;
  font-size: 12px;
}

.share-meta {
  color: #888;
  font-size: 12px;
}

.share-revoke-btn {
  padding: 4px 10px;
  background: transparent;
  border: 1px solid #dc3545;
  border-radius: 4px;
  color: #dc3545;
  font-size: 13px;
  cursor: pointer;
}

.share-revoke-btn:disabled {
  border-color: #ccc;
  color: #999;
  cursor: not-allowed;
}

.share-add-form {
  display: flex;
  gap: 8px;
  align-items: center;
}

.share-subject-input {
  flex: 1;
  padding: 8px 10px;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 14px;
}

.share-role-select {
  padding: 8px 10px;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 14px;
}

.share-add-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
}

.share-add-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}
</style>
