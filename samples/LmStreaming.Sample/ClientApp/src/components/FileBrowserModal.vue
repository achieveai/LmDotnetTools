<script setup lang="ts">
import BaseModal from './BaseModal.vue';
import FileBrowser from './FileBrowser.vue';

defineProps<{ threadId: string | null }>();

defineEmits<{ close: [] }>();
</script>

<template>
  <BaseModal title="Files" data-test-id="file-browser-modal" @close="$emit('close')">
    <FileBrowser :thread-id="threadId" />
  </BaseModal>
</template>
