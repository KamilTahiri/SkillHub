import { api } from '../api.js';
import { reviewPack } from './skillPacks.js';
import { Latest, PROVIDERS, slugify } from '../state.js';
import { renderMarkdown } from '../markdown.js';
import { icon, PROVIDER_NAMES } from '../icons.js';
import { h, button, field, select, panel, modal, errorText, busy, notify, disclosure, confirmAction, fill, bundleBadge, autoLoadMore, responsivePlaceholder } from '../ui.js';

const TOPICS = [['', 'All topics'], ['coding', 'Coding'], ['web', 'Web and design'], ['data', 'Data'],
  ['devops', 'Infrastructure'], ['productivity', 'Productivity'], ['testing', 'Testing and security']];
const GROUPS = [['skills', 'Skills'], ['commands', 'Commands'], ['rules', 'Rules'], ['agents', 'Agents'], ['docs', 'Docs']];
const KIND_LABELS = { skill: 'Skill', command: 'Command', prompt: 'Prompt', rule: 'Rule', instructions: 'Instructions',
  'project-rules': 'Project rules', agent: 'Agent', chatmode: 'Chat mode', doc: 'Doc' };

const compactNumber = value => new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
// Trust context for the user and the AI: "★ 18.4k stars · 420k installs".
const trustLine = (stars, installs) => [stars != null && '★ ' + compactNumber(stars) + (stars === 1 ? ' star' : ' stars'),
  installs > 0 && compactNumber(installs) + ' installs'].filter(Boolean).join(' · ');
// Imports sync to these detected AI tools unless the user unticks them (Tier 1, live-verified).
const DEFAULT_SYNC = new Set(['claude', 'gemini', 'codex']);

/** Markdown files render with their YAML frontmatter folded into a small block; other files show as code. */
function renderFile(path, text, openFile) {
  if (!/\.(md|mdc|markdown)$/i.test(path)) return [h('pre', { class: 'file-code', tabindex: '0' }, text)];
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(text);
  const body = match ? text.slice(match[0].length) : text;
  return [match && h('details', { class: 'frontmatter' }, h('summary', {}, 'Frontmatter'), h('pre', {}, match[1])),
    h('article', { class: 'markdown' }, renderMarkdown(body, { openFile }))];
}

