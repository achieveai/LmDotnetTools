<script lang="ts">
/** Module-scope counter so two mounted panels never share an `aria-controls` id. */
let nextUid = 0;
</script>

<script setup lang="ts">
import { computed, ref } from 'vue';
import type { ContextReportStatus } from '@/composables/useContextReport';
import {
  capacityLabel,
  compactionLabel,
  costLabel,
  decisionLabel,
  executionKindLabel,
  formatPercent,
  formatTokens,
  freshnessLabel,
  temperatureLabel,
  tokensLabel,
  usageCompletenessLabel,
  type ContextRowView,
  type ContextTotalView,
} from '@/utils/contextReport';

/**
 * Context / cost / compaction panel for a conversation (#685, spec 679 §7): one table row per
 * framework-owned agent, one footer row for the descendant-wide total. Read-only.
 *
 * Stateless/presentational, like `TodoBoardPanel`: the rows arrive already shaped by the pure
 * helpers `useContextReport` uses, so a label here and a label in the composable's tests cannot
 * disagree. The panel's own job is the part the helpers cannot do — the DOM a screen reader walks,
 * the keyboard model, and keeping every distinct state distinct in TEXT, not only in colour.
 *
 * It renders NO prompt or message content by design: everything on screen is a number, an id, a
 * model name, or a state word. The endpoint answers 403 and 404 with the same `null`, and this
 * component shows both as the same "unavailable" line, so a refused thread leaks nothing.
 */
const props = defineProps<{
  rows: ContextRowView[];
  total: ContextTotalView | null;
  status: ContextReportStatus;
  generatedAtUtc: string | null;
}>();

const uid = ++nextUid;
const bodyId = `context-panel-body-${uid}`;

/** Collapsed by default: the summary line carries the two numbers most people want. */
const expanded = ref(false);
function toggle(): void {
  expanded.value = !expanded.value;
}

const rootRow = computed(() => props.rows.find((r) => r.parentAgentId === null) ?? props.rows[0] ?? null);

const summary = computed(() => {
  if (props.rows.length === 0) {
    if (props.status === 'unavailable') return 'unavailable';
    if (props.status === 'loading') return 'loading…';
    return '';
  }
  const parts: string[] = [];
  const root = rootRow.value;
  if (root) {
    parts.push(
      root.capacity.kind === 'known'
        ? `${formatPercent(root.capacity.utilization)} of ${formatTokens(root.capacity.window)}`
        : capacityLabel(root.capacity)
    );
  }
  if (props.total) parts.push(`total ${costLabel(props.total.cost)}`);
  if (props.rows.length > 1) parts.push(`${props.rows.length} agents`);
  return parts.join(' · ');
});

function agentName(row: ContextRowView): string {
  return row.agentId === 'root' ? 'Main agent' : row.agentId;
}

function percentOf(row: ContextRowView): number {
  if (row.capacity.kind !== 'known') return 0;
  return Math.max(0, Math.min(100, Math.round(row.capacity.utilization * 100)));
}

/** Colour tier for the gauge; the text beside it carries the same figure, so colour is never alone. */
function tierOf(row: ContextRowView): string {
  if (row.capacity.kind !== 'known') return 'none';
  const u = row.capacity.utilization;
  return u >= 0.9 ? 'hot' : u >= 0.7 ? 'warm' : 'ok';
}

function capacityKind(row: ContextRowView): string {
  return row.capacity.kind === 'known' ? 'known' : row.capacity.reason;
}

// ---- per-row details (disclosure) + roving tabindex across the row buttons -------------------

const openDetails = ref<Set<string>>(new Set());
function detailsId(row: ContextRowView): string {
  return `context-row-details-${uid}-${row.threadId}`;
}
function isOpen(row: ContextRowView): boolean {
  return openDetails.value.has(row.threadId);
}
function toggleDetails(row: ContextRowView): void {
  const next = new Set(openDetails.value);
  if (next.has(row.threadId)) next.delete(row.threadId);
  else next.add(row.threadId);
  openDetails.value = next;
}

