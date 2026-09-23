import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ref } from 'vue';
import { MessageType } from '@/types';
import { WORKSPACE_FILE_LINKS } from '@/utils/workspaceLinks';
import TextMessage from '@/components/TextMessage.vue';

vi.mock('@/components/SmilesViewer.vue', () => ({
  default: {
    props: ['source'],
    template: '<figure class="smiles-proof">{{ source }}</figure>',
  },
}));
vi.mock('@/components/DiagramViewer.vue', () => ({
  default: { props: ['source', 'language'], template: '<figure class="diagram-proof" />' },
}));

const message = (text: string) => ({ $type: MessageType.Text, role: 'assistant' as const, text });
const wrappers: ReturnType<typeof mount>[] = [];
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount());
  document.body.innerHTML = '';
});

type MessageProps = InstanceType<typeof TextMessage>['$props'];

function mountMessage(props: MessageProps, provide?: Record<string | symbol, unknown>) {
  const wrapper = mount(TextMessage, { props, global: { provide }, attachTo: document.body });
  wrappers.push(wrapper);
  return wrapper;
}

describe('TextMessage rich markdown', () => {
  it('hoists a ```smiles fence into a viewer and leaves other fences alone', async () => {
    const wrapper = mountMessage({
      message: message('```smiles\nCCO\n```\n\n```mermaid\ngraph LR; A-->B\n```\n\n```ts\nconst x = 1;\n```'),
    });
    await flushPromises();
    expect(wrapper.findAll('.smiles-proof')).toHaveLength(1);
    expect(wrapper.find('.smiles-proof').text()).toContain('CCO');
    expect(wrapper.findAll('.diagram-proof')).toHaveLength(1);
    expect(wrapper.find('code.language-ts').exists()).toBe(true);
  });

  it('holds the SMILES fence as source while the message is still streaming', async () => {
    const wrapper = mountMessage({ message: message('```smiles\nCCO\n```'), isComplete: false });
    await flushPromises();
    expect(wrapper.find('.smiles-proof').exists()).toBe(false);
    expect(wrapper.find('pre code').text()).toContain('CCO');
    await wrapper.setProps({ isComplete: true });
    await flushPromises();
    expect(wrapper.findAll('.smiles-proof')).toHaveLength(1);
  });

  it('renders math in a chat bubble while streaming and after completion', async () => {
    const wrapper = mountMessage({ message: message('Recall $E = mc^2$.'), isComplete: false });
    await flushPromises();
    expect(wrapper.find('.katex').exists()).toBe(true);
    await wrapper.setProps({ isComplete: true });
    await flushPromises();
    expect(wrapper.find('.katex').exists()).toBe(true);
  });

  /*
   * The file preview (`ArtifactPreviewModal.vue`) renders a markdown artifact through THIS component
   * with exactly these props. Pinned here rather than there so the preview keeps math, chemistry and
   * SMILES without that component having to know they exist.
   */
  it('renders equations, chemistry and SMILES on the file-preview path', async () => {
    const threadId = ref('thread-1');
    const wrapper = mountMessage(
      {
        message: message(
          '# Reaction\n\n$$\\ce{2H2 + O2 -> 2H2O}$$\n\nRate is $k[A]^2$.\n\n' +
            '```smiles\nCCO ethanol\n```\n\n[notes](notes.md)'
        ),
        isComplete: true,
        workspaceLinks: true,
        workspaceLinkBaseDir: 'docs',
      },
      { [WORKSPACE_FILE_LINKS]: { threadId, open: vi.fn() } }
    );
    await flushPromises();
    expect(wrapper.find('.katex-display').exists()).toBe(true);
    expect(wrapper.findAll('.katex').length).toBeGreaterThanOrEqual(2);
    expect(wrapper.find('.smiles-proof').text()).toContain('ethanol');
    expect(wrapper.get('a.workspace-link').attributes('href')).toContain(
      `target=${encodeURIComponent('docs/notes.md')}`
    );
  });
});
