import { api } from '../api.js';
import { deployment, installedProviders, destinationLabel, targetIssues, PROVIDERS, skillKey, tagSlug, isTargetEnabled } from '../state.js';
import { renderMarkdown, outline } from '../markdown.js';
import { h, button, heading, panel, disclosure, copy, download, busy, notify, modal, confirmAction, field, errorText, bundleBadge } from '../ui.js';
import { showProviderPreview } from './transpilerPreview.js';
import { copySkillPrompt, doctorPanel, historySection, updateBanner, proposalsBanner } from './skillInsights.js';
import { icon } from '../icons.js';
import { exportPack } from './skillPacks.js';
import { appearanceFor, skillBadge, showIconPicker } from '../skillBadge.js';

export function createReader(root, app) {
  let modelKey;
  let lastRender;
  let activeSection = 'Instructions';
  let narrowDetailsOpen = false;
  const narrow = matchMedia('(max-width: 600px)');
  narrow.addEventListener('change', () => {
    const details = root.querySelector('.reader-inspector');
    if (details) details.open = !narrow.matches || narrowDetailsOpen;
  });
  const openDisclosures = new Set();
  root.classList.add('reading');

  function loading() {
    lastRender = undefined;
    root.replaceChildren(heading('Skill', button('Back to Library', () => app.navigate('library'))), h('p', { role: 'status' }, 'Loading skill…'));
  }

  function render(detail, snapshot, query = '') {
    const skill = detail.skill;
    const id = skillKey(skill);
    const identity = JSON.stringify([id, skill.revision, skill.title, skill.description, skill.when_to_use,
      skill.tags, skill.targets, skill.destinations, skill.is_catalog_only, query, snapshot.providers,
      skill.is_catalog_only ? skill.instructions_markdown : undefined, app.updates?.get(skill.name)?.latest_revision,
      appearanceFor(skill, snapshot.appearance)]);
    if (identity === lastRender) return;
    const sameSkill = modelKey === id;
    const scroll = window.scrollY;
    const focused = root.contains(document.activeElement) ? document.activeElement.dataset.focusKey : null;
    if (!sameSkill) { openDisclosures.clear(); activeSection = 'Instructions'; narrowDetailsOpen = false; }
    modelKey = id;
    lastRender = identity;
    const readOnly = skill.is_catalog_only || skill.source_category === 'system';
    const changes = response => app.updateDetail(response);
    const sync = deployment(skill, installedProviders(snapshot.providers));
    const files = skill.supporting_files ?? {};
    const openFile = (path) => {
      if (!Object.hasOwn(files, path)) { notify('This file is outside the stored skill bundle.'); return; }
      modal(path, h('pre', { class: 'file-code', tabindex: '0' }, files[path]), button('Copy file', () => copy(files[path])));
    };
    const primary = skill.source_category === 'system' ? null
      : skill.is_catalog_only ? button('Clone to My Skills', () => app.edit(detail, { clone: true }), 'primary')
      : button('Edit skill', () => app.edit(detail), 'primary');
    const actions = button('Actions', () => showActions(detail, snapshot, readOnly), '', { 'data-focus-key': 'actions' });
    const copyPrompt = button([icon('copy'), 'Copy skill prompt'], event => busy(event.currentTarget, () => copySkillPrompt(app, skillKey(skill))), '',
      { 'data-focus-key': 'copy-prompt', title: 'Copy the instructions as a prompt for any chat' });
    const title = heading(skill.title || skill.name, h('div', { class: 'actions' }, primary, copyPrompt, actions),
      h('p', { class: 'result-count' }, skill.origin_provider || 'Personal', ' · ',
        h('span', { class: 'muted' }, sync.key === 'issue' ? 'Managed skill' : sync.label), ' · ',
        bundleBadge(Object.keys(files).length, { paths: Object.keys(files).sort(), verbose: true })));
    // The badge doubles as the "change icon" control; the choice lives in SkillHub only, never in the skill files.
    title.classList.add('with-badge');
    title.prepend(h('button', { type: 'button', class: 'skill-badge-button', 'data-focus-key': 'skill-icon',
      title: 'Change icon', 'aria-label': 'Change icon for ' + (skill.title || skill.name), onclick: () => showIconPicker(app, skill) },
      skillBadge(skill, appearanceFor(skill, snapshot.appearance), { tint: sync.synced[0] ?? (skill.is_catalog_only ? 'catalog' : skill.source_category), className: 'large' }),
      h('span', { class: 'skill-badge-edit', 'aria-hidden': 'true' }, icon('edit'))));

    const tags = h('div', { class: 'chips', 'aria-label': 'Skill tags' },
      ...(skill.tags ?? []).map(tag => readOnly ? h('span', { class: 'chip' }, '#' + tag)
        : h('button', { class: 'chip', type: 'button', 'data-focus-key': 'tag-' + tag, 'aria-label': 'Remove tag ' + tag,
          onclick: event => busy(event.currentTarget, async () => changes(await api.skillTag(skill.name, tag, false))) }, '#' + tag, ' ×')),
      !readOnly && button('Add tag', () => addTag(skill, snapshot, changes), 'small', { 'data-focus-key': 'add-tag' }));

    const issues = targetIssues(skill, installedProviders(snapshot.providers));
    const content = h('div', { class: 'stack reader-section' });
    const tabs = h('nav', { class: 'reader-tabs', 'aria-label': 'Skill sections' });
    const article = h('article', { class: 'markdown reader-document', 'aria-label': 'Skill instructions' },
      renderMarkdown(skill.instructions_markdown, { title: skill.title, query, openFile }));
    const headings = outline(article);
    // Bundles show every file right above the instructions so nothing looks missing.
    let activeFile = '';
    const filePaths = Object.keys(files).sort();
    const fileView = h('div', { class: 'reader-file-view' });
    const switcher = filePaths.length > 0 && h('div', { class: 'file-tabs reader-file-tabs', role: 'tablist', 'aria-label': 'Files in this skill' },
      [['', 'SKILL.md'], ...filePaths.map(path => [path, path])].map(([path, label]) => h('button', {
        type: 'button', role: 'tab', class: 'file-tab', 'data-file': path, 'aria-selected': String(path === activeFile), title: label,
        onclick: () => { activeFile = path; showFileView(); } }, label)));
    function showFileView() {
      for (const tab of switcher?.querySelectorAll('.file-tab') ?? []) tab.setAttribute('aria-selected', String(tab.dataset.file === activeFile));
      fileView.replaceChildren(activeFile ? h('div', { class: 'stack' }, h('div', { class: 'spread' }, h('p', { class: 'path' }, activeFile),
        button([icon('copy'), 'Copy file'], () => copy(files[activeFile]), 'quiet small')),
        h('pre', { class: 'file-code', tabindex: '0' }, files[activeFile])) : article);
    }
    const sections = {
      Instructions: () => { if (!switcher) return article; showFileView(); return h('div', { class: 'stack' }, switcher, fileView); },
      Files: () => Object.keys(files).length ? h('div', { class: 'stack' },
        Object.keys(files).sort().map(path => button(path, () => openFile(path), 'quiet')))
        : h('p', { class: 'muted' }, 'This skill has no supporting files.'),
      Destinations: () => destinations(skill, snapshot, changes, readOnly),
      ...(skill.is_catalog_only ? {} : { History: () => historySection(app, detail) })
    };
    function showSection() {
      for (const tab of tabs.children) tab.setAttribute('aria-pressed', String(tab.dataset.section === activeSection));
      content.replaceChildren(sections[activeSection]());
    }
    tabs.append(...Object.keys(sections).map(name => button(name, event => {
      activeSection = name;
      showSection();
      if (tabs.getBoundingClientRect().top <= 61) tabs.scrollIntoView({ block: 'start' });
      event.currentTarget.focus({ preventScroll: true });
    }, 'quiet small', { 'data-section': name, 'data-focus-key': 'section-' + name })));
    showSection();
    const source = disclosure('Source details', h('p', { class: 'path' }, skill.source_path ?? skill.name),
      skill.revision && h('p', { class: 'muted' }, 'Canonical revision ' + skill.revision));
    const contents = headings.length > 1 && h('nav', { class: 'outline', 'aria-label': 'On this page' },
      h('h3', {}, 'On this page'),
      h('ul', {}, headings.map(item => h('li', { class: 'outline-level-' + item.level },
        h('button', { type: 'button', class: 'outline-link', onclick: () => {
          if (activeSection !== 'Instructions') { activeSection = 'Instructions'; showSection(); }
          if (activeFile) { activeFile = ''; showFileView(); }
          const target = document.getElementById(item.id);
          target?.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' });
          target?.setAttribute('tabindex', '-1');
          target?.focus({ preventScroll: true });
        } }, item.text)))));
    const inspector = h('details', { class: 'reader-inspector' }, h('summary', {}, 'Skill details'),
      h('div', { class: 'stack' }, tags, !skill.is_catalog_only && doctorPanel(skill), contents, source));
    inspector.open = !narrow.matches || narrowDetailsOpen;
    inspector.addEventListener('toggle', () => { if (narrow.matches) narrowDetailsOpen = inspector.open; });
    const section = h('div', { class: 'stack' }, h('div', { class: 'actions' }, button('← Library', () => app.navigate('library'), 'quiet')),
      title,
      updateBanner(app, skill),
      proposalsBanner(app, skill),
      issues.length > 0 && h('div', { class: 'issue-banner stack' }, issues.map(issue => h('div', { class: 'spread' },
        h('p', {}, issue.label + (issue.state === 'missing' ? ' copy is missing.' : ' needs review.')),
        button('Review ' + issue.label, () => app.review(skill.name, issue), 'small', { 'data-focus-key': 'review-' + issue.path })))),
      h('div', { class: 'summary' },
        skill.description && h('p', {}, skill.description),
        skill.when_to_use && h('p', {}, h('strong', {}, 'When to use '), skill.when_to_use)),
      tabs, h('div', { class: 'reader-grid' }, content, inspector));
    for (const details of section.querySelectorAll('details')) {
      if (details === inspector) continue;
      const label = details.firstElementChild.textContent;
      details.open = openDisclosures.has(label);
      details.addEventListener('toggle', () => details.open ? openDisclosures.add(label) : openDisclosures.delete(label));
    }
    root.replaceChildren(section);
    if (sameSkill && !root.hidden) {
      window.scrollTo(0, scroll);
      if (focused) root.querySelector('[data-focus-key="' + CSS.escape(focused) + '"]')?.focus({ preventScroll: true });
    }
  }

  function showActions(detail, snapshot, readOnly) {
    const skill = detail.skill;
    const key = skillKey(skill);
    const content = h('div', { class: 'stack' });
    const dialog = modal('Skill actions', content);
    content.append(button('Preview provider output', () => {
      dialog.close(); showProviderPreview(skill, { saved: detail, providers: snapshot.providers });
    }), button('Export Markdown', () => download(skill.name + '.SKILL.md', detail.transpiled_previews?.canonical ?? skill.instructions_markdown, 'text/markdown')),
    button([icon('download'), 'Export Skill Pack (.skillpack.zip)'], event => busy(event.currentTarget, () => exportPack([skill.name])),
      '', { title: 'SKILL.md, every companion file and a manifest, ready to import on any PC' }),
    button('Download skill folder (.zip)', event => busy(event.currentTarget, async () => {
      const response = await fetch('/api/v1/skills/' + encodeURIComponent(key) + '/bundle');
      if (!response.ok) throw new Error('Could not export this skill bundle.');
      download(skill.name + '.zip', await response.blob(), 'application/zip');
    })));
    if (skill.is_catalog_only && skill.source_category !== 'system') {
      const activate = button('Enable sync', async () => {
        if (!(await confirmAction('Enable template sync', 'Create a managed copy and deploy it to its enabled providers?', 'Enable sync', false))) return;
        busy(activate, async () => {
          const response = await api.activate(skill.catalog_id);
          dialog.close();
          await app.read(response.skill.name, response);
          notify('Template activated.');
        });
      });
      content.append(activate);
    }
    if (!readOnly) {
      const isTemplate = skill.source_category === 'template' || skill.source_category === 'plugin';
      if (isTemplate && skill.catalog_id) content.append(button('Customize a copy', () => { dialog.close(); app.edit(detail, { clone: true }); }));
      const label = isTemplate ? 'Stop syncing template' : 'Move to trash';
      const remove = button(label, async () => {
        if (!(await confirmAction(label, 'Move the managed copy to trash and withdraw its owned provider files?', label))) return;
        busy(remove, async () => {
          await api.remove(skill.name);
          dialog.close();
          await app.navigate('library');
          await app.refresh();
          notify('Skill moved to trash. Restore it in Diagnostics.');
        });
      }, 'danger');
      content.append(remove);
    }
  }

  return { loading, render, failure: message => root.replaceChildren(heading('Skill unavailable', button('Back to Library', () => app.navigate('library'))), errorText(message)) };
}

