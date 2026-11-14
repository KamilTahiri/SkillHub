import { api } from '../api.js';
import { Latest, PROVIDERS, slugify } from '../state.js';
import { renderMarkdown } from '../markdown.js';
import { icon, PROVIDER_NAMES } from '../icons.js';
import { h, button, field, select, panel, modal, errorText, busy, notify, disclosure, confirmAction } from '../ui.js';

const TOPICS = [['', 'All topics'], ['coding', 'Coding'], ['web', 'Web and design'], ['data', 'Data'],
  ['devops', 'Infrastructure'], ['productivity', 'Productivity'], ['testing', 'Testing and security']];

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
  let topic = '';
  let timer;
  let items = [];
  const resultRows = new WeakMap();
  let selected;
  let lastTrigger;
  let listScroll = 0;
  let isApplying = false;
  let initialized = false;
  let visibleCount = 50;
  const sourceChooser = select([['featured/all', 'Featured'], ['local/catalog', 'Local Plugin Catalog']], source, {
    onchange: event => { source = event.target.value; load(); }
  });
  const topicChooser = select(TOPICS, '', { onchange: event => {
    topic = event.target.value;
    resetPreview();
    showList();
  } });
  const search = h('input', { class: 'input', type: 'search', placeholder: 'Find skills', 'aria-label': 'Search Explore',
    oninput: () => {
      clearTimeout(timer);
      listJob.cancel();
      items = [];
      prefetchQueue = [];
      resetPreview();
      results.replaceChildren(h('p', { role: 'status' }, 'Searching…'));
      timer = setTimeout(load, 180);
    } });
  const results = h('div', { class: 'explore-results', tabindex: '0', role: 'region', 'aria-label': 'Explore results' });
  const preview = h('div', { id: 'explore-preview' });
  const layout = h('div', { class: 'explore-layout' }, results,
    h('div', { class: 'explore-detail' },
      button('← Back to results', () => resetPreview(true), 'quiet small explore-back'), preview));
  const toolbar = h('div', { class: 'explore-toolbar', role: 'search', 'aria-label': 'Explore skills' },
    h('div', { class: 'search-field' }, search), field('Source', sourceChooser), field('Topic', topicChooser),
    button('Import from link or file', directImport));
  root.dataset.detail = 'false';
  root.append(toolbar, layout);

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

  function fetchPreview(selection) {
    const cached = cachedPreview(selection);
    if (cached) return Promise.resolve(cached);
    const key = cacheKey(selection);
    if (previewRequests.has(key)) return previewRequests.get(key);
    const pending = api.communityPreview(selection).then(value => {
      if (items.includes(selection)) {
        const row = resultRows.get(selection);
        row?.querySelector('.result-title').replaceChildren(value.title);
        row?.querySelector('.result-description').replaceChildren(value.description);
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
    prefetchQueue = skills.slice(0, 12);
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

  async function initialize() {
    if (initialized) return;
    initialized = true;
    try {
      const response = await api.repositories();
      const options = response.repositories.map(repo => [repo.repo, repo.title]);
      sourceChooser.replaceChildren(...options.map(([key, label]) => h('option', { value: key }, label)));
      sourceChooser.value = source;
    } catch {
      // Built-in source choices are available while repository metadata is unreachable.
    }
  }
  async function load() {
    if (isApplying) return;
    clearTimeout(timer);
    const job = listJob.begin();
    items = [];
    prefetchQueue = [];
    visibleCount = 50;
    resetPreview();
    results.replaceChildren(h('p', { role: 'status' }, 'Loading skills…'));
    results.scrollTop = 0;
    try {
      const response = await api.community(source, search.value.trim(), job.signal);
      if (!job.current()) return;
      items = response.skills ?? [];
      showList();
    } catch (error) {
      if (job.current()) results.replaceChildren(panel(errorText(error.message), button('Retry search', load)));
    }
  }
  function showList() {
    const filtered = items.filter(skill => !topic || skill.topic === topic);
    prefetch(filtered);
    if (!filtered.length) {
      results.replaceChildren(h('div', { class: 'empty' }, h('h2', {}, 'No matching skills'),
        h('p', { class: 'muted' }, 'Change the source, topic, or search term.')));
      return;
    }
    results.replaceChildren(h('p', { class: 'muted explore-count', role: 'status' }, filtered.length + ' results'),
      h('ul', { class: 'results' }, filtered.slice(0, visibleCount).map(skill => {
        const row = h('button', { type: 'button', class: 'result-button', 'aria-current': String(selected === skill),
          onclick: () => {
            lastTrigger = row;
            listScroll = window.scrollY;
            openPreview(skill, skill);
          } }, h('span', { class: 'result-title' }, skill.title || skill.slug),
          h('span', { class: 'result-description' }, skill.description),
          h('span', { class: 'result-meta' }, skill.is_installed ? 'Already in library · ' + skill.repo : skill.repo));
        row.skill = skill;
        resultRows.set(skill, row);
        const cached = cachedPreview(skill);
        if (cached) {
          row.querySelector('.result-title').replaceChildren(cached.title);
          row.querySelector('.result-description').replaceChildren(cached.description);
        }
        return h('li', { class: 'result-row' }, row, skill.existing_name &&
          button('Open existing skill', () => app.read(skill.existing_name), 'quiet small'));
      })));
    if (filtered.length > visibleCount) results.append(button('Show more results', () => {
      const index = visibleCount;
      visibleCount += 50;
      showList();
      results.querySelectorAll('.result-button')[index]?.focus();
    }));
  }
  function resetPreview(restoreFocus = false) {
    if (isApplying) return;
    previewJob.cancel();
    selected = undefined;
    root.dataset.detail = 'false';
    preview.inert = false;
    preview.removeAttribute('aria-busy');
    preview.replaceChildren(panel(h('h2', {}, 'Preview a skill'), h('p', { class: 'muted' }, 'Select a result to review its instructions and files.')));
    for (const row of results.querySelectorAll('.result-button')) row.setAttribute('aria-current', 'false');
    if (restoreFocus) {
      if (lastTrigger?.isConnected) {
        if (narrow.matches) window.scrollTo(0, listScroll);
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
      preview.replaceChildren(h('div', { class: 'preview-loading', role: 'status' }, 'Opening preview…'));
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
    const targets = Object.keys(PROVIDERS).filter(id => app.snapshot.providers.some(provider => provider.provider === id && provider.is_installed))
      .map(id => ({ id, label: PROVIDER_NAMES[id], control: h('input', { type: 'checkbox', class: 'deploy-chip-input' }) }));
    const feedback = h('div', { role: 'status' });
    const deploy = h('fieldset', { class: 'deploy-targets' }, h('legend', {}, 'Also deploy to'),
      targets.length > 0
        ? h('div', { class: 'deploy-chips' }, targets.map(target => h('label', { class: 'deploy-chip', 'data-provider': target.id },
          target.control, icon(target.id), h('span', {}, target.label))))
        : h('p', { class: 'muted' }, 'No AI tools detected. The skill is saved to your library only.'));

    const apply = button('+ Add to My Skills', () => {
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
          if (importPlan.skipped_files?.length && !await confirmAction('Import available files?',
            importPlan.skipped_files.length + ' supporting files were excluded. ' + importPlan.skipped_files.join(' · '),
            'Import available files', false)) {
            feedback.textContent = '';
            return;
          }
          const response = await api.import(importPlan, dest, targets.filter(target => target.control.checked).map(target => target.id));
          if (selected) {
            const key = cacheKey(selected);
            cacheSize -= previewCache.get(key)?.size ?? 0;
            previewCache.delete(key);
          }
          isApplying = false;
          resetPreview();
          await app.read(response.skill.name, response);
          notify('Imported ' + response.skill.title + '.');
        } finally {
          isApplying = false;
          for (const control of blocked) control.disabled = false;
        }
      }, error => feedback.replaceChildren(errorText(error.message)));
    }, 'primary small');

    const files = review.supporting_paths ?? Object.keys(review.supporting_files ?? {});
    const existing = review.existing_name ?? (review.name_exists ? review.slug ?? review.name : null);
    const metadata = h('div', { class: 'preview-metadata' },
      h('h2', { tabindex: '-1', title: review.title }, review.title),
      h('p', { class: 'muted', title: [review.source, review.license].filter(Boolean).join(' · ') },
        review.source, review.license && ' · ' + review.license),
      h('p', { class: 'muted' }, files.length + (files.length === 1 ? ' supporting file' : ' supporting files'),
        review.name_exists && ' · Name in use'));
    const header = h('header', { class: 'preview-header' }, metadata);
    const footer = h('footer', { class: 'review-footer', 'aria-label': 'Import options' },
      h('div', { class: 'review-footer-options' }, h('label', { class: 'field' }, h('span', {}, 'Save as'), name, nameHint), deploy),
      h('div', { class: 'preview-actions' }, h('div', { class: 'preview-feedback' }, feedback), apply));
    checkName();
    const provenance = h('div', { class: 'preview-provenance' },
      h('p', {}, review.description),
      disclosure('Source and revision', h('p', { class: 'path' }, review.path), h('p', { class: 'path' }, 'Revision ' + review.revision)),
      existing && button('Open existing skill', () => app.read(existing), 'quiet small'));
    const openFile = async path => {
      if (!files.includes(path)) { notify('This file is outside the reviewed bundle.'); return; }
      const body = h('div', {}, h('p', { role: 'status' }, 'Opening file…'));
      const dialog = modal(path, body);
      const request = new AbortController();
      dialog.addEventListener('close', () => request.abort(), { once: true });
      try {
        const text = review.supporting_files?.[path] ?? (await api.previewFile(review, path, request.signal)).text;
        if (dialog.open) body.replaceChildren(h('pre', { class: 'file-code', tabindex: '0' }, text));
      } catch (error) {
        if (dialog.open) body.replaceChildren(errorText(error.message));
      }
    };
    const documentBody = h('div', { class: 'import-document', tabindex: '0', role: 'region', 'aria-label': 'Skill preview' },
      provenance, h('article', { class: 'markdown' }, renderMarkdown(review.instructions_markdown, { title: review.title, openFile })),
      (files.length || review.skipped_files?.length) && disclosure('Supporting files (' + files.length + ')', h('div', { class: 'stack' },
        files.map(path => button(path, () => openFile(path), 'quiet')),
        (review.skipped_files ?? []).map(path => h('p', { class: 'warning' }, 'Excluded: ' + path)))));
    preview.replaceChildren(h('div', { class: 'import-review' }, header, documentBody, footer));
  }

  function directImport() {
    if (isApplying) return;
    const input = h('input', { class: 'input', type: 'text', placeholder: 'GitHub or skills.sh link', autofocus: true });
    const file = h('input', { class: 'input', type: 'file', accept: '.md,.mdc,text/markdown,text/plain' });
    const feedback = h('div', { role: 'status' });
    const review = button('Review skill', () => busy(review, async () => {
      const selectedFile = file.files[0];
      let selection;
      if (selectedFile && input.value.trim()) throw new Error('Choose either a link or a Markdown file.');
      if (selectedFile) {
        if (selectedFile.size > 2 * 1024 * 1024) throw new Error('Choose a file smaller than 2 MB.');
        selection = { file_name: selectedFile.name, raw_markdown: await selectedFile.text() };
      } else if (input.value.trim()) selection = { source: input.value.trim() };
      else throw new Error('Paste a link or choose a file.');
      if (!dialog.open) return;
      dialog.close();
      openPreview(selection);
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const dialog = modal('Import from link or file', [field('Link', input), field('Markdown file', file), feedback], review);
  }

  resetPreview();
  return {
    show(catalog = false) {
      if (catalog) { source = 'local/catalog'; sourceChooser.value = source; }
      initialize();
      load();
    },
    leave() { clearTimeout(timer); listJob.cancel(); prefetchQueue = []; resetPreview(); },
    get isApplying() { return isApplying; }
  };
}
