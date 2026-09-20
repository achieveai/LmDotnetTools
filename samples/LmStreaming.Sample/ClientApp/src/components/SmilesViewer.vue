<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { parseSmilesFence, renderSmiles } from '@/utils/smilesRenderer';

/*
 * Renders a ```smiles fence as one 2D structure drawing per line.
 *
 * Deliberately the same shape as `DiagramViewer`: the drawing is produced client-side, sanitized by
 * `sanitizeDiagramSvg`, and handed to an `<img>` over a blob URL. The generated SVG therefore never
 * enters the Markdown HTML allowlist, and a broken structure shows the parser's own message beside
 * the structures that did draw.
 */
const props = defineProps<{ source: string }>();

interface Figure {
  smiles: string;
  label: string;
  url: string | null;
  error: string;
}

const showingSource = ref(false);
const loading = ref(false);
const figures = ref<Figure[]>([]);
let generation = 0;
let disposed = false;

const rendered = computed(() => figures.value.filter((figure) => figure.url));

function revokeAll(): void {
  for (const figure of figures.value) if (figure.url) URL.revokeObjectURL(figure.url);
}

async function updateFigures(): Promise<void> {
  const currentGeneration = ++generation;
  loading.value = true;
  const entries = parseSmilesFence(props.source);
  const next = await Promise.all(
    entries.map(async ({ smiles, label }): Promise<Figure> => {
      try {
        const svg = await renderSmiles(smiles);
        return {
          smiles,
          label,
          url: URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml;charset=utf-8' })),
          error: '',
        };
      } catch (reason) {
        return {
          smiles,
          label,
          url: null,
          error: reason instanceof Error ? reason.message : 'The structure could not be drawn.',
        };
      }
    })
  );

  // A newer source (or an unmount) won while this batch was drawing: its blobs are orphans.
  if (disposed || currentGeneration !== generation) {
    for (const figure of next) if (figure.url) URL.revokeObjectURL(figure.url);
    return;
  }
  revokeAll();
  figures.value = next;
  loading.value = false;
}

watch(() => props.source, updateFigures, { immediate: true });

onBeforeUnmount(() => {
  disposed = true;
  generation += 1;
  revokeAll();
});
</script>

<template>
  <section class="smiles-viewer" aria-label="Chemical structures">
    <div class="smiles-toolbar">
      <span class="smiles-format" data-testid="smiles-format">SMILES</span>
      <div class="smiles-view-options" aria-label="Structure view">
        <button
          type="button"
          :class="{ active: !showingSource }"
          :aria-pressed="!showingSource"
          @click="showingSource = false"
        >
          Structure
        </button>
        <button
          type="button"
          data-testid="smiles-source-view"
          :class="{ active: showingSource }"
          :aria-pressed="showingSource"
          @click="showingSource = true"
        >
          Source
        </button>
      </div>
    </div>

    <div class="smiles-canvas">
      <pre v-if="showingSource" data-testid="smiles-source" tabindex="0"><code>{{ source }}</code></pre>
      <p v-else-if="loading && !figures.length" class="smiles-status" role="status">Drawing structures…</p>
      <template v-else>
        <div class="smiles-figures">
          <figure
            v-for="(figure, index) in rendered"
            :key="`${figure.smiles}-${index}`"
            class="smiles-figure"
            data-testid="smiles-figure"
          >
            <img :src="figure.url!" :alt="figure.label || `Structure for ${figure.smiles}`" />
            <figcaption>{{ figure.label || figure.smiles }}</figcaption>
          </figure>
        </div>
        <div
          v-for="(figure, index) in figures.filter((entry) => entry.error)"
          :key="`error-${index}`"
          class="smiles-error"
          data-testid="smiles-error"
          role="alert"
        >
          <strong>Could not draw {{ figure.smiles }}</strong>
          <span>{{ figure.error }}</span>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.smiles-viewer {
  overflow: hidden;
  margin: 1rem 0;
  border: 1px solid #d9dee5;
  border-radius: 10px;
  background: #fff;
  color: #344054;
  font-family: inherit;
  font-size: 14px;
}

.smiles-toolbar {
  display: flex;
  min-height: 42px;
  align-items: center;
  gap: 6px;
  padding: 5px 8px 5px 12px;
  border-bottom: 1px solid #e7eaee;
  background: #f8fafc;
}

.smiles-format {
  margin-right: auto;
  color: #667085;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.smiles-view-options {
  display: flex;
  padding: 2px;
  border: 1px solid #d9dee5;
  border-radius: 7px;
  background: #fff;
}

.smiles-view-options button {
  padding: 4px 8px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: #5f6874;
  cursor: pointer;
  font-size: 12px;
}

.smiles-view-options button.active {
  background: #e9eef5;
  color: #263445;
}

button:focus-visible,
pre:focus-visible {
  outline: 2px solid #3b82f6;
  outline-offset: 2px;
}

.smiles-canvas {
  display: grid;
  gap: 10px;
  max-height: 460px;
  overflow: auto;
  padding: 16px;
}

.smiles-figures {
  display: flex;
  flex-wrap: wrap;
  /* Structures have wildly different intrinsic heights; bottom-align so the captions form a row
     instead of a staircase. */
  align-items: flex-end;
  gap: 16px;
  justify-content: center;
}

.smiles-figure {
  display: grid;
  max-width: 100%;
  margin: 0;
  gap: 4px;
  justify-items: center;
}

.smiles-figure img {
  display: block;
  max-width: 260px;
  max-height: 240px;
  object-fit: contain;
}

.smiles-figure figcaption {
  color: #667085;
  font-size: 12px;
  text-align: center;
  overflow-wrap: anywhere;
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

.smiles-status {
  margin: 0;
  color: #667085;
  font-size: 13px;
}

.smiles-error {
  display: grid;
  gap: 4px;
  padding: 12px;
  border: 1px solid #f0d5d5;
  border-radius: 7px;
  background: #fff8f8;
  color: #8a3434;
  font-size: 13px;
  overflow-wrap: anywhere;
}

@media (max-width: 520px) {
  .smiles-canvas { padding: 10px; }
}
</style>