function destinations(skill, snapshot, change, readOnly) {
  if (skill.is_catalog_only) return h('p', { class: 'muted' }, 'Read-only catalog entry.');
  const rows = Object.entries(PROVIDERS).map(([key, label]) => {
    const targets = (skill.destinations ?? []).filter(item => item.provider === key);
    const enabled = isTargetEnabled(skill, key);
    const provider = snapshot.providers.find(item => item.provider === key);
    const actions = [];
    if (!readOnly && provider?.is_installed) {
      const toggle = button(enabled ? 'Disable' : 'Enable', async () => {
        if (enabled && !(await confirmAction('Disable ' + label, 'Withdraw this skill’s owned files from ' + label + '?', 'Disable'))) return;
        busy(toggle, async () => change(await api.provider(skill.name, key, !enabled)));
      }, 'small', { 'aria-label': (enabled ? 'Disable ' : 'Enable ') + label, 'data-focus-key': 'toggle-' + key });
      actions.push(toggle);
      if (enabled && targets.some(item => item.state === 'missing')) {
        const repair = button('Repair', () => busy(repair, async () => change(await api.repair(skill.name, key))), 'small',
          { 'aria-label': 'Repair ' + label, 'data-focus-key': 'repair-' + key });
        actions.push(repair);
      }
    }
    return h('div', { class: 'target-row' }, h('div', {}, h('strong', {}, label),
      provider?.is_installed && targets.length ? targets.map(target => h('div', {},
        h('p', { class: ['missing', 'conflict', 'error'].includes(target.state) ? 'warning' : 'muted' },
          target.state === 'synced' && target.applied_revision !== skill.revision ? 'Pending current revision' : destinationLabel(target.state)),
        h('code', { class: 'path' }, target.path), target.error && h('p', { class: 'error' }, target.error)))
        : h('p', { class: 'muted' }, provider?.is_installed ? enabled ? 'Awaiting deployment' : 'Disabled' : 'Client not detected')),
    h('div', { class: 'actions' }, actions));
  });
  return h('div', { class: 'stack' }, rows);
}

function addTag(skill, snapshot, change) {
  const input = h('input', { class: 'input', list: 'reader-tag-suggestions', autofocus: true });
  const suggestions = h('datalist', { id: 'reader-tag-suggestions' }, (snapshot.tags ?? []).map(tag => h('option', { value: tag.tag })));
  const outcome = h('div', { role: 'status' });
  const add = button('Add tag', () => busy(add, async () => {
    change(await api.skillTag(skill.name, tagSlug(input.value), true));
    dialog.close();
  }, error => outcome.replaceChildren(errorText(error.message))), 'primary');
  const dialog = modal('Add tag', [field('Tag', input), suggestions, outcome], add);
}
