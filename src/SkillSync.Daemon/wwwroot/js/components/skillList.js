import { api } from '../api.js?v=20261112-v9';
import { COLLECTIONS, PROVIDERS, Latest, deployment, filterSkills, inCollection, skillKey, slugify, tagSlug } from '../state.js?v=20261112-v9';
import { highlighted } from '../markdown.js?v=20261112-v9';
import { h, button, heading, field, select, modal, confirmAction, errorText, notify, busy } from '../ui.js?v=20261112-v9';

const STATUSES = { synced: 'Synced', issue: 'Needs attention', disabled: 'Disabled', pending: 'Pending or undeployed', catalog: 'Catalog' };
const EMPTY_FILTERS = { tag: '', origin: '', provider: '', status: '' };

export function createLibrary(root, app) {
  let collection = 'user';
  let allScope = false;
  let filters = { ...EMPTY_FILTERS };
  let snapshot = { skills: [], tags: [] };
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
  const filtersButton = button('Filters', () => showFilters());
  const navigation = h('nav', { class: 'collections', 'aria-label': 'Skill collections' });
  const compact = select([], 'user', { 'aria-label': 'Skill collection', onchange: event => setCollection(event.target.value) });
  const active = h('div', { class: 'chips', 'aria-label': 'Active filters' });
  const results = h('div', { id: 'library-results' });
  root.append(title, h('div', { class: 'stack' },
    h('div', { class: 'search-row' }, h('div', { class: 'search-field' }, search), filtersButton),
    navigation, h('div', { class: 'collection-picker' }, compact), active, results));

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
    const filtered = filterSkills(matches, filters);
    const visible = filtered.filter(skill => inCollection(skill, allScope ? 'all' : collection));
    title.querySelector('h1').textContent = allScope ? 'All Skills' : COLLECTIONS[collection];
    count.textContent = state === 'loading' ? 'Loading skills…' : state === 'searching' ? 'Searching…'
      : state === 'error' ? 'Search unavailable' : visible.length + (visible.length === 1 ? ' skill' : ' skills');
    const categories = Object.entries(COLLECTIONS).map(([key, name]) =>
      [key, name + ' (' + filtered.filter(skill => inCollection(skill, key)).length + ')']);
    navigation.replaceChildren(categories.map(([key, name]) => button(name, () => setCollection(key), '', {
      'aria-pressed': String(!allScope && key === collection), 'data-control': 'category-' + key
    })));
    compact.replaceChildren(categories.map(([key, name]) => h('option', { value: key }, name)),
      ...(allScope ? [h('option', { value: 'all' }, 'All Skills')] : []));
    compact.value = allScope ? 'all' : collection;

    const current = Object.entries(filters).filter(([, value]) => value);
    const labels = { tag: '#' + filters.tag, origin: filters.origin, provider: PROVIDERS[filters.provider], status: STATUSES[filters.status] };
    active.hidden = !allScope && !query && !current.length;
    const clear = () => {
      filters = { ...EMPTY_FILTERS };
      allScope = false;
      search.value = '';
      scheduleSearch();
      search.focus({ preventScroll: true });
    };
    active.replaceChildren(
      ...current.map(([key]) => h('button', { type: 'button', class: 'chip', 'aria-label': 'Remove filter ' + labels[key],
        onclick: () => { filters[key] = ''; render(); filtersButton.focus({ preventScroll: true }); } }, labels[key], ' ×')),
      ...(query ? [h('button', { type: 'button', class: 'chip', 'aria-label': 'Clear search',
        onclick: () => { search.value = ''; scheduleSearch(); search.focus(); } }, query, ' ×')] : []),
      ...(allScope ? [h('button', { class: 'chip', type: 'button', 'aria-label': 'Return to current collection',
        onclick: () => { allScope = false; render(); filtersButton.focus(); } }, 'All Skills ×')] : []),
      button('Clear Filters', clear, 'quiet small', { 'data-control': 'clear-filters' })
    );
    if (state === 'error') {
      results.replaceChildren(h('div', { class: 'empty' }, errorText(problem), button('Try again', loadMatches)));
    } else if (state === 'loading' || state === 'searching') {
      results.replaceChildren(h('p', { class: 'muted' }, state === 'loading' ? 'Loading the library…' : 'Finding matching skills…'));
    } else if (!visible.length) {
      results.replaceChildren(h('div', { class: 'empty' },
        h('h2', {}, query || current.length ? 'No matching skills' : 'No skills in this collection'),
        h('div', { class: 'actions' }, query || current.length ? button('Clear Filters', clear) : null,
          button('Browse Plugin Catalog', () => app.exploreCatalog()))));
    } else {
      const list = h('ul', { class: 'results' }, visible.slice(0, limit).map(skill => {
        const key = skillKey(skill);
        const sync = deployment(skill);
        const tags = skill.tags ?? [];
        const row = h('button', { type: 'button', class: 'result-button', 'data-control': 'row-' + key,
          'aria-label': skill.title + ', ' + sync.label, onclick: () => {
            selectedKey = key;
            savedScroll = window.scrollY;
            app.read(key);
          } },
          h('span', { class: 'result-top' }, h('span', { class: 'result-title' }, highlighted(skill.title || skill.name, query)),
            h('span', { class: 'result-status ' + (sync.tone ?? 'muted') }, sync.label)),
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

  function showFilters() {
    const pool = snapshot.skills;
    const tags = [...new Set([...snapshot.tags.map(tag => tag.tag), ...pool.filter(skill => skill.source_category !== 'system')
      .flatMap(skill => skill.tags ?? []).map(slugify).filter(Boolean)])].sort();
    const origins = [...new Set(pool.map(skill => skill.origin_provider).filter(Boolean))].sort();
    const chooser = (label, values, current) => field(label, select([['', 'Any'], ...values], current));
    const scope = select([['current', 'Current Tab · ' + COLLECTIONS[collection]], ['all', 'All Skills']], allScope ? 'all' : 'current');
    const tag = chooser('Tag', tags.map(value => [value, '#' + value]), filters.tag);
    const origin = chooser('Origin', origins.map(value => [value, value]), filters.origin);
    const provider = chooser('Provider', Object.entries(PROVIDERS), filters.provider);
    const status = chooser('Deployment', Object.entries(STATUSES), filters.status);
    const get = control => control.querySelector('select').value;
    const dialog = modal('Filters', [field('Scope', scope), tag, origin, provider, status,
      button('Manage tags', () => showTagManager(app, snapshot.tags), 'quiet')],
      button('Apply filters', () => {
        filters = { tag: get(tag), origin: get(origin), provider: get(provider), status: get(status) };
        allScope = scope.value === 'all';
        limit = 50;
        render();
        dialog.close();
      }, 'primary'));
  }

  render();
  return {
    update(data) {
      snapshot = data;
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
