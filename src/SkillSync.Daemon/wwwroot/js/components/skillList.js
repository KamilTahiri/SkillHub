import { api } from '../api.js';
import { COLLECTIONS, PROVIDERS, Latest, deployment, installedProviders, matchesFilters, inCollection, skillKey, targetIssues, slugify, tagSlug } from '../state.js';
import { icon, PROVIDER_NAMES } from '../icons.js';
import { highlighted } from '../markdown.js';
import { h, button, field, select, modal, confirmAction, errorText, notify, busy, copy } from '../ui.js';
import { copySkillPrompt, reviewUpdate } from './skillInsights.js';

const STATUSES = { synced: 'Synced', issue: 'Needs attention', disabled: 'Disabled', pending: 'Pending or undeployed', catalog: 'Catalog' };
const EMPTY_FILTERS = { tags: [], tagMode: 'all', origin: '', provider: '', status: '' };
const FACETS = ['origin', 'provider', 'status'];
const RESERVED_TAGS = new Set(['system', 'skillhub', 'meta-skill']);
const SYNC_NAMES = { ...PROVIDER_NAMES, codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity' };

export function createLibrary(root, app) {
  let collection = 'user';
  let allScope = false;
  let filters = { ...EMPTY_FILTERS };
  let snapshot = { skills: [], tags: [] };
  let installed = [];
  let health = new WeakMap();
  let query = '';
  let matches = [];
  let state = 'loading';
  let problem = '';
  let searchTimer;
  let limit = 50;
  let selectedKey;
  let savedScroll = 0;
  const searchJob = new Latest();
  const count = h('p', { class: 'sr-only', role: 'status' });
  const search = h('input', { type: 'search', id: 'library-search', class: 'input', 'aria-label': 'Search skills',
    placeholder: 'Search skills, tags, and instructions', oninput: () => scheduleSearch() });
  const filtersButton = button([icon('filters'), 'Tags & filters'], toggleFilters, '', {
    id: 'library-filters-toggle', 'aria-expanded': 'false', 'aria-controls': 'library-filters'
  });
  const navigation = h('nav', { class: 'collections', 'aria-label': 'Skill collections' });
  const active = h('div', { class: 'chips active-filters', 'aria-label': 'Active filters' });
  const filterPanel = h('section', { class: 'library-filters', id: 'library-filters', 'aria-label': 'Tags and filters', hidden: true });
  const tagOptions = h('div', { class: 'chips tag-options', role: 'group', 'aria-label': 'Filter by tags' });
  const tagMode = select([['all', 'Match all tags'], ['any', 'Match any tag']], 'all', {
    'aria-label': 'Tag matching', onchange: event => setFilter('tagMode', event.target.value)
  });
  const tagSearch = h('input', { class: 'input', type: 'search', placeholder: 'Find a tag', 'aria-label': 'Find a tag',
    oninput: () => renderTags() });
  const scope = select([['collection', 'This collection'], ['all', 'All skills']], 'collection', {
    onchange: event => {
      allScope = event.target.value === 'all';
      filters.tags = filters.tags.filter(tag => tagsForCollection().includes(tag));
      limit = 50;
      render();
    }
  });
  const origin = select([], '', { onchange: event => setFilter('origin', event.target.value) });
  const provider = select([], '', { onchange: event => setFilter('provider', event.target.value) });
  const status = select([], '', { onchange: event => setFilter('status', event.target.value) });
  const tagDropSummary = h('summary', {}, 'More tags');
  const extraTags = h('div', { class: 'tag-options', role: 'group', 'aria-label': 'More tags' });
  const tagDropdown = h('details', { class: 'filter-dropdown', hidden: true }, tagDropSummary,
    h('div', { class: 'filter-menu stack' }, tagSearch, extraTags));
  const manage = button('Manage tags', () => showTagManager(app, snapshot.tags, tag => {
    if (filters.tags.includes(tag)) return;
    if (!tagsForCollection().includes(tag)) {
      const next = RESERVED_TAGS.has(tag) ? 'system' : 'all';
      if (!snapshot.skills.some(skill => inCollection(skill, next, syncFor(skill)) &&
        (skill.tags ?? []).some(value => slugify(value) === tag))) {
        notify('No skills in the library use #' + tag + '.');
        return;
      }
      if (next === 'system') collection = 'system';
      allScope = next === 'all';
      filters = { ...EMPTY_FILTERS };
    }
    toggleTag(tag);
  }), 'quiet small');
  filterPanel.append(tagOptions, tagDropdown, h('div', { class: 'filter-facets' },
    h('label', {}, 'Origin', origin), h('label', {}, 'Provider', provider),
    h('label', {}, 'Sync status', status), h('label', {}, 'Scope', scope), tagMode), manage);
  filterPanel.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      if (tagDropdown.open) { tagDropdown.open = false; tagDropSummary.focus(); }
      else { toggleFilters(); filtersButton.focus(); }
    }
  });
  const readSetting = (key, fallback) => { try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; } };
  const writeSetting = (key, value) => { try { localStorage.setItem(key, value); } catch { /* Preferences are optional. */ } };
  let sortOrder = readSetting('skillhub.library.sort', 'name');
  let density = readSetting('skillhub.library.density', 'comfortable');
  const sorter = select([['name', 'Name'], ['issues', 'Needs attention first'], ['synced', 'Most synced'], ['recent', 'Recently changed']],
    sortOrder, { class: 'select sort-select', 'aria-label': 'Sort skills', title: 'Sort skills', onchange: event => {
      sortOrder = event.target.value;
      writeSetting('skillhub.library.sort', sortOrder);
      render();
    } });
  const densityButton = button([icon(density === 'compact' ? 'rows-comfortable' : 'rows-compact')], () => {
    density = density === 'compact' ? 'comfortable' : 'compact';
    writeSetting('skillhub.library.density', density);
    applyDensity();
  }, 'icon-only', { id: 'library-density' });
  function applyDensity() {
    root.dataset.density = density;
    densityButton.replaceChildren(icon(density === 'compact' ? 'rows-comfortable' : 'rows-compact'));
    const label = density === 'compact' ? 'Use comfortable rows' : 'Use compact rows';
    densityButton.setAttribute('aria-label', label);
    densityButton.title = label;
  }
  applyDensity();
  const results = h('div', { id: 'library-results' });
  const summary = h('div', { class: 'sync-summary', hidden: true });
  const collectionView = h('div', { class: 'stack library-view' }, summary, results);
  collectionView.addEventListener('animationend', () => collectionView.classList.remove('collection-enter'));
  root.append(h('div', { class: 'stack library-body' }, count,
    h('div', { class: 'library-search-row', role: 'search', 'aria-label': 'Library' },
      h('div', { class: 'search-field' }, search), filtersButton, sorter, densityButton,
      button([icon('plus'), 'New skill'], () => app.newSkill(), 'primary', { id: 'new-skill' })),
    navigation, filterPanel, active, collectionView));

  function toggleFilters() {
    filterPanel.hidden = !filterPanel.hidden;
    filtersButton.setAttribute('aria-expanded', String(!filterPanel.hidden));
    if (!filterPanel.hidden) renderFilterPanel();
    else tagDropdown.open = false;
  }

  function setFilter(key, value) {
    filters[key] = value;
    limit = 50;
    render();
  }

  function toggleTag(tag) {
    setFilter('tags', filters.tags.includes(tag) ? filters.tags.filter(value => value !== tag) : [...filters.tags, tag]);
  }

  function tagsForCollection() {
    return [...new Set(snapshot.skills.filter(skill => inCollection(skill, allScope ? 'all' : collection, syncFor(skill)))
      .flatMap(skill => skill.tags ?? []).map(slugify)
      .filter(tag => tag && (collection === 'system' && !allScope || !RESERVED_TAGS.has(tag))))].sort();
  }

  function renderTags() {
    const tags = tagsForCollection();
    const available = tags.filter(tag => !filters.tags.includes(tag));
    const term = tagSearch.value.trim().toLowerCase();
    const pill = tag => button('#' + tag, () => {
      toggleTag(tag);
      active.querySelector('[data-control="active-tag-' + CSS.escape(tag) + '"]')?.focus({ preventScroll: true });
    }, 'filter-tag', { 'data-control': 'filter-tag-' + tag, 'data-tag': tag, 'aria-pressed': 'false' });
    const inline = available.slice(0, tags.length > 10 ? 4 : 10);
    tagOptions.replaceChildren(...inline.map(pill));
    tagDropdown.hidden = tags.length <= 10;
    if (tagDropdown.hidden) tagDropdown.open = false;
    if (!tagDropdown.hidden && tagDropdown.open) {
      const matching = available.filter(tag => tag.includes(term) && !inline.includes(tag));
      extraTags.replaceChildren(...matching.map(pill));
      if (!matching.length) extraTags.append(h('p', { class: 'muted' }, 'No matching tags'));
    }
  }
  tagDropdown.addEventListener('toggle', () => {
    if (tagDropdown.open) {
      renderTags();
      tagSearch.focus({ preventScroll: true });
    }
  });

  function renderFilterPanel() {
    renderTags();
    tagMode.value = filters.tagMode;
    tagMode.hidden = filters.tags.length < 2;
    const origins = [...new Set(snapshot.skills.filter(skill => inCollection(skill, allScope ? 'all' : collection, syncFor(skill)))
      .map(skill => skill.origin_provider).filter(Boolean))].sort();
    const replaceOptions = (control, entries, value) => {
      control.replaceChildren(h('option', { value: '' }, 'Any'), ...entries.map(([key, label]) => h('option', { value: key }, label)));
      if (value && !entries.some(([key]) => key === value)) control.append(h('option', { value }, value));
      control.value = value;
    };
    replaceOptions(origin, origins.map(value => [value, value]), filters.origin);
    replaceOptions(provider, installed.map(id => [id, PROVIDER_NAMES[id]]), filters.provider);
    replaceOptions(status, Object.entries(STATUSES), filters.status);
    scope.value = allScope ? 'all' : 'collection';
  }

  function syncFor(skill) {
    if (!health.has(skill)) health.set(skill, deployment(skill, installed));
    return health.get(skill);
  }

  function setCollection(next) {
    if (collection === next && !allScope) return;
    collection = next;
    allScope = false;
    filters.tags = filters.tags.filter(tag => tagsForCollection().includes(tag));
    if (filters.origin && !snapshot.skills.some(skill => inCollection(skill, collection, syncFor(skill)) &&
      skill.origin_provider === filters.origin)) filters.origin = '';
    tagDropdown.open = false;
    tagSearch.value = '';
    limit = 50;
    render();
    collectionView.classList.remove('collection-enter');
    requestAnimationFrame(() => collectionView.classList.add('collection-enter'));
  }

  function scheduleSearch() {
    clearTimeout(searchTimer);
    searchJob.cancel();
    query = search.value.trim();
    limit = 50;
    if (!query) {
      state = 'ready';
      matches = snapshot.skills;
      render();
      return;
    }
    state = 'searching';
    render();
    searchTimer = setTimeout(loadMatches, 130);
  }

  async function loadMatches() {
    const job = searchJob.begin();
    try {
      const data = await api.skills(query, job.signal);
      if (!job.current()) return;
      matches = data;
      state = 'ready';
      render();
    } catch (error) {
      if (!job.current()) return;
      state = 'error';
      problem = error.message;
      render();
    }
  }

  function render() {
    const focused = root.contains(document.activeElement) ? document.activeElement.dataset.control : null;
    const filtered = matches.filter(skill => matchesFilters(skill, filters, syncFor(skill)));
    const scoped = (skill, category) => inCollection(skill, category, syncFor(skill));
    const visible = filtered.filter(skill => scoped(skill, allScope ? 'all' : collection));
    count.textContent = state === 'loading' ? 'Loading skills…' : state === 'searching' ? 'Searching…'
      : state === 'error' ? 'Search unavailable' : visible.length + (visible.length === 1 ? ' skill' : ' skills');
    const totals = Object.fromEntries(Object.keys(COLLECTIONS).map(category => [category, 0]));
    for (const skill of filtered) {
      for (const category of Object.keys(totals)) if (scoped(skill, category)) totals[category]++;
    }
    navigation.replaceChildren(...Object.entries(COLLECTIONS).map(([key, label]) => button([
      label, h('span', { class: 'collection-count' }, state === 'ready' ? totals[key] : '–')
    ], () => setCollection(key), '', {
      'aria-pressed': String(!allScope && key === collection), 'data-control': 'category-' + key
    })));
    const syncView = collection === 'synced' && !allScope;
    summary.hidden = !syncView || state !== 'ready';
    if (!summary.hidden) {
      const activeProviders = installed.filter(provider => visible.some(skill => syncFor(skill).synced.includes(provider)));
      const personal = visible.filter(skill => skill.source_category === 'user').length;
      const templates = visible.filter(skill => skill.source_category === 'template').length;
      const enabled = visible.length ? count.textContent + ' actively synced to ' + activeProviders.map(id => SYNC_NAMES[id]).join(', ')
        : 'No skills actively synced';
      summary.replaceChildren(h('div', {}, h('strong', {}, enabled), h('span', {}, personal + ' Personal · ' + templates + ' Templates')),
        button('+ Enable a template', () => {
          filters = { ...EMPTY_FILTERS };
          search.value = '';
          scheduleSearch();
          setCollection('template');
          navigation.querySelector('[data-control="category-template"]').focus();
        }, 'quiet small'));
    }

    const current = FACETS.filter(key => filters[key]);
    const labels = { origin: filters.origin, provider: PROVIDERS[filters.provider], status: STATUSES[filters.status] };
    const hasFilters = Boolean(allScope || query || current.length || filters.tags.length);
    active.hidden = !hasFilters;
    const clear = () => {
      filters = { ...EMPTY_FILTERS };
      tagSearch.value = '';
      allScope = false;
      limit = 50;
      render();
      filtersButton.focus({ preventScroll: true });
    };
    const chip = (label, action) => h('button', { type: 'button', class: 'chip', 'aria-label': 'Remove filter ' + label,
      onclick: () => { action(); filtersButton.focus({ preventScroll: true }); } }, label, ' ×');
    active.replaceChildren(
      ...filters.tags.map(tag => h('button', { type: 'button', class: 'chip', 'aria-label': 'Remove filter #' + tag,
        'data-control': 'active-tag-' + tag, onclick: () => {
          toggleTag(tag);
          (root.querySelector('[data-control="filter-tag-' + CSS.escape(tag) + '"]') ?? filtersButton).focus({ preventScroll: true });
        } }, '#' + tag, ' ×')),
      ...current.map(key => chip(labels[key], () => setFilter(key, ''))),
      query && button('Clear search', () => { search.value = ''; scheduleSearch(); search.focus(); }, 'quiet small'),
      ...(allScope ? [chip('All skills', () => { allScope = false; render(); })] : []),
      ...(allScope || current.length || filters.tags.length ? [button('Clear filters', clear, 'quiet small', { 'data-control': 'clear-filters' })] : [])
    );
    if (!filterPanel.hidden) renderFilterPanel();
    if (state === 'error') {
      results.replaceChildren(h('div', { class: 'empty' }, errorText(problem), button('Try again', loadMatches)));
    } else if (state === 'loading' || state === 'searching') {
      results.replaceChildren(h('p', { class: 'muted' }, state === 'loading' ? 'Loading the library…' : 'Finding matching skills…'));
    } else if (!visible.length) {
      results.replaceChildren(h('div', { class: 'empty' },
        h('h2', {}, hasFilters ? 'No matching skills' : 'No skills in this collection'),
        h('div', { class: 'actions' }, button('Browse Plugin Catalog', () => app.exploreCatalog()))));
    } else {
      const severity = skill => syncFor(skill).issues.length ? 0 : syncFor(skill).key === 'pending' ? 1 : 2;
      const ordered = sortOrder === 'name' ? visible : [...visible].sort((a, b) =>
        sortOrder === 'issues' ? severity(a) - severity(b) || (a.title || a.name).localeCompare(b.title || b.name)
          : sortOrder === 'synced' ? syncFor(b).synced.length - syncFor(a).synced.length || (a.title || a.name).localeCompare(b.title || b.name)
            : (b.revision ?? 0) - (a.revision ?? 0) || (a.title || a.name).localeCompare(b.title || b.name));
      const list = h('ul', { class: 'results' }, ordered.slice(0, limit).map(skill => {
        const key = skillKey(skill);
        const sync = syncFor(skill);
        const tags = (skill.tags ?? []).filter(tag => collection === 'system' && !allScope || !RESERVED_TAGS.has(slugify(tag)));
        const issues = targetIssues(skill, installed);
        const origin = skill.origin_provider;
        const row = h('button', { type: 'button', class: 'result-button', 'data-control': 'row-' + key,
          'aria-label': skill.title || skill.name, onclick: () => {
            selectedKey = key;
            savedScroll = window.scrollY;
            app.read(key);
          } },
          h('span', { class: 'skill-glyph', 'data-provider': sync.synced[0] ?? (skill.is_catalog_only ? 'catalog' : skill.source_category),
            'aria-hidden': 'true' }, glyphText(skill.title || skill.name)),
          h('span', { class: 'result-title' }, highlighted(skill.title || skill.name, query)),
          h('span', { class: 'result-description' }, highlighted(skill.instruction_match ?? skill.description ?? '', query)),
          !syncView && h('span', { class: 'chips' }, origin && !['My Skill', 'Personal', 'SkillHub'].includes(origin) &&
            h('span', { class: 'result-meta', title: origin }, origin),
            tags.slice(0, 2).map(tag => h('span', { class: 'chip' }, '#' + tag)),
            tags.length > 2 && h('span', { class: 'chip', title: tags.slice(2).join(', ') }, '+' + (tags.length - 2))));
        const providers = h('div', { class: 'result-providers' },
          sync.synced.map(id => {
            const partial = sync.issues.includes(id);
            const label = (partial ? 'Partially synced to ' : 'Synced to ') + SYNC_NAMES[id];
            return h('span', { class: 'provider-badge' + (syncView ? ' sync-pill' + (partial ? ' partial' : '') : ''), title: label,
              'aria-label': label, 'data-synced-provider': id },
              syncView ? h('span', { class: 'sync-dot', 'aria-hidden': 'true' }) : icon(id),
              h('span', { class: syncView ? '' : 'sr-only' }, PROVIDER_NAMES[id]));
          }),
          !sync.synced.length && !issues.length && h('span', { class: 'result-status muted' }, sync.label));
        const exceptions = issues.map(issue => button(issue.label + (issue.state === 'missing' ? ' copy missing' : ' needs review') + ' →',
          () => app.review(skill.name, issue), 'quiet small warning', { 'data-control': 'review-' + issue.path }));
        const readOnly = skill.source_category === 'system';
        const detailThen = (control, action) => busy(control, async () => action(await api.detail(key)));
        const menu = h('details', { class: 'action-menu row-menu' },
          h('summary', { class: 'btn small icon-only', 'aria-label': 'More actions for ' + (skill.title || skill.name), title: 'More actions' }, icon('more')),
          h('div', { class: 'action-menu-list' },
            button([icon('copy'), 'Copy instructions only'], event => { menu.open = false; detailThen(event.currentTarget, detail => copy(detail.skill.instructions_markdown)); }, 'quiet small'),
            button([icon('copy'), 'Copy name'], () => { menu.open = false; copy(skill.name); }, 'quiet small'),
            issues[0] && button([icon('sync'), 'Review ' + issues[0].label], () => { menu.open = false; app.review(skill.name, issues[0]); }, 'quiet small')));
        menu.addEventListener('focusout', event => { if (!menu.contains(event.relatedTarget)) menu.open = false; });
        menu.addEventListener('keydown', event => {
          if (event.key === 'Escape' && menu.open) { event.stopPropagation(); menu.open = false; menu.firstElementChild.focus(); }
        });
        const update = app.updates?.get(skill.name);
        const quick = h('div', { class: 'row-actions' },
          button([icon('copy')], event => busy(event.currentTarget, () => copySkillPrompt(app, key)), 'small icon-only',
            { 'aria-label': 'Copy skill prompt for ' + (skill.title || skill.name), title: 'Copy skill prompt' }),
          button([icon('open')], () => { selectedKey = key; savedScroll = window.scrollY; app.read(key); }, 'small icon-only',
            { 'aria-label': 'Open ' + (skill.title || skill.name), title: 'Open' }),
          !readOnly && button([icon('edit')], event => detailThen(event.currentTarget, detail => app.edit(detail, { clone: skill.is_catalog_only })),
            'small icon-only', { 'aria-label': (skill.is_catalog_only ? 'Clone ' : 'Edit ') + (skill.title || skill.name),
              title: skill.is_catalog_only ? 'Clone to My Skills' : 'Edit' }),
          menu);
        const updateBadge = update && button([icon('sync'), 'Update'], () => reviewUpdate(app, skill), 'small update-badge',
          { title: update.repo + ' has a newer version', 'aria-label': 'Review the upstream update for ' + (skill.title || skill.name) });
        return h('li', { class: 'result-row library-row' }, row, h('div', { class: 'library-row-status' }, updateBadge, providers, exceptions), quick);
      }));
      results.replaceChildren(list);
      if (visible.length > limit) results.append(button('Show more skills', () => {
        const next = visible[limit];
        limit += 50;
        render();
        root.querySelector('[data-control="' + CSS.escape('row-' + skillKey(next)) + '"]')?.focus();
      }));
    }
    if (focused) root.querySelector('[data-control="' + CSS.escape(focused) + '"]')?.focus({ preventScroll: true });
  }

  render();
  return {
    update(data) {
      snapshot = data;
      installed = installedProviders(data.providers);
      health = new WeakMap();
      if (filters.provider && !installed.includes(filters.provider)) filters.provider = '';
      if (query) { clearTimeout(searchTimer); loadMatches(); }
      else { matches = snapshot.skills; state = 'ready'; render(); }
    },
    error(message) { state = 'error'; problem = message; render(); },
    focusSearch() { search.focus(); search.select(); },
    restore() {
      window.scrollTo(0, savedScroll);
      root.querySelector('[data-control="' + CSS.escape('row-' + selectedKey) + '"]')?.focus({ preventScroll: true });
    },
    get query() { return query; }
  };
}

