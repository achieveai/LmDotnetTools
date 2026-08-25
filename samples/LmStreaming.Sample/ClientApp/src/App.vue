<script setup lang="ts">
import { onMounted } from 'vue';
import ChatLayout from '@/components/ChatLayout.vue';
import IdentityGate from '@/components/IdentityGate.vue';
import { useIdentity, initializeIdentity } from '@/composables/useIdentity';

/**
 * The chat layout is mounted only once identity has resolved to "show the app".
 *
 * `v-if`, not `v-show`: ChatLayout starts loading conversations the moment it mounts, and those
 * requests would go out before a token exists — arriving unauthenticated at an enforcing
 * deployment and failing for a reason that has nothing to do with the user.
 *
 * With `Identity:Enforce` false — every developer machine, and every existing test — the state
 * resolves to `disabled` on the first tick and the app renders as it always did.
 */
const { isReady } = useIdentity();

onMounted(initializeIdentity);
</script>

<template>
  <ChatLayout v-if="isReady" />
  <IdentityGate v-else />
</template>
