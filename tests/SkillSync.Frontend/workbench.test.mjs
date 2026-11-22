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
  scope: 'global', always_loaded_tokens: 40,
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
const allProviders = ['codex', 'gemini', 'claude', 'cursor', 'copilot', 'windsurf', 'cline', 'roocode'];
const everyProvider = [{ provider: 'canonical', is_installed: true }, ...allProviders.map(provider => ({ provider, is_installed: true, skills_root: '/' + provider }))];
const everywhere = { ...sample, name: 'everywhere', title: 'Everywhere', tags: [],
  targets: Object.fromEntries(allProviders.map(id => [id, { enabled: true }])), destinations: allProviders.map(id => destination(id, 'synced')) };
const template = { ...sample, name: 'template', catalog_id: 'fixture-template', source_category: 'template',
  is_catalog_only: true, tags: ['workflow'] };
const system = { ...sample, name: 'bootstrap', source_category: 'system', tags: ['system', 'skillhub', 'meta-skill'] };
const item = name => ({ slug: name.toLowerCase(), title: name, description: name, repo: 'fixture/repo',
  path: name + '/SKILL.md', git_ref: 'main', kind: name === 'Skill 29' ? 'doc' : 'skill', group: name === 'Skill 29' ? 'docs' : 'skills',
  file_count: name === 'Fast' ? 1 : 0, stars: name === 'Fast' ? 18400 : undefined });

