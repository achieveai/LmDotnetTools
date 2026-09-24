<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { launchSandboxApp } from '@/api/sandboxAppsApi';

const props = defineProps<{ threadId: string; workspaceId: string; appId: string; name: string }>();
const emit = defineEmits<{ close: [] }>();

const frame = ref<HTMLIFrameElement | null>(null);
const status = ref<'Starting' | 'Ready' | 'Unavailable'>('Starting');
const frameName = `sandbox-app-${crypto.randomUUID()}`;
let origin: string | null = null;
let launchAbort: AbortController | null = null;
let disposed = false;
let posted = false;
let failed = false;

async function launch(): Promise<void> {
  launchAbort?.abort();
  const abort = new AbortController();
  launchAbort = abort;
  status.value = 'Starting';
  origin = null;
  posted = false;
  failed = false;
  try {
    const { url, ticket } = await launchSandboxApp(props.threadId, props.workspaceId, props.appId, abort.signal);
    if (disposed || abort.signal.aborted) return;
    origin = new URL(url).origin;
    await nextTick();
    if (!frame.value || disposed || abort.signal.aborted) return;
    const form = document.createElement('form');
    form.method = 'post';
    form.action = url;
    form.target = frameName;
    form.hidden = true;
    const input = document.createElement('input');
    input.type = 'hidden';
    input.name = 'ticket';
    input.value = ticket;
    form.append(input);
    frame.value.parentElement?.append(form);
    posted = true;
    form.submit();
    // The browser snapshots the submitted form synchronously. Remove the credential from the DOM.
    window.setTimeout(() => form.remove(), 0);
  } catch {
    if (!disposed && !abort.signal.aborted) status.value = 'Unavailable';
  }
}

function reload(): void {
  if (!frame.value || !origin) return;
  status.value = 'Starting';
  failed = false;
  frame.value.src = `${origin}/`;
}

function onFrameLoad(): void {
  if (posted && !failed) status.value = 'Ready';
}

function onFrameError(): void {
  failed = true;
  status.value = 'Unavailable';
}

function onAppMessage(event: MessageEvent): void {
  if (event.origin !== origin || event.source !== frame.value?.contentWindow) return;
  if (event.data?.type !== 'sandbox-app-failed') return;
  failed = true;
  status.value = 'Unavailable';
}

onMounted(() => { window.addEventListener('message', onAppMessage); void launch(); });
onBeforeUnmount(() => {
  disposed = true;
  launchAbort?.abort();
  window.removeEventListener('message', onAppMessage);
});
</script>

<template>
  <section class="sandbox-app-preview" :aria-label="`Sandbox app: ${name}`" data-testid="sandbox-app-preview">
    <header class="sandbox-app-toolbar">
      <div class="sandbox-app-title">
        <strong>{{ name }}</strong>
        <span class="sandbox-app-badge">Sandbox app</span>
        <span role="status" :class="`sandbox-app-status-${status.toLowerCase()}`">{{ status }}</span>
      </div>
      <div class="sandbox-app-actions">
        <button type="button" :disabled="!origin || status !== 'Ready'" @click="reload">Reload</button>
        <button type="button" aria-label="Close app" @click="emit('close')">×</button>
      </div>
    </header>
    <div v-if="status === 'Unavailable'" class="sandbox-app-failure">
      <p>The app could not start.</p>
      <button type="button" data-testid="sandbox-app-retry" @click="launch">Retry</button>
    </div>
    <iframe
      ref="frame"
      :name="frameName"
      title="Sandbox app content"
      sandbox="allow-scripts allow-forms allow-same-origin"
      referrerpolicy="no-referrer"
      :hidden="status === 'Unavailable'"
      @load="onFrameLoad"
      @error="onFrameError"
    />
  </section>
</template>

<style scoped>
.sandbox-app-preview { display: flex; flex-direction: column; min-height: 320px; height: 100%; background: #fff; }
.sandbox-app-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 14px; border-bottom: 1px solid #dce3ec; }
.sandbox-app-title, .sandbox-app-actions { display: flex; align-items: center; gap: 9px; }
.sandbox-app-title strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sandbox-app-badge { border: 1px solid #c5d5eb; border-radius: 999px; padding: 2px 7px; color: #315c92; font-size: 11px; white-space: nowrap; }
.sandbox-app-title [role="status"] { font-size: 12px; color: #526176; }
.sandbox-app-status-unavailable { color: #a33b33 !important; }
.sandbox-app-actions button, .sandbox-app-failure button { border: 1px solid #c5d0df; border-radius: 6px; background: #fff; padding: 5px 9px; cursor: pointer; }
.sandbox-app-actions button:disabled { opacity: .5; cursor: default; }
.sandbox-app-preview iframe { flex: 1; width: 100%; min-height: 260px; border: 0; background: #fff; }
.sandbox-app-failure { padding: 24px; }
</style>
