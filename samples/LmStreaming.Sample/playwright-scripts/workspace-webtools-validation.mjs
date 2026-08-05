async (page) => {
  const failures = [];
  const steps = [];
  const base = 'http://127.0.0.1:5050/dist/index.html';
  const prompt = (chain) => `<|instruction_start|>${JSON.stringify({ instruction_chain: chain })}<|instruction_end|>`;

  await page.goto(base);
  await page.waitForSelector('[data-testid="send-button"]', { timeout: 30000 });

  const choose = async (testId) => {
    await page.getByTestId(testId.split('/')[0]).click();
    await page.getByTestId(testId.split('/')[1]).click();
  };
  await choose('provider-selector-button/provider-option-test');
  await choose('mode-selector-button/mode-option-workspace-agent');
  steps.push('selected Workspace Agent + test mock provider');

  const send = async (text) => {
    const input = page.getByTestId('chat-input-textarea');
    await input.fill(text);
    await page.getByTestId('send-button').click();
    await page.getByTestId('stop-button').waitFor({ state: 'hidden', timeout: 120000 });
    await page.getByTestId('send-button').waitFor({ state: 'visible', timeout: 30000 });
  };

  await send(prompt([{ id: 'workspace-tools', id_message: 'List Workspace web tools', messages: [{ tools_list: {} }] }]));
  const firstText = (await page.getByTestId('assistant-text').last().textContent()) || '';
  for (const name of ['WebSearch', 'WebFetch']) {
    if (!firstText.includes(name)) failures.push(`tools_list missing ${name}: ${firstText}`);
  }
  steps.push({ toolsList: firstText });

  await send(prompt([{ id: 'workspace-search', id_message: 'Use Jina WebSearch', messages: [{ tool_call: [{ name: 'WebSearch', args: { query: 'LmDotnetTools GitHub repository', count: 2 } }] }] }]));
  await send(prompt([{ id: 'workspace-fetch', id_message: 'Use Jina WebFetch', messages: [{ tool_call: [{ name: 'WebFetch', args: { url: 'https://example.com' } }] }] }]));

  const pills = await page.getByTestId('tool-call-pill').evaluateAll((nodes) => nodes.map((n) => ({
    name: n.getAttribute('data-tool-name'),
    text: (n.textContent || '').trim(),
  })));
  for (const name of ['WebSearch', 'WebFetch']) {
    const matches = pills.filter((p) => p.name === name);
    if (!matches.length) failures.push(`no ${name} pill rendered`);
    if (matches.some((p) => /unknown function/i.test(p.text))) failures.push(`${name} resolved as unknown function`);
  }
  steps.push({ toolPills: pills.filter((p) => ['WebSearch', 'WebFetch'].includes(p.name)) });

  const threadId = await page.locator('[data-testid="conversation-item"]').first().getAttribute('data-thread-id');
  const persisted = threadId ? await page.evaluate(async (id) => {
    const r = await fetch(`/api/conversations/${id}/messages`);
    return r.ok ? r.json() : { status: r.status };
  }, threadId) : null;
  const persistedText = JSON.stringify(persisted);
  const messages = Array.isArray(persisted) ? persisted : [];
  const toolResults = messages
    .map((message) => {
      try { return JSON.parse(message.messageJson || '{}'); } catch { return {}; }
    })
    .filter((message) => message.$type === 'tool_call_result' && ['WebSearch', 'WebFetch'].includes(message.tool_name));
  const searchResult = toolResults.find((message) => message.tool_name === 'WebSearch');
  const fetchResult = toolResults.find((message) => message.tool_name === 'WebFetch');
  if (!searchResult || searchResult.is_error || !searchResult.result?.includes('github.com/achieveai/LmDotnetTools')) {
    failures.push(`WebSearch did not return the expected live GitHub result: ${JSON.stringify(searchResult)}`);
  }
  if (!fetchResult || fetchResult.is_error || !fetchResult.result?.includes('# Example Domain')) {
    failures.push(`WebFetch did not return the expected page content: ${JSON.stringify(fetchResult)}`);
  }
  if (/Unknown function: (WebSearch|WebFetch)/i.test(persistedText)) failures.push('persisted transcript contains unknown-function error');
  steps.push({
    threadId,
    WebSearch: searchResult ? { isError: searchResult.is_error, preview: searchResult.result.slice(0, 500) } : null,
    WebFetch: fetchResult ? { isError: fetchResult.is_error, preview: fetchResult.result.slice(0, 500) } : null,
  });

  return { pass: failures.length === 0, failures, steps };
}
