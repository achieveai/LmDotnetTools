import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ChatInput from '@/components/ChatInput.vue';

const SEND = '[data-testid="send-button"]';
const STOP = '[data-testid="stop-button"]';
const QUEUE = '[data-testid="queue-button"]';
const TEXTAREA = '[data-testid="chat-input-textarea"]';
const HINT = '[data-testid="chat-input-hint"]';

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

  it('keeps Shift+Enter available for a new line instead of sending', async () => {
    const wrapper = mount(ChatInput, { props: { streaming: false } });
    const textarea = wrapper.get(TEXTAREA);
    await textarea.setValue('first line');
    await textarea.trigger('keydown', { key: 'Enter', shiftKey: true });

    expect(wrapper.emitted('send')).toBeFalsy();
    expect((textarea.element as HTMLTextAreaElement).value).toBe('first line');
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
