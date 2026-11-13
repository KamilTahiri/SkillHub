import { api } from './api.js?v=20261113-v12';
import { Latest, PROVIDERS, deployment, installedProviders, skillKey, tagSlug } from './state.js?v=20261113-v12';
import { icon } from './icons.js?v=20261113-v12';
import { button, confirmAction, h, modal, notify } from './ui.js?v=20261113-v12';
import { createLibrary } from './components/skillList.js?v=20261113-v12';
import { createReader } from './components/skillReader.js?v=20261113-v12';
import { createEditor } from './components/skillEditor.js?v=20261113-v12';
import { createExplore } from './components/explore.js?v=20261113-v12';
import { showDestinationReview } from './components/destinationReview.js?v=20261113-v12';
import { createSettings } from './components/statusBar.js?v=20261113-v12';

const views = Object.fromEntries(['library', 'reader', 'editor', 'explore', 'settings']
  .map(name => [name, document.getElementById(name + '-screen')]));
const nav = document.getElementById('primary-nav');
const status = document.getElementById('connection-status');
const providerNames = { ...PROVIDERS, codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity' };
const providerButton = document.getElementById('provider-overview');
const themeButton = document.getElementById('quick-theme');
let online = false;
for (const tab of nav.querySelectorAll('button')) tab.prepend(icon(tab.dataset.route));
const snapshotJob = new Latest();
const detailJob = new Latest();
const systemTheme = matchMedia('(prefers-color-scheme: dark)');
let route = 'library';
let theme = 'system';
let detail;
let selectedKey;
let refreshTimer;
let guardPending = false;
let loaded = false;
let snapshot = { skills: [], tags: [], providers: [] };
let lastEvent = 0;

const app = {
  get snapshot() { return snapshot; },
  refresh,
  navigate,
  showConnections: () => navigate('settings', { section: 'Connections' }),
  read,
  edit: (data, { clone = false } = {}) => openEditor(data, clone),
  newSkill: () => openEditor(),
  cancelEditor: key => key ? read(key) : navigate('library'),
  updateDetail,
  applyTheme,
  setTheme,
  get theme() { return theme; },
  review: (skill, target) => showDestinationReview(app, skill, target),
  refreshDiagnostics: () => { if (route === 'settings') settings.refreshDiagnostics(); },
  exploreCatalog: () => navigate('explore', { catalog: true })
};
const library = createLibrary(views.library, app);
const reader = createReader(views.reader, app);
const editor = createEditor(views.editor, app);
const explore = createExplore(views.explore, app);
const settings = createSettings(views.settings, app);

async function canLeave() {
  if (guardPending || (route === 'editor' && editor.isSaving) || (route === 'explore' && explore.isApplying)) return false;
  if (route !== 'editor' || !editor.isDirty) return true;
  guardPending = true;
  try {
    const discard = await confirmAction('Discard unsaved changes?', 'Your changes to this draft will be lost.', 'Discard changes');
    if (discard) editor.discard();
    return discard;
  } finally { guardPending = false; }
}

function activate(next, focus = true) {
  if (route !== next) {
    if (route === 'explore') explore.leave();
    if (route === 'settings') settings.leave();
    if (route === 'reader') detailJob.cancel();
  }
  route = next;
  for (const [name, view] of Object.entries(views)) view.hidden = name !== next;
  for (const tab of nav.querySelectorAll('button')) {
    const selected = tab.dataset.route === next || tab.dataset.route === 'library' && ['reader', 'editor'].includes(next);
    if (selected) tab.setAttribute('aria-current', 'page');
    else tab.removeAttribute('aria-current');
  }
  const hash = next === 'reader' && selectedKey ? 'skill/' + encodeURIComponent(selectedKey) : next === 'editor' ? 'library' : next;
  history.replaceState(null, '', '#' + hash);
  window.scrollTo(0, 0);
  if (focus) (views[next].querySelector('h1, input[type=search]') ?? document.getElementById('workspace')).focus({ preventScroll: true });
}

async function navigate(next, { focusSearch = false, catalog = false, section } = {}) {
  if (!await canLeave()) return false;
  if (next !== route) activate(next);
  if (next === 'library') {
    if (focusSearch) library.focusSearch();
    else library.restore();
  }
  if (next === 'settings') settings.show(section);
  if (next === 'explore') explore.show(catalog);
  return true;
}

async function read(key, preloaded) {
  if (!await canLeave()) return false;
  selectedKey = key;
  detail = undefined;
  reader.loading();
  activate('reader');
  if (preloaded) {
    updateDetail(preloaded);
    views.reader.querySelector('h1')?.focus({ preventScroll: true });
  } else await loadDetail(true);
  return true;
}

async function loadDetail(focus = false) {
  if (route !== 'reader' || !selectedKey) return;
  const job = detailJob.begin();
  try {
    const data = await api.detail(selectedKey, job.signal);
    if (!job.current() || route !== 'reader') return;
    detail = data;
    reader.render(data, snapshot, library.query);
    if (focus) views.reader.querySelector('h1')?.focus({ preventScroll: true });
  } catch (error) {
    if (job.current()) {
      if (detail && error.status !== 404) notify(error.message);
      else reader.failure(error.message);
    }
  }
}

function updateDetail(data) {
  if (route !== 'reader' || skillKey(data.skill) !== selectedKey && data.skill.name !== selectedKey) return;
  if ((detail?.skill?.revision ?? 0) > (data.skill.revision ?? 0)) return;
  detailJob.cancel();
  detail = data;
  reader.render(data, snapshot, library.query);
  refresh();
}

async function openEditor(data, clone) {
  if (!await canLeave()) return;
  editor.start(data, snapshot, clone);
  activate('editor');
}

async function refresh() {
  const job = snapshotJob.begin();
  try {
    const [skills, tags, providers] = await Promise.all([api.skills('', job.signal), api.tags(job.signal), api.providers(job.signal)]);
    if (!job.current()) return;
    snapshot = { skills, tags, providers };
    loaded = true;
    renderProviderButton();
    library.update(snapshot);
    if (route === 'reader') await loadDetail();
    if (route === 'settings') settings.refreshDiagnostics();
  } catch (error) {
    if (job.current()) {
      if (!loaded) library.error(error.message);
      else notify('Could not refresh: ' + error.message);
    }
  }
}

function scheduleRefresh() {
  if (refreshTimer) return;
  refreshTimer = setTimeout(() => {
    refreshTimer = undefined;
    refresh();
  }, 160);
}
function renderProviderButton() {
  const providers = installedProviders(snapshot.providers);
  const count = providers.length;
  const label = loaded ? count + (count === 1 ? ' AI Provider Connected' : ' AI Providers Connected') : 'Checking providers';
  const tooltip = label + (count ? ': ' + providers.map(id => providerNames[id]).join(', ') : '') +
    (online ? '' : ' · Reconnecting to SkillHub');
  providerButton.disabled = !loaded;
  providerButton.classList.toggle('offline', !online);
  providerButton.classList.toggle('no-providers', !count);
  providerButton.setAttribute('aria-label', tooltip);
  providerButton.querySelector('.provider-overview-icons').replaceChildren(...providers.map(icon));
  providerButton.querySelector('.provider-overview-count').textContent = loaded ? count : '–';
  providerButton.title = tooltip;
}
function connection(connected) {
  online = connected;
  status.textContent = connected ? 'Connected to local SkillHub' : 'Reconnecting to SkillHub';
  renderProviderButton();
}

providerButton.addEventListener('click', () => {
  const installed = installedProviders(snapshot.providers);
  const skills = snapshot.skills.filter(skill => skill.source_category !== 'system')
    .map(skill => deployment(skill, installed));
  const rows = Object.entries(PROVIDERS).map(([id, label]) => {
    const provider = snapshot.providers.find(item => item.provider === id);
    const detected = installed.includes(id);
    const synced = skills.filter(skill => skill.synced.includes(id)).length;
    const issues = skills.filter(skill => skill.issues.includes(id)).length;
    return h('li', { class: 'provider-overview-row' }, h('div', { class: 'provider-overview-name' }, icon(id), h('strong', {}, label)),
      h('p', { class: detected ? 'ok' : 'muted' }, detected ? 'Detected · ' + synced + (synced === 1 ? ' skill synced' : ' skills synced') : 'Not detected',
        issues > 0 && h('span', { class: 'warning' }, ' · ' + issues + ' need attention')),
      detected && provider.skills_root && h('code', { class: 'path' }, provider.skills_root));
  });
  const dialog = modal('AI providers', [
    !online && h('p', { class: 'warning' }, 'Reconnecting to SkillHub. Showing last detected providers.'),
    h('ul', { class: 'provider-overview-list' }, rows)
  ], button('Connection settings', () => {
    dialog.addEventListener('close', app.showConnections, { once: true });
    dialog.close();
  }, 'primary'));
});

function startEvents() {
  const events = new EventSource('/api/v1/events');
  events.addEventListener('connected', () => { connection(true); scheduleRefresh(); });
  events.addEventListener('activity', event => {
    const sequence = Number(event.lastEventId);
    if (lastEvent && sequence !== lastEvent + 1) scheduleRefresh();
    lastEvent = sequence;
    scheduleRefresh();
  });
  events.addEventListener('reset', () => { lastEvent = 0; scheduleRefresh(); });
  events.addEventListener('error', () => connection(false));
  window.addEventListener('pagehide', () => events.close(), { once: true });
}

function applyTheme(value) {
  theme = ['system', 'dark', 'light'].includes(value) ? value : 'system';
  const effective = theme === 'system' ? systemTheme.matches ? 'dark' : 'light' : theme;
  document.documentElement.dataset.theme = effective;
  const switchLabel = effective === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';
  themeButton.replaceChildren(icon(effective === 'dark' ? 'sun' : 'moon'));
  themeButton.setAttribute('aria-label', switchLabel);
  themeButton.title = switchLabel;
  settings.syncTheme(theme);
  window.chrome?.webview?.postMessage(JSON.stringify({ type: 'theme', theme: effective }));
}
systemTheme.addEventListener('change', () => { if (theme === 'system') applyTheme(theme); });

nav.addEventListener('click', event => {
  const destination = event.target.closest('button')?.dataset.route;
  if (destination) navigate(destination);
});
document.getElementById('brand-home').addEventListener('click', () => navigate('library'));
document.addEventListener('keydown', event => {
  if (event.defaultPrevented || document.querySelector('dialog[open]')) return;
  const key = event.key.toLowerCase();
  const editable = event.target.closest?.('input, textarea, select, [contenteditable="true"]');
  if ((event.ctrlKey || event.metaKey) && key === 'k' || key === '/' && !editable && !event.ctrlKey && !event.metaKey) {
    event.preventDefault();
    navigate('library', { focusSearch: true });
  } else if ((event.ctrlKey || event.metaKey) && key === 's' && route === 'editor') {
    event.preventDefault();
    editor.save();
  }
});
window.addEventListener('beforeunload', event => {
  if (route === 'editor' && editor.isDirty) event.preventDefault();
});

async function migrateTags() {
  let previous;
  try { previous = JSON.parse(localStorage.getItem('skillhub_custom_tags') ?? '[]'); } catch { return; }
  if (!Array.isArray(previous) || !previous.length) return;
  const valid = [];
  for (const tag of previous) {
    try { valid.push(tagSlug(tag)); } catch { /* Invalid legacy tags were never accepted by the daemon. */ }
  }
  try {
    for (const tag of new Set(valid)) await api.createTag(tag);
    localStorage.removeItem('skillhub_custom_tags');
  } catch { /* Leave the old copy available for retry on next startup. */ }
}
let themeWrites = 0;
let confirmedTheme = theme;
let themeQueue = Promise.resolve();
function setTheme(next) {
  const id = ++themeWrites;
  applyTheme(next);
  const operation = themeQueue.then(() => api.theme(next)).then(result => {
    confirmedTheme = result.theme;
    if (id === themeWrites) applyTheme(confirmedTheme);
  }).catch(error => {
    if (id === themeWrites) applyTheme(confirmedTheme);
    throw error;
  });
  themeQueue = operation.catch(() => {});
  return operation;
}
themeButton.addEventListener('click', async () => {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  themeButton.disabled = true;
  try { await setTheme(next); }
  catch (error) { notify(error.message); }
  finally { themeButton.disabled = false; }
});
applyTheme('system');
api.preferences().then(data => { if (!themeWrites) applyTheme(confirmedTheme = data.theme); }).catch(() => {}).finally(() => { themeButton.disabled = false; });
migrateTags().finally(() => {
  refresh();
  startEvents();
});
const initialRoute = location.hash.slice(1);
if (initialRoute === 'explore' || initialRoute === 'settings') navigate(initialRoute);
if (initialRoute.startsWith('skill/')) {
  try { read(decodeURIComponent(initialRoute.slice(6))); } catch { navigate('library'); }
}
