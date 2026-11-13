import { api } from '../api.js?v=20261112-v9';
import { Latest, PROVIDERS, slugify } from '../state.js?v=20261112-v9';
import { renderMarkdown } from '../markdown.js?v=20261112-v9';
import { h, button, heading, field, select, panel, modal, errorText, busy, notify, disclosure } from '../ui.js?v=20261112-v9';

const TOPICS = [['', 'All topics'], ['coding', 'Coding'], ['web', 'Web and design'], ['data', 'Data'],
  ['devops', 'Infrastructure'], ['productivity', 'Productivity'], ['testing', 'Testing and security']];

export function createExplore(root, app) {
  const listJob = new Latest();
  const previewJob = new Latest();
  let source = 'featured/all';
  let topic = '';
  let timer;
  let items = [];
  let plan;
  let selected;
  let lastTrigger;
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
      resetPreview();
      results.replaceChildren(h('p', { role: 'status' }, 'Searching…'));
      timer = setTimeout(load, 180);
    } });
  const results = h('div', { class: 'stack' });
  const preview = h('div', { class: 'stack', id: 'explore-preview' });
  const layout = h('div', { class: 'explore-layout', 'data-detail': 'false' },
    h('div', { class: 'explore-results stack' },
      h('div', { class: 'search-field' }, search),
      h('div', { class: 'columns' }, field('Source', sourceChooser), field('Topic', topicChooser)), results),
    h('div', { class: 'explore-detail stack' },
      button('← Back to results', () => resetPreview(true), 'quiet explore-back'), preview));
  root.append(heading('Explore', button('Import from link or file', directImport, 'primary')), layout);

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
    clearTimeout(timer);
    const job = listJob.begin();
    items = [];
    visibleCount = 50;
    resetPreview();
    results.replaceChildren(h('p', { role: 'status' }, 'Loading skills…'));
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
    if (!filtered.length) {
      results.replaceChildren(h('div', { class: 'empty' }, h('h2', {}, 'No matching skills'),
        h('p', { class: 'muted' }, 'Change the source, topic, or search term.')));
      return;
    }
    results.replaceChildren(h('p', { class: 'muted', role: 'status' }, filtered.length + ' results'),
      h('ul', { class: 'results' }, filtered.slice(0, visibleCount).map(skill => {
        const row = h('button', { type: 'button', class: 'result-button', 'aria-current': String(selected === skill),
          onclick: () => {
            lastTrigger = row;
            prepare({ repo: skill.repo, path: skill.path, git_ref: skill.git_ref }, skill);
          } }, h('span', { class: 'result-title' }, skill.title || skill.slug),
          h('span', { class: 'result-description' }, skill.description),
          h('span', { class: 'result-meta' }, skill.is_installed ? 'Already in library · ' + skill.repo : skill.repo));
        row.skill = skill;
        return h('li', { class: 'result-row' }, row);
      })));
    if (filtered.length > visibleCount) results.append(button('Show more results', () => {
      const index = visibleCount;
      visibleCount += 50;
      showList();
      results.querySelectorAll('.result-button')[index]?.focus();
    }));
  }
  function resetPreview(restoreFocus = false) {
    previewJob.cancel();
    plan = undefined;
    selected = undefined;
    layout.dataset.detail = 'false';
    preview.replaceChildren(panel(h('h2', {}, 'Preview a skill'), h('p', { class: 'muted' }, 'Select a result to review its instructions and files.')));
    for (const row of results.querySelectorAll('.result-button')) row.setAttribute('aria-current', 'false');
    if (restoreFocus) {
      if (lastTrigger?.isConnected) lastTrigger.focus();
      else search.focus();
    }
  }
  async function prepare(selection, skill) {
    const job = previewJob.begin();
    plan = undefined;
    selected = skill;
    for (const row of results.querySelectorAll('.result-button')) row.setAttribute('aria-current', String(row.skill === skill));
    layout.dataset.detail = 'true';
    preview.replaceChildren(panel(h('h2', { tabindex: '-1' }, skill?.title ?? 'Import review'),
      h('p', { role: 'status' }, 'Loading the exact revision and files…'),
      button('Import skill', () => {}, 'primary', { disabled: true })));
    if (matchMedia('(max-width: 780px), (max-height: 620px)').matches) {
      preview.querySelector('h2').focus();
      window.scrollTo(0, 0);
    }
    try {
      const next = await api.prepare(selection, job.signal);
      if (!job.current()) return;
      plan = next;
      renderPlan(next);
    } catch (error) {
      if (!job.current()) return;
      preview.replaceChildren(panel(h('h2', {}, 'Preview unavailable'), errorText(error.message),
        button('Retry preview', () => prepare(selection, skill)), button('Import skill', () => {}, 'primary', { disabled: true })));
    }
  }
  function renderPlan(review) {
    const proposedName = review.name_exists ? slugify(review.name + '-copy') : review.name;
    const name = h('input', { class: 'input', value: proposedName, maxlength: '64' });
    const targets = Object.entries(PROVIDERS).filter(([id]) => app.snapshot.providers.some(provider => provider.provider === id && provider.is_installed))
      .map(([id, label]) => ({ id, label, control: h('input', { type: 'checkbox' }) }));
    const feedback = h('div', { role: 'status' });
    const apply = button('Import skill', () => {
      if (plan !== review || isApplying) return;
      const dest = slugify(name.value);
      if (!dest || dest.length > 64) {
        feedback.replaceChildren(errorText('Enter a valid destination name.'));
        name.focus();
        return;
      }
      busy(apply, async () => {
        isApplying = true;
        try {
          const response = await api.import(review, dest, targets.filter(target => target.control.checked).map(target => target.id));
          resetPreview();
          isApplying = false;
          await app.read(response.skill.name, response);
          notify('Imported ' + response.skill.title + '.');
        } finally { isApplying = false; }
      }, error => feedback.replaceChildren(errorText(error.message)));
    }, 'primary');
    const files = Object.entries(review.supporting_files ?? {});
    preview.replaceChildren(panel(
      h('h2', {}, review.title),
      h('div', { class: 'summary' }, h('p', {}, review.description), h('p', {}, review.source, ' · ', review.license),
        h('code', { class: 'path' }, review.path), h('code', { class: 'path', title: review.revision }, 'Revision ' + review.revision.slice(0, 12))),
      h('article', { class: 'markdown' }, renderMarkdown(review.instructions_markdown, { title: review.title,
        openFile: path => {
          const file = files.find(([filePath]) => filePath === path);
          if (file) modal(path, h('pre', { class: 'file-code', tabindex: '0' }, file[1]));
          else notify('This file is outside the reviewed bundle.');
        } })),
      disclosure('Supporting files (' + files.length + ')', h('div', { class: 'stack' },
        files.map(([path, text]) => button(path, () => modal(path, h('pre', { class: 'file-code', tabindex: '0' }, text)), 'quiet')),
        (review.skipped_files ?? []).map(path => h('p', { class: 'warning' }, 'Excluded: ' + path)))),
      h('div', { class: 'stack' },
        review.name_exists && h('p', { class: 'warning' }, 'A skill already uses the original name. Choose a unique name.'),
        field('Save as', name),
        h('fieldset', {}, h('legend', {}, 'Deploy after import'),
          targets.length ? h('div', { class: 'checks' }, targets.map(target => h('label', {}, target.control, target.label)))
            : h('p', { class: 'muted' }, 'No providers detected.')),
        feedback, apply)));
  }
  function directImport() {
    const input = h('input', { class: 'input', type: 'text', placeholder: 'GitHub or skills.sh link', autofocus: true });
    const file = h('input', { class: 'input', type: 'file', accept: '.md,.mdc,text/markdown,text/plain' });
    const feedback = h('div', { role: 'status' });
    const review = button('Review skill', () => busy(review, async () => {
      const selectedFile = file.files[0];
      let selection;
      if (selectedFile) {
        if (selectedFile.size > 2 * 1024 * 1024) throw new Error('Choose a file smaller than 2 MB.');
        selection = { file_name: selectedFile.name, raw_markdown: await selectedFile.text() };
      } else if (input.value.trim()) selection = { source: input.value.trim() };
      else throw new Error('Paste a link or choose a file.');
      if (!dialog.open) return;
      dialog.close();
      prepare(selection);
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
    leave() { clearTimeout(timer); listJob.cancel(); resetPreview(); },
    get isApplying() { return isApplying; }
  };
}
