import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const root = new URL('../../src/SkillSync.Daemon/wwwroot/', import.meta.url);
const browser = process.env.SKILLHUB_BROWSER ??
  ['C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const sample = {
  name: 'fixture', title: 'Fixture', revision: 1, source_category: 'user', origin_provider: 'Personal',
  description: 'A test skill', tags: ['one', 'two', 'three', 'four', 'five'],
  targets: { codex: { enabled: false }, claude: { enabled: false }, gemini: { enabled: false }, cursor: { enabled: false } },
  destinations: [], supporting_files: {}, x_passthrough: { external: 'retained' },
  instructions_markdown: '# Fixture\n\n#### Fourth\n##### Fifth\n###### Sixth\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n- Parent\n  - Child\n\n[Search here](https://example.com/?q=fixture&mode=2)'
};
const installed = [
  { provider: 'canonical', is_installed: true },
  { provider: 'codex', is_installed: true, skills_root: '/codex' },
  { provider: 'gemini', is_installed: true, skills_root: '/gemini' },
  { provider: 'claude', is_installed: false },
  { provider: 'cursor', is_installed: false }
];
const destination = (provider, state, path = '/' + provider) => ({ provider, state, path, applied_revision: 1 });
const healthy = { ...sample, name: 'mirrored', title: 'Mirrored', tags: ['two', 'three'],
  targets: { codex: { enabled: true }, gemini: { enabled: true }, claude: { enabled: true }, cursor: { enabled: true } },
  destinations: [destination('codex', 'synced', '/codex'), destination('codex', 'synced', '/agents'),
    destination('gemini', 'synced'), destination('claude', 'missing'), destination('cursor', 'conflict')] };
const needsAttention = { ...healthy, name: 'conflicted', title: 'Conflicted', tags: ['three'],
  destinations: [destination('codex', 'synced'), destination('gemini', 'conflict')] };
const item = name => ({ slug: name.toLowerCase(), title: name, description: name, repo: 'fixture/repo',
  path: name + '/SKILL.md', git_ref: 'main' });

async function fixtureServer() {
  const fixture = { theme: 'dark', applied: null, preparing: new Set(), events: null, reads: 0 };
  const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const send = (value, status = 200) => {
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(value));
    };
    if (url.pathname === '/api/v1/events') {
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.write('event: connected\ndata: {}\n\n');
      fixture.events = res;
      return;
    }
    if (url.pathname.startsWith('/api/v1/')) {
      const route = url.pathname.slice('/api/v1'.length);
      let body = '';
      for await (const chunk of req) body += chunk;
      const data = body ? JSON.parse(body) : {};
      if (route === '/skills') {
        fixture.reads++;
        return send([sample, healthy, needsAttention]);
      }
      if (route === '/skills/fixture') return send({ skill: sample });
      if (route === '/tags') return send([{ tag: 'one', skill_count: 1 }]);
      if (route === '/providers') return send(installed);
      if (route === '/preferences/theme' && req.method === 'PATCH') {
        fixture.theme = data.theme;
        return send({ theme: fixture.theme });
      }
      if (route === '/status') return send({ install: { mcp_clients: [] } });
      if (route === '/diagnostics') return send({ watcher: { running: true, roots: 1 } });
      if (route === '/preferences') {
        if (req.method === 'PUT') fixture.theme = data.theme;
        return send({ theme: fixture.theme, close_to_tray: true, auto_start: false });
      }
      if (route === '/community/repos') return send({ repositories: [{ repo: 'featured/all', title: 'Featured' }] });
      if (route === '/community/skills') return send({ skills: ['Slow', 'Fast', 'Broken'].map(item) });
      if (route === '/imports/prepare') {
        fixture.preparing.add(data.path);
        if (data.path.startsWith('Slow')) await delay(750);
        if (data.path.startsWith('Broken')) return send({ error: 'Upstream unavailable' }, 503);
        const title = data.path.split('/')[0];
        return send({ title: title + ' review', plan_id: title + '-plan', revision: title + '-commit', source: data.repo,
          path: data.path, license: 'MIT', name: 'fixture', description: '', instructions_markdown: 'Reviewed body',
          supporting_files: {}, skipped_files: [] });
      }
      if (route.endsWith('/apply')) {
        fixture.applied = { route, data };
        return send({ skill: sample });
      }
      return send({ error: 'Unexpected request ' + route }, 404);
    }
    const path = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    const mime = path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html';
    try {
      const file = await readFile(new URL(path, root));
      res.writeHead(200, { 'Content-Type': mime });
      res.end(file);
    } catch {
      res.writeHead(404).end();
    }
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { fixture, server, url: 'http://127.0.0.1:' + server.address().port };
}

