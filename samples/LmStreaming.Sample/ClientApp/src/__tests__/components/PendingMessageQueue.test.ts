import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import PendingMessageQueue from '@/components/PendingMessageQueue.vue';
import { MessageType } from '@/types';
import type { TextMessage } from '@/types';

describe('PendingMessageQueue', () => {
  describe('Visibility', () => {
    it('should not render when no pending messages', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [],
        },
      });

      expect(wrapper.find('.pending-queue').exists()).toBe(false);
    });

    it('should render when there are pending messages', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Hello world',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      expect(wrapper.find('.pending-queue').exists()).toBe(true);
    });
  });

  describe('Header Display', () => {
    it('should show waiting icon and header text', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Test message',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      expect(wrapper.find('.waiting-icon').exists()).toBe(true);
      expect(wrapper.find('.waiting-icon').text()).toBe('⏳');
      expect(wrapper.find('.header-text').text()).toBe('Waiting to send...');
    });

    it('should have waiting-icon class for animation', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Test',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const icon = wrapper.find('.waiting-icon');
      // The waiting-icon class has CSS animation defined in the component
      expect(icon.exists()).toBe(true);
      expect(icon.classes()).toContain('waiting-icon');
    });
  });

  describe('Single Message Display', () => {
    it('should display a single pending message', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'My test message',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const items = wrapper.findAll('.pending-item');
      expect(items.length).toBe(1);
      expect(items[0].text()).toContain('📤');
      expect(items[0].text()).toContain('My test message');
    });

    it('should show send icon for each message', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Test',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      expect(wrapper.find('.item-icon').text()).toBe('📤');
    });
  });

  describe('Multiple Messages Display', () => {
    it('should display multiple pending messages', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'First message',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
            {
              id: 'msg-2',
              content: {
                $type: MessageType.Text,
                text: 'Second message',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now() + 1000,
            },
            {
              id: 'msg-3',
              content: {
                $type: MessageType.Text,
                text: 'Third message',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now() + 2000,
            },
          ],
        },
      });

      const items = wrapper.findAll('.pending-item');
      expect(items.length).toBe(3);
      expect(items[0].text()).toContain('First message');
      expect(items[1].text()).toContain('Second message');
      expect(items[2].text()).toContain('Third message');
    });

    it('should show each message with send icon', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Message 1',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
            {
              id: 'msg-2',
              content: {
                $type: MessageType.Text,
                text: 'Message 2',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now() + 1000,
            },
          ],
        },
      });

      const icons = wrapper.findAll('.item-icon');
      expect(icons.length).toBe(2);
      icons.forEach(icon => {
        expect(icon.text()).toBe('📤');
      });
    });
  });

  describe('Text Truncation', () => {
    it('should have item-text class with ellipsis styling', () => {
      const longText = 'This is a very long message that should be truncated with ellipsis because it exceeds the available width of the container';
      
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: longText,
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const itemText = wrapper.find('.item-text');
      
      // Check that the element has the class for ellipsis styling
      expect(itemText.exists()).toBe(true);
      expect(itemText.classes()).toContain('item-text');
      expect(itemText.text()).toBe(longText); // Full text is in DOM, CSS handles truncation
    });

    it('should display full short text', () => {
      const shortText = 'Short message';
      
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: shortText,
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const itemText = wrapper.find('.item-text');
      expect(itemText.text()).toBe(shortText);
    });
  });

  describe('Styling Classes', () => {
    it('should have proper CSS classes for layout', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Test',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const item = wrapper.find('.pending-item');
      expect(item.classes()).toContain('pending-item');
      expect(item.find('.item-icon').exists()).toBe(true);
      expect(item.find('.item-text').exists()).toBe(true);
    });

    it('should have required structural elements', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'Test',
                role: 'user',
              } as TextMessage,
              timestamp: Date.now(),
            },
          ],
        },
      });

      const queue = wrapper.find('.pending-queue');
      expect(queue.exists()).toBe(true);
      expect(queue.find('.pending-header').exists()).toBe(true);
      expect(queue.find('.pending-list').exists()).toBe(true);
    });
  });

  describe('Message Order', () => {
    it('should display messages in the order they are provided', () => {
      const wrapper = mount(PendingMessageQueue, {
        props: {
          pendingMessages: [
            {
              id: 'msg-1',
              content: {
                $type: MessageType.Text,
                text: 'First',
                role: 'user',
              } as TextMessage,
              timestamp: 1000,
            },
            {
              id: 'msg-2',
              content: {
                $type: MessageType.Text,
                text: 'Second',
                role: 'user',
              } as TextMessage,
              timestamp: 2000,
            },
            {
              id: 'msg-3',
              content: {
                $type: MessageType.Text,
                text: 'Third',
                role: 'user',
              } as TextMessage,
              timestamp: 3000,
            },
          ],
        },
      });

      const items = wrapper.findAll('.item-text');
      expect(items[0].text()).toBe('First');
      expect(items[1].text()).toBe('Second');
      expect(items[2].text()).toBe('Third');
    });
  });
});

