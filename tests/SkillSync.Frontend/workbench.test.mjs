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
  description: 'A test skill', tags: ['one', 'two', 'three', 'four', 'five', 'system'],
  targets: { codex: { enabled: false }, claude: { enabled: false }, gemini: { enabled: false }, cursor: { enabled: false } },
  destinations: [], supporting_files: { 'references/notes.md': 'Supporting notes' }, x_passthrough: { external: 'retained' },
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
const template = { ...sample, name: 'template', catalog_id: 'fixture-template', source_category: 'template',
  is_catalog_only: true, tags: ['workflow'] };
const system = { ...sample, name: 'bootstrap', source_category: 'system', tags: ['system', 'skillhub', 'meta-skill'] };
const item = name => ({ slug: name.toLowerCase(), title: name, description: name, repo: 'fixture/repo',
  path: name + '/SKILL.md', git_ref: 'main', kind: name === 'Skill 29' ? 'doc' : 'skill', group: name === 'Skill 29' ? 'docs' : 'skills',
  file_count: name === 'Fast' ? 1 : 0 });

async function fixtureServer() {
  let releaseSlow;
  const slowPreview = new Promise(resolve => { releaseSlow = resolve; });
  const fixture = { releaseSlow, slowFinished: false, theme: 'dark', tray: true, startup: true, reviewed: null, applied: null, preparing: new Map(), prefetched: new Map(), events: null, reads: 0 };
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
        return send([sample, healthy, needsAttention, template, system]);
      }
      if (route === '/skills/fixture') return send({ skill: sample });
      if (route === '/skills/conflicted') return send({ skill: needsAttention });
      if (route === '/skills/conflicted/destination-review') {
        if (req.method === 'POST') {
          fixture.reviewed = data;
          return send({ skill: needsAttention });
        }
        return send({ issue: { title: 'Conflicted' }, reason: 'different', version: 'review-hash',
          files: [{ path: 'SKILL.md', change: 'Different', skill_hub: '---\nname: skill\n---\nOld body',
            provider: '---\nname: skill\n---\nNew body' }], actions: ['keep', 'accept', 'copy', 'leave'] });
      }
      if (route === '/tags') return send(['one', 'system', 'skillhub', 'meta-skill'].map(tag => ({ tag, skill_count: 1 })));
      if (route === '/providers') return send(installed);
      if (route === '/preferences/theme' && req.method === 'PATCH') {
        fixture.theme = data.theme;
        return send({ theme: fixture.theme });
      }
      if (route === '/status') return send({ install: { mcp_clients: [] } });
      if (route === '/diagnostics') return send({ watcher: { running: true, roots: 1 },
        issues: [{ title: 'Conflicted', label: 'Gemini', state: 'conflict', path: '/gemini', skill: 'conflicted' }],
        recent_activity: [{ event_type: 'synced', timestamp_utc: '2026-11-13T12:00:00Z', details: 'Applied a projection' }],
        trash: [] });
      if (route === '/preferences/tray' || route === '/preferences/startup') {
        const key = route.split('/').at(-1);
        fixture[key] = data.enabled;
        return send({ enabled: fixture[key] });
      }
      if (route === '/preferences') {
        if (req.method === 'PUT') fixture.theme = data.theme;
        return send({ theme: fixture.theme, close_to_tray: fixture.tray, auto_start: fixture.startup });
      }
      if (route === '/community/repos') return send({ repositories: [{ repo: 'featured/all', title: 'Featured' }] });
      if (route === '/community/skills') return send({ skills: ['Slow', 'Fast', 'Broken',
        ...Array.from({ length: 30 }, (_, index) => 'Skill ' + index)].map(item),
        repository: { repo: 'fixture/repo', sha: 'f'.repeat(40), mode: 'tarball', readme_excerpt: '# Fixture\n\nFixture readme text.',
          license_path: 'LICENSE', counts: { skills: 32, docs: 1 }, truncated: false, pinned: false } });
      if (route === '/community/preview') {
        const path = url.searchParams.get('path');
        fixture.prefetched.set(path, (fixture.prefetched.get(path) ?? 0) + 1);
        if (path.startsWith('Slow')) { await slowPreview; fixture.slowFinished = true; }
        if (path.startsWith('Broken')) { await delay(200); return send({ error: 'Upstream unavailable' }, 503); }
        const title = path.split('/')[0];
        return send({ title: title + ' review', revision: title + '-commit', repo: 'fixture/repo', source: 'fixture/repo',
          path, license: 'MIT', slug: 'fixture-import', description: 'Skill frontmatter',
          instructions_markdown: 'Reviewed body\n\n' + 'A long document.\n\n'.repeat(80) + '~~~\n' + 'line\n'.repeat(70) + '~~~',
          supporting_paths: ['references/notes.md'] });
      }
      if (route === '/community/preview-file') return send({ text: 'The pinned supporting file.' });
      if (route === '/imports/prepare') {
        fixture.preparing.set(data.path, data.git_ref);
        const title = data.path.split('/')[0];
        return send({ plan_id: title + '-plan', revision: title + '-commit', skipped_files: [] });
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

async function until(check, message, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  do {
    const value = await check();
    if (value) return value;
    await delay(30);
  } while (Date.now() < deadline);
  throw new Error(message);
}

async function devTools(endpoint) {
  const socket = new WebSocket(endpoint);
  const responses = new Map();
  const pageErrors = [];
  let sequence = 0;
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  socket.addEventListener('message', event => {
    const reply = JSON.parse(event.data);
    if (reply.method === "Runtime.exceptionThrown") pageErrors.push(reply.params.exceptionDetails.exception?.description);
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
  await send("Runtime.enable");
  return { socket, send, evaluate, pageErrors };
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
  { skip: !browser && 'Set SKILLHUB_BROWSER to a Chromium executable.', timeout: 60000 }, async () => {
    const { server, fixture, url } = await fixtureServer();
    const profile = await mkdtemp(join(tmpdir(), 'skillhub-browser-'));
    const chrome = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
      '--remote-debugging-port=0', '--user-data-dir=' + profile, url], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
    let startupError = '';
    let browserLog = '';
    chrome.once('error', error => { startupError = error.message; });
    chrome.stderr.setEncoding('utf8').on('data', chunk => { browserLog = (browserLog + chunk).slice(-2000); });
    let page;
    try {
      const endpoint = await until(async () => {
        if (startupError || chrome.exitCode !== null) throw new Error(startupError || 'Chromium exited with code ' + chrome.exitCode);
        try {
          const port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0];
          const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list', { signal: AbortSignal.timeout(1500) })).json();
          return pages.find(tab => tab.type === 'page')?.webSocketDebuggerUrl;
        } catch { return null; }
      }, 'Chromium never opened DevTools', 25000).catch(error => {
        throw new Error(error.message + (browserLog ? '\n' + browserLog : ''));
      });
      page = await devTools(endpoint);
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

      await size(480, 600, 1.5);
      await wait('document.querySelector("#library-results .result-button") && document.documentElement.dataset.theme === "dark"')
        .catch(error => { throw new Error(error.message + '\\n' + page.pageErrors.join('\\n')); });
      assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'));
      assert.ok(await evaluate('document.querySelector(".result-button").getBoundingClientRect().top < innerHeight'));
      await verifyContrast(evaluate);
      assert.match(await evaluate('document.querySelector(".library-row").textContent'), /Disabled/);
      assert.match(await evaluate('document.querySelector(".result-button").textContent'), /\+[1-9]/);
      const assertNoSerializedElements = async () =>
        assert.equal(await evaluate('document.body.textContent.includes("[object HTML")'), false);
      const rowCount = () => evaluate('document.querySelectorAll("#library-results .result-button").length');
      await assertNoSerializedElements();
      assert.equal(await evaluate('document.querySelectorAll(".collections button").length'), 4);
      assert.ok(await evaluate('[...document.querySelectorAll(".collections button")].every(tab => tab.getBoundingClientRect().width > 0)'));
      assert.equal(await evaluate('document.querySelectorAll("[data-route] svg").length'), 3);
      assert.equal(await evaluate('document.querySelector("#provider-overview .provider-overview-count").textContent'), '2');
      assert.match(await evaluate('document.querySelector("#provider-overview").title'), /2 AI Providers Connected: ChatGPT \/ Codex, Gemini & Antigravity/);
      assert.deepEqual(await evaluate('[...document.querySelectorAll(".collection-count")].map(tab => tab.textContent)'), ['3', '2', '1', '1']);
      assert.equal(await evaluate('document.querySelectorAll("#provider-overview svg").length'), 2);
      await click('#provider-overview');
      await wait('document.querySelector("dialog[open]")?.getAttribute("aria-modal") === "true"');
      assert.equal(await evaluate('document.querySelectorAll(".provider-overview-row").length'), 8);
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await click('#quick-theme');
      await until(() => fixture.theme === 'light', 'Quick theme was not saved');
      await wait('!document.querySelector("#quick-theme").disabled');
      await click('#quick-theme');
      await until(() => fixture.theme === 'dark', 'Quick theme was not restored');

      assert.deepEqual(await evaluate('[...document.querySelectorAll("[data-control=row-mirrored] + div [data-synced-provider]")].map(e => e.textContent)'), ['Codex', 'Gemini']);
      assert.equal(await evaluate('document.querySelector("[data-control=row-mirrored] + div .warning")'), null);
      assert.match(await evaluate('document.querySelector("[data-control=row-conflicted] + div .warning").textContent'), /Gemini needs review/);
      await click('[data-control=row-conflicted] + div .warning');
      await wait('document.querySelector("dialog[open]")?.textContent.includes("Frontmatter unchanged · Instructions changed")');
      await textButton('Leave provider-managed');
      await until(() => fixture.reviewed, 'Destination review action was not submitted');
      assert.deepEqual(fixture.reviewed, { path: '/gemini', version: 'review-hash', action: 'leave' });
      await wait('!document.querySelector("dialog[open]")');
      await click('[data-control="category-synced"]');
      assert.equal(await rowCount(), 2);
      assert.equal(await evaluate('document.querySelector(".sync-summary").hidden'), false);
      assert.match(await evaluate('document.querySelector(".sync-summary").textContent'), /2 Personal · 0 Templates/);
      assert.ok(await evaluate('document.querySelectorAll(".sync-pill").length >= 3'));
      await click('[data-control="category-user"]');
      assert.equal(await evaluate('document.querySelector(".sync-summary").hidden'), true);
      assert.equal(await rowCount(), 3);

      await textButton('Tags & filters');
      assert.equal(await evaluate('document.querySelector("dialog[open]")'), null);
      assert.equal(await evaluate('document.querySelector("#library-filters .filter-menu input").getClientRects().length'), 0);
      assert.equal(await evaluate('document.querySelector("[data-tag=system], [data-tag=skillhub], [data-tag=meta-skill]")'), null);
      await click('[data-tag="one"]');
      assert.equal(await rowCount(), 1);
      await click('[data-tag="three"]');
      assert.equal(await rowCount(), 1);
      assert.equal(await evaluate('document.querySelectorAll(".active-filters button.chip").length'), 2);
      await evaluate('const mode = document.querySelector("[aria-label=\\"Tag matching\\"]"); mode.value = "any"; mode.dispatchEvent(new Event("change"));');
      assert.equal(await rowCount(), 3);
      await assertNoSerializedElements();
      await textButton('Clear filters');
      assert.equal(await evaluate('document.querySelectorAll(".active-filters button.chip").length'), 0);
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

      await textButton('Files');
      await textButton('references/notes.md');
      await wait('document.querySelector("dialog[open]")?.textContent.includes("Supporting notes")');
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await textButton('Destinations');
      await textButton('Instructions');
      await click('.reader-inspector summary');
      await textButton('Add tag');
      await wait('document.querySelector("dialog[open] input") === document.activeElement');
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      assert.ok(await evaluate('[...document.querySelectorAll("#reader-screen button")].some(b => b.textContent.includes("Copy skill prompt"))'));
      await textButton('Edit skill');
      // Editor 2.0: write pane with toolbar and budget meters beside the live preview.
      await wait('document.querySelector(".editor-workspace .editor-toolbar") && document.querySelectorAll(".budget-meter").length === 3');
      await wait('document.querySelector(".editor-side .markdown")?.textContent.length > 0');
      await evaluate('const titleField = document.querySelector("#editor-screen label input"); titleField.value = "Edited"; titleField.dispatchEvent(new Event("input", { bubbles: true }));');
      // Ctrl+K opens the command palette; leaving the dirty editor from it still goes through the discard guard.
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog.palette[open]") && document.activeElement.classList.contains("palette-input")');
      await key('Escape');
      await wait('!document.querySelector("dialog.palette")');
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog.palette[open]")');
      await click('.palette-close');
      await wait('!document.querySelector("dialog.palette")');
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog.palette[open]") && document.activeElement.classList.contains("palette-input")');
      await evaluate('const paletteInput = document.querySelector(".palette-input"); paletteInput.value = "srch lib"; paletteInput.dispatchEvent(new Event("input"));');
      await wait('document.querySelector(".palette-item[aria-selected=true] .palette-label")?.textContent === "Search the library"');
      await key('Enter');
      await wait('document.querySelector("dialog[open]") && document.activeElement.textContent === "Cancel"');
      await key('Enter');
      await wait('!document.querySelector("dialog") && !document.querySelector("#editor-screen").hidden');
      await key('k', 2, 'KeyK');
      await wait('document.querySelector("dialog.palette[open]")');
      await evaluate('const paletteAgain = document.querySelector(".palette-input"); paletteAgain.value = "Search the library"; paletteAgain.dispatchEvent(new Event("input"));');
      await key('Enter');
      await wait('document.querySelector("dialog[open]:not(.palette)")');
      await textButton('Discard changes');
      await wait('document.activeElement.id === "library-search"');

      await size(1280, 820, 1.25);
      await click('[data-route="explore"]');
      await wait('document.querySelector("#explore-screen .result-button")');
      await until(() => fixture.prefetched.has('Slow/SKILL.md'), 'Prefetch did not start');
      assert.match(await evaluate('document.querySelector(".repo-card h2").textContent'), /fixture\/repo/);
      assert.deepEqual(await evaluate('[...document.querySelectorAll(".group-tabs .btn")].map(tab => tab.textContent)'), ['All33', 'Skills32', 'Docs1']);
      // A repository opens on the Skills tab so READMEs and docs do not crowd out actual skills.
      assert.equal(await evaluate('document.querySelector(".group-tabs [aria-pressed=true]").textContent'), 'Skills32');
      assert.match(await evaluate('document.querySelector(".result-row:nth-child(2) .files-badge").textContent'), /\+1 files/);
      await evaluate('for (const box of [...document.querySelectorAll(".result-pick")].slice(3, 5)) box.click()');
      await wait('!document.querySelector(".batch-bar").hidden && document.querySelector(".batch-bar").textContent.includes("2 selected")');
      await textButton('Clear');
      await wait('document.querySelector(".batch-bar").hidden');
      await wait('document.querySelector("#explore-screen .result-row:nth-child(2) .result-title")?.textContent === "Fast review"');
      assert.equal(fixture.preparing.size, 0);
      await click('#explore-screen .result-row:nth-child(1) button');
      await wait('document.querySelector(".preview-loading")');
      assert.equal(await evaluate(`(() => {
        document.querySelector("#explore-screen .result-row:nth-child(2) button").click();
        return document.querySelector("#explore-preview h2")?.textContent === "Fast review" && !document.querySelector(".preview-loading");
      })()`), true);
      for (const [width, height] of [[1280, 820], [1024, 660]]) {
        await size(width, height, 1);
        await wait('innerWidth === ' + width + ' && innerHeight === ' + height);
        assert.deepEqual(await evaluate(`(() => {
          const action = document.querySelector(".preview-actions .primary").getBoundingClientRect();
          const documentBody = document.querySelector(".import-document");
          const results = document.querySelector(".explore-results");
          const code = documentBody.querySelector("pre");
          return {
            outerFits: document.documentElement.scrollHeight === innerHeight && scrollY === 0,
            actionFits: action.height >= 29 && action.top >= 0 && action.bottom <= innerHeight && action.right <= innerWidth,
            documentScrolls: documentBody.scrollHeight > documentBody.clientHeight,
            resultsScroll: results.scrollHeight > results.clientHeight,
            codeOverflow: getComputedStyle(code).overflowY,
            backDisplay: getComputedStyle(document.querySelector(".explore-back")).display
          };
        })()`), { outerFits: true, actionFits: true, documentScrolls: true, resultsScroll: true,
          codeOverflow: 'visible', backDisplay: 'none' }, width + ' × ' + height);
      }
      await size(768, 660, 1);
      await wait('innerWidth === 768');
      assert.equal(await evaluate('getComputedStyle(document.querySelector(".explore-back")).display'), 'none');
      fixture.releaseSlow();
      await until(() => fixture.slowFinished, 'Slow preview never completed');
      await wait('document.querySelector("#explore-screen .result-row:nth-child(1) .result-title")?.textContent === "Slow review"');
      assert.equal(await evaluate('document.querySelector("#explore-preview h2").textContent'), 'Fast review');
      assert.equal(fixture.prefetched.get('Fast/SKILL.md'), 1);
      await size(480, 600, 1);
      await wait('innerWidth === 480');
      assert.equal(await evaluate('getComputedStyle(document.querySelector(".explore-results-column")).display'), 'none');
      await textButton('← Back to results');
      await click('#explore-screen .result-row:nth-child(3) button');
      await wait('document.querySelector("#explore-preview h2")?.textContent === "Preview unavailable"');
      assert.equal(await evaluate('document.querySelector(".preview-actions .primary")'), null);
      await textButton('← Back to results');
      await click('#explore-screen .result-row:nth-child(2) button');
      await wait('document.querySelector("#explore-preview h2")?.textContent === "Fast review"');
      // Bundle files open as tabs inside the preview instead of a modal.
      await click('.file-tab[data-path="references/notes.md"]');
      await wait('document.querySelector(".import-document")?.textContent.includes("The pinned supporting file.")');
      await click('.file-tab[data-path=""]');
      await wait('document.querySelector(".import-document")?.textContent.includes("Reviewed body")');
      await textButton('+ Add this skill');
      await until(() => fixture.applied, 'No import request received');
      assert.equal(fixture.preparing.get('Fast/SKILL.md'), 'Fast-commit');
      assert.deepEqual(fixture.applied, { route: '/imports/Fast-plan/apply', data: { revision: 'Fast-commit', name: 'fixture-import', providers: [] } });
      await wait('!document.querySelector("#reader-screen").hidden');

      await click('[data-route="settings"]');
      await textButton('Appearance & Startup');
      await wait('document.querySelector("#settings-screen select")');
      await evaluate('const themeSelect = document.querySelector("#settings-screen select"); themeSelect.value = "light"; themeSelect.dispatchEvent(new Event("change"));');
      await wait('document.documentElement.dataset.theme === "light"');
      await verifyContrast(evaluate);
      assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth'));
      await textButton('Diagnostics');
      await wait('document.querySelector("#settings-screen").textContent.includes("Watching 1 folders")');
      await textButton('Review Gemini');
      await wait('document.querySelector("dialog[open]")?.textContent.includes("Frontmatter unchanged")');
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await textButton('Recent Operations');
      await wait('document.querySelector("#settings-screen").textContent.includes("Applied a projection")');
      await textButton('Trash');
      await wait('!document.querySelector("#settings-screen").textContent.includes("Applied a projection")');
      assert.equal(fixture.startup, true);
      assert.equal(fixture.tray, true);

      await delay(250);
      await size(1280, 820, 1.25);
      await verifyContrast(evaluate);
      await click('#quick-theme');
      await wait('document.documentElement.dataset.theme === "dark"');
      await verifyContrast(evaluate);
      const baseline = fixture.reads;
      for (let id = 1; id <= 8; id++) fixture.events.write('id: ' + id + '\nevent: activity\ndata: {}\n\n');
      await until(() => fixture.reads > baseline, 'Activity did not refresh the snapshot');
      await delay(250);
      assert.equal(fixture.reads, baseline + 1);
      await assertNoSerializedElements();
    } finally {
      fixture.releaseSlow();
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
