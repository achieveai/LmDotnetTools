import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import DataTablePreview from '@/components/DataTablePreview.vue';
import type { SheetPreview, TablePreview } from '@/types/fileBrowser';

/**
 * The tabular viewer shared by the CSV/TSV preview (parsed on the client) and the XLSX preview
 * (parsed on the server). Both hand it the same `TablePreview` shape, so every behaviour here —
 * sorting, filtering, sheet tabs, numeric alignment, truncation — holds for both sources.
 */

const sheet = (name: string, rows: string[][], truncated = false): SheetPreview => ({ name, rows, truncated });

const table = (sheets: SheetPreview[], truncated = false): TablePreview => ({ sheets, truncated });

/** name | qty | note — `qty` is numeric, `name`/`note` are not. */
const sales = () =>
  table([
    sheet('items', [
      ['name', 'qty', 'note'],
      ['pear', '2', 'ripe'],
      ['apple', '10', 'crisp'],
      ['fig', '9', 'ripe'],
    ]),
  ]);

const mountTable = (t: TablePreview) => mount(DataTablePreview, { props: { table: t } });

const headers = (w: ReturnType<typeof mountTable>) =>
  w.findAll('[data-testid="data-table-header"]').map((h) => h.text());

/** The body rows, gutter cell excluded. */
const bodyRows = (w: ReturnType<typeof mountTable>) =>
  w.findAll('tbody tr').map((tr) => tr.findAll('td:not(.data-table-gutter)').map((td) => td.text()));

const rowNumbers = (w: ReturnType<typeof mountTable>) =>
  w.findAll('[data-testid="data-table-row-number"]').map((td) => td.text());

describe('DataTablePreview — structure', () => {
  it('renders the first row as the header and the rest as body rows', () => {
    const wrapper = mountTable(sales());

    expect(wrapper.find('[data-testid="artifact-preview-table"]').exists()).toBe(true);
    expect(headers(wrapper)).toEqual(['name', 'qty', 'note']);
    expect(bodyRows(wrapper)).toEqual([
      ['pear', '2', 'ripe'],
      ['apple', '10', 'crisp'],
      ['fig', '9', 'ripe'],
    ]);
  });

  it('numbers the body rows in a gutter that is not a data column', () => {
    const wrapper = mountTable(sales());

    expect(rowNumbers(wrapper)).toEqual(['1', '2', '3']);
    // The gutter must not leak into the data cells, or every consumer's column indexes shift by one.
    expect(bodyRows(wrapper)[0]).toHaveLength(3);
  });

  it('pads a short row so its cells stay under the right headers', () => {
    const wrapper = mountTable(table([sheet('s', [['a', 'b', 'c'], ['1']])]));

    expect(bodyRows(wrapper)).toEqual([['1', '', '']]);
  });

  it('says so when a sheet has no rows at all', () => {
    const wrapper = mountTable(table([sheet('blank', [])]));

    expect(wrapper.find('[data-testid="data-table-empty"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="artifact-preview-table"]').exists()).toBe(false);
  });

  it('keeps the header row visible while the body scrolls', () => {
    // Stickiness is purely CSS, and a mounted component in jsdom carries no stylesheet, so asserting
    // a computed style here would assert nothing. The shipped rule is the artifact that matters.
    const source = readFileSync(resolve(__dirname, '../../components/DataTablePreview.vue'), 'utf8');
    const styles = source.slice(source.indexOf('<style'));

    expect(styles).toMatch(/thead\s+th\s*\{[^}]*position:\s*sticky/);
    expect(mountTable(sales()).find('[data-testid="data-table-scroll"]').exists()).toBe(true);
  });
});

describe('DataTablePreview — sorting', () => {
  it('cycles a column through ascending, descending and back to the source order', async () => {
    const wrapper = mountTable(sales());
    const nameHeader = wrapper.findAll('[data-testid="data-table-header"]')[0];

    await nameHeader.trigger('click');
    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['apple', 'fig', 'pear']);
    expect(nameHeader.attributes('aria-sort')).toBe('ascending');

    await nameHeader.trigger('click');
    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['pear', 'fig', 'apple']);
    expect(nameHeader.attributes('aria-sort')).toBe('descending');

    await nameHeader.trigger('click');
    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['pear', 'apple', 'fig']);
    expect(nameHeader.attributes('aria-sort')).toBe('none');
  });

  it('sorts a numeric column by value, not by text', async () => {
    const wrapper = mountTable(sales());

    await wrapper.findAll('[data-testid="data-table-header"]')[1].trigger('click');

    // Lexicographic order would be 10, 2, 9 — this is the case that distinguishes the two.
    expect(bodyRows(wrapper).map((r) => r[1])).toEqual(['2', '9', '10']);
  });

  it('keeps the row numbers with their original rows when sorted', async () => {
    const wrapper = mountTable(sales());

    await wrapper.findAll('[data-testid="data-table-header"]')[0].trigger('click');

    // apple was source row 2, fig row 3, pear row 1 — a row number that renumbered on sort would be
    // useless for finding the row back in the real file.
    expect(rowNumbers(wrapper)).toEqual(['2', '3', '1']);
  });

  it('is stable: rows that tie keep their source order', async () => {
    const wrapper = mountTable(
      table([
        sheet('s', [
          ['group', 'id'],
          ['b', 'first'],
          ['a', 'second'],
          ['a', 'third'],
          ['a', 'fourth'],
        ]),
      ])
    );

    await wrapper.findAll('[data-testid="data-table-header"]')[0].trigger('click');

    expect(bodyRows(wrapper).map((r) => r[1])).toEqual(['second', 'third', 'fourth', 'first']);
  });

  it('sorts blanks last in both directions rather than treating them as a value', async () => {
    const wrapper = mountTable(
      table([
        sheet('s', [
          ['n'],
          ['3'],
          [''],
          ['1'],
        ]),
      ])
    );
    const header = wrapper.findAll('[data-testid="data-table-header"]')[0];

    await header.trigger('click');
    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['1', '3', '']);

    await header.trigger('click');
    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['3', '1', '']);
  });
});

