import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ContextCostPanel from '@/components/ContextCostPanel.vue';
import type { ContextReportStatus } from '@/composables/useContextReport';
import type { CompactionControlView } from '@/composables/useManualCompaction';
import type { ContextRowView, ContextTotalView } from '@/utils/contextReport';

/**
 * The panel is stateless/presentational, like TodoBoardPanel: it takes the view rows (built by
 * `useContextReport` through the shared pure helpers) and renders them. Its own responsibilities,
 * pinned here, are the ones the helpers cannot carry: the DOM structure a screen reader walks, the
 * keyboard model, and that every distinct state reaches the page as distinct text AND distinct aria.
 */

function row(overrides: Partial<ContextRowView> = {}): ContextRowView {
  return {
    agentId: 'root',
    threadId: 't1',
    parentAgentId: null,
    executionKind: 'Primary',
    modelId: 'claude-sonnet-4-5-20250929',
    capacity: {
      kind: 'known',
      used: 6_000,
      window: 200_000,
      reserve: 8_000,
      utilization: 6_000 / 192_000,
      provenance: 'Estimated',
      scope: 'history',
    },
    tokens: { kind: 'value', input: 100, output: 40, cacheRead: 0, cacheWrite: 0, reasoning: 0, total: 140 },
    cost: { kind: 'value', micros: 700, provenance: 'PublicEstimate', completeness: 'Complete' },
    freshness: 'Fresh',
    cacheTemperature: 'Hot',
    compaction: { state: 'None', checkpointId: null, reason: null, decision: null },
    generationOrdinal: 2,
    observedAtUtc: '2026-09-02T10:00:02Z',
    provisional: false,
    ...overrides,
  };
}

function total(overrides: Partial<ContextTotalView> = {}): ContextTotalView {
  return {
    tokens: { kind: 'value', input: 100, output: 40, cacheRead: 0, cacheWrite: 0, reasoning: 0, total: 140 },
    cost: { kind: 'value', micros: 700, provenance: 'PublicEstimate', completeness: 'Complete' },
    usageCompleteness: 'Complete',
    ...overrides,
  };
}

function mountPanel(
  rows: ContextRowView[],
  opts: { total?: ContextTotalView | null; status?: ContextReportStatus; generatedAtUtc?: string | null } = {}
) {
  return mount(ContextCostPanel, {
    props: {
      rows,
      total: opts.total === undefined ? total() : opts.total,
      status: opts.status ?? 'ready',
      generatedAtUtc: opts.generatedAtUtc === undefined ? '2026-09-02T10:00:05Z' : opts.generatedAtUtc,
    },
    attachTo: document.body,
  });
}

