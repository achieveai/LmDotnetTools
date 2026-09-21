import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ConversationInspector from '@/components/ConversationInspector.vue';
import { TodoStatus, type TodoTask } from '@/types/todo';
import type { SubAgentSummary } from '@/api/subAgentsApi';

const task: TodoTask = { id: '1', status: TodoStatus.Completed, title: 'Done', notes: [], artifacts: ['report.md'], subTasks: [] };
const child: SubAgentSummary = { agentId: 'agent-1', name: 'Reviewer', template: 'review', task: 'Review', status: 'running', threadId: 'child-1', lastActivityUtc: null };
function mountInspector(overrides: Record<string, unknown> = {}) {
  return mount(ConversationInspector, { attachTo: document.body, props: {
    open: true, tasks: [task], hasWork: true, children: [child], activeConversationTabId: 'main', ...overrides,
  }, slots: { preview: '<div data-testid="preview-body">Preview</div>' } });
}

describe('ConversationInspector', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1200 });
    Object.defineProperty(window, 'innerHeight', { configurable: true, writable: true, value: 900 });
  });
  afterEach(() => document.body.replaceChildren());

  it('renders no rail while closed', () => expect(mountInspector({ open: false }).find('[data-testid="conversation-inspector"]').exists()).toBe(false));

  it('keeps Work and Agents independently open with live counts', async () => {
    const wrapper = mountInspector();
    expect(wrapper.get('#inspector-tab-work').attributes('aria-expanded')).toBe('true');
    expect(wrapper.get('#inspector-tab-agents').text()).toContain('1');
    expect(wrapper.find('[data-testid="todo-panel"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(true);
    await wrapper.get('#inspector-tab-work').trigger('click');
    expect(wrapper.get('#inspector-tab-work').attributes('aria-expanded')).toBe('false');
    expect(wrapper.get('#inspector-tab-agents').attributes('aria-expanded')).toBe('true');
  });

  it('preserves artifact and agent events', async () => {
    const wrapper = mountInspector();
    await wrapper.get('[data-testid="todo-artifact-chip"]').trigger('click');
    await wrapper.get('[data-testid="subagent-focus-button"]').trigger('click');
    expect(wrapper.emitted('openArtifact')).toEqual([['report.md']]);
    expect(wrapper.emitted('selectAgent')).toEqual([['agent-1', false]]);
  });

  it('renders file tabs, closes them, and provides a bounded vertical splitter', async () => {
    const tabs = [{ id: 'path:a.md', label: 'a.md', path: 'docs/a.md' }, { id: 'path:b.md', label: 'b.md', path: 'docs/b.md' }];
    const wrapper = mountInspector({ previewTabs: tabs, activePreviewId: tabs[0].id, previewHeight: 360, previewMaxHeight: 600 });
    const fileTabs = wrapper.findAll('[role="tab"]');
    expect(fileTabs).toHaveLength(2);
    expect(fileTabs[0].attributes('tabindex')).toBe('0');
    expect(wrapper.get('[data-testid="workspace-vertical-splitter"]').attributes('aria-valuenow')).toBe('360');
    expect(wrapper.get('[data-testid="workspace-vertical-splitter"]').attributes('aria-controls')).toBe('workspace-preview-panel');
    await fileTabs[0].trigger('keydown', { key: 'ArrowRight' });
    await wrapper.get('[aria-label="Close a.md"]').trigger('click');
    expect(wrapper.emitted('selectPreview')).toEqual([[tabs[1].id]]);
    expect(wrapper.emitted('closePreview')).toEqual([[tabs[0].id]]);
  });

  it('labels the shared preview panel with the active tab and updates when it changes (F-002, #784)', async () => {
    const tabs = [{ id: 'path:a.md', label: 'a.md', path: 'docs/a.md' }, { id: 'path:b.md', label: 'b.md', path: 'docs/b.md' }];
    const wrapper = mountInspector({ previewTabs: tabs, activePreviewId: tabs[0].id });
    expect(wrapper.get('#workspace-preview-panel').attributes('aria-labelledby')).toBe('preview-tab-0');

    await wrapper.setProps({ activePreviewId: tabs[1].id });
    expect(wrapper.get('#workspace-preview-panel').attributes('aria-labelledby')).toBe('preview-tab-1');
  });

  it('uses a drawer at 1100px and no vertical splitter there', async () => {
    const wrapper = mountInspector({ previewTabs: [{ id: 'a', label: 'a', path: 'a' }], activePreviewId: 'a' });
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1100 });
    window.dispatchEvent(new Event('resize')); await wrapper.vm.$nextTick();
    expect(wrapper.get('[data-testid="conversation-inspector"]').attributes('role')).toBe('dialog');
    expect(wrapper.find('[data-testid="workspace-vertical-splitter"]').exists()).toBe(false);
  });

  it('fills the available medium workspace when expanded but keeps phone drawer sizing', async () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 900 });
    const wrapper = mountInspector({
      expanded: true,
      desktopWidth: 620,
      previewTabs: [{ id: 'a', label: 'a', path: 'a' }],
      activePreviewId: 'a',
    });
    await wrapper.vm.$nextTick();
    const inspector = wrapper.get<HTMLElement>('[data-testid="conversation-inspector"]');
    expect(inspector.element.style.width).toBe('620px');

    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 390 });
    window.dispatchEvent(new Event('resize'));
    await wrapper.vm.$nextTick();
    expect(inspector.element.style.width).toBe('');
    expect(inspector.classes()).toContain('overlay');
  });

  it('uses one scroll flow without a vertical splitter on short wide screens', async () => {
    Object.defineProperty(window, 'innerHeight', { configurable: true, writable: true, value: 600 });
    const wrapper = mountInspector({
      previewTabs: [{ id: 'a', label: 'a', path: 'a' }],
      activePreviewId: 'a',
    });
    await wrapper.vm.$nextTick();

    expect(wrapper.get('[data-testid="conversation-inspector"]').classes()).not.toContain('overlay');
    expect(wrapper.find('[data-testid="workspace-vertical-splitter"]').exists()).toBe(false);
  });

  it('does not close the inspector for Escape inside a nested dialog', async () => {
    const wrapper = mountInspector();
    const nested = document.createElement('div'); nested.setAttribute('role', 'dialog');
    const button = document.createElement('button'); nested.appendChild(button);
    wrapper.get('[data-testid="conversation-inspector"]').element.appendChild(nested);
    button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(wrapper.emitted('close')).toBeUndefined();
    await wrapper.get('#inspector-tab-work').trigger('keydown', { key: 'Escape' });
    expect(wrapper.emitted('close')).toHaveLength(1);
  });

  it('hides monitoring without unmounting it in expanded reading', () => {
    const wrapper = mountInspector({ expanded: true, previewTabs: [{ id: 'a', label: 'a', path: 'a' }], activePreviewId: 'a' });
    expect(wrapper.get('[data-testid="workspace-monitoring-region"]').attributes('style')).toContain('display: none');
    expect(wrapper.find('[data-testid="todo-panel"]').exists()).toBe(true);
  });
});

