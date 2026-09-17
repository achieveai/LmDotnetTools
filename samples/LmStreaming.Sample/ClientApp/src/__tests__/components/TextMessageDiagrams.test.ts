import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ref } from 'vue';
import { MessageType } from '@/types';
import { WORKSPACE_FILE_LINKS } from '@/utils/workspaceLinks';
import TextMessage from '@/components/TextMessage.vue';

vi.mock('@/components/DiagramViewer.vue', () => ({
  default: {
    props: ['source', 'language'],
    template: '<figure class="diagram-proof" :data-language="language">{{ source }}</figure>',
  },
}));

const fence = (language: string, source: string) => `\`\`\`${language}\n${source}\n\`\`\``;
const message = (text: string) => ({ $type: MessageType.Text, role: 'assistant' as const, text });
const wrappers: ReturnType<typeof mount>[] = [];
afterEach(() => { wrappers.splice(0).forEach((wrapper) => wrapper.unmount()); document.body.innerHTML = ''; });

describe('TextMessage diagram integration', () => {
  it('keeps workspace links routed to the current thread beside a rendered diagram', async () => {
    const threadId = ref('thread-original');
    const open = vi.fn();
    const wrapper = mount(TextMessage, {
      props: { message: message(`${fence('mermaid', 'graph LR; A-->B')}\n\n[Design](docs/design.md)`), workspaceLinks: true },
      global: { provide: { [WORKSPACE_FILE_LINKS]: { threadId, open } } },
      attachTo: document.body,
    });
    wrappers.push(wrapper);
    await flushPromises();
    threadId.value = 'thread-current';
    await flushPromises();
    await wrapper.get('a.workspace-link').trigger('click');
    expect(open).toHaveBeenCalledWith({ threadId: 'thread-current', target: 'docs/design.md' });
    expect(wrapper.findAll('.diagram-proof')).toHaveLength(1);
  });
  it('enhances nested and repeated diagram fences without changing ordinary code or source text', async () => {
    const text = [fence('mermaid', 'graph LR; A-->B'), fence('mermaid', 'graph LR; A-->B'),
      '> ```puml\n> @startuml\n> Alice -> Bob\n> @enduml\n> ```', fence('typescript', 'const x = 1;')].join('\n\n');
    const wrapper = mount(TextMessage, { props: { message: message(text) }, attachTo: document.body });
    wrappers.push(wrapper);
    await flushPromises();
    expect(wrapper.findAll('.diagram-proof')).toHaveLength(3);
    expect(wrapper.find('[data-language="plantuml"]').text()).toContain('Alice -> Bob');
    expect(wrapper.find('code.language-typescript').exists()).toBe(true);
    expect(wrapper.find('.diagram-proof').text()).toContain('A-->B');
  });

  it('leaves streaming source intact and renders when completion changes without new text', async () => {
    const wrapper = mount(TextMessage, { props: { message: message(fence('mermaid', 'graph LR; A-->B')), isComplete: false }, attachTo: document.body });
    wrappers.push(wrapper);
    await flushPromises();
    expect(wrapper.find('.diagram-proof').exists()).toBe(false);
    expect(wrapper.find('pre code').text()).toContain('A-->B');
    await wrapper.setProps({ isComplete: true });
    await flushPromises();
    expect(wrapper.findAll('.diagram-proof')).toHaveLength(1);
    await wrapper.setProps({ isComplete: false });
    await flushPromises();
    expect(wrapper.find('.diagram-proof').exists()).toBe(false);
    expect(wrapper.find('pre code').exists()).toBe(true);
  });

  it('retires old diagram hosts when the message changes and removes them on unmount', async () => {
    const wrapper = mount(TextMessage, { props: { message: message(fence('uml', '@startuml\nA -> B\n@enduml')) }, attachTo: document.body });
    wrappers.push(wrapper);
    await flushPromises();
    expect(wrapper.find('.diagram-proof').exists()).toBe(true);
    await wrapper.setProps({ message: message('Updated **answer**') });
    await flushPromises();
    expect(wrapper.find('.diagram-proof').exists()).toBe(false);
    expect(wrapper.find('strong').text()).toBe('answer');
  });
});