describe('DataTablePreview — numeric columns', () => {
  it('right-aligns a column whose values are all numbers', () => {
    const wrapper = mountTable(sales());
    const firstRow = wrapper.findAll('tbody tr')[0].findAll('td:not(.data-table-gutter)');

    expect(firstRow[0].classes()).not.toContain('data-table-numeric');
    expect(firstRow[1].classes()).toContain('data-table-numeric');
    expect(firstRow[2].classes()).not.toContain('data-table-numeric');
  });

  it('does not treat a column as numeric when a single value is not a number', () => {
    const wrapper = mountTable(table([sheet('s', [['n'], ['1'], ['2'], ['n/a']])]));

    expect(wrapper.findAll('tbody tr')[0].findAll('td:not(.data-table-gutter)')[0].classes()).not.toContain(
      'data-table-numeric'
    );
  });

  it('ignores blanks when deciding a column is numeric', () => {
    const wrapper = mountTable(table([sheet('s', [['n'], ['1'], [''], ['2']])]));

    expect(wrapper.findAll('tbody tr')[0].findAll('td:not(.data-table-gutter)')[0].classes()).toContain(
      'data-table-numeric'
    );
  });
});

describe('DataTablePreview — filtering', () => {
  it('keeps only the rows containing the query, case-insensitively, across all columns', async () => {
    const wrapper = mountTable(sales());

    await wrapper.get('[data-testid="data-table-filter"]').setValue('RIPE');

    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['pear', 'fig']);
  });

  it('never filters the header row away', async () => {
    const wrapper = mountTable(sales());

    await wrapper.get('[data-testid="data-table-filter"]').setValue('zzz-no-such-value');

    expect(headers(wrapper)).toEqual(['name', 'qty', 'note']);
    expect(bodyRows(wrapper)).toEqual([]);
    expect(wrapper.get('[data-testid="data-table-row-count"]').text()).toContain('0 of 3');
  });

  it('applies the filter before the sort, and keeps the original row numbers', async () => {
    const wrapper = mountTable(sales());

    await wrapper.get('[data-testid="data-table-filter"]').setValue('ripe');
    await wrapper.findAll('[data-testid="data-table-header"]')[0].trigger('click');

    expect(bodyRows(wrapper).map((r) => r[0])).toEqual(['fig', 'pear']);
    expect(rowNumbers(wrapper)).toEqual(['3', '1']);
  });
});

describe('DataTablePreview — sheets', () => {
  const workbook = () =>
    table([
      sheet('Summary', [['a'], ['1']]),
      sheet('Detail', [['b'], ['2']]),
    ]);

  it('shows no tab strip for a single-sheet table', () => {
    expect(mountTable(sales()).find('[data-testid="data-table-sheet-tab"]').exists()).toBe(false);
  });

  it('shows one tab per sheet and starts on the first', () => {
    const wrapper = mountTable(workbook());
    const tabs = wrapper.findAll('[data-testid="data-table-sheet-tab"]');

    expect(tabs.map((t) => t.text())).toEqual(['Summary', 'Detail']);
    expect(tabs[0].attributes('aria-selected')).toBe('true');
    expect(headers(wrapper)).toEqual(['a']);
  });

  it('switches the rendered sheet when a tab is clicked', async () => {
    const wrapper = mountTable(workbook());

    await wrapper.findAll('[data-testid="data-table-sheet-tab"]')[1].trigger('click');

    expect(headers(wrapper)).toEqual(['b']);
    expect(bodyRows(wrapper)).toEqual([['2']]);
    expect(wrapper.findAll('[data-testid="data-table-sheet-tab"]')[1].attributes('aria-selected')).toBe('true');
  });

  it('clears the sort and the filter when switching sheets, so neither carries over to other columns', async () => {
    const wrapper = mountTable(workbook());
    await wrapper.get('[data-testid="data-table-filter"]').setValue('1');
    await wrapper.findAll('[data-testid="data-table-header"]')[0].trigger('click');

    await wrapper.findAll('[data-testid="data-table-sheet-tab"]')[1].trigger('click');

    expect((wrapper.get('[data-testid="data-table-filter"]').element as HTMLInputElement).value).toBe('');
    expect(wrapper.findAll('[data-testid="data-table-header"]')[0].attributes('aria-sort')).toBe('none');
    expect(bodyRows(wrapper)).toEqual([['2']]);
  });
});

describe('DataTablePreview — truncation', () => {
  it('says how much of a truncated sheet is shown', () => {
    const wrapper = mountTable(table([sheet('s', [['a'], ['1'], ['2']], true)]));

    expect(wrapper.get('[data-testid="data-table-truncation-note"]').text()).toContain('first 2 rows');
  });

  it('says nothing when the sheet is complete', () => {
    expect(mountTable(sales()).find('[data-testid="data-table-truncation-note"]').exists()).toBe(false);
  });

  it('says so when whole sheets were dropped', () => {
    const wrapper = mountTable(table([sheet('s', [['a'], ['1']])], true));

    expect(wrapper.get('[data-testid="data-table-truncation-note"]').text()).toContain('sheets');
  });
});