async function until(check, message) {
  for (let i = 0; i < 150; i++) {
    const value = await check();
    if (value) return value;
    await delay(30);
  }
  throw new Error(message);
}

async function devTools(endpoint) {
  const socket = new WebSocket(endpoint);
  const responses = new Map();
  let sequence = 0;
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  socket.addEventListener('message', event => {
    const reply = JSON.parse(event.data);
    const callback = responses.get(reply.id);
    if (callback) {
      responses.delete(reply.id);
      callback(reply);
    }
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence;
    responses.set(id, reply => reply.error ? reject(new Error(reply.error.message)) : resolve(reply.result));
    socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text);
    return result.result.value;
  };
  return { socket, send, evaluate };
}

async function verifyContrast(evaluate) {
  const ratios = await evaluate(`
    (() => {
      const colors = getComputedStyle(document.documentElement);
      const luminance = value => {
        const raw = value.trim().slice(1);
        const hex = raw.length === 3 ? raw.split('').map(digit => digit + digit).join('') : raw;
        const channels = hex.match(/../g).map(pair => Number.parseInt(pair, 16) / 255);
        return channels.map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4)
          .reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
      };
      const ratio = (text, surface) => {
        const pair = [text, surface].map(name => luminance(colors.getPropertyValue('--' + name))).sort((a, b) => b - a);
        return (pair[0] + 0.05) / (pair[1] + 0.05);
      };
      return Object.fromEntries(['surface', 'panel', 'raised'].flatMap(surface =>
        ['text', 'secondary', 'muted', 'ok', 'warn', 'error', 'accent'].map(text =>
          [text + '/' + surface, ratio(text, surface)])).concat([['accent-text/accent', ratio('accent-text', 'accent')]]));
    })()
  `);
  for (const [pair, ratio] of Object.entries(ratios)) assert.ok(ratio >= 4.5, pair + ' measured ' + ratio.toFixed(2));
}

