import { api } from '../api.js?v=20261113-v10';
import { COLLECTIONS, PROVIDERS, Latest, deployment, installedProviders, matchesFilters, inCollection, skillKey, slugify, tagSlug } from '../state.js?v=20261113-v10';
import { icon, PROVIDER_NAMES } from '../icons.js?v=20261113-v10';
import { highlighted } from '../markdown.js?v=20261113-v10';
import { h, button, heading, field, select, modal, confirmAction, errorText, notify, busy } from '../ui.js?v=20261113-v10';

const STATUSES = { synced: 'Synced', issue: 'Needs attention', disabled: 'Disabled', pending: 'Pending or undeployed', catalog: 'Catalog' };
const EMPTY_FILTERS = { tags: [], tagMode: 'all', origin: '', provider: '', status: '' };
const FACETS = ['origin', 'provider', 'status'];

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
  const count = h('p', { class: 'result-count', role: 'status' });
  const title = heading('My Skills', button('+ New skill', () => app.newSkill(), 'primary', { id: 'new-skill' }), count);
  const search = h('input', { type: 'search', id: 'library-search', class: 'input', 'aria-label': 'Search skills',
    placeholder: 'Search skills, tags, and instructions', oninput: () => scheduleSearch() });
  const filtersButton = button([icon('filters'), 'Tags & filters'], toggleFilters, '', {
    id: 'library-filters-toggle', 'aria-expanded': 'false', 'aria-controls': 'library-filters'
  });
  const navigation = h('nav', { class: 'collections', 'aria-label': 'Skill collections' });
  const active = h('div', { class: 'chips active-filters', 'aria-label': 'Active filters' });
  const filterPanel = h('section', { class: 'library-filters stack', id: 'library-filters', 'aria-label': 'Tags and filters', hidden: true });
  const tagOptions = h('div', { class: 'chips tag-options', role: 'group', 'aria-label': 'Filter by tags' });
  const tagMode = select([['all', 'Match all tags'], ['any', 'Match any tag']], 'all', {
    'aria-label': 'Tag matching', onchange: event => setFilter('tagMode', event.target.value)
  });
  const tagSearch = h('input', { class: 'input', type: 'search', placeholder: 'Find a tag', 'aria-label': 'Find a tag',
    oninput: () => renderTags() });
  const scope = button('All skills', () => { allScope = !allScope; limit = 50; render(); }, '', { 'aria-pressed': 'false' });
  const origin = select([], '', { onchange: event => setFilter('origin', event.target.value) });
  const provider = select([], '', { onchange: event => setFilter('provider', event.target.value) });
  const status = select([], '', { onchange: event => setFilter('status', event.target.value) });
  const tagTools = h('div', { class: 'filter-tag-tools' }, tagSearch, tagMode,
    button('Manage tags', () => showTagManager(app, snapshot.tags), 'quiet small'));
  filterPanel.append(h('div', { class: 'stack filter-tags' }, tagTools, tagOptions),
    h('div', { class: 'filter-facets' }, field('Origin', origin), field('Provider', provider),
      field('Sync status', status), field('Search scope', scope)));
  filterPanel.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      toggleFilters();
      filtersButton.focus();
    }
  });
  const results = h('div', { id: 'library-results' });
  root.append(title, h('div', { class: 'stack library-body' },
    h('div', { class: 'search-row' }, h('div', { class: 'search-field' }, search), filtersButton),
    navigation, active, filterPanel, results));

  function toggleFilters() {
    filterPanel.hidden = !filterPanel.hidden;
    filtersButton.setAttribute('aria-expanded', String(!filterPanel.hidden));
    if (!filterPanel.hidden) renderFilterPanel();
  }

  function setFilter(key, value) {
    filters[key] = value;
    limit = 50;
    render();
  }

  function toggleTag(tag) {
    setFilter('tags', filters.tags.includes(tag) ? filters.tags.filter(value => value !== tag) : [...filters.tags, tag]);
  }

  function renderTags() {
    const tags = [...new Set([...snapshot.tags.map(tag => tag.tag), ...filters.tags, ...snapshot.skills
      .flatMap(skill => skill.tags ?? []).map(slugify).filter(Boolean)])].sort();
    const term = tagSearch.value.trim().toLowerCase();
    const available = tags.filter(tag => filters.tags.includes(tag) || tag.includes(term));
    tagOptions.replaceChildren(...available.map(tag => button('#' + tag, () => toggleTag(tag), 'filter-tag', {
      'data-control': 'filter-tag-' + tag, 'data-tag': tag, 'aria-pressed': String(filters.tags.includes(tag))
    })));
    if (!available.length) tagOptions.append(h('p', { class: 'muted' }, term ? 'No matching tags' : 'No tags in the library'));
  }

  function renderFilterPanel() {
    renderTags();
    tagMode.value = filters.tagMode;
    const origins = [...new Set(snapshot.skills.map(skill => skill.origin_provider).filter(Boolean))].sort();
    const replaceOptions = (control, entries, value) => {
      control.replaceChildren(h('option', { value: '' }, 'Any'), ...entries.map(([key, label]) => h('option', { value: key }, label)));
      control.value = value;
    };
    replaceOptions(origin, origins.map(value => [value, value]), filters.origin);
    replaceOptions(provider, installed.map(id => [id, PROVIDERS[id]]), filters.provider);
    replaceOptions(status, Object.entries(STATUSES), filters.status);
    scope.setAttribute('aria-pressed', String(allScope));
  }

  function syncFor(skill) {
    if (!health.has(skill)) health.set(skill, deployment(skill, installed));
    return health.get(skill);
  }

  function setCollection(next) {
    collection = next;
    allScope = false;
    limit = 50;
    render();
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
    title.querySelector('h1').textContent = allScope ? 'All Skills' : COLLECTIONS[collection];
    count.textContent = state === 'loading' ? 'Loading skills…' : state === 'searching' ? 'Searching…'
      : state === 'error' ? 'Search unavailable' : visible.length + (visible.length === 1 ? ' skill' : ' skills');
    navigation.replaceChildren(...Object.entries(COLLECTIONS).map(([key, name]) => button([
      name, h('span', { class: 'collection-count' }, filtered.filter(skill => scoped(skill, key)).length)
    ], () => setCollection(key), '', {
      'aria-pressed': String(!allScope && key === collection), 'data-control': 'category-' + key
    })));

    const current = FACETS.filter(key => filters[key]);
    const labels = { origin: filters.origin, provider: PROVIDERS[filters.provider], status: STATUSES[filters.status] };
    const hasFilters = Boolean(allScope || query || current.length || filters.tags.length);
    active.hidden = !hasFilters;
    const clear = () => {
      filters = { ...EMPTY_FILTERS };
      tagSearch.value = '';
      allScope = false;
      search.value = '';
      scheduleSearch();
      search.focus({ preventScroll: true });
    };
    const chip = (label, action) => h('button', { type: 'button', class: 'chip', 'aria-label': 'Remove filter ' + label,
      onclick: () => { action(); filtersButton.focus({ preventScroll: true }); } }, label, ' ×');
    active.replaceChildren(
      ...filters.tags.map(tag => chip('#' + tag, () => toggleTag(tag))),
      ...current.map(key => chip(labels[key], () => setFilter(key, ''))),
      ...(query ? [chip(query, () => { search.value = ''; scheduleSearch(); })] : []),
      ...(allScope ? [chip('All skills', () => { allScope = false; render(); })] : []),
      button('Clear filters', clear, 'quiet small', { 'data-control': 'clear-filters' })
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
      const list = h('ul', { class: 'results' }, visible.slice(0, limit).map(skill => {
        const key = skillKey(skill);
        const sync = syncFor(skill);
        const tags = skill.tags ?? [];
        const row = h('button', { type: 'button', class: 'result-button', 'data-control': 'row-' + key,
          'aria-label': [skill.title || skill.name, ...sync.synced.map(id => 'Synced to ' + PROVIDER_NAMES[id]),
            sync.issues.length ? 'Needs attention: ' + sync.issues.map(id => PROVIDER_NAMES[id]).join(', ') : !sync.synced.length && sync.label]
            .filter(Boolean).join(', '), onclick: () => {
            selectedKey = key;
            savedScroll = window.scrollY;
            app.read(key);
          } },
          h('span', { class: 'result-top' }, h('span', { class: 'result-title' }, highlighted(skill.title || skill.name, query)),
            h('span', { class: 'result-providers' },
              sync.synced.map(id => h('span', { class: 'provider-badge', title: 'Synced to ' + PROVIDERS[id], 'data-synced-provider': id },
                icon(id), PROVIDER_NAMES[id])),
              (sync.issues.length > 0 || !sync.synced.length) &&
                h('span', { class: 'result-status ' + (sync.tone ?? 'muted'),
                  title: sync.issues.map(id => PROVIDERS[id]).join(', ') }, sync.label))),
          h('span', { class: 'result-description' }, highlighted(skill.instruction_match ?? skill.description ?? '', query)),
          h('span', { class: 'chips' }, h('span', { class: 'result-meta', title: skill.origin_provider }, skill.origin_provider || 'Personal'),
            tags.slice(0, 2).map(tag => h('span', { class: 'chip' }, '#' + tag)),
            tags.length > 2 && h('span', { class: 'chip', title: tags.slice(2).join(', ') }, '+' + (tags.length - 2))));
        return h('li', { class: 'result-row' }, row);
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

function showTagManager(app, initialTags) {
  const body = h('div', { class: 'stack' });
  const input = h('input', { class: 'input', 'aria-label': 'New tag' });
  const outcome = h('div', { role: 'status' });
  const list = h('div', { class: 'stack' });
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
  body.append(h('div', { class: 'search-row' }, input, create), outcome, list);
  function render(tags) {
    list.replaceChildren(...tags.map(({ tag, skill_count: count }) => h('div', { class: 'spread' },
      h('span', {}, '#' + tag + ' · ' + count + (count === 1 ? ' skill' : ' skills')),
      h('div', { class: 'actions' },
        button('Rename', () => {
          const editor = h('input', { class: 'input', value: tag, autofocus: true });
          const feedback = h('div');
          const save = button('Rename tag', () => busy(save, async () => {
            const next = tagSlug(editor.value);
            if (next === tag) { dialog.close(); return; }
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
  modal('Manage tags', body);
}