function glyphText(title) {
  const words = String(title).replace(/[^\p{L}\p{Nd}\s-]/gu, ' ').split(/[\s-]+/).filter(Boolean);
  return ((words[0]?.[0] ?? '?') + (words[1]?.[0] ?? '')).toUpperCase();
}

function showTagManager(app, initialTags, onUsage) {
  const body = h('div', { class: 'stack' });
  const input = h('input', { class: 'input', 'aria-label': 'New tag' });
  const outcome = h('div', { role: 'status' });
  const list = h('div', { class: 'stack' });
  let tags = initialTags;
  const search = h('input', { class: 'input', type: 'search', placeholder: 'Find a tag', 'aria-label': 'Search tags',
    oninput: () => render(tags) });
  const refresh = async () => {
    render(await api.tags());
    app.refresh();
  };
  const create = button('Create tag', () => busy(create, async () => {
    const response = await api.createTag(tagSlug(input.value));
    input.value = '';
    outcome.textContent = '#' + response.tag + ' saved.';
    await refresh();
  }, error => outcome.replaceChildren(errorText(error.message))));
  body.append(h('div', { class: 'search-row' }, input, create), search, outcome, list);
  function render(values) {
    tags = values;
    const visible = tags.filter(item => item.tag.includes(search.value.trim().toLowerCase()));
    list.replaceChildren(...visible.map(({ tag, skill_count: count }) => h('div', { class: 'spread' },
      h('span', {}, '#' + tag),
      button(count + (count === 1 ? ' skill' : ' skills'), () => { manager.close(); onUsage(tag); }, 'quiet small', {
        'aria-label': 'Show skills tagged ' + tag
      }),
      h('div', { class: 'actions' },
        button('Rename', () => {
          const editor = h('input', { class: 'input', value: tag, autofocus: true });
          const feedback = h('div');
          const save = button('Rename tag', () => busy(save, async () => {
            const next = tagSlug(editor.value);
            if (next === tag) { dialog.close(); return; }
            const existing = tags.find(item => item.tag === next);
            if (!await confirmAction(existing ? 'Merge tags' : 'Rename tag', existing
              ? 'Move #' + tag + ' to #' + next + ' on ' + count + ' skills. #' + next + ' already has ' + existing.skill_count + ' skills.'
              : 'Rename #' + tag + ' to #' + next + ' on ' + count + ' skills.', existing ? 'Merge tags' : 'Rename tag', false)) return;
            const result = await api.renameTag(tag, next);
            dialog.close();
            await after(result);
          }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
          const dialog = modal('Rename #' + tag, [field('New tag name', editor), feedback], save);
        }, 'small', { 'aria-label': 'Rename #' + tag }),
        button('Delete', async () => {
          if (!(await confirmAction('Delete #' + tag, 'Remove this tag from ' + count + ' skills?', 'Delete tag'))) return;
          try { await after(await api.deleteTag(tag)); } catch (error) { outcome.replaceChildren(errorText(error.message)); }
        }, 'small danger', { 'aria-label': 'Delete #' + tag })))));
  }
  async function after(result) {
    outcome.replaceChildren(h('p', {}, result.updated_skills + ' skills updated.'));
    if (result.undo_id) {
      const undo = button('Undo', () => busy(undo, async () => {
        await api.undoTag(result.undo_id);
        outcome.textContent = 'Tag change undone.';
        await refresh();
      }, error => notify(error.message)), 'small');
      outcome.append(undo);
    }
    await refresh();
  }
  render(initialTags);
  const manager = modal('Manage tags', body);
}
