import { api } from './api.js?v=20261112-v9';
import { Latest, skillKey, tagSlug } from './state.js?v=20261112-v9';
import { confirmAction, notify } from './ui.js?v=20261112-v9';
import { createLibrary } from './components/skillList.js?v=20261112-v9';
import { createReader } from './components/skillReader.js?v=20261112-v9';
import { createEditor } from './components/skillEditor.js?v=20261112-v9';
import { createExplore } from './components/explore.js?v=20261112-v9';
import { createSettings } from './components/statusBar.js?v=20261112-v9';

const views = Object.fromEntries(['library', 'reader', 'editor', 'explore', 'settings']
  .map(name => [name, document.getElementById(name + '-screen')]));
const nav = document.getElementById('primary-nav');
const status = document.getElementById('connection');
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
  read,
  edit: (data, { clone = false } = {}) => openEditor(data, clone),
  newSkill: () => openEditor(),
  cancelEditor: key => key ? read(key) : navigate('library'),
  updateDetail,
  applyTheme,
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
  if (focus) (views[next].querySelector('h1') ?? document.getElementById('workspace')).focus({ preventScroll: true });
}

async function navigate(next, { focusSearch = false, catalog = false } = {}) {
  if (!await canLeave()) return false;
  if (next !== route) activate(next);
  if (next === 'library') {
    if (focusSearch) library.focusSearch();
    else library.restore();
  }
  if (next === 'settings') settings.show();
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
    library.update(snapshot);
    if (route === 'reader') await loadDetail();
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
function connection(online) {
  status.classList.toggle('online', online);
  status.lastElementChild.textContent = online ? 'Local' : 'Reconnecting';
  status.title = online ? 'Connected to the local SkillHub daemon' : 'Reconnecting to SkillHub';
}
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
applyTheme('system');
api.preferences().then(data => applyTheme(data.theme)).catch(() => {});
migrateTags().finally(() => {
  refresh();
  startEvents();
});
const initialRoute = location.hash.slice(1);
if (initialRoute === 'explore' || initialRoute === 'settings') navigate(initialRoute);
if (initialRoute.startsWith('skill/')) {
  try { read(decodeURIComponent(initialRoute.slice(6))); } catch { navigate('library'); }
}
