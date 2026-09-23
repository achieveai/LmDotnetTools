<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { TablePreview } from '@/types/fileBrowser';

/**
 * The tabular viewer for a workspace file preview. It is the single renderer for BOTH tabular
 * sources, because both arrive as the same {@link TablePreview}:
 *
 *   - `.csv` / `.tsv`, parsed on the client from the preview endpoint's text (`utils/delimitedText`);
 *   - `.xlsx` / `.xlsm`, parsed on the SERVER (the client never sees workbook bytes) into one
 *     {@link TablePreview.sheets} entry per worksheet.
 *
 * Row 0 of a sheet is the header. Everything here is view state over data the producer already
 * capped — this component never re-reads, re-fetches, or re-parses anything, and sorting/filtering
 * apply only to the rows that were delivered, which is exactly what the truncation note says.
 */
const props = defineProps<{ table: TablePreview }>();

type SortDirection = 'asc' | 'desc';

/**
 * A body row carried with the index it had in the delivered sheet, so the gutter number survives
 * sorting and filtering — the number identifies the row, it is not the position on screen.
 * Numbering is 1-based over DATA rows (the header is not row 1), which is what a reader scanning a
 * filtered view wants: "the 12th record", not "spreadsheet line 13".
 */
interface NumberedRow {
  index: number;
  cells: string[];
}

const activeSheetIndex = ref(0);
const sortColumn = ref<number | null>(null);
const sortDirection = ref<SortDirection>('asc');
const filter = ref('');

const activeSheet = computed(() => props.table.sheets[activeSheetIndex.value] ?? props.table.sheets[0] ?? null);
const header = computed<string[]>(() => activeSheet.value?.rows[0] ?? []);
const hasRows = computed(() => (activeSheet.value?.rows.length ?? 0) > 0);

/**
 * The body rows, padded to the header's width. Ragged input is normal — a delimited file's short
 * line and a worksheet's trailing-blank trim both produce one — and an unpadded row would slide its
 * cells left under the wrong headers.
 */
const dataRows = computed<NumberedRow[]>(() =>
  (activeSheet.value?.rows.slice(1) ?? []).map((cells, index) => ({
    index,
    cells: Array.from({ length: header.value.length }, (_, c) => cells[c] ?? ''),
  }))
);

/**
 * Parses a cell as a number, or null. Deliberately strict: a plain decimal, optionally signed, with
 * optional exponent or thousands separators. Anything else (a currency symbol, a percentage, a date)
 * stays text, because guessing here would sort a column by a number the reader cannot see.
 */