async function fixtureServer() {
  let releaseSlow;
  const slowPreview = new Promise(resolve => { releaseSlow = resolve; });
  const fixture = { appearance: {}, appearanceRequests: [], manyProviders: false, scopeRequests: [], triggerPrompts: [], learning: true, proposalApplied: null, updateApplied: false, onboarded: true, workspaces: [], workspaceUpdate: null, packUpload: null, packApply: null, releaseSlow, slowFinished: false, theme: 'dark', tray: true, startup: true, reviewed: null, applied: null, preparing: new Map(), prefetched: new Map(), events: null, reads: 0 };
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
        return send([sample, healthy, needsAttention, template, system, ...(fixture.manyProviders ? [everywhere] : [])]);
      }
      if (route === '/skill-appearance' && req.method === 'POST') {
        fixture.appearanceRequests.push(data);
        if (data.reset) delete fixture.appearance[data.key];
        else fixture.appearance[data.key] = data.image ? { image: data.image } : { icon: data.icon };
        return send({ key: data.key });
      }
      if (route === '/skill-appearance') return send({ skills: fixture.appearance });
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
      if (route === '/context-budget') return send({ tokens: 6000, characters: 24000, always_loaded_bodies_tokens: 0, global_skills: 40,
        project_only_skills: 0, off_skills: 0, safe_tokens: 2000, limit_tokens: 4000, state: 'over',
        message: 'About 6.0k tokens of skill descriptions load into every chat.',
        packs: [{ pack: 'acme/roblox', label: 'acme/roblox', kind: 'imported', tokens: 3000, global_skills: 2, project_only_skills: 0, off_skills: 0,
          skills: [{ name: 'roblox-ui', title: 'Roblox UI', tokens: 1600, scope: 'global' }, { name: 'roblox-audio', title: 'Roblox Audio', tokens: 1400, scope: 'global' }] },
        { pack: 'personal', label: 'Personal skills', kind: 'personal', tokens: 1500, global_skills: 1, project_only_skills: 0, off_skills: 0,
          skills: [{ name: 'fixture', title: 'Fixture', tokens: 1500, scope: 'global' }] }] });
      if (route === '/skills/scope' && req.method === 'POST') {
        fixture.scopeRequests.push(data);
        return send({ result: { scope: data.scope, changed: data.names ?? ['roblox-ui', 'roblox-audio'], unchanged: [], skipped: [] },
          budget: { tokens: 1500, state: 'ok' } });
      }
      if (route === '/doctor/library') return send({ score: 71, skills_checked: 3, errors: 1, warnings: 2,
        budget: { tokens: 6000, state: 'over', message: 'Too many tokens.' },
        collisions: [{ skills: ['game-ui-ux', 'roblox-ui', 'roblox-ui-layout'], shared_terms: ['hud', 'ui'], max_similarity: 0.41, all_global: true }],
        missing_references: [{ skill: 'roblox-ui-layout', missing: ['roblox-ui-genre-scifi'] }],
        skills: [{ name: 'roblox-ui-layout', title: 'Roblox UI Layout', scope: 'global', pack: 'acme/roblox', score: 59,
          findings: [{ code: 'description-length', severity: 'error', message: 'Description is 1042 characters.' }] }] });
      if (route === '/trigger-test' && req.method === 'POST') {
        fixture.triggerPrompts.push(data);
        return send({ prompt: data.prompt, collision: true, project_only_matches: [],
          summary: 'Collision: roblox-ui and game-ui-ux score within 15% of each other, so the agent may load any of them.',
          candidates: [{ name: 'roblox-ui', title: 'Roblox UI', scope: 'global', score: 4.2, relative: 1, verdict: 'tie', matched_terms: ['roblox', 'hud'] },
            { name: 'game-ui-ux', title: 'Game UI UX', scope: 'global', score: 3.9, relative: 0.93, verdict: 'tie', matched_terms: ['hud'] }] });
      }
      if (route === '/tags') return send(['one', 'system', 'skillhub', 'meta-skill'].map(tag => ({ tag, skill_count: 1 })));
      if (route === '/providers') return send(fixture.manyProviders ? everyProvider : installed);
      if (route === '/preferences/theme' && req.method === 'PATCH') {
        fixture.theme = data.theme;
        return send({ theme: fixture.theme });
      }
      if (route === '/status') return send({ install: { mcp_clients: [] } });
      if (route === '/diagnostics') return send({ watcher: { running: true, roots: 1, last_error: null,
          folders: [{ path: '/codex', provider: 'codex', label: 'ChatGPT / Codex', purpose: 'Picks up edits you make to skills inside this tool.' }] },
        conflicts: [{ id: 'fixture/20261113-093739918-a.json', skill: 'fixture', destination: '/gemini/fixture/SKILL.md', revision: 'r1',
          can_undo: false, saved_at: '2026-11-13T09:37:39Z', reason: 'The skill was edited in SkillHub and in this tool at the same time.',
          provider: 'gemini', file_count: 1, skill_exists: true }],
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
        return send({ theme: fixture.theme, close_to_tray: fixture.tray, auto_start: fixture.startup, onboarding_completed: fixture.onboarded, continuous_learning: fixture.learning });
      }
      if (route === '/updates/skillhub') return send({ current_version: '1.4.0', current_commit: 'a'.repeat(40), latest_version: '1.4.1',
        latest_commit: 'b'.repeat(40), update_available: true, method: 'installer', source: 'github-api', summary: 'SkillHub 1.4.1 is available (you have 1.4.0).' });
      if (route === '/updates/skillhub/apply') { fixture.updateApplied = true; return send({ started: true, method: 'installer', message: 'Installing SkillHub 1.4.1.' }); }
      if (route === '/preferences/learning') { fixture.learning = data.enabled; return send({ enabled: data.enabled }); }
      if (route === '/proposals' && url.searchParams.get('skill') === 'fixture') return send({ proposals: fixture.proposalApplied ? [] : [{ id: 'p1', skill: 'fixture',
        summary: 'Add the cache-busting step', instructions_markdown: 'Better body', source: 'mcp', created_at: '2026-11-17T10:00:00Z' }] });
      if (route === '/proposals') return send({ proposals: [] });
      if (route === '/proposals/p1/apply') { fixture.proposalApplied = true; return send({ skill: sample }); }
      if (route === '/preferences/onboarding') { fixture.onboarded = data.enabled; return send({ enabled: data.enabled }); }
      if (route === '/workspaces' && req.method === 'GET') return send({ workspaces: fixture.workspaces,
        targets: ['claude', 'codex', 'gemini', 'cursor', 'copilot'].map(id => ({ id, label: id + ' folder' })) });
      if (route === '/workspaces' && req.method === 'POST') {
        const created = { id: 'ws1', name: data.name ?? data.path.split('/').at(-1), path: data.path, exists: true, skills: [],
          targets: ['claude', 'codex', 'gemini', 'cursor'], file_count: 0, last_synced_at: null, issues: [], missing_skills: [],
          keep_local: true, git_repo: true, also_global: [], off_skills: [] };
        fixture.workspaces.push(created);
        return send(created);
      }
      if (route === '/workspaces/ws1' && req.method === 'PUT') {
        const current = fixture.workspaces[0];
        Object.assign(current, { skills: data.skills, targets: data.targets, file_count: data.skills.length * data.targets.length,
          last_synced_at: '2026-11-15T10:00:00Z', keep_local: data.keep_local, also_global: data.skills.filter(name => name === 'fixture') });
        fixture.workspaceUpdate = data;
        return send(current);
      }
      if (route === '/skillpacks/preview') {
        fixture.packUpload = data;
        return send({ pack_id: 'pack1', file_name: data.file_name, created_by: 'SkillHub 1.3.0', created_at: '2026-11-15T10:00:00Z', skipped: [],
          skills: [{ name: 'fixture', title: 'Fixture', description: 'From a pack', files: ['scripts/run.sh'], name_exists: true,
            same_as_existing: false, suggested_name: 'fixture-2', instructions_markdown: 'Pack body',
            scripts: [{ path: 'scripts/run.sh', language: 'shell', concerns: ['downloads and runs code'] }], providers: [] }] });
      }
      if (route === '/skillpacks/pack1/apply') { fixture.packApply = data; return send({ imported: ['fixture-2'], replaced: [], skipped: [] }); }
      if (route === '/community/repos') return send({ repositories: [{ repo: 'featured/all', title: 'Featured' }] });
      if (route === '/community/skills') return send({ skills: ['Slow', 'Fast', 'Broken',
        ...Array.from({ length: 70 }, (_, index) => 'Skill ' + index)].map(item),
        repository: { repo: 'fixture/repo', sha: 'f'.repeat(40), mode: 'tarball', readme_excerpt: '# Fixture\n\nFixture readme text.',
          license_path: 'LICENSE', counts: { skills: 72, docs: 1 }, truncated: false, pinned: false } });
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
    const mime = path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : path.endsWith('.svg') ? 'image/svg+xml' : 'text/html';
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
  { skip: !browser && 'Set SKILLHUB_BROWSER to a Chromium executable.', timeout: 180000 }, async () => {
    const { server, fixture, url } = await fixtureServer();
    let chrome;
    let profile;
    let page;
    const stopBrowser = async () => {
      if (chrome && chrome.exitCode === null && chrome.signalCode === null && chrome.pid) {
        const closed = once(chrome, 'exit');
        chrome.kill();
        await closed;
      }
      if (profile) await rm(profile, { recursive: true, force: true, maxRetries: 8, retryDelay: 150 });
    };
    // A fresh headless Chromium on a CI runner occasionally hangs before DevTools is up (component updates, first-run
    // work). Background work is switched off, and a stalled launch is retried with a new profile.
    const launch = async () => {
      profile = await mkdtemp(join(tmpdir(), 'skillhub-browser-'));
      chrome = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
        '--disable-background-networking', '--disable-component-update', '--disable-sync', '--disable-extensions',
        '--disable-features=Translate,OptimizationHints,MediaRouter', '--enable-logging=stderr', '--v=0',
        '--remote-debugging-port=0', '--user-data-dir=' + profile, url], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
      let startupError = '';
      let browserLog = '';
      let stage = 'waiting for DevToolsActivePort';
      chrome.once('error', error => { startupError = error.message; });
      chrome.stderr.setEncoding('utf8').on('data', chunk => { browserLog = (browserLog + chunk).slice(-2000); });
      return until(async () => {
        if (startupError || chrome.exitCode !== null) throw new Error(startupError || 'Chromium exited with code ' + chrome.exitCode);
        try {
          const port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0];
          stage = 'DevTools on port ' + port + ', waiting for the page target';
          const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list', { signal: AbortSignal.timeout(1500) })).json();
          stage = 'targets: ' + pages.map(tab => tab.type + ' ' + tab.url).join(', ');
          return pages.find(tab => tab.type === 'page')?.webSocketDebuggerUrl;
        } catch { return null; }
      }, 'Chromium never opened DevTools', 25000).catch(error => {
        throw new Error(error.message + ' (' + stage + ')' + (browserLog ? '\n' + browserLog : ''));
      });
    };
    try {
      let endpoint;
      for (let attempt = 1; !endpoint; attempt++) {
        try { endpoint = await launch(); }
        catch (error) {
          await stopBrowser();
          if (attempt === 3) throw error;
          console.log('# Chromium launch ' + attempt + ' stalled, retrying: ' + error.message.split('\n')[0]);
        }
      }
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
      // One brand mark everywhere: the header logo and the favicon are the same SVG the .ico is generated from.
      await wait('document.querySelector(".brand-logo").complete && document.querySelector(".brand-logo").naturalWidth > 0', 'brand logo did not load');
      assert.deepEqual(await evaluate('[new URL(document.querySelector(".brand-logo").src).pathname, new URL(document.querySelector("link[rel=icon]").href).pathname]'),
        ['/img/skillhub-logo.svg', '/img/skillhub-logo.svg']);
      assert.equal(await evaluate('document.querySelector(".brand-mark")'), null);
      // Header at every width: tools stay inside the window, the brand never touches the tabs, the tabs never touch the tools.
      for (const width of [460, 540, 640, 720, 768, 850, 900, 1024, 1280, 1440]) {
        await size(width, 800, 1);
        await wait('innerWidth === ' + width);
        const expectedPlaceholder = width <= 720 ? 'Search skills… (/)' : 'Search skills, tags, and instructions';
        await wait('document.querySelector("#library-search").placeholder === ' + JSON.stringify(expectedPlaceholder), 'placeholder at ' + width);
        const header = await evaluate(`(() => {
          const rect = selector => document.querySelector(selector).getBoundingClientRect();
          const brand = rect('.brand'), nav = rect('#primary-nav'), tools = rect('.app-tools');
          const theme = rect('#quick-theme'), pill = rect('#provider-overview');
          const search = document.querySelector('#library-search');
          return {
            themeInside: theme.left >= 0 && theme.right <= innerWidth && theme.width > 0,
            pillInside: pill.left >= 0 && pill.right <= innerWidth && pill.width > 0,
            brandBeforeNav: brand.right <= nav.left,
            navBeforeTools: nav.right <= tools.left,
            noPageOverflow: document.documentElement.scrollWidth <= innerWidth,
            navNotClipped: document.querySelector('#primary-nav').scrollWidth <= document.querySelector('#primary-nav').clientWidth + 1,
            brandLabel: getComputedStyle(document.querySelector('.brand-label')).clipPath !== 'none',
            placeholder: search.placeholder,
            ellipsis: getComputedStyle(search).textOverflow
          };
        })()`);
        const compact = width <= 720;
        assert.deepEqual(header, { themeInside: true, pillInside: true, brandBeforeNav: true, navBeforeTools: true, noPageOverflow: true, navNotClipped: true,
          brandLabel: compact, placeholder: compact ? 'Search skills… (/)' : 'Search skills, tags, and instructions', ellipsis: 'ellipsis' },
          'header at ' + width + 'px');
      }
      await size(480, 600, 1.5);
      await wait('innerWidth === 480');
      await verifyContrast(evaluate);
      assert.match(await evaluate('document.querySelector(".library-row").textContent'), /Disabled/);
      assert.match(await evaluate('document.querySelector(".result-button").textContent'), /\+[1-9]/);
      const assertNoSerializedElements = async () =>
        assert.equal(await evaluate('document.body.textContent.includes("[object HTML")'), false);
      const rowCount = () => evaluate('document.querySelectorAll("#library-results .result-button").length');
      await assertNoSerializedElements();
      assert.equal(await evaluate('document.querySelectorAll(".collections button").length'), 4);
      assert.ok(await evaluate('[...document.querySelectorAll(".collections button")].every(tab => tab.getBoundingClientRect().width > 0)'));
      assert.equal(await evaluate('document.querySelectorAll("[data-route] svg").length'), 4);
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

      await wait('document.querySelector("#reader-screen .proposal-banner")?.textContent.includes("Add the cache-busting step")');
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
      // Skill icon: initials by default, then a built-in icon, an uploaded image (resized to 128 px) and back to initials.
      assert.equal(await evaluate('document.querySelector("#reader-screen .skill-badge-button .skill-glyph").dataset.badge'), 'initials');
      assert.equal(await evaluate('document.querySelector("#reader-screen .skill-badge-button .skill-glyph").textContent'), 'F');
      await click('#reader-screen .skill-badge-button');
      await wait('document.querySelector("dialog.icon-picker[open]")');
      assert.ok(await evaluate('document.querySelectorAll("dialog.icon-picker .icon-choice").length >= 30'));
      assert.equal(await evaluate('document.querySelector("[data-control=save-icon]").disabled'), true);
      await click('dialog.icon-picker [data-icon="terminal"]');
      assert.equal(await evaluate('document.querySelector("dialog.icon-picker [data-icon=terminal]").getAttribute("aria-pressed")'), 'true');
      await click('[data-control=save-icon]');
      await until(() => fixture.appearanceRequests.length === 1, 'Icon choice was not saved');
      assert.deepEqual(fixture.appearanceRequests[0], { key: 'fixture', icon: 'terminal' });
      await wait('document.querySelector("#reader-screen .skill-badge-button .skill-glyph")?.dataset.badge === "icon"', 'reader badge did not show the icon');
      await wait('document.querySelector("#library-screen [data-control=row-fixture] .skill-glyph")?.dataset.badge === "icon"', 'library badge did not show the icon');
      await click('#reader-screen .skill-badge-button');
      await wait('document.querySelector("dialog.icon-picker[open]")');
      await evaluate(`(async () => {
        const canvas = document.createElement('canvas'); canvas.width = 400; canvas.height = 300;
        const context = canvas.getContext('2d'); context.fillStyle = '#e4572e'; context.fillRect(0, 0, 400, 300);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        const input = document.querySelector('dialog.icon-picker input[type=file]');
        const transfer = new DataTransfer(); transfer.items.add(new File([blob], 'logo.png', { type: 'image/png' }));
        input.files = transfer.files; input.dispatchEvent(new Event('change'));
      })()`);
      await wait('document.querySelector("dialog.icon-picker .icon-picker-preview img")', 'uploaded image was not previewed');
      assert.deepEqual(await evaluate(`new Promise(resolve => { const image = new Image(); image.onload = () => resolve([image.width, image.height]);
        image.src = document.querySelector("dialog.icon-picker .icon-picker-preview img").src; })`), [128, 128]);
      await click('[data-control=save-icon]');
      await until(() => fixture.appearanceRequests.length === 2, 'Image was not saved');
      assert.match(fixture.appearanceRequests[1].image, /^data:image\/(webp|png);base64,/);
      await wait('document.querySelector("#reader-screen .skill-badge-button .skill-glyph")?.dataset.badge === "image"', 'reader badge did not show the image');
      await click('#reader-screen .skill-badge-button');
      await wait('document.querySelector("dialog.icon-picker[open]")');
      await click('[data-control=reset-initials]');
      await click('[data-control=save-icon]');
      await until(() => fixture.appearanceRequests.length === 3, 'Reset was not saved');
      assert.deepEqual(fixture.appearanceRequests[2], { key: 'fixture', reset: true });
      await wait('document.querySelector("#reader-screen .skill-badge-button .skill-glyph")?.dataset.badge === "initials"', 'reader badge did not return to initials');
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
      assert.deepEqual(await evaluate('[...document.querySelectorAll(".group-tabs .btn")].map(tab => tab.textContent)'), ['All73', 'Skills72', 'Docs1']);
      // A repository opens on the Skills tab so READMEs and docs do not crowd out actual skills.
      assert.equal(await evaluate('document.querySelector(".group-tabs [aria-pressed=true]").textContent'), 'Skills72');
      assert.match(await evaluate('document.querySelector(".result-row:nth-child(2) .files-badge").textContent'), /📦 Bundle · 2 files/);
      assert.match(await evaluate('document.querySelector(".result-row:nth-child(2) .stars-badge").textContent'), /★ 18[.,]4K/i);
      assert.equal(await evaluate('document.querySelector(".result-row:nth-child(30) .files-badge").textContent'), '1 file (SKILL.md)');
      // Infinite scroll: the first 50 rows render, more load as the sentinel scrolls into view, and the count stays pinned.
      assert.equal(await evaluate('document.querySelectorAll("#explore-screen .result-row").length'), 50);
      assert.ok(await evaluate('Boolean(document.querySelector(".load-more-sentinel button"))'));
      await evaluate('{ const list = document.querySelector(".explore-results"); list.scrollTop = list.scrollHeight; }');
      await wait('document.querySelectorAll("#explore-screen .result-row").length === 72 && !document.querySelector(".load-more-sentinel")',
        'more Explore rows load on scroll');
      const pinned = await evaluate(`(() => {
        const list = document.querySelector(".explore-results");
        list.scrollTop = list.scrollHeight;
        const summary = document.querySelector(".explore-summary").getBoundingClientRect();
        return { position: getComputedStyle(document.querySelector(".explore-summary")).position, scrolled: list.scrollTop,
          offset: Math.round(summary.top - list.getBoundingClientRect().top), height: summary.height };
      })()`);
      assert.ok(pinned.position === 'sticky' && pinned.scrolled > 0 && Math.abs(pinned.offset) <= 4 && pinned.height > 0,
        'the result count stays visible while scrolling ' + JSON.stringify(pinned));
      await evaluate('document.querySelector(".explore-results").scrollTop = 0');
      assert.equal(await evaluate('document.querySelector("[data-control=direct-import]").textContent'), 'Import from URL / File');
      await evaluate('for (const box of [...document.querySelectorAll(".result-pick")].slice(3, 5)) box.click()');
      await wait('!document.querySelector(".batch-bar").hidden && document.querySelector(".batch-bar").textContent.includes("2 selected")');
      assert.equal(await evaluate('document.querySelector("[data-control=import-selected]").textContent'), 'Import 2 selected skills');
      assert.equal(await evaluate('document.querySelector("[data-control=deselect-all]").textContent'), 'Deselect all');
      await textButton('Deselect all');
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
      assert.match(await evaluate('document.querySelector(".preview-trust")?.textContent ?? ""'), /★ 18[.,]4K stars/i);
      assert.deepEqual(await evaluate('[...document.querySelectorAll(".deploy-chip")].filter(chip => chip.querySelector("input").checked).map(chip => chip.dataset.provider)'), ['codex', 'gemini']);
      // Bundle files open as tabs inside the preview instead of a modal.
      await click('.file-tab[data-path="references/notes.md"]');
      await wait('document.querySelector(".import-document")?.textContent.includes("The pinned supporting file.")');
      await click('.file-tab[data-path=""]');
      await wait('document.querySelector(".import-document")?.textContent.includes("Reviewed body")');
      await textButton('+ Add this skill');
      await until(() => fixture.applied, 'No import request received');
      assert.equal(fixture.preparing.get('Fast/SKILL.md'), 'Fast-commit');
      assert.deepEqual(fixture.applied, { route: '/imports/Fast-plan/apply', data: { revision: 'Fast-commit', name: 'fixture-import', providers: ['codex', 'gemini'] } });
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
      // Native replaceChildren would print null or undefined as text; nothing like that may reach the screen.
      assert.equal(await evaluate(`(() => {
        const walker = document.createTreeWalker(document.querySelector("#settings-screen"), NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) if (/^(null|undefined|false)$/.test(walker.currentNode.textContent.trim())) return walker.currentNode.textContent;
        return "";
      })()`), '');
      assert.ok(await evaluate('[...document.querySelectorAll("#settings-screen button")].some(b => b.textContent.includes("Verify & Sync All Folders"))'));
      assert.match(await evaluate('document.querySelector("#settings-screen .watched-folder").textContent'), /ChatGPT \/ Codex.*\/codex/);
      assert.match(await evaluate('document.querySelector("#settings-screen .backups summary").textContent'), /Conflict & Safety Backups \(1\)/);
      assert.match(await evaluate('document.querySelector("#settings-screen .backup-row").textContent'),
        /edited in SkillHub and in this tool[\s\S]*Preview \/ Compare Changes[\s\S]*Restore & Replace Current Skill[\s\S]*Restore as New Separate Skill/);
      await textButton('Review Gemini');
      await wait('document.querySelector("dialog[open]")?.textContent.includes("Frontmatter unchanged")');
      await key('Escape');
      await wait('!document.querySelector("dialog[open]")');
      await textButton('Recent Operations');
      await wait('document.querySelector("#settings-screen").textContent.includes("Applied a projection")');
      assert.equal(await evaluate('document.querySelector("#settings-screen .activity-row .status-badge").textContent'), 'Synced');
      await textButton('Trash');
      await wait('!document.querySelector("#settings-screen").textContent.includes("Applied a projection")');
      assert.equal(fixture.startup, true);
      assert.equal(fixture.tray, true);

      // Continuous skill learning toggle and the Updates section.
      await textButton('Appearance & Startup');
      await wait('[...document.querySelectorAll("#settings-screen label")].some(l => l.textContent.includes("Let AI tools offer skill improvements"))');
      await evaluate('[...document.querySelectorAll("#settings-screen label")].find(l => l.textContent.includes("Let AI tools offer skill improvements")).querySelector("input").click()');
      await until(() => fixture.learning === false, 'Learning preference was not saved');
      await wait('document.querySelector("#settings-screen").textContent.includes("SkillHub 1.4.1 is available")');
      // The update notice is a slim strip under the header (not a dot in the crowded header) that stays tinted on hover.
      await wait('!document.querySelector("#update-banner").hidden');
      assert.match(await evaluate('document.querySelector("#update-banner").textContent'), /Update available: v1\.4\.1 · You have v1\.4\.0/);
      assert.ok(await evaluate('document.querySelector("#update-banner").getBoundingClientRect().top >= document.querySelector(".app-bar").getBoundingClientRect().bottom - 1'));
      assert.equal(await evaluate('document.querySelector(".app-tools [data-update-action]")'), null);
      const stripBefore = await evaluate('getComputedStyle(document.querySelector("#update-banner")).backgroundColor');
      { const box = await evaluate('(({ x, y, width, height }) => ({ x, y, width, height }))(document.querySelector(".update-strip-text").getBoundingClientRect())');
        await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: box.x + box.width / 2, y: box.y + box.height / 2 }); }
      assert.equal(await evaluate('getComputedStyle(document.querySelector("#update-banner")).backgroundColor'), stripBefore, 'the strip must not change on hover');
      assert.notEqual(stripBefore, await evaluate('getComputedStyle(document.querySelector("#update-banner [data-update-action=apply]")).backgroundColor'));
      // Explore fills the window below the header and the strip without a page scrollbar.
      await size(1280, 820, 1);
      await click('[data-route="explore"]');
      await wait('innerWidth === 1280 && document.querySelector("#explore-screen .result-button")');
      await wait('document.documentElement.scrollHeight === innerHeight', 'Explore overflows the window while the update strip is shown');
      await size(480, 600, 1);
      await click('[data-route="settings"]');
      await wait('innerWidth === 480 && !document.querySelector("#settings-screen").hidden');
      await click('#update-banner [data-update-action=apply]');
      await wait('document.querySelector("dialog[open]")?.textContent.includes("Update & Restart")');
      await evaluate('[...document.querySelectorAll("dialog[open] button")].find(b => b.textContent === "Update & Restart").click()');
      await until(() => fixture.updateApplied, 'Update was not applied');
      await wait('!document.querySelector("dialog[open]")');
      await click('#update-banner [data-update-action=dismiss]');
      await wait('document.querySelector("#update-banner").hidden');
      assert.equal(await evaluate('localStorage.getItem("skillhub_dismissed_update")'), 'v1.4.1');
      await evaluate('localStorage.removeItem("skillhub_dismissed_update")');

      // Welcome guide: three steps, opened from Settings, marks onboarding done when closed.
      fixture.onboarded = false;
      await textButton('Appearance & Startup');
      await wait('[...document.querySelectorAll("#settings-screen button")].some(b => b.textContent.includes("Backup All My Skills"))');
      await textButton('Run Welcome Guide');
      await wait('document.querySelector("dialog.onboarding[open]")?.textContent.includes("Your AI tools")');
      assert.match(await evaluate('document.querySelector(".onboarding-body").textContent'), /SkillHub found 2 AI tools/);
      await click('.onboarding-foot .primary');
      await wait('document.querySelector(".onboarding-stats")');
      assert.match(await evaluate('document.querySelector(".onboarding-stats").textContent'), /personal skills/);
      await click('.onboarding-foot .primary');
      await wait('document.querySelectorAll(".onboarding-prompts li").length === 3');
      assert.match(await evaluate('document.querySelector(".onboarding-prompts").textContent'), /Use SkillHub to find or create a skill for/);
      await click('.onboarding-foot .primary');
      await wait('!document.querySelector("dialog.onboarding")');
      await until(() => fixture.onboarded === true, 'Onboarding was not marked complete');

      // Library health: the always-loaded meter, pack scope, library audit and trigger tester.
      await click('[data-route="library"]');
      await wait('document.querySelector("#context-budget-meter")?.dataset.state === "over"', 'Budget meter did not load');
      assert.match(await evaluate('document.querySelector("#context-budget-meter").textContent'), /Always-loaded: ~6\.0k tokens \/ chat/);
      // Modals lock the page behind them: no wheel scrolling, and a click on the dimmed backdrop closes them.
      await size(480, 360, 1);
      await wait('innerHeight === 360 && document.documentElement.scrollHeight > innerHeight + 100');
      await evaluate('window.scrollTo(0, 0)');
      const wheel = () => send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: 12, y: 300, deltaX: 0, deltaY: 400 });
      await wheel();
      await wait('scrollY > 0', 'the page itself should scroll without a dialog');
      await evaluate('window.scrollTo(0, 0)');
      await click('#context-budget-meter');
      await wait('document.querySelector("dialog[open] .budget-summary")');
      assert.equal(await evaluate('getComputedStyle(document.documentElement).overflowY'), 'hidden');
      for (let turn = 0; turn < 3; turn++) await wheel();
      await delay(200);
      assert.equal(await evaluate('scrollY'), 0, 'background scrolled behind the budget dialog');
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: 4, y: 354, button: 'left', clickCount: 1 });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 4, y: 354, button: 'left', clickCount: 1 });
      await wait('!document.querySelector("dialog[open]")', 'a backdrop click did not close the dialog');
      assert.notEqual(await evaluate('getComputedStyle(document.documentElement).overflowY'), 'hidden');
      await size(480, 600, 1);
      await wait('innerHeight === 600');
      await click('#context-budget-meter');
      await wait('document.querySelector("dialog[open] .budget-pack[data-pack=\'acme/roblox\'] [data-pack-action=project]")');
      assert.match(await evaluate('document.querySelector("dialog[open] .budget-summary").textContent'), /6\.0k tokens in every chat/);
      await click('dialog[open] .budget-pack[data-pack="acme/roblox"] [data-pack-action=project]');
      await until(() => fixture.scopeRequests.length === 1, 'Pack scope was not sent');
      assert.deepEqual(fixture.scopeRequests[0], { scope: 'project', source: 'acme/roblox' });
      await evaluate('document.querySelector("dialog[open]").close()');
      await wait('!document.querySelector("dialog[open]")');
      await click('#library-audit');
      await wait('document.querySelector("dialog[open] .audit-score")');
      const audit = await evaluate('document.querySelector("dialog[open]").textContent');
      assert.match(audit, /71\/100/);
      assert.match(audit, /game-ui-ux[\s\S]*roblox-ui-layout/);
      assert.match(audit, /refers to roblox-ui-genre-scifi/);
      await evaluate('document.querySelector("dialog[open]").close()');
      await wait('!document.querySelector("dialog[open]")');
      await click('#trigger-tester');
      await wait('document.querySelector("dialog[open] input[type=search]")');
      await evaluate('const prompt = document.querySelector("dialog[open] input[type=search]"); prompt.value = "Make a Roblox HUD"; prompt.dispatchEvent(new Event("input"));');
      await wait('document.querySelectorAll("dialog[open] .trigger-results li[data-verdict=tie]").length === 2', 'Trigger tester did not show the tie');
      assert.equal(fixture.triggerPrompts.at(-1).prompt, 'Make a Roblox HUD');
      assert.match(await evaluate('document.querySelector("dialog[open] .trigger-summary").textContent'), /Collision/);
      await evaluate('document.querySelector("dialog[open]").close()');
      await wait('!document.querySelector("dialog[open]")');

      // Projects: add a folder, choose skills and targets, save.
      await click('[data-route="projects"]');
      await wait('document.querySelector("#projects-screen .workspaces-empty")');
      await textButton('Add your first project');
      await wait('document.querySelector("dialog[open] input")');
      await evaluate('const projectPath = document.querySelector("dialog[open] input"); projectPath.value = "C:/code/demo-app"; projectPath.dispatchEvent(new Event("input"));');
      await evaluate('[...document.querySelectorAll("dialog[open] button")].find(b => b.textContent === "Add project").click()');
      await wait('document.querySelector(".workspace-detail h2")?.textContent === "demo-app"');
      assert.equal(await evaluate('document.querySelector(".workspace-foot .primary").disabled'), true);
      await click('.workspace-skill input[data-skill="fixture"]');
      await click('.workspace-targets input[data-target="copilot"]');
      await click('.workspace-foot .primary');
      await until(() => fixture.workspaceUpdate, 'Workspace update was not sent');
      assert.deepEqual(fixture.workspaceUpdate, { skills: ['fixture'], targets: ['claude', 'codex', 'gemini', 'cursor', 'copilot'], keep_local: true });
      await wait('document.querySelector(".workspace-detail")?.textContent.includes("5 files written")');
      // The chosen skill is also global: flagged, with a one-click switch to Project-only.
      await wait('document.querySelector(".workspace-skill-flags [data-redundant=fixture]")', 'Redundant badge missing');
      assert.match(await evaluate('document.querySelector("[data-redundant-summary]").textContent'), /1 skill is also synced globally/);
      assert.equal(await evaluate('document.querySelector("[data-keep-local]").checked'), true);
      await click('[data-make-project-only="fixture"]');
      await until(() => fixture.scopeRequests.length === 2, 'Project-only switch was not sent');
      assert.deepEqual(fixture.scopeRequests[1], { scope: 'project', names: ['fixture'] });
      assert.doesNotMatch(await evaluate('document.querySelector("main").textContent'), /github\/instructions/);
      assert.match(await evaluate('document.querySelector("[data-umbrella-note]")?.textContent ?? ""'), /umbrella folder once/);

      // Skill pack import: the review lists conflicts and scripts before anything is applied.
      await click('[data-route="explore"]');
      await wait('document.querySelector("#explore-screen .result-button")');
      await evaluate(`(async () => {
        const { reviewPack } = await import('/js/components/skillPacks.js');
        const file = new File([new Uint8Array([80, 75, 3, 4])], 'team.skillpack.zip', { type: 'application/zip' });
        await reviewPack(file, { refresh: async () => {}, read: () => {} });
      })()`);
      await wait('document.querySelector("dialog[open] .pack-skill")');
      assert.equal(fixture.packUpload.file_name, 'team.skillpack.zip');
      assert.match(await evaluate('document.querySelector("dialog[open] .pack-skill").textContent'), /Name in use: fixture[\s\S]*risky patterns/);
      assert.equal(await evaluate('document.querySelector("dialog[open] .pack-rename input").value'), 'fixture-2');
      await evaluate('[...document.querySelectorAll("dialog[open] button")].find(b => b.textContent === "Import selected skills").click()');
      await until(() => fixture.packApply, 'Pack import was not applied');
      assert.deepEqual(fixture.packApply, { choices: [{ name: 'fixture', action: 'import', new_name: 'fixture-2' }], keep_sync_settings: false });
      await wait('!document.querySelector("dialog[open]")');
      await click('[data-route="settings"]');
      await textButton('Diagnostics');

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

      // Eight AI tools: the header and each row show three logos plus "+5"; the popover lists all eight and stays on screen.
      fixture.manyProviders = true;
      await click('[data-route="library"]');
      fixture.events.write('id: 9\nevent: activity\ndata: {}\n\n');
      await wait('document.querySelector("[data-control=row-everywhere]")', 'the eight-provider skill did not appear');
      for (const width of [460, 768, 1440]) {
        await size(width, 800, 1);
        await wait('innerWidth === ' + width);
        const overflow = await evaluate(`(() => {
          const pill = document.querySelector('#provider-overview');
          const status = document.querySelector('[data-control=row-everywhere] + div');
          return { headerIcons: pill.querySelectorAll('.provider-overview-icons svg').length, headerMore: pill.querySelector('.provider-overview-more')?.textContent,
            count: pill.querySelector('.provider-overview-count').textContent, rowIcons: status.querySelectorAll('[data-synced-provider]').length,
            rowMore: status.querySelector('.overflow-more-button')?.textContent, pillInside: pill.getBoundingClientRect().right <= innerWidth,
            noPageOverflow: document.documentElement.scrollWidth <= innerWidth };
        })()`);
        assert.deepEqual(overflow, { headerIcons: 3, headerMore: '+5', count: '8', rowIcons: 3, rowMore: '+5', pillInside: true, noPageOverflow: true }, 'overflow at ' + width);
        await click('[data-control=row-everywhere] + div .overflow-more-button');
        const pop = await evaluate(`(() => {
          const pop = document.querySelector('[data-control=row-everywhere] + div .overflow-pop');
          const box = pop.getBoundingClientRect();
          return { shown: getComputedStyle(pop).display !== 'none', items: pop.querySelectorAll('.overflow-pop-item').length,
            inside: box.left >= 0 && box.right <= innerWidth, text: pop.textContent };
        })()`);
        assert.equal(pop.shown && pop.inside, true, 'popover at ' + width + ' ' + JSON.stringify(pop));
        assert.equal(pop.items, 8);
        assert.match(pop.text, /Synced to · 8[\s\S]*Roo Code/);
        await evaluate('document.querySelector("[data-control=row-everywhere] + div .overflow-more-button").focus()');
        await key('Escape');
        await wait('document.querySelector("[data-control=row-everywhere] + div .overflow-more-button").getAttribute("aria-expanded") === "false"');
        await evaluate('document.activeElement.blur()');
      }
      assert.match(await evaluate('document.querySelector("#provider-overview").title'), /8 AI Providers Connected: .*Roo Code/);
    } finally {
      fixture.releaseSlow();
      page?.socket.close();
      await stopBrowser();
      fixture.events?.end();
      server.closeAllConnections();
      server.close();
    }
  });
