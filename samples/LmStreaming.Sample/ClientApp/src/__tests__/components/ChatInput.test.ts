import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ChatInput from '@/components/ChatInput.vue';

const SEND = '[data-testid="send-button"]';
const STOP = '[data-testid="stop-button"]';
const QUEUE = '[data-testid="queue-button"]';
const TEXTAREA = '[data-testid="chat-input-textarea"]';

describe('ChatInput button states', () => {
  describe('Not streaming', () => {
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