function parseNumeric(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  if (!/^[+-]?(\d+|\d{1,3}(,\d{3})+)(\.\d+)?([eE][+-]?\d+)?$/.test(trimmed)) return null;
  const parsed = Number(trimmed.replace(/,/g, ''));
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * Which columns are numeric, judged over every delivered row (not the filtered view, so alignment
 * does not flicker as you type). A column needs at least one number and no non-numeric non-blank.
 */
const numericColumns = computed<boolean[]>(() =>
  header.value.map((_, c) => {
    let sawNumber = false;
    for (const row of dataRows.value) {
      const cell = row.cells[c] ?? '';
      if (cell.trim() === '') continue;
      if (parseNumeric(cell) === null) return false;
      sawNumber = true;
    }
    return sawNumber;
  })
);

const filteredRows = computed<NumberedRow[]>(() => {
  const query = filter.value.trim().toLowerCase();
  if (query === '') return dataRows.value;
  return dataRows.value.filter((row) => row.cells.some((cell) => cell.toLowerCase().includes(query)));
});

const visibleRows = computed<NumberedRow[]>(() => {
  const column = sortColumn.value;
  if (column === null) return filteredRows.value;

  const numeric = numericColumns.value[column] ?? false;
  const factor = sortDirection.value === 'asc' ? 1 : -1;
  // Copy before sorting: `filteredRows` may be `dataRows` itself, and sorting a computed's array in
  // place would mutate the source order the "no sort" state has to restore.
  return [...filteredRows.value].sort((left, right) => {
    const a = left.cells[column] ?? '';
    const b = right.cells[column] ?? '';
    // Blanks sink in BOTH directions. They are absence, not a smallest value, so parking them at the
    // top of a descending sort would bury the rows the reader asked to see.
    if (a.trim() === '' || b.trim() === '') {
      if (a.trim() === b.trim()) return left.index - right.index;
      return a.trim() === '' ? 1 : -1;
    }
    const compared = numeric
      ? (parseNumeric(a) ?? 0) - (parseNumeric(b) ?? 0)
      : a.localeCompare(b, undefined, { sensitivity: 'base' });
    // Ties fall back to the delivered order, which is what makes the sort stable.
    return compared === 0 ? left.index - right.index : compared * factor;
  });
});

const sheetTruncated = computed(() => activeSheet.value?.truncated ?? false);

function ariaSort(column: number): 'ascending' | 'descending' | 'none' {
  if (sortColumn.value !== column) return 'none';
  return sortDirection.value === 'asc' ? 'ascending' : 'descending';
}

/** Ascending, then descending, then back to the delivered order. */
function toggleSort(column: number): void {
  if (sortColumn.value !== column) {
    sortColumn.value = column;
    sortDirection.value = 'asc';
  } else if (sortDirection.value === 'asc') {
    sortDirection.value = 'desc';
  } else {
    sortColumn.value = null;
  }
}

function selectSheet(index: number): void {
  activeSheetIndex.value = index;
}

// A sort is an index into THIS sheet's columns and a filter is a query against its values; carrying
// either onto another sheet silently sorts or hides by something the reader never chose.
watch(activeSheetIndex, () => {
  sortColumn.value = null;
  sortDirection.value = 'asc';
  filter.value = '';
});
</script>

<template>
  <div class="data-table-preview" data-testid="data-table-preview">
    <div v-if="table.sheets.length > 1" class="data-table-sheets" role="tablist" aria-label="Worksheets">
      <button
        v-for="(sheet, index) in table.sheets"
        :key="sheet.name + index"
        type="button"
        role="tab"
        class="data-table-sheet"
        :class="{ 'data-table-sheet-active': index === activeSheetIndex }"
        :aria-selected="index === activeSheetIndex"
        :data-sheet="sheet.name"
        data-testid="data-table-sheet-tab"
        @click="selectSheet(index)"
      >
        {{ sheet.name }}
      </button>
    </div>

    <div v-if="hasRows" class="data-table-toolbar">
      <input
        v-model="filter"
        type="search"
        class="data-table-filter"
        data-testid="data-table-filter"
        placeholder="Filter rows"
        aria-label="Filter rows"
      />
      <span class="data-table-count" data-testid="data-table-row-count">
        {{ filteredRows.length }} of {{ dataRows.length }} rows
      </span>
    </div>

    <div v-if="hasRows" class="data-table-scroll" data-testid="data-table-scroll">
      <table class="artifact-preview-table" data-testid="artifact-preview-table">
        <thead>
          <tr>
            <th class="data-table-gutter" scope="col"><span class="data-table-sr-only">Row</span></th>
            <th
              v-for="(cell, c) in header"
              :key="c"
              scope="col"
              tabindex="0"
              role="columnheader"
              data-testid="data-table-header"
              :aria-sort="ariaSort(c)"
              :class="{ 'data-table-numeric': numericColumns[c] }"
              @click="toggleSort(c)"
              @keydown.enter.prevent="toggleSort(c)"
              @keydown.space.prevent="toggleSort(c)"
            >
              <span class="data-table-header-label">{{ cell }}</span>
              <span v-if="sortColumn === c" class="data-table-sort-mark" aria-hidden="true">{{
                sortDirection === 'asc' ? '▲' : '▼'
              }}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in visibleRows" :key="row.index">
            <td class="data-table-gutter" data-testid="data-table-row-number">{{ row.index + 1 }}</td>
            <td v-for="(cell, c) in row.cells" :key="c" :class="{ 'data-table-numeric': numericColumns[c] }">
              {{ cell }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-else class="data-table-message" data-testid="data-table-empty">This sheet is empty.</div>

    <div
      v-if="sheetTruncated || table.truncated"
      class="data-table-message"
      data-testid="data-table-truncation-note"
    >
      <span v-if="sheetTruncated">
        Showing the first {{ dataRows.length }} rows. Download the file to see all of it.
      </span>
      <span v-if="table.truncated">
        Only the first {{ table.sheets.length }} sheets are shown.
      </span>
    </div>
  </div>
</template>

<style scoped>
.data-table-preview {
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 8px;
  gap: 8px;
}

.data-table-sheets {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.data-table-sheet {
  padding: 3px 10px;
  border: 1px solid #e0e0e0;
  border-radius: 999px;
  background: #fff;
  font-size: 12px;
  color: #444;
  cursor: pointer;
}

.data-table-sheet-active {
  border-color: #007bff;
  background: #eaf3ff;
  color: #0b5ed7;
  font-weight: 600;
}

.data-table-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.data-table-filter {
  flex: 0 1 220px;
  padding: 4px 8px;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  font-size: 12px;
}

.data-table-count {
  font-size: 11px;
  color: #777;
}

.data-table-scroll {
  overflow: auto;
  min-height: 0;
}

.artifact-preview-table {
  border-collapse: collapse;
  font-size: 12px;
}

.artifact-preview-table th,
.artifact-preview-table td {
  border: 1px solid #e0e0e0;
  padding: 4px 8px;
  text-align: left;
  vertical-align: top;
  white-space: pre-wrap;
}

.artifact-preview-table thead th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: #f3f4f6;
  font-weight: 600;
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
}

.artifact-preview-table td.data-table-numeric,
.artifact-preview-table th.data-table-numeric {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.data-table-gutter {
  position: sticky;
  left: 0;
  background: #f3f4f6;
  color: #999;
  text-align: right;
  font-variant-numeric: tabular-nums;
  cursor: default;
}

.artifact-preview-table thead th.data-table-gutter {
  z-index: 2;
}

.data-table-sort-mark {
  margin-left: 4px;
  font-size: 9px;
  color: #0b5ed7;
}

.data-table-sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

.data-table-message {
  padding: 4px 2px;
  color: #777;
  font-size: 12px;
}
</style>