test('workbench at desktop and docked sizes, with keyboard, themes, Markdown and pinned imports',
  { skip: !browser && 'Set SKILLHUB_BROWSER to a Chromium executable.', timeout: 30000 }, async () => {
    const { server, fixture, url } = await fixtureServer();
    const profile = await mkdtemp(join(tmpdir(), 'skillhub-browser-'));
    const chrome = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
      '--remote-debugging-port=0', '--user-data-dir=' + profile, url], { windowsHide: true, stdio: 'ignore' });
    let page;
    try {
      const port = await until(async () => {
        try { return (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
        catch { return null; }
      }, 'Chromium never opened DevTools');
      const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
      page = await devTools(pages.find(tab => tab.type === 'page').webSocketDebuggerUrl);
      const { send, evaluate } = page;
      const wait = (expression, label = expression) => until(() => evaluate("Boolean(" + expression + ")"), label);
      const click = selector => evaluate('document.querySelector(' + JSON.stringify(selector) + ').click()');
      const textButton = text => evaluate('{ const b = [...document.querySelectorAll("button")].find(b => !b.closest("[hidden]") && b.textContent.trim() === ' +
        JSON.stringify(text) + '); b.focus(); b.click(); }');
      const key = async (value, modifiers = 0, code = value) => {
        const virtual = { Enter: 13, Escape: 27, k: 75 }[value];
        const args = { key: value, code, modifiers, windowsVirtualKeyCode: virtual };
        await send('Input.dispatchKeyEvent', { type: 'keyDown', ...args, ...(value === 'Enter' ? { text: '\r', unmodifiedText: '\r' } : {}) });
        await send('Input.dispatchKeyEvent', { type: 'keyUp', ...args });
      };
      const size = (width, height, deviceScaleFactor) =>
        send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor, mobile: false });

      await size(460, 500, 1.5);
      await wait('document.querySelector("#library-results .result-button") && document.documentElement.dataset.theme === "dark"');
      assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'));
      assert.ok(await evaluate('document.querySelector(".result-button").getBoundingClientRect().top < innerHeight'));
      await verifyContrast(evaluate);
      assert.match(await evaluate('document.querySelector(".result-button").textContent'), /Disabled/);
      assert.match(await evaluate('document.querySelector(".result-button").textContent'), /\+[1-9]/);
      const assertNoSerializedElements = async () =>
        assert.equal(await evaluate('document.body.textContent.includes("[object HTML")'), false);
      const rowCount = () => evaluate('document.querySelectorAll("#library-results .result-button").length');
      await assertNoSerializedElements();
      assert.equal(await evaluate('document.querySelectorAll(".collections button").length'), 4);
      assert.ok(await evaluate('[...document.querySelectorAll(".collections button")].every(tab => tab.getBoundingClientRect().width > 0)'));
      assert.equal(await evaluate('document.querySelectorAll("[data-route] svg").length'), 3);
      assert.equal(await evaluate('document.querySelector("#provider-overview .provider-overview-label").textContent'), '2 AI Providers Connected');
      assert.equal(await evaluate('document.querySelectorAll("#provider-overview svg").length'), 2);
      await click('#provider-overview');
      await wait('document.querySelector("dialog[open]")?.getAttribute("aria-modal") === "true"');
      assert.equal(await evaluate('document.querySelectorAll(".provider-overview-row").length'), 4);
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await click('#quick-theme');
      await until(() => fixture.theme === 'light', 'Quick theme was not saved');
      await wait('!document.querySelector("#quick-theme").disabled');
      await click('#quick-theme');
      await until(() => fixture.theme === 'dark', 'Quick theme was not restored');

      assert.deepEqual(await evaluate('[...document.querySelectorAll("[data-control=row-mirrored] [data-synced-provider]")].map(e => e.textContent)'), ['Codex', 'Gemini']);
      assert.equal(await evaluate('document.querySelector("[data-control=row-mirrored] .warning")'), null);
      assert.match(await evaluate('document.querySelector("[data-control=row-conflicted] .warning").textContent'), /Needs attention/);
      await click('[data-control="category-synced"]');
      assert.equal(await rowCount(), 2);
      await click('[data-control="category-user"]');
      assert.equal(await rowCount(), 3);

      await textButton('Tags & filters');
      assert.equal(await evaluate('document.querySelector("dialog[open]")'), null);
      await click('[data-tag="one"]');
      assert.equal(await rowCount(), 1);
      await click('[data-tag="three"]');
      assert.equal(await rowCount(), 1);
      assert.equal(await evaluate('document.querySelectorAll("[data-tag][aria-pressed=true]").length'), 2);
      await evaluate('const mode = document.querySelector("[aria-label=\\"Tag matching\\"]"); mode.value = "any"; mode.dispatchEvent(new Event("change"));');
      assert.equal(await rowCount(), 3);
      await assertNoSerializedElements();
      await textButton('Clear filters');
      assert.equal(await evaluate('document.querySelectorAll("[data-tag][aria-pressed=true]").length'), 0);
      await evaluate('document.querySelector("[data-tag=one]").focus()');
      await key('Escape');
      await wait('document.querySelector("#library-filters").hidden && document.activeElement.id === "library-filters-toggle"');

      await evaluate('document.querySelector(".result-button").focus()');
      await key('Enter');
      await wait('document.querySelector("#reader-screen h6")?.textContent === "Sixth"')
        .catch(async error => { throw new Error(error.message + '\\n' + await evaluate('location.hash + " " + document.querySelector("#reader-screen").innerText')); });
      assert.equal(await evaluate('document.querySelector("#reader-screen article h1") === null'), true);
      assert.equal(await evaluate('document.querySelectorAll("#reader-screen th").length'), 2);
      assert.equal(await evaluate('document.querySelectorAll("#reader-screen li li").length'), 1);
      assert.equal(await evaluate('document.querySelector("#reader-screen article a")?.href'), 'https://example.com/?q=fixture&mode=2');

      await textButton('Add tag');
      await wait('document.querySelector("dialog[open] input") === document.activeElement');
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await textButton('Edit skill');
      await evaluate('const titleField = document.querySelector("#editor-screen label input"); titleField.value = "Edited"; titleField.dispatchEvent(new Event("input", { bubbles: true }));');
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog[open]") && document.activeElement.textContent === "Cancel"');
      await key('Enter');
      await wait('!document.querySelector("dialog") && !document.querySelector("#editor-screen").hidden');
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog[open]")');
      await textButton('Discard changes');
      await wait('document.activeElement.id === "library-search"');

      await size(1280, 820, 1.25);
      await click('[data-route="explore"]');
      await wait('document.querySelector("#explore-screen .result-button")');
      await click('#explore-screen .result-row:nth-child(1) button');
      await until(() => fixture.preparing.has('Slow/SKILL.md'), 'Slow preview did not start');
      await click('#explore-screen .result-row:nth-child(2) button');
      await wait('document.querySelector("#explore-preview h2")?.textContent === "Fast review"');
      await delay(800);
      assert.equal(await evaluate('document.querySelector("#explore-preview h2").textContent'), 'Fast review');
      await size(460, 500, 1);
      assert.equal(await evaluate('getComputedStyle(document.querySelector(".explore-results")).display'), 'none');
      await textButton('← Back to results');
      await click('#explore-screen .result-row:nth-child(3) button');
      await wait('document.querySelector("#explore-preview h2")?.textContent === "Preview unavailable"');
      assert.equal(await evaluate('[...document.querySelectorAll("#explore-preview button")].find(b => b.textContent === "Import skill").disabled'), true);
      await textButton('← Back to results');
      await click('#explore-screen .result-row:nth-child(2) button');
      await wait('document.querySelector("#explore-preview h2")?.textContent === "Fast review"');
      await textButton('Import skill');
      await until(() => fixture.applied, 'No import request received');
      assert.deepEqual(fixture.applied, { route: '/imports/Fast-plan/apply', data: { revision: 'Fast-commit', name: 'fixture', providers: [] } });
      await wait('!document.querySelector("#reader-screen").hidden');

      await click('[data-route="settings"]');
      await textButton('Appearance & Startup');
      await wait('document.querySelector("#settings-screen select")');
      await evaluate('document.querySelector("#settings-screen select").value = "light"');
      await textButton('Save preferences');
      await wait('document.documentElement.dataset.theme === "light"');
      await verifyContrast(evaluate);
      assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'));
      await textButton('Diagnostics');
      await wait('document.querySelector("#settings-screen").textContent.includes("1 watched roots")');

      await delay(250);
      const baseline = fixture.reads;
      for (let id = 1; id <= 8; id++) fixture.events.write('id: ' + id + '\nevent: activity\ndata: {}\n\n');
      await until(() => fixture.reads > baseline, 'Activity did not refresh the snapshot');
      await delay(250);
      assert.equal(fixture.reads, baseline + 1);
      await assertNoSerializedElements();
    } finally {
      page?.socket.close();
      if (chrome.exitCode === null && chrome.signalCode === null && chrome.pid) {
        const closed = once(chrome, 'exit');
        chrome.kill();
        await closed;
      }
      fixture.events?.end();
      server.closeAllConnections();
      server.close();
      await rm(profile, { recursive: true, force: true, maxRetries: 8, retryDelay: 150 });
    }
  });