export function createExplore(root, app) {
  const listJob = new Latest();
  const previewJob = new Latest();
  const previewCache = new Map();
  const previewRequests = new Map();
  const narrow = matchMedia('(width < 768px)');
  const cacheKey = ({ repo, path, git_ref }) => JSON.stringify([repo.toLowerCase(), path, git_ref]);
  let prefetchQueue = [];
  let prefetchWorkers = 0;
  let cacheSize = 0;
  let source = 'featured/all';
  let sources = [];
  let topic = '';
  let group = '';
  let timer;
  let items = [];
  let repository = null;
  let pendingSources = [];
  let pendingTimer;
  const resultRows = new WeakMap();
  const chosen = new Map();
  let selected;
  let lastTrigger;
  let listScroll = 0;
  let isApplying = false;
  let initialized = false;
  let visibleCount = 50;
  const sourceChooser = select([['featured/all', 'Featured'], ['local/catalog', 'Local Plugin Catalog']], source, {
    onchange: event => { source = event.target.value; group = ''; search.value = ''; load(); }
  });
  const topicChooser = select(TOPICS, '', { onchange: event => {
    topic = event.target.value;
    resetPreview();
    showList();
  } });
  const search = h('input', { class: 'input', type: 'search', placeholder: 'Find skills, or paste owner/repo or a GitHub link',
    'aria-label': 'Search Explore',
    oninput: () => {
      clearTimeout(timer);
      listJob.cancel();
      prefetchQueue = [];
      resetPreview();
      // Keep the previous results visible but dimmed until the new ones arrive.
      results.classList.add('is-stale');
      results.setAttribute('aria-busy', 'true');
      timer = setTimeout(load, 200);
    } });
  responsivePlaceholder(search, 'Find skills, or paste owner/repo or a GitHub link', 'Search or paste a repo…');
  const results = h('div', { class: 'explore-results', tabindex: '0', role: 'region', 'aria-label': 'Explore results' });
  const batchBar = h('div', { class: 'batch-bar', hidden: true, role: 'region', 'aria-label': 'Selected skills' });
  const resultsColumn = h('div', { class: 'explore-results-column' }, results, batchBar);
  const preview = h('div', { id: 'explore-preview' });
  const layout = h('div', { class: 'explore-layout' }, resultsColumn,
    h('div', { class: 'explore-detail' },
      button('← Back to results', () => resetPreview(true), 'quiet small explore-back'), preview));
  const toolbar = h('div', { class: 'explore-toolbar', role: 'search', 'aria-label': 'Explore skills' },
    h('div', { class: 'search-field' }, search), field('Source', sourceChooser), field('Topic', topicChooser),
    button([icon('plus'), 'Import from link or file'], directImport));
  root.dataset.detail = 'false';
  root.append(toolbar, layout);

  function installedTargets() {
    return Object.keys(PROVIDERS).filter(id => app.snapshot.providers.some(provider => provider.provider === id && provider.is_installed));
  }

  function deployChips(onchange) {
    const targets = installedTargets().map(id => ({ id, label: PROVIDER_NAMES[id],
      control: h('input', { type: 'checkbox', class: 'deploy-chip-input', checked: DEFAULT_SYNC.has(id), onchange }) }));
    const node = h('fieldset', { class: 'deploy-targets' }, h('legend', {}, 'Sync to'),
      targets.length > 0
        ? h('div', { class: 'deploy-chips' }, targets.map(target => h('label', { class: 'deploy-chip', 'data-provider': target.id },
          target.control, icon(target.id), h('span', {}, target.label))))
        : h('p', { class: 'muted' }, 'No AI tools detected. Skills are saved to your library only.'));
    return { node, selected: () => targets.filter(target => target.control.checked).map(target => target.id) };
  }

  function cachedPreview(selection) {
    const key = cacheKey(selection);
    const cached = previewCache.get(key);
    if (!cached) return undefined;
    previewCache.delete(key);
    if (cached.expiresAt <= Date.now()) {
      cacheSize -= cached.size;
      return undefined;
    }
    previewCache.set(key, cached);
    return cached.value;
  }

  const mainFile = path => (path ?? '').split('/').at(-1) || 'SKILL.md';
  // Single file or bundle, said plainly on every row. Listings that do not know the folder yet (skills.sh) say so until the preview loads.
  const rowBadge = skill => bundleBadge(skill.file_count, { known: skill.files_known !== false,
    main: /\.(md|mdc|txt)$/i.test(skill.path ?? '') ? mainFile(skill.path) : 'SKILL.md' });
  const previewBadge = (skill, value) => {
    const paths = value.supporting_paths ?? Object.keys(value.supporting_files ?? {});
    return bundleBadge(paths.length, { paths, main: mainFile(value.path ?? skill.path) });
  };

  function fetchPreview(selection) {
    const cached = cachedPreview(selection);
    if (cached) return Promise.resolve(cached);
    const key = cacheKey(selection);
    if (previewRequests.has(key)) return previewRequests.get(key);
    const pending = api.communityPreview(selection).then(value => {
      if (items.includes(selection)) {
        const row = resultRows.get(selection);
        row?.querySelector('.result-title').replaceChildren(value.title);
        if (value.description) row?.querySelector('.result-description').replaceChildren(value.description);
        row?.querySelector('.files-badge')?.replaceWith(previewBadge(selection, value));
      }
      const size = (value.instructions_markdown?.length ?? 0) * 2;
      const budget = 8 * 1024 * 1024;
      while (previewCache.size && (previewCache.size >= 48 || cacheSize + size > budget)) {
        const oldest = previewCache.keys().next().value;
        cacheSize -= previewCache.get(oldest).size;
        previewCache.delete(oldest);
      }
      if (size <= budget) {
        previewCache.set(key, { value, size, expiresAt: Date.now() + 3 * 60_000 });
        cacheSize += size;
      }
      return value;
    }).finally(() => previewRequests.delete(key));
    previewRequests.set(key, pending);
    return pending;
  }

  function prefetch(skills) {
    // skills.sh rows live in many different repositories; previewing one downloads its whole repository, so only on click.
    prefetchQueue = skills.filter(skill => skill.source !== 'skills.sh').slice(0, 12);
    while (prefetchWorkers < 3 && prefetchQueue.length) {
      prefetchWorkers++;
      warmPreviews();
    }
  }

  async function warmPreviews() {
    try {
      while (prefetchQueue.length) {
        const skill = prefetchQueue.shift();
        try { await fetchPreview(skill); }
        catch { /* The foreground preview can retry. */ }
      }
    } finally { prefetchWorkers--; }
  }

  function renderSources(list) {
    sources = list;
    const curated = list.filter(repo => !repo.pinned).map(repo => h('option', { value: repo.repo }, repo.title));
    const pinned = list.filter(repo => repo.pinned).map(repo => h('option', { value: repo.repo }, repo.title));
    fill(sourceChooser, ...curated, pinned.length ? h('optgroup', { label: 'Pinned' }, pinned) : null);
    if (![...sourceChooser.options].some(option => option.value === source)) {
      sourceChooser.append(h('option', { value: source }, source));
    }
    sourceChooser.value = source;
  }

  async function initialize() {
    if (initialized) return;
    initialized = true;
    try {
      renderSources((await api.repositories()).repositories);
    } catch {
      // Built-in source choices are available while repository metadata is unreachable.
    }
  }

  async function load() {
    if (isApplying) return;
    clearTimeout(timer);
    const job = listJob.begin();
    prefetchQueue = [];
    visibleCount = 50;
    resetPreview();
    if (!items.length) results.replaceChildren(h('p', { class: 'explore-loading', role: 'status' }, 'Loading skills…'));
    results.classList.add('is-stale');
    results.setAttribute('aria-busy', 'true');
    try {
      const response = await api.community(source, search.value.trim(), job.signal);
      if (!job.current()) return;
      const nextRepo = response.repository?.repo ?? null;
      const changedRepo = nextRepo !== repository?.repo;
      items = response.skills ?? [];
      if (changedRepo) {
        chosen.clear();
        // A repository opens on its actual skills; README files, AGENTS.md and docs stay one tab away.
        group = nextRepo && items.some(skill => skill.group === 'skills') && items.some(skill => skill.group !== 'skills') ? 'skills' : '';
      }
      repository = response.repository ?? null;
      pendingSources = response.pending_sources ?? [];
      clearTimeout(pendingTimer);
      // Sources that were still being indexed are searched again once their first download has had time to finish.
      if (pendingSources.length && search.value.trim()) pendingTimer = setTimeout(() => { if (!isApplying && root.isConnected && !root.hidden) load(); }, 12000);
      results.scrollTop = 0;
      showList();
    } catch (error) {
      if (job.current()) {
        items = [];
        repository = null;
        results.replaceChildren(panel(errorText(error.message), button('Retry', load)));
      }
    } finally {
      if (job.current()) {
        results.classList.remove('is-stale');
        results.removeAttribute('aria-busy');
      }
    }
  }

  function repositoryCard() {
    if (!repository) return null;
    const [owner, name] = repository.repo.split('/');
    const userPinned = sources.some(item => item.pinned && item.repo.toLowerCase() === repository.repo.toLowerCase());
    const curated = sources.some(item => !item.pinned && item.repo.toLowerCase() === repository.repo.toLowerCase());
    const pin = curated ? null : button([icon(userPinned ? 'unpin' : 'pin'), userPinned ? 'Unpin' : 'Pin source'], event => busy(event.currentTarget, async () => {
      const response = userPinned ? await api.unpinSource(repository.repo) : await api.pinSource(repository.repo);
      if (!userPinned) { source = repository.repo; search.value = ''; }
      else if (source.toLowerCase() === repository.repo.toLowerCase()) source = 'featured/all';
      renderSources(response.repositories);
      notify(userPinned ? 'Removed ' + repository.repo + ' from your sources.' : 'Pinned ' + repository.repo + ' to Explore sources.');
      if (userPinned) load(); else showList();
    }), 'small', { title: userPinned ? 'Remove from Explore sources' : 'Keep this repository in the Source list' });
    const meta = [repository.sha.slice(0, 7), repository.license_path ? 'License: ' + repository.license_path : 'No license file',
      repository.mode === 'tarball' ? 'Indexed offline copy' : 'Indexed via GitHub API'].join(' · ');
    const readme = repository.readme_excerpt && disclosure('README', h('div', { class: 'markdown readme-excerpt' },
      renderMarkdown(repository.readme_excerpt, {})));
    return h('section', { class: 'repo-card', 'aria-label': 'Repository' },
      h('div', { class: 'repo-card-head' },
        h('span', { class: 'repo-avatar', 'aria-hidden': 'true' }, owner[0]?.toUpperCase() ?? '?'),
        h('div', { class: 'repo-card-title' }, h('h2', {}, h('span', { class: 'muted' }, owner + '/'), name),
          h('p', { class: 'muted' }, meta)),
        pin),
      repository.truncated && h('p', { class: 'warning' }, 'This repository is very large. Only part of it was indexed.'),
      readme);
  }

  function groupTabs(filtered) {
    const counts = Object.fromEntries(GROUPS.map(([key]) => [key, filtered.filter(skill => (skill.group ?? 'skills') === key).length]));
    const present = GROUPS.filter(([key]) => counts[key] > 0);
    if (present.length < 2) return null;
    const tab = (key, label, count) => button([label, h('span', { class: 'group-count' }, count)], () => {
      group = key;
      visibleCount = 50;
      showList();
      results.querySelector('.group-tabs [aria-pressed="true"]')?.focus();
    }, 'small', { 'aria-pressed': String(group === key) });
    return h('nav', { class: 'group-tabs', 'aria-label': 'Document types' },
      tab('', 'All', filtered.length), present.map(([key, label]) => tab(key, label, counts[key])));
  }

  function showList() {
    const byTopic = items.filter(skill => !topic || skill.topic === topic);
    if (group && !byTopic.some(skill => (skill.group ?? 'skills') === group)) group = '';
    const filtered = byTopic.filter(skill => !group || (skill.group ?? 'skills') === group);
    prefetch(filtered);
    const card = repositoryCard();
    const tabs = groupTabs(byTopic);
    if (!filtered.length) {
      results.replaceChildren(...[card, tabs].filter(Boolean), h('div', { class: 'empty' }, h('h2', {}, 'No matching skills'),
        h('p', { class: 'muted' }, repository ? 'This repository has no Markdown skills or rules that match.' : 'Change the source, topic, or search term.')));
      renderBatch();
      return;
    }
    const resultRow = skill => {
        const key = cacheKey(skill);
        const row = h('button', { type: 'button', class: 'result-button', 'aria-current': String(selected === skill),
          onclick: () => {
            lastTrigger = row;
            listScroll = narrow.matches ? window.scrollY : results.scrollTop;
            openPreview(skill, skill);
          } }, h('span', { class: 'result-title' }, skill.title || skill.slug),
          h('span', { class: 'result-description' }, skill.installs > 0 ? 'Listed on skills.sh · ' + skill.repo
            : skill.description || (skill.has_frontmatter === false ? 'No description in this file.' : '')),
          h('span', { class: 'result-meta' },
            skill.kind && skill.kind !== 'skill' && h('span', { class: 'kind-badge', 'data-kind': skill.kind }, KIND_LABELS[skill.kind] ?? skill.kind),
            rowBadge(skill),
            skill.stars != null && h('span', { class: 'stars-badge', title: skill.stars.toLocaleString() + ' GitHub stars on ' + skill.repo }, '★ ' + compactNumber(skill.stars)),
            skill.installs > 0 && h('span', { class: 'installs-badge', title: skill.installs.toLocaleString() + ' installs on skills.sh' },
              icon('download'), compactNumber(skill.installs)),
            skill.is_installed && h('span', { class: 'installed-badge' }, icon('check'), 'In library'),
            h('span', { class: 'result-path' }, repository ? skill.path : skill.repo)));
        row.skill = skill;
        resultRows.set(skill, row);
        const cached = cachedPreview(skill);
        if (cached) {
          row.querySelector('.result-title').replaceChildren(cached.title);
          row.querySelector('.files-badge')?.replaceWith(previewBadge(skill, cached));
          if (cached.description) row.querySelector('.result-description').replaceChildren(cached.description);
        }
        const pick = !skill.is_installed && skill.repo !== 'local/catalog' && h('input', {
          type: 'checkbox', class: 'result-pick', checked: chosen.has(key), 'aria-label': 'Select ' + (skill.title || skill.slug),
          onchange: event => {
            if (event.target.checked) chosen.set(key, skill); else chosen.delete(key);
            renderBatch();
          } });
        return h('li', { class: 'result-row' + (pick ? ' pickable' : '') }, pick, row, skill.existing_name &&
          button('Open existing skill', () => app.read(skill.existing_name), 'quiet small result-open'));
      };
    // The count and kind tabs stay pinned while the list scrolls, so the active filter is always visible.
    const summary = h('div', { class: 'explore-summary' }, tabs,
      h('p', { class: 'muted explore-count', role: 'status' }, filtered.length + (filtered.length === 1 ? ' result' : ' results'),
        pendingSources.length > 0 && h('span', { class: 'pending-note' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }),
          'Still indexing ' + pendingSources.join(', ') + '. Results update automatically.')));
    const list = h('ul', { class: 'results' }, filtered.slice(0, visibleCount).map(resultRow));
    results.replaceChildren(...[card, summary, list].filter(Boolean));
    autoLoadMore(results, filtered.length, () => visibleCount, more => {
      const index = visibleCount;
      visibleCount += more;
      list.append(...filtered.slice(index, visibleCount).map(resultRow));
      return index;
    }, list, 'results');
    renderBatch();
  }

  function uniqueName(base, taken) {
    let candidate = slugify(base).slice(0, 64) || 'imported-skill';
    for (let suffix = 2; taken.has(candidate); suffix++) {
      const tail = '-' + suffix;
      candidate = slugify(base).slice(0, 64 - tail.length).replace(/-$/, '') + tail;
    }
    taken.add(candidate);
    return candidate;
  }

  function renderBatch() {
    batchBar.hidden = chosen.size === 0;
    if (batchBar.hidden) { batchBar.replaceChildren(); return; }
    const deploy = deployChips();
    const status = h('div', { class: 'batch-status', role: 'status' });
    const run = button([icon('download'), 'Import ' + chosen.size], () => busy(run, async () => {
      isApplying = true;
      const picked = [...chosen.values()];
      const taken = new Set(app.snapshot.skills.filter(skill => !skill.is_catalog_only).map(skill => skill.name));
      const done = [];
      const failed = [];
      try {
        for (const [position, skill] of picked.entries()) {
          status.textContent = 'Importing ' + (position + 1) + ' of ' + picked.length + '…';
          try {
            const plan = await api.prepare({ repo: skill.repo, path: skill.path, git_ref: skill.git_ref });
            const response = await api.import(plan, uniqueName(plan.name, taken), deploy.selected());
            done.push(response.skill.title);
            chosen.delete(cacheKey(skill));
          } catch (error) {
            failed.push((skill.title || skill.slug) + ': ' + error.message);
          }
        }
      } finally {
        isApplying = false;
      }
      app.refresh();
      notify(done.length ? 'Imported ' + done.length + (done.length === 1 ? ' skill' : ' skills') + (failed.length ? '. ' + failed.length + ' failed.' : '.')
        : 'No skills were imported.', failed.length ? { tone: 'warning' } : {});
      if (failed.length) status.replaceChildren(errorText(failed.join(' · ')));
      await load();
    }, error => status.replaceChildren(errorText(error.message))), 'primary small');
    batchBar.replaceChildren(
      h('div', { class: 'batch-head' }, h('strong', {}, chosen.size + ' selected'),
        button('Clear', () => { chosen.clear(); showList(); }, 'quiet small')),
      deploy.node, h('div', { class: 'batch-actions' }, status, run));
  }

  function resetPreview(restoreFocus = false) {
    if (isApplying) return;
    previewJob.cancel();
    selected = undefined;
    root.dataset.detail = 'false';
    preview.inert = false;
    preview.removeAttribute('aria-busy');
    preview.replaceChildren(h('div', { class: 'preview-empty' }, icon('explore'), h('h2', {}, 'Preview a skill'),
      h('p', { class: 'muted' }, 'Select a result to read its instructions and every file in its bundle.')));
    for (const row of results.querySelectorAll('.result-button')) row.setAttribute('aria-current', 'false');
    if (restoreFocus) {
      if (lastTrigger?.isConnected) {
        if (narrow.matches) window.scrollTo(0, listScroll); else results.scrollTop = listScroll;
        lastTrigger.focus({ preventScroll: true });
      } else search.focus();
    }
  }

  async function openPreview(selection, skill, force = false) {
    if (isApplying) return;
    const job = previewJob.begin();
    selected = skill;
    for (const row of results.querySelectorAll('.result-button')) row.setAttribute('aria-current', String(row.skill === skill));
    const reveal = () => { root.dataset.detail = 'true'; };
    const finish = () => {
      reveal();
      preview.inert = false;
      preview.removeAttribute('aria-busy');
      if (narrow.matches) {
        window.scrollTo(0, 0);
        preview.querySelector('h2')?.focus({ preventScroll: true });
      }
    };
    if (force && skill) {
      const key = cacheKey(selection);
      cacheSize -= previewCache.get(key)?.size ?? 0;
      previewCache.delete(key);
    }
    const cached = skill && cachedPreview(selection);
    if (cached) {
      renderPreview(cached);
      finish();
      return;
    }

    preview.inert = true;
    preview.setAttribute('aria-busy', 'true');
    const slow = setTimeout(() => {
      if (!job.current()) return;
      reveal();
      preview.inert = false;
      preview.replaceChildren(h('div', { class: 'preview-loading', role: 'status' },
        h('span', { class: 'spinner', 'aria-hidden': 'true' }), 'Opening preview…'));
      if (narrow.matches) {
        window.scrollTo(0, 0);
        layout.querySelector('.explore-back').focus({ preventScroll: true });
      }
    }, 150);
    try {
      const value = skill ? await fetchPreview(selection) : await api.prepare(selection, job.signal);
      if (!job.current()) return;
      renderPreview(value);
      finish();
    } catch (error) {
      if (!job.current()) return;
      preview.replaceChildren(panel(h('h2', { tabindex: '-1' }, 'Preview unavailable'), errorText(error.message),
        button('Retry preview', () => openPreview(selection, skill, true))));
      finish();
    } finally {
      clearTimeout(slow);
    }
  }

  function renderPreview(review) {
    const proposedName = review.name_exists ? slugify(review.slug ?? review.name).slice(0, 59).replace(/-$/, '') + '-copy' : review.slug ?? review.name;
    const nameHint = h('small', { class: 'field-help', id: 'import-name-hint', 'aria-live': 'polite' });
    const name = h('input', { class: 'input', value: proposedName, maxlength: '64', spellcheck: 'false',
      'aria-describedby': 'import-name-hint', oninput: () => checkName() });
    const taken = new Set(app.snapshot.skills.filter(skill => !skill.is_catalog_only).map(skill => skill.name));
    function checkName() {
      const dest = slugify(name.value);
      const problem = !dest || dest.length > 64 ? 'Use 1 to 64 letters, numbers, or hyphens.'
        : taken.has(dest) ? 'A skill named ' + dest + ' already exists. Choose another name.' : '';
      nameHint.textContent = problem || (dest !== name.value.trim() ? 'Saved as ' + dest : '');
      nameHint.classList.toggle('error', Boolean(problem));
      name.setAttribute('aria-invalid', String(Boolean(problem)));
      return problem ? null : dest;
    }
    const files = review.supporting_paths ?? Object.keys(review.supporting_files ?? {});
    const excluded = review.excluded_paths ?? [];
    const capability = h('p', { class: 'capability-note', hidden: true }, icon('info'),
      'Cursor receives the main file only. The ' + files.length + ' bundled ' + (files.length === 1 ? 'file stays' : 'files stay') + ' in your library and other providers.');
    const deploy = deployChips(() => { capability.hidden = !(files.length && deploy.selected().includes('cursor')); });
    const feedback = h('div', { role: 'status' });

    const apply = button('+ Add this skill', () => {
      if (isApplying) return;
      const dest = checkName();
      if (!dest) {
        feedback.replaceChildren(errorText('Choose a valid, unused name before importing.'));
        name.focus();
        return;
      }
      busy(apply, async () => {
        isApplying = true;
        feedback.textContent = 'Preparing the reviewed bundle…';
        const blocked = [...root.querySelectorAll('button, input, select')].filter(control => control !== apply && !control.disabled);
        for (const control of blocked) control.disabled = true;
        try {
          const importPlan = review.plan_id ? review : await api.prepare({
            repo: review.repo, path: review.path, git_ref: review.revision
          });
          if (importPlan.revision !== review.revision) {
            const source = selected;
            feedback.replaceChildren(errorText('The catalog changed. Open a fresh preview before importing.'));
            if (source) {
              feedback.append(button('Refresh preview', () => openPreview(source, source, true), 'quiet small'));
            }
            return;
          }
          const planScripts = importPlan.scripts ?? scripts;
          const targets = deploy.selected();
          if (planScripts.length && !await confirmAction('Import a skill that contains scripts?',
            (review.repo ?? 'This source') + (trust ? ' (' + trust + ')' : '') + ' ships ' + planScripts.length +
              (planScripts.length === 1 ? ' item' : ' items') + ' an agent may run: ' + planScripts.map(script => script.path +
              (script.concerns.length ? ' (' + script.concerns.join(', ') + ')' : '')).join(' · ') + '. ' +
              (targets.length ? 'SkillHub imports it and syncs it to ' + targets.map(id => PROVIDER_NAMES[id] ?? id).join(', ') + ' right away.'
                : 'SkillHub imports it into your library only.'),
            targets.length ? 'Confirm, Import & Sync Now' : 'Confirm & Import', planScripts.some(script => script.concerns.length))) {
            feedback.textContent = '';
            return;
          }
          if (importPlan.skipped_files?.length && !await confirmAction('Import available files?',
            importPlan.skipped_files.length + ' supporting files were excluded. ' + importPlan.skipped_files.join(' · '),
            'Import available files', false)) {
            feedback.textContent = '';
            return;
          }
          const response = await api.import(importPlan, dest, deploy.selected());
          if (selected) {
            const key = cacheKey(selected);
            cacheSize -= previewCache.get(key)?.size ?? 0;
            previewCache.delete(key);
          }
          isApplying = false;
          resetPreview();
          await app.read(response.skill.name, response);
          notify('Imported ' + response.skill.title + '.', { action: 'Open', run: () => app.read(response.skill.name) });
        } finally {
          isApplying = false;
          for (const control of blocked) control.disabled = false;
        }
      }, error => feedback.replaceChildren(errorText(error.message)));
    }, 'primary small');

    const existing = review.existing_name ?? (review.name_exists ? review.slug ?? review.name : null);
    const row = selected && selected.repo === review.repo ? selected : null;
    const installs = row?.installs ?? null;
    const trust = trustLine(review.stars ?? row?.stars ?? null, installs);
    const metadata = h('div', { class: 'preview-metadata' },
      h('h2', { tabindex: '-1', title: review.title }, review.title),
      h('p', { class: 'muted', title: [review.source, review.license].filter(Boolean).join(' · ') },
        review.kind && review.kind !== 'skill' ? (KIND_LABELS[review.kind] ?? review.kind) + ' · ' : '',
        review.source, review.license && ' · ' + review.license),
      trust && h('p', { class: 'preview-trust', title: 'GitHub stars of ' + (review.repo ?? 'the repository') + (installs > 0 ? ' and installs on skills.sh' : '') }, trust),
      h('p', { class: 'preview-files' }, bundleBadge(files.length, { paths: files, verbose: true, main: mainFile(review.path) }),
        excluded.length > 0 && h('span', { class: 'muted' }, ' · ' + excluded.length + ' not importable (binary or too large)'),
        review.name_exists && h('span', { class: 'muted' }, ' · Name in use')));
    // Script review: executable files and shell instructions are listed before anything is imported.
    const scripts = review.scripts ?? [];
    const risky = scripts.filter(script => script.concerns.length);
    const scriptReview = scripts.length > 0 && h('div', { class: 'script-review', 'data-risk': String(risky.length > 0) },
      icon('info'),
      h('div', {}, h('strong', {}, risky.length ? 'Review scripts before importing' : 'Contains executable content'),
        h('p', {}, scripts.length + (scripts.length === 1 ? ' item' : ' items') + ' an agent may run' +
          (risky.length ? ' · ' + risky.length + ' with risky patterns' : '') + '. Open each one below.'),
        h('p', { class: 'script-trust' }, 'Source: ' + (review.repo ?? review.source ?? 'unknown') + ' · ' + (trust || 'no GitHub stars data')),
        h('ul', {}, scripts.map(script => h('li', {},
          button(script.path, () => script.path === 'SKILL.md' ? selectTab('') : openFile(script.path), 'quiet small script-link'),
          h('span', { class: 'muted' }, ' ' + script.language),
          script.concerns.length > 0 && h('span', { class: 'warning' }, ' · ' + script.concerns.join(', ')))))));
    const header = h('header', { class: 'preview-header' }, metadata, scriptReview);
    const footer = h('footer', { class: 'review-footer', 'aria-label': 'Import options' },
      h('div', { class: 'review-footer-options' }, h('label', { class: 'field' }, h('span', {}, 'Save as'), name, nameHint), deploy.node),
      capability,
      h('div', { class: 'preview-actions' }, h('div', { class: 'preview-feedback' }, feedback,
        h('span', { class: 'muted per-skill-note' }, 'Imports only this skill. Tick skills in the list to add several at once.')), apply));
    checkName();
    const provenance = h('div', { class: 'preview-provenance' },
      review.description && h('p', {}, review.description),
      review.has_frontmatter === false && h('p', { class: 'muted' }, 'This file has no frontmatter. SkillHub inferred its name and description.'),
      disclosure('Source and revision', h('p', { class: 'path' }, review.path), h('p', { class: 'path' }, 'Revision ' + review.revision),
        excluded.length > 0 && h('div', { class: 'stack' }, h('p', {}, 'Not imported (binary, too large, or unsupported location):'),
          excluded.map(path => h('p', { class: 'path warning' }, path)))),
      existing && button('Open existing skill', () => app.read(existing), 'quiet small'));
    const fileText = new Map();
    const documentBody = h('div', { class: 'import-document', tabindex: '0', role: 'region', 'aria-label': 'Skill preview' });
    const tabs = files.length > 0 && h('div', { class: 'file-tabs', role: 'tablist', 'aria-label': 'Bundle files' });
    const showMain = () => documentBody.replaceChildren(provenance,
      h('article', { class: 'markdown' }, renderMarkdown(review.instructions_markdown, { title: review.title, openFile })));
    async function showFile(path) {
      const request = new AbortController();
      documentBody.replaceChildren(h('p', { class: 'preview-loading', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), 'Opening ' + path + '…'));
      try {
        const text = fileText.get(path) ?? review.supporting_files?.[path] ?? (await api.previewFile(review, path, request.signal)).text;
        fileText.set(path, text);
        if (activeTab !== path) return;
        documentBody.replaceChildren(h('p', { class: 'file-path path' }, path), ...renderFile(path, text, openFile));
      } catch (error) {
        if (activeTab === path) documentBody.replaceChildren(errorText(error.message));
      }
    }
    let activeTab = '';
    let picker;
    function selectTab(path) {
      activeTab = path;
      for (const tab of tabs?.querySelectorAll('.file-tab') ?? []) tab.setAttribute('aria-selected', String(tab.dataset.path === path));
      if (picker) picker.value = path;
      documentBody.scrollTop = 0;
      if (path) showFile(path); else showMain();
    }
    function openFile(path) {
      if (!files.includes(path)) { notify('This file is outside the reviewed bundle.'); return; }
      selectTab(path);
      tabs?.querySelector('[data-path="' + CSS.escape(path) + '"]')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
    if (tabs) {
      const tab = (path, label) => h('button', { type: 'button', role: 'tab', class: 'file-tab', 'data-path': path,
        'aria-selected': String(path === ''), title: label, onclick: () => selectTab(path) }, label);
      const main = tab('', review.path.split('/').at(-1) || 'SKILL.md');
      if (files.length <= 8) tabs.append(main, ...files.map(path => tab(path, path)));
      else {
        // Large bundles (for example 70 rules/*.md files) get a grouped picker instead of an endless tab strip.
        const folders = Map.groupBy(files, path => path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');
        picker = h('select', { class: 'select file-picker', 'aria-label': 'Open a bundle file', onchange: event => selectTab(event.target.value) },
          h('option', { value: '' }, 'Browse ' + files.length + ' bundle files…'),
          [...folders.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([folder, paths]) => h('optgroup', { label: (folder || 'Skill folder') + ' (' + paths.length + ')' },
            paths.map(path => h('option', { value: path }, path.slice(folder ? folder.length + 1 : 0))))));
        tabs.append(main, ...files.filter(path => !path.includes('/')).slice(0, 4).map(path => tab(path, path)), picker);
      }
      tabs.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight'].includes(event.key) || event.target === picker) return;
        const list = [...tabs.querySelectorAll('.file-tab')];
        const index = list.indexOf(document.activeElement);
        const next = list[(index + (event.key === 'ArrowRight' ? 1 : -1) + list.length) % list.length];
        next?.focus();
        next?.click();
      });
    }
    showMain();
    preview.replaceChildren(h('div', { class: 'import-review' }, header, tabs, documentBody, footer));
  }

  function directImport() {
    if (isApplying) return;
    const input = h('input', { class: 'input', type: 'text', placeholder: 'GitHub or skills.sh link', autofocus: true });
    const file = h('input', { class: 'input', type: 'file', accept: '.md,.mdc,.zip,text/markdown,text/plain,application/zip' });
    const feedback = h('div', { role: 'status' });
    const review = button('Review skill', () => busy(review, async () => {
      const selectedFile = file.files[0];
      let selection;
      if (selectedFile && input.value.trim()) throw new Error('Choose either a link or a file.');
      if (selectedFile && /\.zip$/i.test(selectedFile.name)) {
        // A .skillpack.zip gets its own review: every skill, conflicts and scripts before anything is written.
        await reviewPack(selectedFile, app);
        dialog.close();
        return;
      }
      if (selectedFile) {
        if (selectedFile.size > 2 * 1024 * 1024) throw new Error('Choose a file smaller than 2 MB.');
        selection = { file_name: selectedFile.name, raw_markdown: await selectedFile.text() };
      } else if (input.value.trim()) selection = { source: input.value.trim() };
      else throw new Error('Paste a link or choose a file.');
      if (!dialog.open) return;
      dialog.close();
      openPreview(selection);
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const dialog = modal('Import from link or file', [field('Link', input),
      field('Markdown file or skill pack', file, 'A SKILL.md, .mdc or .md file, or a .skillpack.zip exported from SkillHub.'), feedback], review);
  }

  resetPreview();
  return {
    show(catalog = false) {
      if (catalog) { source = 'local/catalog'; sourceChooser.value = source; }
      initialize();
      load();
    },
    leave() { clearTimeout(timer); clearTimeout(pendingTimer); listJob.cancel(); prefetchQueue = []; resetPreview(); },
    directImport,
    get isApplying() { return isApplying; }
  };
}
