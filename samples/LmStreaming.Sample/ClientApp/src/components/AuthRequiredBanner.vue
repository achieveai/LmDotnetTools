<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch } from 'vue';
import type { AuthRequiredEvent } from '@/types';

/**
 * Banner shown while the backend is HOLDING a sandbox webhook call waiting for an interactive
 * sign-in (deferred auth). One row per pending provider. The sign-in button opens the
 * same-origin landing page (e.g. /auth/github) in a popup; that page drives the OAuth flow and
 * self-polls its status. The banner is dismissed by the backend's `auth_completed` frame; as a
 * fallback (e.g. a missed frame after a real sign-in), it also polls the provider's status
 * endpoint and self-dismisses on SignedIn.
 *
 * Note: the chat WebSocket connects lazily on the first message, so prompts can only arrive
 * once a conversation is active — the backend replays pending prompts to late connections.
 */
const props = defineProps<{
  requests: AuthRequiredEvent[];
}>();

const emit = defineEmits<{
  dismiss: [providerId: string];
}>();

function openSignIn(request: AuthRequiredEvent): void {
  window.open(request.signinUrl, `auth-${request.providerId}`, 'popup,width=640,height=760');
}

const POLL_INTERVAL_MS = 2000;
let pollTimer: ReturnType<typeof setInterval> | null = null;

async function pollStatuses(): Promise<void> {
  for (const request of props.requests) {
    try {
      const response = await fetch(`/api/auth/${encodeURIComponent(request.providerId)}/status`);
      if (!response.ok) continue;
      const status = (await response.json()) as { state?: string };
      if (status.state === 'SignedIn') {
        emit('dismiss', request.providerId);
      }
    } catch {
      // Best-effort fallback; the auth_completed frame is the primary dismissal path.
    }
  }
}

function syncTimer(): void {
  if (props.requests.length > 0 && pollTimer === null) {
    pollTimer = setInterval(() => void pollStatuses(), POLL_INTERVAL_MS);
  } else if (props.requests.length === 0 && pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

watch(() => props.requests.length, syncTimer);
onMounted(syncTimer);
onBeforeUnmount(() => {
  if (pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
});
</script>

<template>
  <div
    v-for="request in requests"
    :key="request.providerId"
    class="auth-required-banner"
    data-testid="auth-required-banner"
    :data-provider-id="request.providerId"
  >
    <span class="auth-required-text">
      Sign in to <strong>{{ request.providerId }}</strong> required —
      {{ request.reason || 'a sandbox request is waiting for your credentials' }}
    </span>
    <span class="auth-required-actions">
      <button
        type="button"
        class="auth-signin-btn"
        data-testid="auth-signin-button"
        @click="openSignIn(request)"
      >
        Sign in
      </button>
      <button
        type="button"
        class="auth-dismiss-btn"
        data-testid="auth-dismiss-button"
        aria-label="Dismiss sign-in prompt"
        @click="emit('dismiss', request.providerId)"
      >
        ✕
      </button>
    </span>
  </div>
</template>

<style scoped>
.auth-required-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 16px;
  background: #fff3cd;
  color: #664d03;
  border-top: 1px solid #ffe69c;
}

.auth-required-text {
  flex: 1;
  min-width: 0;
}

.auth-required-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.auth-signin-btn {
  padding: 6px 14px;
  background: #664d03;
  color: #fff;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  font-weight: 600;
}

.auth-signin-btn:hover {
  background: #806205;
}

.auth-dismiss-btn {
  padding: 4px 8px;
  background: transparent;
  color: #664d03;
  border: none;
  cursor: pointer;
  font-size: 14px;
}

.auth-dismiss-btn:hover {
  color: #403002;
}
</style>