/**
 * The Files disclosure (bug: "make the file explorer visible in the right panel"). It is the third
 * section beside Work and Agents and is LAZY: the inspector auto-opens in developer view for every
 * conversation, so an eagerly mounted browser would issue a listing request for conversations the
 * user never looks at.
 */
describe('ConversationInspector Files section', () => {
  const FileBrowserStub = {
    name: 'FileBrowser',
    props: {
      threadId: { type: String as unknown as () => string | null, default: null },
      embedded: { type: Boolean, default: false },
    },
    emits: ['openFile'],
    template:
      '<div data-testid="file-browser-stub" @click="$emit(\'openFile\', \'docs/report.md\')"></div>',
  };

  function mountWithFiles(overrides: Record<string, unknown> = {}) {
    return mount(ConversationInspector, {
      attachTo: document.body,
      props: {
        open: true,
        tasks: [task],
        hasWork: true,
        children: [child],
        activeConversationTabId: 'main',
        filesThreadId: 'thread-1',
        ...overrides,
      },
      global: { stubs: { FileBrowser: FileBrowserStub } },
      slots: { preview: '<div data-testid="preview-body">Preview</div>' },
    });
  }

  beforeEach(() => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1200 });
    Object.defineProperty(window, 'innerHeight', { configurable: true, writable: true, value: 900 });
  });
  afterEach(() => document.body.replaceChildren());

  it('mounts FileBrowser on first expand and keeps it mounted (state intact) when collapsed', async () => {
    const wrapper = mountWithFiles();

    // Collapsed by default, and NOTHING is mounted behind it yet.
    expect(wrapper.get('#inspector-tab-files').attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('[data-testid="file-browser-stub"]').exists()).toBe(false);

    await wrapper.get('#inspector-tab-files').trigger('click');
    expect(wrapper.get('#inspector-tab-files').attributes('aria-expanded')).toBe('true');
    expect(wrapper.find('[data-testid="file-browser-stub"]').exists()).toBe(true);
    expect(wrapper.getComponent(FileBrowserStub).props('threadId')).toBe('thread-1');

    // Collapsing HIDES it (v-show + inert) rather than unmounting: path, filter, scroll and any
    // in-flight upload survive a collapse.
    await wrapper.get('#inspector-tab-files').trigger('click');
    expect(wrapper.find('[data-testid="file-browser-stub"]').exists()).toBe(true);
    expect(wrapper.get('#inspector-panel-files').attributes('inert')).toBeDefined();
    expect(wrapper.get('#inspector-panel-files').attributes('style')).toContain('display: none');
  });

  it('orders the monitoring sections Work, Files, Agents', () => {
    const wrapper = mountWithFiles();
    const headings = wrapper
      .get('[data-testid="workspace-monitoring-region"]')
      .findAll('h3 button')
      .map((button) => button.attributes('id'));
    expect(headings).toEqual(['inspector-tab-work', 'inspector-tab-files', 'inspector-tab-agents']);
  });

  it('revealSection("files") expands Files and focuses its disclosure', async () => {
    const wrapper = mountWithFiles();
    (wrapper.vm as unknown as { revealSection: (section: string) => void }).revealSection('files');
    await nextTick();
    await nextTick();

    expect(wrapper.get('#inspector-tab-files').attributes('aria-expanded')).toBe('true');
    expect(wrapper.find('[data-testid="file-browser-stub"]').exists()).toBe(true);
    expect(document.activeElement).toBe(wrapper.get('#inspector-tab-files').element);
  });

  it('forwards the browser open-file as openArtifact so it opens as a preview TAB', async () => {
    const wrapper = mountWithFiles();
    await wrapper.get('#inspector-tab-files').trigger('click');
    await wrapper.get('[data-testid="file-browser-stub"]').trigger('click');

    expect(wrapper.emitted('openArtifact')).toEqual([['docs/report.md']]);
  });
});