const focusIndex = ref(0);
const detailButtons = ref<HTMLButtonElement[]>([]);
function setDetailButton(el: unknown, index: number): void {
  if (el instanceof HTMLButtonElement) detailButtons.value[index] = el;
}
function moveFocus(index: number): void {
  const clamped = Math.max(0, Math.min(props.rows.length - 1, index));
  focusIndex.value = clamped;
  detailButtons.value[clamped]?.focus();
}
function onRowKeydown(event: KeyboardEvent, index: number): void {
  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault();
      moveFocus(index + 1);
      break;
    case 'ArrowUp':
      event.preventDefault();
      moveFocus(index - 1);
      break;
    case 'Home':
      event.preventDefault();
      moveFocus(0);
      break;
    case 'End':
      event.preventDefault();
      moveFocus(props.rows.length - 1);
      break;
    default:
      break;
  }
}
</script>

<template>
  <section
    class="context-panel"
    data-testid="context-panel"
    role="region"
    aria-label="Context and cost"
    :data-status="status"
  >
    <button
      type="button"
      class="context-toggle"
      data-testid="context-panel-toggle"
      :aria-expanded="expanded ? 'true' : 'false'"
      :aria-controls="bodyId"
      @click="toggle"
    >
      <span class="context-toggle-caret" aria-hidden="true">{{ expanded ? '▾' : '▸' }}</span>
      <span class="context-toggle-title">Context</span>
      <span class="context-toggle-summary" data-testid="context-panel-summary">{{ summary }}</span>
    </button>

    <div v-show="expanded" :id="bodyId" class="context-body">
      <p
        v-if="rows.length === 0 && status === 'unavailable'"
        class="context-note"
        data-testid="context-unavailable"
        role="status"
      >
        Context report unavailable for this conversation.
      </p>
      <p
        v-else-if="rows.length === 0 && status === 'loading'"
        class="context-note"
        data-testid="context-loading"
        role="status"
      >
        Loading context…
      </p>
      <p v-else-if="rows.length === 0" class="context-note" data-testid="context-empty" role="status">
        No agent has been observed in this conversation yet.
      </p>

      <table v-else class="context-table" data-testid="context-table">
        <caption class="sr-only">
          Context and cost, one row per agent; the footer row is the total across all agents.
        </caption>
        <thead>
          <tr>
            <th scope="col">Agent</th>
            <th scope="col">Context window</th>
            <th scope="col">Usage</th>
            <th scope="col">Cost</th>
            <th scope="col">State</th>
            <th scope="col"><span class="sr-only">Details</span></th>
          </tr>
        </thead>
        <tbody>
          <template v-for="(row, index) in rows" :key="row.threadId">
            <tr
              class="context-row"
              data-testid="context-row"
              :data-agent-id="row.agentId"
              :data-provisional="row.provisional ? 'true' : undefined"
            >
              <th scope="row" data-label="Agent" class="context-agent">
                <span class="context-agent-name">{{ agentName(row) }}</span>
                <span class="context-agent-kind">{{ executionKindLabel(row.executionKind) }}</span>
                <span v-if="row.provisional" class="context-badge context-badge-provisional">live only</span>
              </th>
              <td data-label="Context window" class="context-capacity-cell">
                <div
                  v-if="row.capacity.kind === 'known'"
                  class="context-gauge"
                  role="meter"
                  aria-valuemin="0"
                  aria-valuemax="100"
                  :aria-valuenow="percentOf(row)"
                  :aria-valuetext="capacityLabel(row.capacity)"
                  :aria-label="`${agentName(row)} context utilization`"
                >
                  <div
                    class="context-gauge-fill"
                    :class="`context-gauge-${tierOf(row)}`"
                    :style="{ width: `${percentOf(row)}%` }"
                  ></div>
                </div>
                <span data-testid="context-capacity" :data-kind="capacityKind(row)" class="context-capacity">
                  {{ capacityLabel(row.capacity) }}
                </span>
              </td>
              <td data-label="Usage" data-testid="context-tokens" :data-kind="row.tokens.kind">
                {{ tokensLabel(row.tokens) }}
              </td>
              <td data-label="Cost" data-testid="context-cost" :data-kind="row.cost.kind">
                {{ costLabel(row.cost) }}
              </td>
              <td data-label="State" class="context-state">
                <span class="context-badge" data-testid="context-freshness" :data-value="row.freshness">
                  {{ freshnessLabel(row.freshness) }}
                </span>
                <span class="context-badge" data-testid="context-temperature" :data-value="row.cacheTemperature">
                  {{ temperatureLabel(row.cacheTemperature) }}
                </span>
              </td>
              <td data-label="Details" class="context-details-cell">
                <button
                  :ref="(el) => setDetailButton(el, index)"
                  type="button"
                  class="context-details-toggle"
                  data-testid="context-row-details-toggle"
                  :aria-expanded="isOpen(row) ? 'true' : 'false'"
                  :aria-controls="detailsId(row)"
                  :aria-label="`Details for ${agentName(row)}`"
                  :tabindex="index === focusIndex ? 0 : -1"
                  @click="toggleDetails(row)"
                  @keydown="onRowKeydown($event, index)"
                >
                  {{ isOpen(row) ? '−' : '+' }}
                </button>
              </td>
            </tr>
            <tr v-if="isOpen(row)" :id="detailsId(row)" class="context-row-details" data-testid="context-row-details">
              <td colspan="6">
                <dl class="context-dl">
                  <dt>Compaction</dt>
                  <dd data-testid="context-compaction">{{ compactionLabel(row.compaction) }}</dd>
                  <dt>Recommendation</dt>
                  <dd data-testid="context-decision">{{ decisionLabel(row.compaction.decision) }}</dd>
                  <template v-if="row.compaction.checkpointId">
                    <dt>Checkpoint</dt>
                    <dd>{{ row.compaction.checkpointId }}</dd>
                  </template>
                  <dt>Model</dt>
                  <dd>{{ row.modelId ?? 'unknown' }}</dd>
                  <template v-if="row.capacity.kind === 'known'">
                    <dt>Window</dt>
                    <dd>
                      {{ formatTokens(row.capacity.window) }} tokens, {{ formatTokens(row.capacity.reserve) }}
                      reserved, {{ formatTokens(row.capacity.used) }} in use ({{ row.capacity.provenance.toLowerCase() }})
                    </dd>
                  </template>
                  <dt>Generation</dt>
                  <dd>{{ row.generationOrdinal ?? 'none' }}</dd>
                  <dt>Observed</dt>
                  <dd>{{ row.observedAtUtc ?? 'never' }}</dd>
                  <template v-if="row.tokens.kind === 'value'">
                    <dt>Tokens</dt>
                    <dd>
                      in {{ formatTokens(row.tokens.input) }}, out {{ formatTokens(row.tokens.output) }},
                      cache read {{ formatTokens(row.tokens.cacheRead) }}, cache write
                      {{ formatTokens(row.tokens.cacheWrite) }}, reasoning {{ formatTokens(row.tokens.reasoning) }}
                    </dd>
                  </template>
                  <dt>Thread</dt>
                  <dd>{{ row.threadId }}</dd>
                </dl>
              </td>
            </tr>
          </template>
        </tbody>
        <tfoot v-if="total">
          <tr class="context-total" data-testid="context-total">
            <th scope="row" data-label="Agent">Total (all agents)</th>
            <td data-label="Context window"></td>
            <td data-label="Usage" data-testid="context-total-tokens" :data-kind="total.tokens.kind">
              {{ tokensLabel(total.tokens) }}
            </td>
            <td data-label="Cost" data-testid="context-total-cost" :data-kind="total.cost.kind">
              {{ costLabel(total.cost) }}
            </td>
            <td data-label="State" data-testid="context-total-completeness">
              Usage {{ usageCompletenessLabel(total.usageCompleteness) }}
            </td>
            <td data-label="Details"></td>
          </tr>
        </tfoot>
      </table>

      <p v-if="generatedAtUtc" class="context-footer" data-testid="context-generated-at">
        Report generated {{ generatedAtUtc }}<span v-if="status === 'loading'"> · refreshing…</span>
      </p>
    </div>
  </section>
