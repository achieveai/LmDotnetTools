import { describe, it, expect, vi, afterEach } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import AppMarketplacePanel from '@/components/AppMarketplacePanel.vue';

const row = {
  appId: 'lmstreaming-sample',
  alias: 'team',
  url: 'https://github.com/acme/plugins',
  repoKey: 'plugins-0a1b',
  ref: 'latest',
  resolvedSha: '8ca22dba9a94f28898bbce59f2537ff4d87c747d',
  createdAt: 't0',
  updatedAt: 't1',
  lastError: null,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('AppMarketplacePanel', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  it('registers with a null ref for latest, lists the row and tells the catalog to reload', async () => {
    let registered = false;
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) => {
      if (init?.method === 'PUT') {
        registered = true;
        return json(row);
      }
      return json(registered ? [row] : []);
    });
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    await wrapper.find('[data-testid="app-marketplace-alias"]').setValue('team');
    await wrapper.find('[data-testid="app-marketplace-url"]').setValue(row.url);
    await wrapper.find('form').trigger('submit');
    await flushPromises();

    const put = fetchSpy.mock.calls.find(([, init]) => init?.method === 'PUT')!;
    expect(put[0]).toBe('/api/marketplaces/app/team');
    expect(JSON.parse(put[1]!.body as string)).toEqual({ url: row.url, ref: null });
    expect(wrapper.find('[data-testid="app-marketplace-row-team"]').text()).toContain('latest @ 8ca22dba9a94');
    expect(wrapper.emitted('changed')).toHaveLength(1);
  });

  it('shows the gateway error code when registration is rejected', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) =>
      init?.method === 'PUT'
        ? json(
            {
              error: 'ref_not_found',
              detail: "Sandbox gateway returned 400 for registering app marketplace 'team' (error_code ref_not_found).",
            },
            400
          )
        : json([])
    );
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    await wrapper.find('[data-testid="app-marketplace-alias"]').setValue('team');
    await wrapper.find('[data-testid="app-marketplace-url"]').setValue(row.url);
    await wrapper.find('[data-testid="app-marketplace-ref"]').setValue('nope');
    await wrapper.find('form').trigger('submit');
    await flushPromises();

    expect(wrapper.find('[data-testid="app-marketplace-error"]').text()).toBe(
      "Could not register 'team' (400 ref_not_found): no such tag, branch or commit on the remote."
    );
    expect(wrapper.emitted('changed')).toBeUndefined();
  });

  it('falls back to the server detail for a code it has no hint for', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
      json({ error: 'marketplace_gateway_unavailable', detail: 'Sandbox gateway was unreachable.' }, 503)
    );
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    expect(wrapper.find('[data-testid="app-marketplace-error"]').text()).toBe(
      'Could not list git marketplaces (503 marketplace_gateway_unavailable): Sandbox gateway was unreachable.'
    );
  });

  it('says which setting turns git marketplaces on when the gateway has no store', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
      json({ error: 'marketplaces_disabled', detail: 'MARKETPLACE_STORE_PATH is not set' }, 404)
    );
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    expect(wrapper.find('[data-testid="app-marketplace-error"]').text()).toBe(
      'Could not list git marketplaces (404 marketplaces_disabled): git marketplaces are off. Set ' +
        'SandboxGateway:MarketplaceStorePath (or MARKETPLACE_STORE_PATH on an external gateway) and restart.'
    );
  });

  it('sends the operator secret typed into the panel on register, and none on list', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) =>
      init?.method === 'PUT' ? json(row) : json([])
    );
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    await wrapper.find('[data-testid="operator-secret"]').setValue('s3cret');
    await wrapper.find('[data-testid="app-marketplace-alias"]').setValue('team');
    await wrapper.find('[data-testid="app-marketplace-url"]').setValue(row.url);
    await wrapper.find('form').trigger('submit');
    await flushPromises();

    const put = fetchSpy.mock.calls.find(([, init]) => init?.method === 'PUT')!;
    expect(new Headers(put[1]!.headers).get('X-Operator-Secret')).toBe('s3cret');
    const list = fetchSpy.mock.calls[0];
    expect(new Headers(list[1]?.headers).get('X-Operator-Secret')).toBeNull();
  });

  it.each([
    ['operator_auth_failed', 403, 'Needs the operator secret'],
    ['operator_secret_not_configured', 503, 'LMSTREAMING_IDENTITY_OPERATOR_SECRET'],
  ])('explains an operator refusal (%s)', async (code, status, expected) => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) =>
      init?.method === 'DELETE' ? json({ error: 'forbidden', code }, status) : json([row])
    );
    const wrapper = mount(AppMarketplacePanel);
    await flushPromises();

    await wrapper.find('[data-testid="app-marketplace-remove-team"]').trigger('click');
    await flushPromises();

    expect(wrapper.find('[data-testid="app-marketplace-error"]').text()).toContain(expected);
    expect(wrapper.find('[data-testid="app-marketplace-row-team"]').exists()).toBe(true);
  });
});