describe('ContextCostPanel — structure', () => {
  it('is a labelled region with a collapsed-by-default body behind an aria-expanded toggle', async () => {
    const wrapper = mountPanel([row()]);
    const region = wrapper.get('[data-testid="context-panel"]');
    expect(region.attributes('role')).toBe('region');
    expect(region.attributes('aria-label')).toBe('Context and cost');

    const toggle = wrapper.get('[data-testid="context-panel-toggle"]');
    expect(toggle.element.tagName).toBe('BUTTON');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    const body = wrapper.get(`#${toggle.attributes('aria-controls')}`);
    expect((body.element as HTMLElement).style.display).toBe('none');

    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect((body.element as HTMLElement).style.display).not.toBe('none');
    wrapper.unmount();
  });

  it('shows the root utilization and the conversation total in the always-visible summary', () => {
    const wrapper = mountPanel([row()]);
    const summary = wrapper.get('[data-testid="context-panel-summary"]').text();
    expect(summary).toContain('3.1%');
    expect(summary).toContain('$0.0007');
    wrapper.unmount();
  });

  it('says a full-request figure includes tool definitions and the system prompt, and a history figure does not', async () => {
    const request = row({
      capacity: { kind: 'known', used: 12_880, window: 18_000, reserve: 1_024, utilization: 12_880 / 16_976, provenance: 'Estimated', scope: 'request' },
    });
    const wrapper = mountPanel([request]);
    const summary = wrapper.get('[data-testid="context-panel-summary"]');
    expect(summary.text()).toContain('76% of 18,000');
    expect(summary.attributes('title')).toBe('Includes tool definitions and the system prompt');

    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-capacity"]').attributes('title')).toBe(
      'Includes tool definitions and the system prompt'
    );
    await wrapper.get('[data-testid="context-row-details-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-window-detail"]').text()).toContain(
      '12,880 in use (estimated, including tool definitions and the system prompt)'
    );
    wrapper.unmount();

    const history = mountPanel([row()]);
    expect(history.get('[data-testid="context-panel-summary"]').attributes('title')).toBeUndefined();
    await history.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(history.get('[data-testid="context-capacity"]').attributes('title')).toBeUndefined();
    await history.get('[data-testid="context-row-details-toggle"]').trigger('click');
    expect(history.get('[data-testid="context-window-detail"]').text()).toContain('6,000 in use (estimated)');
    history.unmount();
  });

  it('marks a post-compaction figure as after compaction in the summary and the Window detail', async () => {
    const after = row({
      capacity: {
        kind: 'known',
        used: 10_300,
        window: 18_000,
        reserve: 1_024,
        utilization: 10_300 / 16_976,
        provenance: 'Estimated',
        scope: 'request',
        afterCompaction: true,
      },
    });
    const wrapper = mountPanel([after]);
    const summary = wrapper.get('[data-testid="context-panel-summary"]');
    expect(summary.text()).toContain('61% of 18,000 after compaction');
    expect(summary.attributes('title')).toBe('After compaction. Includes tool definitions and the system prompt');

    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-capacity"]').text()).toBe('61% of 18,000 tokens (estimated, after compaction)');
    await wrapper.get('[data-testid="context-row-details-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-window-detail"]').text()).toContain(
      '10,300 in use (estimated, after compaction, including tool definitions and the system prompt)'
    );
    wrapper.unmount();
  });

  it('notes a compaction kept without a summary under the gauge, with the raw reason as a tooltip', async () => {
    const fallback = mountPanel([
      row({ compaction: { state: 'Active', checkpointId: 'cp-1', reason: null, decision: null, summaryFallback: 'validation_failed:V3' } }),
    ]);
    await fallback.get('[data-testid="context-panel-toggle"]').trigger('click');
    const note = fallback.get('[data-testid="context-summary-fallback"]');
    expect(note.text()).toBe('Last compaction kept the turns without a summary. Earlier details can be recalled.');
    expect(note.attributes('title')).toBe('validation_failed:V3');
    expect(note.element.closest('td')?.getAttribute('data-label')).toBe('Context window');
    fallback.unmount();

    for (const summaryFallback of [null, undefined]) {
      const normal = mountPanel([row({ compaction: { state: 'Active', checkpointId: 'cp-1', reason: null, decision: null, summaryFallback } })]);
      await normal.get('[data-testid="context-panel-toggle"]').trigger('click');
      expect(normal.find('[data-testid="context-summary-fallback"]').exists()).toBe(false);
      normal.unmount();
    }
  });

  it('renders one table row per agent, root first, and one total row in the footer', async () => {
    const wrapper = mountPanel([
      row(),
      row({ agentId: 'a1', threadId: 'sub-1', parentAgentId: 'root', executionKind: 'SubAgent' }),
      row({ agentId: 'a2', threadId: 'sub-2', parentAgentId: 'root', executionKind: 'SubAgent' }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');

    const rows = wrapper.findAll('[data-testid="context-row"]');
    expect(rows.map((r) => r.attributes('data-agent-id'))).toEqual(['root', 'a1', 'a2']);
    expect(rows[0].text()).toContain('Main agent');
    expect(rows[1].text()).toContain('sub-agent');

    const tfoot = wrapper.get('tfoot [data-testid="context-total"]');
    expect(tfoot.text()).toContain('Total');
    expect(wrapper.get('[data-testid="context-total-cost"]').text()).toBe('$0.0007 (public estimate, complete)');
    expect(wrapper.get('[data-testid="context-total-tokens"]').text()).toContain('140');
    expect(wrapper.get('[data-testid="context-total-completeness"]').text()).toContain('complete');
    // Every data cell carries its column label, which is what the narrow layout stacks on.
    const unlabeled = wrapper.findAll('tbody td').filter((td) => !td.attributes('data-label'));
    expect(unlabeled).toHaveLength(0);
    wrapper.unmount();
  });
});

describe('ContextCostPanel — zero is not unknown', () => {
  it('gives 0% a meter at 0 and the unknown/unsupported/unobserved rows no meter and distinct text', async () => {
    const wrapper = mountPanel([
      row({ capacity: { ...row().capacity, utilization: 0, used: 0 } as ContextRowView['capacity'] }),
      row({ agentId: 'a1', threadId: 'sub-1', capacity: { kind: 'unknown', reason: 'no-window' } }),
      row({ agentId: 'a2', threadId: 'sub-2', capacity: { kind: 'unknown', reason: 'no-observation' }, freshness: 'None' }),
      row({
        agentId: 'a3',
        threadId: 'sub-3',
        capacity: { kind: 'unknown', reason: 'unsupported' },
        freshness: 'None',
        compaction: { state: 'Unsupported', checkpointId: null, reason: null, decision: null },
      }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const rows = wrapper.findAll('[data-testid="context-row"]');

    const zero = rows[0].get('[role="meter"]');
    expect(zero.attributes('aria-valuenow')).toBe('0');
    expect(zero.attributes('aria-valuetext')).toBe('0% of 200,000 tokens (estimated)');
    expect(rows[0].get('[data-testid="context-capacity"]').text()).toBe('0% of 200,000 tokens (estimated)');

    for (const i of [1, 2, 3]) {
      expect(rows[i].find('[role="meter"]').exists()).toBe(false);
    }
    const texts = [1, 2, 3].map((i) => rows[i].get('[data-testid="context-capacity"]').text());
    expect(texts).toEqual(['Unknown window', 'No observation', 'Unsupported (provider-owned session)']);
    const kinds = rows.map((r) => r.get('[data-testid="context-capacity"]').attributes('data-kind'));
    expect(kinds).toEqual(['known', 'no-window', 'no-observation', 'unsupported']);
    wrapper.unmount();
  });

  it('keeps $0.0000, no usage, and unpriced usage apart in text and in a data attribute', async () => {
    const wrapper = mountPanel([
      row({ cost: { kind: 'value', micros: 0, provenance: 'ProviderReported', completeness: 'Complete' } }),
      row({ agentId: 'a1', threadId: 'sub-1', cost: { kind: 'none' }, tokens: { kind: 'none' } }),
      row({ agentId: 'a2', threadId: 'sub-2', cost: { kind: 'unavailable' } }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const cells = wrapper.findAll('[data-testid="context-cost"]');
    expect(cells.map((c) => c.text())).toEqual([
      '$0.0000 (provider-reported)',
      'No usage recorded',
      'Unavailable',
    ]);
    expect(cells.map((c) => c.attributes('data-kind'))).toEqual(['value', 'none', 'unavailable']);
    expect(wrapper.findAll('[data-testid="context-tokens"]')[1].text()).toBe('No usage recorded');
    wrapper.unmount();
  });

  it('labels freshness and cache temperature as words, not colours alone', async () => {
    const wrapper = mountPanel([
      row({ freshness: 'Stale', cacheTemperature: 'Cold' }),
      row({ agentId: 'a1', threadId: 'sub-1', freshness: 'None', cacheTemperature: 'Unknown' }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const rows = wrapper.findAll('[data-testid="context-row"]');
    expect(rows[0].get('[data-testid="context-freshness"]').text()).toBe('Stale');
    expect(rows[0].get('[data-testid="context-temperature"]').text()).toBe('Cold cache');
    expect(rows[1].get('[data-testid="context-freshness"]').text()).toBe('No observation');
    expect(rows[1].get('[data-testid="context-temperature"]').text()).toBe('Cache unknown');
    expect(rows[0].get('[data-testid="context-freshness"]').attributes('data-value')).toBe('Stale');
    expect(rows[1].get('[data-testid="context-freshness"]').attributes('data-value')).toBe('None');
    wrapper.unmount();
  });

  it('marks a provisional (live-only) row so it is not read as an endpoint-confirmed one', async () => {
    const wrapper = mountPanel([row({ provisional: true, cost: { kind: 'none' }, tokens: { kind: 'none' } })]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const r = wrapper.get('[data-testid="context-row"]');
    expect(r.attributes('data-provisional')).toBe('true');
    expect(r.text()).toContain('live only');
    wrapper.unmount();
  });
});

describe('ContextCostPanel — endpoint states', () => {
  it('renders ONE unavailable state with no rows and no metadata (403 and 404 look identical)', async () => {
    const wrapper = mountPanel([], { status: 'unavailable', total: null, generatedAtUtc: null });
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-panel"]').attributes('data-status')).toBe('unavailable');
    expect(wrapper.get('[data-testid="context-unavailable"]').text()).toBe(
      'Context report unavailable for this conversation.'
    );
    expect(wrapper.find('[data-testid="context-table"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="context-generated-at"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="context-panel-summary"]').text()).toBe('unavailable');
    wrapper.unmount();
  });

  it('says it is loading only while nothing is on screen yet, and "refreshing" over existing rows', async () => {
    const empty = mountPanel([], { status: 'loading', total: null, generatedAtUtc: null });
    await empty.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(empty.get('[data-testid="context-loading"]').attributes('role')).toBe('status');
    empty.unmount();

    const refreshing = mountPanel([row()], { status: 'loading' });
    await refreshing.get('[data-testid="context-panel-toggle"]').trigger('click');
    expect(refreshing.find('[data-testid="context-loading"]').exists()).toBe(false);
    expect(refreshing.get('[data-testid="context-generated-at"]').text()).toContain('refreshing');
    refreshing.unmount();
  });
});

describe('ContextCostPanel — per-row details and keyboard', () => {
  it('opens a details row with compaction status, recommendation and observation facts', async () => {
    const wrapper = mountPanel([
      row({
        compaction: {
          state: 'Rejected',
          checkpointId: 'cp-1',
          reason: 'validation_failed',
          decision: { decision: 'skipped', reason: 'cooldown' },
        },
      }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const toggle = wrapper.get('[data-testid="context-row-details-toggle"]');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(toggle.attributes('aria-label')).toBe('Details for Main agent');
    expect(wrapper.find('[data-testid="context-row-details"]').exists()).toBe(false);

    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    const details = wrapper.get('[data-testid="context-row-details"]');
    expect(details.attributes('id')).toBe(toggle.attributes('aria-controls'));
    expect(details.get('[data-testid="context-compaction"]').text()).toBe('Compaction rejected: validation_failed');
    expect(details.get('[data-testid="context-decision"]').text()).toBe('Skipped: cooldown');
    expect(details.text()).toContain('claude-sonnet-4-5-20250929');
    expect(details.text()).toContain('cp-1');
    expect(details.text()).toContain('200,000');
    wrapper.unmount();
  });

  it('says "No decision yet" when no policy has run, distinct from "No action"', async () => {
    const wrapper = mountPanel([
      row(),
      row({
        agentId: 'a1',
        threadId: 'sub-1',
        compaction: { state: 'None', checkpointId: null, reason: null, decision: { decision: 'no_action', reason: null } },
      }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const toggles = wrapper.findAll('[data-testid="context-row-details-toggle"]');
    await toggles[0].trigger('click');
    await toggles[1].trigger('click');
    const decisions = wrapper.findAll('[data-testid="context-decision"]').map((d) => d.text());
    expect(decisions).toEqual(['No decision yet', 'No action']);
    wrapper.unmount();
  });

  it('moves focus between rows with ArrowDown/ArrowUp/Home/End and opens details with Enter', async () => {
    const wrapper = mountPanel([
      row(),
      row({ agentId: 'a1', threadId: 'sub-1' }),
      row({ agentId: 'a2', threadId: 'sub-2' }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    const toggles = wrapper.findAll('[data-testid="context-row-details-toggle"]');

    // Roving tabindex: exactly one row button is in the tab order.
    expect(toggles.map((t) => t.attributes('tabindex'))).toEqual(['0', '-1', '-1']);

    (toggles[0].element as HTMLElement).focus();
    await toggles[0].trigger('keydown', { key: 'ArrowDown' });
    expect(document.activeElement).toBe(toggles[1].element);
    expect(toggles.map((t) => t.attributes('tabindex'))).toEqual(['-1', '0', '-1']);

    await toggles[1].trigger('keydown', { key: 'End' });
    expect(document.activeElement).toBe(toggles[2].element);
    await toggles[2].trigger('keydown', { key: 'ArrowDown' }); // clamps at the last row
    expect(document.activeElement).toBe(toggles[2].element);
    await toggles[2].trigger('keydown', { key: 'Home' });
    expect(document.activeElement).toBe(toggles[0].element);
    await toggles[0].trigger('keydown', { key: 'ArrowUp' }); // clamps at the first row
    expect(document.activeElement).toBe(toggles[0].element);

    // A native button: Enter/Space produce click. Simulate the click the browser would dispatch.
    await toggles[0].trigger('click');
    expect(toggles[0].attributes('aria-expanded')).toBe('true');
    wrapper.unmount();
  });
});

// Manual compaction: the Compact now control lives in the panel header (visible while collapsed), with
// an optional one-line focus prompt. The panel stays presentational: it renders the state it is given
// and emits `compact` with the trimmed focus; `useManualCompaction` owns the request and the frames.
describe('ContextCostPanel — Compact now', () => {
  function control(overrides: Partial<CompactionControlView> = {}): CompactionControlView {
    return { supported: true, phase: 'idle', busy: false, statusText: '', ...overrides };
  }

  function mountWithControl(compaction: CompactionControlView | undefined, rows: ContextRowView[] = [row()]) {
    return mount(ContextCostPanel, {
      props: { rows, total: total(), status: 'ready', generatedAtUtc: null, compaction },
      attachTo: document.body,
    });
  }

  it('is hidden when the host does not advertise manual compaction', () => {
    const absent = mountWithControl(undefined);
    expect(absent.find('[data-testid="compact-now-button"]').exists()).toBe(false);
    absent.unmount();

    const off = mountWithControl(control({ supported: false }));
    expect(off.find('[data-testid="compact-now-button"]').exists()).toBe(false);
    off.unmount();
  });

  it('opens a labelled focus form from the header button, without expanding the table', async () => {
    const wrapper = mountWithControl(control());
    const button = wrapper.get('[data-testid="compact-now-button"]');
    expect(button.attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('[data-testid="compact-form"]').exists()).toBe(false);

    await button.trigger('click');

    expect(button.attributes('aria-expanded')).toBe('true');
    const form = wrapper.get('[data-testid="compact-form"]');
    expect(button.attributes('aria-controls')).toBe(form.attributes('id'));
    const input = wrapper.get('[data-testid="compact-focus-input"]');
    const label = wrapper.get(`label[for="${input.attributes('id')}"]`);
    expect(label.text()).toMatch(/focus/i);
    expect(input.attributes('placeholder')).toMatch(/keep the API design decisions/);
    expect(input.attributes('maxlength')).toBe('2000');
    expect(document.activeElement).toBe(input.element);
    expect(wrapper.get('[data-testid="context-panel-toggle"]').attributes('aria-expanded')).toBe('false');
    wrapper.unmount();
  });

  it('submits the trimmed focus on Enter and closes the form', async () => {
    const wrapper = mountWithControl(control());
    await wrapper.get('[data-testid="compact-now-button"]').trigger('click');
    const input = wrapper.get('[data-testid="compact-focus-input"]');
    await input.setValue('  keep the API decisions  ');

    await input.trigger('keydown', { key: 'Enter' });

    expect(wrapper.emitted('compact')).toEqual([['keep the API decisions']]);
    expect(wrapper.find('[data-testid="compact-form"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('submits an empty focus from the Compact button and cancels on Escape', async () => {
    const wrapper = mountWithControl(control());
    await wrapper.get('[data-testid="compact-now-button"]').trigger('click');
    await wrapper.get('[data-testid="compact-submit-button"]').trigger('click');
    expect(wrapper.emitted('compact')).toEqual([['']]);

    await wrapper.get('[data-testid="compact-now-button"]').trigger('click');
    await wrapper.get('[data-testid="compact-focus-input"]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[data-testid="compact-form"]').exists()).toBe(false);
    expect(wrapper.emitted('compact')).toHaveLength(1);
    wrapper.unmount();
  });

  it.each(['requesting', 'queued', 'running'] as const)(
    'is disabled with a live status and a spinner while %s',
    async (phase) => {
      const wrapper = mountWithControl(control({ phase, busy: true, statusText: 'Compacting…' }));
      const button = wrapper.get('[data-testid="compact-now-button"]');
      expect(button.attributes('disabled')).toBeDefined();
      const status = wrapper.get('[data-testid="compact-status"]');
      expect(status.attributes('role')).toBe('status');
      expect(status.attributes('data-phase')).toBe(phase);
      expect(status.text()).toContain('Compacting…');
      expect(wrapper.find('[data-testid="compact-spinner"]').exists()).toBe(true);
      wrapper.unmount();
    }
  );

  it('closes an open form and refuses to emit once a compaction starts', async () => {
    const wrapper = mountWithControl(control());
    await wrapper.get('[data-testid="compact-now-button"]').trigger('click');

    await wrapper.setProps({ compaction: control({ phase: 'running', busy: true, statusText: 'Compacting…' }) });

    expect(wrapper.find('[data-testid="compact-form"]').exists()).toBe(false);
    expect(wrapper.emitted('compact')).toBeUndefined();
    wrapper.unmount();
  });

  it.each([
    ['InFlight', /in flight|already/i],
    ['Unsupported', /provider/i],
  ] as const)('is disabled when the report says the compaction state is %s', (state, title) => {
    const wrapper = mountWithControl(control(), [
      row({ compaction: { state, checkpointId: null, reason: null, decision: null } }),
    ]);
    const button = wrapper.get('[data-testid="compact-now-button"]');
    expect(button.attributes('disabled')).toBeDefined();
    expect(button.attributes('title')).toMatch(title);
    wrapper.unmount();
  });

  it('shows a refusal message with the control enabled again', () => {
    const wrapper = mountWithControl(
      control({ phase: 'refused', busy: false, statusText: 'Nothing to compact yet.' })
    );
    expect(wrapper.get('[data-testid="compact-now-button"]').attributes('disabled')).toBeUndefined();
    const status = wrapper.get('[data-testid="compact-status"]');
    expect(status.text()).toBe('Nothing to compact yet.');
    expect(wrapper.find('[data-testid="compact-spinner"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('shows Compacting… for an automatic compaction even where the button is hidden', () => {
    const wrapper = mountWithControl(
      control({ supported: false, phase: 'running', busy: true, statusText: 'Compacting…' })
    );
    expect(wrapper.find('[data-testid="compact-now-button"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="compact-status"]').text()).toContain('Compacting…');
    wrapper.unmount();
  });

  it('renders no status line while idle', () => {
    const wrapper = mountWithControl(control());
    expect(wrapper.find('[data-testid="compact-status"]').exists()).toBe(false);
    wrapper.unmount();
  });
});

describe('ContextCostPanel — last decision age', () => {
  it('shows the last decision with its age in the row details', async () => {
    const wrapper = mountPanel([
      row({
        compaction: {
          state: 'Active',
          checkpointId: 'cp-1',
          reason: null,
          decision: { decision: 'compact', reason: 'hard', ageSeconds: 125 },
        },
      }),
    ]);
    await wrapper.get('[data-testid="context-panel-toggle"]').trigger('click');
    await wrapper.get('[data-testid="context-row-details-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="context-decision"]').text()).toBe('Compaction recommended · 2m ago');
    wrapper.unmount();
  });
});