</template>

<style scoped>
.context-panel {
  border-top: 1px solid #dee2e6;
  background: #f8f9fa;
  font-size: 13px;
  color: #212529;
}

.context-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 16px;
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.context-toggle:focus-visible {
  outline: 2px solid #0d6efd;
  outline-offset: -2px;
}

.context-toggle-caret {
  color: #666;
  font-size: 12px;
}

.context-toggle-title {
  font-weight: 600;
}

.context-toggle-summary {
  color: #495057;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.context-body {
  padding: 0 16px 8px;
  overflow-x: auto;
}

.context-note,
.context-footer {
  margin: 4px 0;
  color: #6c757d;
}

.context-table {
  width: 100%;
  border-collapse: collapse;
}

.context-table th,
.context-table td {
  padding: 4px 8px;
  text-align: left;
  vertical-align: top;
  border-bottom: 1px solid #e9ecef;
}

.context-table thead th {
  font-weight: 600;
  color: #495057;
  white-space: nowrap;
}

.context-table tbody th {
  font-weight: 500;
}

.context-agent-name {
  display: block;
}

.context-agent-kind {
  color: #6c757d;
  font-size: 12px;
}

.context-capacity-cell {
  min-width: 180px;
}

.context-gauge {
  height: 6px;
  border-radius: 3px;
  background: #e9ecef;
  overflow: hidden;
  margin-bottom: 3px;
}

