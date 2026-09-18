<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import BaseModal from './BaseModal.vue';
import { renderDiagram, type DiagramLanguage } from '@/utils/diagramRenderer';

const props = defineProps<{
  source: string;
  language: DiagramLanguage;
}>();

const showingSource = ref(false);
const expanded = ref(false);
const loading = ref(false);
const error = ref('');
const svgUrl = ref<string | null>(null);
const zoom = ref(1);
let generation = 0;
let disposed = false;

const formatLabel = computed(() => (props.language === 'mermaid' ? 'Mermaid' : 'PlantUML'));
const downloadName = computed(() => `diagram-${props.language}.svg`);

function revokeCurrentUrl(): void {
  if (svgUrl.value) {
    URL.revokeObjectURL(svgUrl.value);
    svgUrl.value = null;
  }
}

async function updateDiagram(): Promise<void> {
  const currentGeneration = ++generation;
  loading.value = true;
  error.value = '';

  try {
    const svg = await renderDiagram(props.source, props.language);
    if (disposed || currentGeneration !== generation) return;

    const nextUrl = URL.createObjectURL(
      new Blob([svg], { type: 'image/svg+xml;charset=utf-8' }),
    );
    revokeCurrentUrl();
    svgUrl.value = nextUrl;
  } catch (reason) {
    if (disposed || currentGeneration !== generation) return;
    revokeCurrentUrl();
    error.value = reason instanceof Error ? reason.message : 'The diagram could not be rendered.';
    expanded.value = false;
  } finally {
    if (!disposed && currentGeneration === generation) loading.value = false;
  }
}

function openExpanded(): void {
  if (!svgUrl.value) return;
  zoom.value = 1;
  expanded.value = true;
}

function adjustZoom(change: number): void {
  zoom.value = Math.min(3, Math.max(0.5, zoom.value + change));
}

watch(() => [props.source, props.language] as const, updateDiagram, { immediate: true });

onBeforeUnmount(() => {
  disposed = true;
  generation += 1;
  revokeCurrentUrl();
});
</script>

<template>
  <section class="diagram-viewer" :aria-label="`${formatLabel} diagram`">
    <div class="diagram-toolbar">
      <span class="diagram-format" data-testid="diagram-format">{{ formatLabel }}</span>
      <div class="diagram-view-options" aria-label="Diagram view">
        <button
          type="button"
          :class="{ active: !showingSource }"
          :aria-pressed="!showingSource"
          @click="showingSource = false"
        >
          Diagram
        </button>
        <button
          type="button"
          data-testid="diagram-source-view"
          :class="{ active: showingSource }"
          :aria-pressed="showingSource"
          @click="showingSource = true"
        >
          Source
        </button>
      </div>
      <div class="diagram-actions">
        <button
          type="button"
          data-testid="diagram-expand"
          :disabled="!svgUrl"
          title="Expand diagram"
          aria-label="Expand diagram"
          @click="openExpanded"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5" /></svg>
        </button>
        <a
          v-if="svgUrl"
          data-testid="diagram-download"
          :href="svgUrl"
          :download="downloadName"
          title="Download SVG"
          aria-label="Download SVG"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12m-5-5 5 5 5-5M5 21h14" /></svg>
        </a>
      </div>
    </div>

    <div class="diagram-canvas">
      <pre v-if="showingSource" data-testid="diagram-source" tabindex="0"><code>{{ source }}</code></pre>
      <p v-else-if="loading" class="diagram-status" role="status">Rendering diagram…</p>
      <div v-else-if="error" class="diagram-error" data-testid="diagram-error" role="alert">
        <strong>Could not render diagram</strong>
        <span>{{ error }}</span>
        <button
          type="button"
          data-testid="diagram-retry"
          :disabled="loading"
          @click="updateDiagram"
        >
          Retry
        </button>
      </div>
      <img
        v-else-if="svgUrl"
        class="diagram-image"
        data-testid="diagram-image"
        :src="svgUrl"
        :alt="`${formatLabel} diagram`"
      />
    </div>
  </section>

  <BaseModal
    v-if="expanded && svgUrl"
    data-test-id="diagram-modal"
    :title="`${formatLabel} diagram`"
    @close="expanded = false"
  >
    <div class="diagram-expanded">
      <div class="diagram-expanded-toolbar" aria-label="Diagram zoom controls">
        <button type="button" aria-label="Zoom out" @click="adjustZoom(-0.25)">−</button>
        <span aria-live="polite">{{ Math.round(zoom * 100) }}%</span>
        <button
          type="button"
          data-testid="diagram-zoom-in"
          aria-label="Zoom in"
          @click="adjustZoom(0.25)"
        >+</button>
        <button type="button" data-testid="diagram-fit" @click="zoom = 1">Fit</button>
      </div>
      <div class="diagram-pan-region" tabindex="0" aria-label="Scrollable diagram canvas">
        <img
          data-testid="diagram-expanded-image"
          :src="svgUrl"
          :alt="`${formatLabel} diagram, expanded`"
          :style="{
            width: '100%',
            height: '100%',
            objectFit: 'contain',
            transform: `scale(${zoom})`,
          }"
        />
      </div>
    </div>
  </BaseModal>
