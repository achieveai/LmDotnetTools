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

describe('EnvEditor duplicate keys', () => {
  it('flags both rows when the same key is entered twice, and reports the form as invalid', async () => {
    // buildRecord() writes every row into one record, so the second row silently overwrote the first and
    // the payload simply lost a variable the user had typed. Nothing anywhere told them.
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    const keys = wrapper.findAll('[data-testid="test-env-key"]');
    await keys[0].setValue('FOO');
    await keys[1].setValue('FOO');

    const errors = wrapper.findAll('[data-testid="test-env-error"]');
    expect(errors).toHaveLength(2);
    expect(errors[0].text()).toContain('already set above');
    expect(wrapper.vm.hasErrors).toBe(true);
  });

  it('flags a case-only collision, because the gateway compares names case-insensitively', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    const keys = wrapper.findAll('[data-testid="test-env-key"]');
    await keys[0].setValue('foo');
    await keys[1].setValue('FOO');

    expect(wrapper.findAll('[data-testid="test-env-error"]')).toHaveLength(2);
    expect(wrapper.vm.hasErrors).toBe(true);
  });

  it('reports no error for two DIFFERENT keys, so the duplicate check is not simply always on', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    const keys = wrapper.findAll('[data-testid="test-env-key"]');
    await keys[0].setValue('FOO');
    await keys[1].setValue('BAR');

    expect(wrapper.find('[data-testid="test-env-error"]').exists()).toBe(false);
    expect(wrapper.vm.hasErrors).toBe(false);
  });
});

describe('EnvEditor positive emission', () => {
  // The paired POSITIVE case for "shows no error for a well-formed, unprotected key such as PATH",
  // which asserts only an absence and passes with the whole component deleted. This one fails unless
  // the editor actually emits what was typed.
  it('emits the entered key and value', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-key"]').setValue('PATH');
    await wrapper.get('[data-testid="test-env-value"]').setValue('/usr/bin');

    expect(lastEmitted(wrapper)).toEqual({ PATH: '/usr/bin' });
  });

  // `__proto__` is a well-formed name, so no row error fires — which made its loss invisible. On a `{}`
  // record the assignment hit the inherited setter and the key never reached the payload.
  it('emits a key named __proto__ as an own property that survives serialisation', async () => {
    const wrapper = mountEditor({});
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    await wrapper.get('[data-testid="test-env-key"]').setValue('__proto__');
    await wrapper.get('[data-testid="test-env-value"]').setValue('kept');

    const record = lastEmitted(wrapper);
    expect(Object.prototype.hasOwnProperty.call(record, '__proto__')).toBe(true);
    expect(Object.keys(record)).toEqual(['__proto__']);
    // What a parent actually sends: a spread copy, serialised.
    expect(JSON.stringify({ ...record })).toBe('{"__proto__":"kept"}');
    expect(wrapper.find('[data-testid="test-env-error"]').exists()).toBe(false);
  });

  // The `lastEmitted` echo guard had no direct test: the parent reflecting our own emission back as
  // a prop must NOT reset the rows, or an in-progress blank/invalid draft row is wiped mid-typing.
  it('keeps an in-progress blank row when the parent echoes the emitted record back', async () => {
    const wrapper = mountEditor({ FOO: 'bar' });
    await wrapper.get('[data-testid="test-env-add"]').trigger('click');
    expect(wrapper.findAll('[data-testid="test-env-row"]')).toHaveLength(2);

    // Exactly what a v-model parent does: hand back the record we just emitted.
    await wrapper.setProps({ modelValue: lastEmitted(wrapper) });

    expect(wrapper.findAll('[data-testid="test-env-row"]')).toHaveLength(2);
  });

  it('DOES reseed when the parent supplies a genuinely different record', async () => {
    // Over-refusal bound on the echo guard: it must not turn into "never accept a prop change",
    // which would strand the form on the previous workspace's variables.
    const wrapper = mountEditor({ FOO: 'bar' });

    await wrapper.setProps({ modelValue: { OTHER: 'x' } });

    const keys = wrapper
      .findAll('[data-testid="test-env-key"]')
      .map((k) => (k.element as HTMLInputElement).value);
    expect(keys).toEqual(['OTHER']);
  });
});
