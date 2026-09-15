import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import EnvEditor from '@/components/EnvEditor.vue';

function mountEditor(modelValue: Record<string, string> = {}, disabled = false) {
  return mount(EnvEditor, {
    props: {
      modelValue,
      disabled,
      testidPrefix: 'test-env',
    },
  });
}

function lastEmitted(wrapper: ReturnType<typeof mount>): Record<string, string> {
  const events = wrapper.emitted('update:modelValue');
  expect(events).toBeTruthy();
  return events![events!.length - 1][0] as Record<string, string>;
}

describe('EnvEditor rendering', () => {
  it('renders one row per entry in modelValue, with key and value filled in', () => {
    const wrapper = mountEditor({ FOO: 'bar', BAZ: 'qux' });

    const rows = wrapper.findAll('[data-testid="test-env-row"]');
    expect(rows).toHaveLength(2);

    const keys = wrapper
      .findAll('[data-testid="test-env-key"]')
      .map((k) => (k.element as HTMLInputElement).value);
    const values = wrapper
      .findAll('[data-testid="test-env-value"]')
      .map((v) => (v.element as HTMLInputElement).value);
    expect(keys).toEqual(['FOO', 'BAZ']);
    expect(values).toEqual(['bar', 'qux']);
  });

  it('renders no rows for an empty modelValue', () => {
    const wrapper = mountEditor({});
    expect(wrapper.findAll('[data-testid="test-env-row"]')).toHaveLength(0);
  });
});

describe('EnvEditor add + fill', () => {
  it('adds a blank row on "Add variable" and emits the merged record once key and value are filled', async () => {
    const wrapper = mountEditor({ EXISTING: '1' });

    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    expect(wrapper.findAll('[data-testid="test-env-row"]')).toHaveLength(2);

    const newRowKey = wrapper.findAll('[data-testid="test-env-key"]')[1];
    const newRowValue = wrapper.findAll('[data-testid="test-env-value"]')[1];
    await newRowKey.setValue('NEW_KEY');
    await newRowValue.setValue('value1');

    expect(lastEmitted(wrapper)).toEqual({ EXISTING: '1', NEW_KEY: 'value1' });
  });

  it('drops a blank-key row from the emitted record', async () => {
    const wrapper = mountEditor({ EXISTING: '1' });

    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    // Never filled in — the add itself must not inject an empty key into the record.
    expect(lastEmitted(wrapper)).toEqual({ EXISTING: '1' });
  });
});

describe('EnvEditor remove', () => {
  it('emits the record without the removed row', async () => {
    const wrapper = mountEditor({ A: '1', B: '2' });

    await wrapper.findAll('[data-testid="test-env-remove"]')[0].trigger('click');

    expect(wrapper.findAll('[data-testid="test-env-row"]')).toHaveLength(1);
    expect(lastEmitted(wrapper)).toEqual({ B: '2' });
  });
});

describe('EnvEditor key validation', () => {
  it('shows an inline error for a malformed key', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-key"]').setValue('123bad');

    const error = wrapper.get('[data-testid="test-env-error"]');
    expect(error.text()).toContain(
      'Key must start with a letter or underscore and use only letters, digits, underscores.'
    );
  });

  it('shows an inline error for a protected key, case-insensitively', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-key"]').setValue('http_proxy');

    const error = wrapper.get('[data-testid="test-env-error"]');
    expect(error.text()).toContain('This name is protected by the sandbox and cannot be set.');
  });

  it('shows no error for a well-formed, unprotected key such as PATH', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-key"]').setValue('PATH');

    expect(wrapper.find('[data-testid="test-env-error"]').exists()).toBe(false);
  });
});

describe('EnvEditor disabled state', () => {
  it('disables inputs and buttons when disabled is true', () => {
    const wrapper = mountEditor({ A: '1' }, true);

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="test-env-key"]').element.disabled
    ).toBe(true);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="test-env-value"]').element.disabled
    ).toBe(true);
    expect(
      wrapper.get<HTMLButtonElement>('[data-testid="test-env-remove"]').element.disabled
    ).toBe(true);
    expect(
      wrapper.get<HTMLButtonElement>('[data-testid="test-env-add"]').element.disabled
    ).toBe(true);
  });
});