.context-gauge-fill {
  height: 100%;
}

.context-gauge-ok {
  background: #28a745;
}

.context-gauge-warm {
  background: #ffc107;
}

.context-gauge-hot {
  background: #dc3545;
}

.context-capacity[data-kind='no-window'],
.context-capacity[data-kind='no-observation'],
.context-capacity[data-kind='unsupported'] {
  color: #6c757d;
  font-style: italic;
}

.context-badge {
  display: inline-block;
  padding: 1px 6px;
  margin-right: 4px;
  border-radius: 8px;
  font-size: 12px;
  background: #e9ecef;
  color: #495057;
}

.context-badge[data-value='Fresh'] {
  background: #d4edda;
  color: #155724;
}

.context-badge[data-value='Hot'] {
  background: #fff3cd;
  color: #856404;
}

.context-badge-provisional {
  background: #cfe2ff;
  color: #084298;
}

.context-details-toggle {
  min-width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid #ced4da;
  border-radius: 4px;
  background: #fff;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.context-details-toggle:focus-visible {
  outline: 2px solid #0d6efd;
  outline-offset: 1px;
}

.context-row-details td {
  background: #fff;
}

.context-dl {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 2px 12px;
  margin: 0;
}

.context-dl dt {
  color: #6c757d;
}

.context-dl dd {
  margin: 0;
  word-break: break-word;
}

.context-total th,
.context-total td {
  font-weight: 600;
  border-bottom: none;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

/* Narrow widths: each row becomes a card, each cell prefixed by its column label. The header row
   stays in the DOM for assistive tech; only its visual box goes. */
@media (max-width: 600px) {
  .context-table thead {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
  }

  .context-table,
  .context-table tbody,
  .context-table tfoot,
  .context-table tr,
  .context-table th,
  .context-table td {
    display: block;
  }

  .context-table tr {
    padding: 6px 0;
    border-bottom: 1px solid #dee2e6;
  }

  .context-table th,
  .context-table td {
    border-bottom: none;
    padding: 2px 0;
  }

  .context-table td[data-label]::before,
  .context-table tbody th[data-label]::before,
  .context-table tfoot th[data-label]::before {
    content: attr(data-label) ': ';
    color: #6c757d;
  }

  .context-table td[data-label='Details']::before,
  .context-table th[data-label='Agent']::before {
    content: none;
  }

  .context-table td:empty {
    display: none;
  }

  .context-capacity-cell {
    min-width: 0;
  }

  .context-dl {
    grid-template-columns: 1fr;
  }
}
</style>
