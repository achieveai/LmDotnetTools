import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ChatInput from '@/components/ChatInput.vue';
import chatInputSource from '@/components/ChatInput.vue?raw';

const SEND = '[data-testid="send-button"]';
const STOP = '[data-testid="stop-button"]';
const QUEUE = '[data-testid="queue-button"]';
const TEXTAREA = '[data-testid="chat-input-textarea"]';
const HINT = '[data-testid="chat-input-hint"]';
const FOOTER = '[data-testid="chat-input-footer"]';
const ACTIONS = '[data-testid="chat-input-actions"]';
const PROJECT = '[data-testid="chat-input-project-control"]';
const MODE = '[data-testid="chat-input-mode-control"]';

describe('ChatInput button states', () => {
  it('gives the composer a label and associates its keyboard hint', async () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });
    const textarea = wrapper.get(TEXTAREA);

    expect(wrapper.get('label[for="chat-message-input"]').text()).toBe('Message');
    expect(textarea.attributes('aria-describedby')).toBe('chat-input-hint');
    expect(wrapper.get(HINT).text()).toBe('Enter to send · Shift+Enter for a new line');

    await wrapper.setProps({ streaming: true });
    expect(wrapper.get(HINT).text()).toBe('Enter to queue · Shift+Enter for a new line');
  });

  it('hides the hint (without removing it) once text is typed, and shows it again when cleared', async () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });
    const textarea = wrapper.get(TEXTAREA);
    const hint = wrapper.get(HINT);

    expect(hint.classes()).not.toContain('is-hidden');

    await textarea.setValue('hello');
    expect(wrapper.get(HINT).classes()).toContain('is-hidden');
    expect(wrapper.get(HINT).text()).toBe('Enter to send · Shift+Enter for a new line');
    expect(textarea.attributes('aria-describedby')).toBe('chat-input-hint');

    await textarea.setValue('');
    expect(wrapper.get(HINT).classes()).not.toContain('is-hidden');
  });

  it('keeps Shift+Enter available for a new line instead of sending', async () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });
    const textarea = wrapper.get(TEXTAREA);
    await textarea.setValue('first line');
    await textarea.trigger('keydown', { key: 'Enter', shiftKey: true });

    expect(wrapper.emitted('send')).toBeFalsy();
    expect((textarea.element as HTMLTextAreaElement).value).toBe('first line');
  });

  it('places an optional context control immediately before Send on the right', () => {
    const wrapper = mount(ChatInput, {
      props: { streaming: false },
      slots: {
        'context-control': '<button data-testid="context-control">Provider</button>',
      },
    });

    const footer = wrapper.get(FOOTER);
    const actions = footer.get(ACTIONS);
    const contextControl = actions.get('[data-testid="context-control"]');
    const send = actions.get(SEND);

    expect(wrapper.get(HINT).text()).toContain('Enter to send');
    expect(footer.find(HINT).exists()).toBe(true);
    expect(contextControl.element.nextElementSibling).toBe(send.element);
    expect(wrapper.get(TEXTAREA).element.compareDocumentPosition(footer.element)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING
    );
  });

  it('places an optional mode control on the left outside the Provider and Send actions', () => {
    const wrapper = mount(ChatInput, {
      props: { streaming: false },
      slots: {
        'mode-control': '<button data-testid="mode-picker">Full access</button>',
        'context-control': '<button data-testid="context-control">Provider</button>',
      },
    });

    const footer = wrapper.get(FOOTER);
    const mode = footer.get(MODE);
    const hint = footer.get(HINT);
    const actions = footer.get(ACTIONS);
    expect(mode.get('[data-testid="mode-picker"]').text()).toBe('Full access');
    expect(mode.element.nextElementSibling).toBe(hint.element);
    expect(hint.element.nextElementSibling).toBe(actions.element);
    expect(actions.find('[data-testid="mode-picker"]').exists()).toBe(false);
    expect(actions.get('[data-testid="context-control"]').element.nextElementSibling).toBe(
      actions.get(SEND).element
    );
  });

  it('renders the shared composer without a context control when the slot is omitted', () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });

    expect(wrapper.get(ACTIONS).find('[data-testid="context-control"]').exists()).toBe(false);
    expect(wrapper.find(MODE).exists()).toBe(false);
    expect(wrapper.get(ACTIONS).get(SEND).attributes('aria-label')).toBe('Send message');
  });

  it('places an optional project control above the rounded composer surface', () => {
    const wrapper = mount(ChatInput, {
      props: { streaming: false },
      slots: {
        'project-control': '<button data-testid="project-picker">Default</button>',
      },
    });

    const project = wrapper.get(PROJECT);
    const surface = wrapper.get('[data-testid="chat-input-surface"]');
    expect(project.get('[data-testid="project-picker"]').text()).toBe('Default');
    expect(project.element.nextElementSibling).toBe(surface.element);
    expect(surface.find(TEXTAREA).exists()).toBe(true);
  });

  it('omits the project strip when the slot is not provided', () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });

    expect(wrapper.find(PROJECT).exists()).toBe(false);
    expect(wrapper.get('[data-testid="chat-input-surface"]').find(TEXTAREA).exists()).toBe(true);
  });

  it('uses one rounded composer surface with a borderless message area and internal footer', () => {
    const rule = (selector: string): string => {
      const start = chatInputSource.indexOf(`${selector} {`);
      expect(start).toBeGreaterThanOrEqual(0);
      const end = chatInputSource.indexOf('}', start);
      return chatInputSource.slice(start, end);
    };

    expect(rule('.chat-input')).toMatch(/flex-direction:\s*column\s*;/);
    expect(rule('.chat-input-surface')).toMatch(/border:\s*1px\s+solid\s+[^;]+;/);
    expect(rule('.chat-input-surface')).toMatch(/border-radius:\s*[^;]+;/);
    expect(rule('textarea')).toMatch(/border:\s*(?:0|none)\s*;/);
    expect(rule('.composer-footer')).toMatch(/display:\s*flex\s*;/);
    expect(rule('.composer-actions')).toMatch(/display:\s*flex\s*;/);
    expect(chatInputSource).toMatch(/\.composer-actions\s+:deep\(\.provider-label\)/);
  });

  describe('Not streaming', () => {
    it('renders Send as an accessible icon-only control', () => {
      const wrapper = mount(ChatInput, { props: { streaming: false } });
      const send = wrapper.get(SEND);

      expect(send.attributes('aria-label')).toBe('Send message');
      expect(send.attributes('title')).toBe('Send message');
      expect(send.text()).toBe('');
      expect(send.get('svg').attributes('aria-hidden')).toBe('true');
    });

    it('shows the Send button (no Stop, no Queue)', () => {
      const wrapper = mount(ChatInput, { props: { streaming: false } });
      expect(wrapper.find(SEND).exists()).toBe(true);
      expect(wrapper.find(STOP).exists()).toBe(false);
      expect(wrapper.find(QUEUE).exists()).toBe(false);
    });

    it('Send stays visible even with text typed', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: false } });
      await wrapper.find(TEXTAREA).setValue('hello');
      expect(wrapper.find(SEND).exists()).toBe(true);
      expect(wrapper.find(QUEUE).exists()).toBe(false);
      expect(wrapper.find(STOP).exists()).toBe(false);
    });
  });

  describe('Streaming with an empty box', () => {
    it('shows the red Stop button (no Queue)', () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      expect(wrapper.find(STOP).exists()).toBe(true);
      expect(wrapper.find(QUEUE).exists()).toBe(false);
      expect(wrapper.find(SEND).exists()).toBe(false);
    });

    it('whitespace-only text still shows Stop, not Queue', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      await wrapper.find(TEXTAREA).setValue('   ');
      expect(wrapper.find(STOP).exists()).toBe(true);
      expect(wrapper.find(QUEUE).exists()).toBe(false);
    });

    it('clicking Stop emits cancel and leaves the draft untouched', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      await wrapper.find(STOP).trigger('click');
      expect(wrapper.emitted('cancel')).toBeTruthy();
      expect(wrapper.emitted('send')).toBeFalsy();
    });
  });

  describe('Streaming with text in the box', () => {
    it('shows the blue Queue button and hides Stop', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      await wrapper.find(TEXTAREA).setValue('follow-up while streaming');
      expect(wrapper.find(QUEUE).exists()).toBe(true);
      expect(wrapper.find(STOP).exists()).toBe(false);
      expect(wrapper.find(SEND).exists()).toBe(false);
    });

    it('clicking Queue emits send with the trimmed text and clears the box', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      const textarea = wrapper.find(TEXTAREA);
      await textarea.setValue('  queued message  ');
      await wrapper.find(QUEUE).trigger('click');

      const sent = wrapper.emitted('send');
      expect(sent).toBeTruthy();
      expect(sent![0]).toEqual(['queued message']);
      expect(wrapper.emitted('cancel')).toBeFalsy();
      expect((textarea.element as HTMLTextAreaElement).value).toBe('');
    });

    it('after Queue clears the box, the button reverts to Stop', async () => {
      const wrapper = mount(ChatInput, { props: { streaming: true } });
      await wrapper.find(TEXTAREA).setValue('queued message');
      await wrapper.find(QUEUE).trigger('click');
      await wrapper.vm.$nextTick();
      expect(wrapper.find(STOP).exists()).toBe(true);
      expect(wrapper.find(QUEUE).exists()).toBe(false);
    });
  });
});
