<script setup lang="ts">
import type { AuthRequiredEvent } from '@/types';
import { isPredefinedKeyProvider } from '@/types/auth';
import { openEgressDialog } from '@/composables/useEgressAuth';

/**
 * Banner shown while the backend is HOLDING a sandbox webhook call waiting for a credential
 * (deferred auth). One row per pending provider. For an OAuth provider the "Sign in" button opens
 * the same-origin landing page (e.g. /auth/github) in a popup; for a pre-defined egress key
 * (`predefined-<id>`, issue #210) the "Update egress key" button instead opens the Egress Auth
 * dialog so the user can re-enter the failing credential. Either way the banner is dismissed by the
 * backend's terminal frame — `auth_completed` (a valid credential landed) or `auth_denied` (the hold
 * timed out) — both handled in useChat. The ✕ button dismisses locally at any time.
 *
 * Note: the chat WebSocket connects lazily on the first message, so prompts can only arrive
 * once a conversation is active — the backend replays pending prompts to late connections.
 */
defineProps<{
  requests: AuthRequiredEvent[];
}>();

const emit = defineEmits<{
  dismiss: [providerId: string];
}>();

function openSignIn(request: AuthRequiredEvent): void {
  window.open(request.signinUrl, `auth-${request.providerId}`, 'popup,width=640,height=760');
}
</script>

<template>
  <div
    v-for="request in requests"
    :key="request.providerId"
    class="auth-required-banner"
    data-testid="auth-required-banner"
    :data-provider-id="request.providerId"
  >
    <span v-if="isPredefinedKeyProvider(request.providerId)" class="auth-required-text">
      A sandbox egress key needs updating —
      {{ request.reason || 'a sandbox request is waiting for a valid egress key' }}
    </span>
    <span v-else class="auth-required-text">
      Sign in to <strong>{{ request.providerId }}</strong> required —
      {{ request.reason || 'a sandbox request is waiting for your credentials' }}
    </span>
    <span class="auth-required-actions">
      <button
        v-if="isPredefinedKeyProvider(request.providerId)"
        type="button"
        class="auth-signin-btn"
        data-testid="auth-update-key-button"
        @click="openEgressDialog()"
      >
        Update egress key
      </button>
      <button
        v-else
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