</template>

<style scoped>
.diagram-viewer {
  overflow: hidden;
  margin: 1rem 0;
  border: 1px solid #d9dee5;
  border-radius: 10px;
  background: #fff;
  color: #344054;
  font-family: inherit;
  font-size: 14px;
  font-weight: 400;
}

:deep(.modal-container) {
  max-width: min(92vw, 1100px);
}

:deep(.modal-content) {
  min-height: 0;
}

.diagram-toolbar,
.diagram-expanded-toolbar {
  display: flex;
  align-items: center;
  gap: 6px;
}

.diagram-toolbar {
  min-height: 42px;
  padding: 5px 8px 5px 12px;
  border-bottom: 1px solid #e7eaee;
  background: #f8fafc;
}

.diagram-format {
  margin-right: auto;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: #667085;
}

.diagram-view-options {
  display: flex;
  padding: 2px;
  border: 1px solid #d9dee5;
  border-radius: 7px;
  background: #fff;
}

button,
.diagram-actions a {
  border: 0;
  color: #5f6874;
  background: transparent;
  cursor: pointer;
}

.diagram-view-options button {
  padding: 4px 8px;
  border-radius: 5px;
  font-size: 12px;
}

.diagram-view-options button.active {
  background: #e9eef5;
  color: #263445;
}

.diagram-actions {
  display: flex;
  align-items: center;
  gap: 2px;
}

.diagram-actions button,
.diagram-actions a {
  display: grid;
  width: 30px;
  height: 30px;
  place-items: center;
  border-radius: 6px;
}

.diagram-actions button:hover:not(:disabled),
.diagram-actions a:hover,
.diagram-expanded-toolbar button:hover {
  background: #e9eef5;
  color: #263445;
}

button:focus-visible,
.diagram-actions a:focus-visible,
.diagram-pan-region:focus-visible,
pre:focus-visible {
  outline: 2px solid #3b82f6;
  outline-offset: 2px;
}

button:disabled {
  cursor: default;
  opacity: 0.45;
}

.diagram-actions svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.7;
}

.diagram-canvas {
  display: grid;
  min-height: 96px;
  max-height: 420px;
  place-items: center;
  overflow: auto;
  padding: 16px;
}

.diagram-image {
  display: block;
  max-width: 100%;
  max-height: 380px;
  object-fit: contain;
}

pre {
  box-sizing: border-box;
  width: 100%;
  min-height: 64px;
  margin: 0;
  overflow: auto;
  white-space: pre;
  color: #344054;
  background: transparent;
}

.diagram-status,
.diagram-error {
  margin: 0;
  color: #667085;
  font-size: 13px;
}

.diagram-error button {
  width: fit-content;
  margin-top: 4px;
  padding: 6px 10px;
  border: 1px solid #d0d5dd;
  border-radius: 6px;
  background: #fff;
  color: #344054;
  font-weight: 600;
}

.diagram-error {
  display: grid;
  gap: 4px;
  justify-self: stretch;
  padding: 12px;
  border: 1px solid #f0d5d5;
  border-radius: 7px;
  background: #fff8f8;
  color: #8a3434;
}

.diagram-expanded {
  display: flex;
  height: min(70vh, 680px);
  min-height: 0;
  max-height: calc(90vh - 62px);
  flex-direction: column;
}

.diagram-expanded-toolbar {
  justify-content: flex-end;
  padding: 8px 12px;
  border-bottom: 1px solid #e7eaee;
  background: #f8fafc;
}

.diagram-expanded-toolbar button {
  min-width: 32px;
  min-height: 30px;
  padding: 4px 8px;
  border-radius: 6px;
}

.diagram-expanded-toolbar span {
  min-width: 48px;
  text-align: center;
  color: #667085;
  font-size: 12px;
}

.diagram-pan-region {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 24px;
  background-color: #fff;
  background-image: radial-gradient(#d9dee5 0.7px, transparent 0.7px);
  background-size: 14px 14px;
}

.diagram-pan-region img {
  display: block;
  max-width: none;
  margin: auto;
  transform-origin: top left;
  transition: transform 120ms ease-out;
}

@media (prefers-reduced-motion: reduce) {
  .diagram-pan-region img { transition: none; }
}

@media (max-width: 520px) {
  .diagram-toolbar { flex-wrap: wrap; }
  .diagram-format { flex: 1 0 calc(100% - 70px); }
  .diagram-view-options { order: 3; }
  .diagram-canvas { padding: 10px; }
}
</style>
