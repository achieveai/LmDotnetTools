<script setup lang="ts">
/**
 * The screen shown instead of the application whenever identity has not resolved to "show the app".
 *
 * The refusal states are the reason this component exists. `tenant_not_provisioned` and
 * `tenant_suspended` mean the token was genuine and the organisation behind it may not be here —
 * so the one thing this screen must never do is offer a sign-in button. Signing in again produces
 * the identical refusal, and a client that retries automatically produces an infinite loop against
 * the identity provider.
 *
 * `expired` is the mirror image and is kept visibly apart for that reason: the session was fine
 * and simply ran out, so the sign-in button is the whole point of the screen.
 */
import { useIdentity, startSignIn } from '@/composables/useIdentity';

const { status, refusalCode, errorMessage } = useIdentity();
</script>

<template>
  <div class="identity-gate" data-testid="identity-gate">
    <div class="identity-gate-content">
      <template v-if="status === 'loading'">
        <h2>Starting up</h2>
        <p>Checking how this deployment handles sign-in.</p>
      </template>

      <template v-else-if="status === 'signing-in'">
        <h2>Signing you in</h2>
        <p>Redirecting to your organisation's sign-in page.</p>
      </template>

      <template v-else-if="refusalCode === 'tenant_not_provisioned'">
        <h2 data-testid="identity-gate-not-provisioned">Your organisation is not set up yet</h2>
        <p>
          You signed in successfully, but your organisation has not been onboarded to this service.
          An administrator has to provision it before anyone from your directory can use it.
        </p>
        <p class="identity-gate-hint">
          Ask whoever requested this service to contact support with your organisation's name.
        </p>
      </template>

      <template v-else-if="refusalCode === 'tenant_suspended'">
        <h2 data-testid="identity-gate-suspended">Your organisation's access is suspended</h2>
        <p>
          You signed in successfully, but access for your organisation is currently suspended.
          Signing in again will not restore it.
        </p>
        <p class="identity-gate-hint">Contact your administrator or support to have it reinstated.</p>
      </template>

      <template v-else-if="status === 'expired'">
        <h2 data-testid="identity-gate-expired">Your session has expired</h2>
        <p>
          You were signed in, but the session has run out and could not be renewed on its own.
          Signing in again picks up where you left off.
        </p>
        <button type="button" class="identity-gate-btn" @click="startSignIn">
          Sign in again
        </button>
      </template>

      <template v-else-if="status === 'error'">
        <h2>Sign-in could not be completed</h2>
        <p data-testid="identity-gate-error">{{ errorMessage }}</p>
        <button type="button" class="identity-gate-btn" @click="startSignIn">Try again</button>
      </template>

      <template v-else>
        <h2>Sign in to continue</h2>
        <p>This deployment requires you to sign in with your work account.</p>
        <button type="button" class="identity-gate-btn" @click="startSignIn">
          Sign in with Microsoft
        </button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.identity-gate {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100vh;
}

.identity-gate-content {
  text-align: center;
  padding: 24px;
  max-width: 440px;
}

.identity-gate-content h2 {
  margin: 0 0 8px;
  font-size: 20px;
}

.identity-gate-content p {
  color: #666;
  margin: 0 0 16px;
  word-break: break-word;
}

.identity-gate-hint {
  font-size: 13px;
}

.identity-gate-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
}
</style>
