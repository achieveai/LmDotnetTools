import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import TextMessage from '@/components/TextMessage.vue';
import { type TextMessage as TextMessageType, MessageType } from '@/types';

describe('TextMessage.vue', () => {
  const createMessage = (overrides: Partial<TextMessageType> = {}): TextMessageType => ({
    $type: MessageType.Text,
    text: 'Hello, world!',
    role: 'assistant',
    ...overrides,
  });

  it('should render text content correctly', () => {
    const message = createMessage({ text: 'Test message content' });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.markdown-content').text()).toContain('Test message content');
  });

  it('should show streaming cursor when isStreaming is true', () => {
    const message = createMessage();
    const wrapper = mount(TextMessage, {
      props: { message, isStreaming: true },
    });

    expect(wrapper.find('.cursor').exists()).toBe(true);
    expect(wrapper.find('.cursor').text()).toBe('|');
  });

  it('should hide cursor when isStreaming is false', () => {
    const message = createMessage();
    const wrapper = mount(TextMessage, {
      props: { message, isStreaming: false },
    });

    expect(wrapper.find('.cursor').exists()).toBe(false);
  });

  it('should hide cursor when isStreaming is undefined', () => {
    const message = createMessage();
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.cursor').exists()).toBe(false);
  });

  it('should apply thinking class when message.isThinking is true', () => {
    const message = createMessage({ isThinking: true });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.text-message').classes()).toContain('thinking');
  });

  it('should not apply thinking class when message.isThinking is false', () => {
    const message = createMessage({ isThinking: false });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.text-message').classes()).not.toContain('thinking');
  });

  it('should preserve whitespace in text', () => {
    const message = createMessage({ text: 'Line 1\nLine 2\n  Indented' });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    // The pre-wrap style should preserve whitespace
    const textElement = wrapper.find('.text-message');
    expect(textElement.attributes('style') || '').not.toContain('white-space: normal');
  });

  it('should render empty text correctly', () => {
    const message = createMessage({ text: '' });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.markdown-content').text()).toBe('');
  });

  it('should render long text with word break', () => {
    const longText = 'a'.repeat(1000);
    const message = createMessage({ text: longText });
    const wrapper = mount(TextMessage, {
      props: { message },
    });

    expect(wrapper.find('.markdown-content').text()).toContain(longText);
  });

  describe('isComplete (streaming highlight opt-out)', () => {
    const FENCE = ['```csharp', 'var s = "a & b";', 'if (x is null) { }', '```'].join('\n');
    // hljs TOKEN classes only -- the block's own `hljs language-csharp` class must survive both ways.
    const tokenSpans = (html: string) => (html.match(/class="hljs-/g) || []).length;

    it('skips syntax highlighting while the message is incomplete', () => {
      const wrapper = mount(TextMessage, {
        props: { message: createMessage({ text: FENCE }), isComplete: false },
      });

      expect(tokenSpans(wrapper.html())).toBe(0);
      expect(wrapper.html()).toContain('hljs language-csharp');
      expect(wrapper.html()).toContain('a &amp; b');
    });

    it('highlights when isComplete is true', () => {
      const wrapper = mount(TextMessage, {
        props: { message: createMessage({ text: FENCE }), isComplete: true },
      });

      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    it('highlights when isComplete is absent (defaults to complete)', () => {
      const wrapper = mount(TextMessage, {
        props: { message: createMessage({ text: FENCE }) },
      });

      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    it('highlights once isComplete flips true, without touching the cursor', async () => {
      const wrapper = mount(TextMessage, {
        props: { message: createMessage({ text: FENCE }), isComplete: false },
      });
      expect(tokenSpans(wrapper.html())).toBe(0);

      await wrapper.setProps({ isComplete: true });

      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
      // isComplete must not drag the blinking cursor along with it -- that is `isStreaming`'s job,
      // and a stray '|' would land in [data-testid="assistant-text"] textContent.
      expect(wrapper.find('.cursor').exists()).toBe(false);
    });
  });
});
