import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import EventPill from '@/components/EventPill.vue';

describe('EventPill.vue', () => {
  const defaultProps = {
    icon: '\u{1F527}',
    label: 'Test Label',
    type: 'tool-call' as const,
  };

  it('should render icon and label correctly', () => {
    const wrapper = mount(EventPill, {
      props: defaultProps,
    });

    expect(wrapper.find('.pill-icon').text()).toBe('\u{1F527}');
    expect(wrapper.find('.pill-label').text()).toBe('Test Label');
  });

  it('should apply correct type class', () => {
    const wrapper = mount(EventPill, {
      props: { ...defaultProps, type: 'thinking' },
    });

    expect(wrapper.find('.event-pill').classes()).toContain('thinking');
  });

  it('should not be expandable without fullContent', () => {
    const wrapper = mount(EventPill, {
      props: defaultProps,
    });

    expect(wrapper.find('.event-pill').classes()).not.toContain('expandable');
    expect(wrapper.find('.pill-expand-icon').exists()).toBe(false);
  });

  it('should be expandable with fullContent different from label', () => {
    const wrapper = mount(EventPill, {
      props: {
        ...defaultProps,
        fullContent: 'Full content that is different from label',
      },
    });

    expect(wrapper.find('.event-pill').classes()).toContain('expandable');
    expect(wrapper.find('.pill-expand-icon').exists()).toBe(true);
  });

  it('should expand on click when expandable', async () => {
    const wrapper = mount(EventPill, {
      props: {
        ...defaultProps,
        fullContent: 'Full expanded content',
      },
    });

    expect(wrapper.find('.pill-content').exists()).toBe(false);

    await wrapper.find('.event-pill').trigger('click');

    expect(wrapper.find('.pill-content').exists()).toBe(true);
    expect(wrapper.find('.pill-content pre').text()).toBe('Full expanded content');
    expect(wrapper.find('.event-pill').classes()).toContain('expanded');
  });

  it('should collapse on second click', async () => {
    const wrapper = mount(EventPill, {
      props: {
        ...defaultProps,
        fullContent: 'Full expanded content',
      },
    });

    await wrapper.find('.event-pill').trigger('click');
    expect(wrapper.find('.pill-content').exists()).toBe(true);

    await wrapper.find('.event-pill').trigger('click');
    expect(wrapper.find('.pill-content').exists()).toBe(false);
  });

  it('should show spinner when loading', () => {
    const wrapper = mount(EventPill, {
      props: {
        ...defaultProps,
        isLoading: true,
      },
    });

    expect(wrapper.find('.pill-spinner').exists()).toBe(true);
    expect(wrapper.find('.event-pill').classes()).toContain('loading');
  });

  it('should not show spinner when not loading', () => {
    const wrapper = mount(EventPill, {
      props: {
        ...defaultProps,
        isLoading: false,
      },
    });

    expect(wrapper.find('.pill-spinner').exists()).toBe(false);
  });

  it('should apply tool-call type styling', () => {
    const wrapper = mount(EventPill, {
      props: { ...defaultProps, type: 'tool-call' },
    });

    expect(wrapper.find('.event-pill').classes()).toContain('tool-call');
  });

  it('should apply tool-result type styling', () => {
    const wrapper = mount(EventPill, {
      props: { ...defaultProps, type: 'tool-result' },
    });

    expect(wrapper.find('.event-pill').classes()).toContain('tool-result');
  });
});
