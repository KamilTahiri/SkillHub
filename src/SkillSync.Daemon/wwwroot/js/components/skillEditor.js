import { api } from '../api.js';
import { deployment, installedProviders, PROVIDERS, slugify, tagSlug } from '../state.js';
import { renderMarkdown } from '../markdown.js';
import { h, button, heading, field, disclosure, errorText, modal, notify } from '../ui.js';
import { showProviderPreview } from './transpilerPreview.js';

const blank = () => ({
  title: '', name: '', description: '', when_to_use: '', instructions_markdown: '', tags: [],
  targets: Object.fromEntries(Object.keys(PROVIDERS).map(name => [name, { enabled: false }])), supporting_files: {}
});
const supported = new Set(['scripts', 'resources', 'references', 'examples', 'assets', 'agents']);
const windowsDevice = /^(?:con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])$/i;

function validatePath(value) {
  const path = value.trim().replaceAll('\\', '/');
  const parts = path.split('/');
  if (parts.length < 2 || !supported.has(parts[0].toLowerCase()) ||
    parts.some(part => !part || part.length > 255 || /[. ]$/.test(part) ||
      /[<>:"|?*\u0000-\u001f]/.test(part) || windowsDevice.test(part.split('.')[0].trimEnd()))) {
    throw new Error('Use a safe relative file path under a supported bundle folder.');
  }
  return path;
}

function draftStorage(key, value) {
  try {
    if (value === undefined) return JSON.parse(localStorage.getItem(key) ?? 'null');
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {
    return null;
  }
}

export function createEditor(root, app) {
  let baseline;
  let cloneId;
  let returnKey;
  let draftKey;
  let readDraft;
  let initial;
  let isSaving = false;
  let dirty = false;
  let storageTimer;

  function start(detail, snapshot, clone = false) {
    root.classList.add('reading');
    baseline = structuredClone(detail?.skill ?? blank());
    cloneId = clone ? baseline.catalog_id : null;
    returnKey = detail ? (baseline.is_catalog_only ? baseline.catalog_id : baseline.name) : null;
    if (clone) {
      baseline.title += ' Copy';
      baseline.revision = 0;
      Object.values(baseline.targets).forEach(target => { target.enabled = false; });
    }
    draftKey = 'skillhub:draft:' + (cloneId ? 'clone:' + cloneId : returnKey ?? 'new');
    const restored = draftStorage(draftKey);
    const form = h('form', { class: 'editor-form', novalidate: true, onsubmit: event => { event.preventDefault(); save(); } });
    const feedback = h('div', { role: 'status' });
    const formTitle = h('input', { class: 'input', value: baseline.title, required: true, maxlength: '128' });
    const summary = h('textarea', { rows: '2', maxlength: '1024' }, baseline.description ?? '');
    const trigger = h('textarea', { rows: '2', maxlength: '1024' }, baseline.when_to_use ?? '');
    const instructions = h('textarea', { class: 'instructions', rows: '10' }, baseline.instructions_markdown ?? '');
    const tags = h('input', { class: 'input', value: (baseline.tags ?? []).join(', '), list: 'editor-tag-suggestions' });
    const suggestionList = h('datalist', { id: 'editor-tag-suggestions' },
      snapshot.tags.map(tag => h('option', { value: tag.tag })));
    const deploy = Object.fromEntries(Object.entries(PROVIDERS).map(([key, label]) => [key, {
      label, control: h('input', { type: 'checkbox', checked: baseline.targets?.[key]?.enabled !== false })
    }]));
    const tooling = h('input', { class: 'input', value: (baseline.targets?.claude?.allowed_tools ?? []).join(', ') });
    const globs = h('input', { class: 'input', value: (baseline.targets?.cursor?.globs ?? []).join(', ') });
    const always = h('input', { type: 'checkbox', checked: baseline.targets?.cursor?.always_apply === true });
    const concise = h('input', { class: 'input', value: baseline.summary_short ?? '', maxlength: '1024' });
    const bundleDraft = Object.entries(baseline.supporting_files ?? {});
    const files = h('div', { class: 'stack' });
    const fileList = h('div', { class: 'file-selector stack', 'aria-label': 'Supporting files' });
    const fileEditor = h('div', { class: 'stack' });
    let selectedFile = 0;
    function showFiles(focus = false) {
      fileList.replaceChildren(...bundleDraft.map(([path], index) => button(path || 'Untitled file', () => {
        selectedFile = index;
        showFiles(true);
      }, 'quiet small', { 'aria-pressed': String(index === selectedFile) })));
      fileEditor.replaceChildren();
      const file = bundleDraft[selectedFile];
      if (!file) {
        fileEditor.append(h('p', { class: 'muted' }, 'No supporting files.'));
        return;
      }
      const pathError = h('small', { class: 'error', role: 'status' });
      const name = h('input', { class: 'input', value: file[0], placeholder: 'references/example.md', oninput: event => {
        file[0] = event.target.value;
        fileList.children[selectedFile].textContent = file[0] || 'Untitled file';
        checkPath();
      } });
      function checkPath() {
        try {
          const path = validatePath(file[0]).toLowerCase();
          if (bundleDraft.some(other => other !== file && other[0].trim().replaceAll('\\', '/').toLowerCase() === path))
            throw new Error('Supporting files need unique paths.');
          pathError.textContent = '';
          name.removeAttribute('aria-invalid');
        } catch (error) {
          pathError.textContent = error.message;
          name.setAttribute('aria-invalid', 'true');
        }
      }
      const content = h('textarea', { rows: '8', oninput: event => { file[1] = event.target.value; } }, file[1]);
      fileEditor.append(field('Relative path', name), pathError, field('File contents', content),
        button('Remove file', () => {
          bundleDraft.splice(selectedFile, 1);
          selectedFile = Math.max(0, Math.min(selectedFile, bundleDraft.length - 1));
          showFiles();
          edited();
        }, 'quiet danger small'));
      if (file[0]) checkPath();
      if (focus) name.focus();
    }
    files.append(h('div', { class: 'file-workspace' }, fileList, fileEditor));
    showFiles();
    const preview = button('Provider output', () => {
      try { showProviderPreview(readDraft(true), { providers: snapshot.providers }); }
      catch (error) { feedback.replaceChildren(errorText(error.message)); }
    });
    const deploymentSection = disclosure('Deployment and provider settings',
      h('div', { class: 'stack' }, h('div', { class: 'spread' }, h('h3', {}, 'Deployment'), preview),
        h('fieldset', {}, h('legend', {}, 'Providers'),
        h('div', { class: 'checks' }, Object.entries(deploy).map(([key, item]) => h('label', {}, item.control,
          item.label + (snapshot.providers.some(provider => provider.provider === key && provider.is_installed) ? '' : ' · not detected'))))),
        field('Codex concise summary', concise), field('Claude allowed tools', tooling, 'Separate tool names with commas.'),
        field('Cursor file globs', globs, 'Separate glob patterns with commas.'), h('label', { class: 'actions' }, always, 'Always apply Cursor rule')));
    const fileSection = disclosure('Supporting files', files, button('Add file', () => {
      bundleDraft.push(['', '']);
      selectedFile = bundleDraft.length - 1;
      showFiles(true);
      edited();
    }, 'small'));
    const saveButton = h('button', { type: 'submit', class: 'btn primary' }, 'Save skill');
    const cancel = button('Cancel', () => app.cancelEditor(returnKey));
    const draftStatus = h('span', { class: 'muted', role: 'status' });
    const markdownPreview = h('article', { class: 'markdown', hidden: true, 'aria-label': 'Markdown preview' });
    const write = button('Write', () => showMarkdown(false), 'quiet small', { 'aria-pressed': 'true' });
    const previewMarkdown = button('Preview', () => showMarkdown(true), 'quiet small', { 'aria-pressed': 'false' });
    function showMarkdown(show) {
      instructions.hidden = show;
      markdownPreview.hidden = !show;
      write.setAttribute('aria-pressed', String(!show));
      previewMarkdown.setAttribute('aria-pressed', String(show));
      if (show) markdownPreview.replaceChildren(renderMarkdown(instructions.value, { title: formTitle.value }));
    }
    const titleError = h('small', { class: 'error', role: 'status' });
    function checkTitle() {
      const slug = baseline.revision ? baseline.name : slugify(formTitle.value);
      const valid = formTitle.value.trim() && slug && slug.length <= 64 && !windowsDevice.test(slug);
      titleError.textContent = valid ? '' : 'Enter a title with a valid skill name of up to 64 characters.';
      formTitle.setAttribute('aria-invalid', String(!valid));
      return Boolean(valid);
    }
    formTitle.addEventListener('input', checkTitle);
    const scope = h('small', { class: 'muted' });
    function updateScope() {
      const targets = installedProviders(snapshot.providers).filter(id => deploy[id].control.checked);
      scope.textContent = targets.length ? 'Deploy to ' + targets.map(id => PROVIDERS[id]).join(', ') : 'Save to My Skills only';
    }
    updateScope();
    const headingRow = h('div', { class: 'editor-actions stack' },
      h('div', { class: 'spread' }, field('Title', formTitle), h('div', { class: 'actions' }, saveButton, cancel)),
      titleError, h('div', { class: 'spread' }, scope, draftStatus));
    form.append(headingRow, feedback, h('div', { class: 'columns' }, field('Description', summary), field('When to use', trigger)),
      h('div', { class: 'spread' }, h('h2', {}, 'Instructions'), h('div', { class: 'actions' }, write, previewMarkdown)),
      h('div', { class: 'field' }, instructions, markdownPreview), field('Tags', tags, 'Separate tags with commas.'),
      suggestionList, deploymentSection, fileSection);
    instructions.setAttribute('aria-label', 'Instructions');
    const list = text => text.split(',').map(item => item.trim()).filter(Boolean);
    const raw = () => ({
      title: formTitle.value, description: summary.value, when_to_use: trigger.value, instructions_markdown: instructions.value,
      tags: tags.value, targets: Object.fromEntries(Object.entries(deploy).map(([key, item]) => [key, item.control.checked])),
      summary_short: concise.value, tools: tooling.value, globs: globs.value, always: always.checked,
      files: bundleDraft.map(file => [...file])
    });
    const restore = draft => {
      formTitle.value = draft.title; summary.value = draft.description; trigger.value = draft.when_to_use;
      instructions.value = draft.instructions_markdown; tags.value = draft.tags; concise.value = draft.summary_short;
      tooling.value = draft.tools; globs.value = draft.globs; always.checked = draft.always;
      for (const key of Object.keys(deploy)) deploy[key].control.checked = draft.targets[key];
      bundleDraft.splice(0, bundleDraft.length, ...draft.files);
      selectedFile = 0;
      showFiles();
      updateScope();
    };
    readDraft = validate => {
      const values = raw();
      const name = baseline.revision ? baseline.name : slugify(values.title);
      if (validate && !checkTitle()) {
        formTitle.focus();
        throw new Error('Enter a title that creates a valid skill name of up to 64 characters.');
      }
      const bundle = Object.create(null);
      const paths = new Set();
      for (const [index, [path, contents]] of values.files.entries()) {
        let safe;
        try {
          safe = validatePath(path);
          if (paths.has(safe.toLowerCase())) throw new Error('Supporting files need unique paths.');
        } catch (error) {
          selectedFile = index;
          fileSection.open = true;
          showFiles(true);
          throw error;
        }
        paths.add(safe.toLowerCase());
        bundle[safe] = contents;
      }
      const targetData = structuredClone(baseline.targets ?? {});
      for (const key of Object.keys(deploy)) targetData[key] = { ...targetData[key], enabled: values.targets[key] };
      targetData.claude.allowed_tools = list(values.tools);
      targetData.cursor.globs = list(values.globs);
      targetData.cursor.always_apply = values.always;
      return {
        ...baseline, name, title: values.title.trim(), description: values.description, when_to_use: values.when_to_use,
        instructions_markdown: values.instructions_markdown, tags: [...new Set(list(values.tags).map(tagSlug))],
        summary_short: values.summary_short, targets: targetData, supporting_files: bundle
      };
    };
    initial = JSON.stringify(raw());
    dirty = false;
    isSaving = false;
    function edited() {
      updateScope();
      dirty = JSON.stringify(raw()) !== initial;
      draftStatus.textContent = dirty ? 'Draft · unsaved changes' : '';
      clearTimeout(storageTimer);
      storageTimer = setTimeout(() => {
        const stored = draftStorage(draftKey, dirty ? { revision: baseline.revision ?? 0, fields: raw() } : null);
        if (dirty && stored === null) draftStatus.textContent = 'Draft · automatic recovery unavailable';
      }, 100);
    }
    form.addEventListener('input', edited);
    async function save() {
      if (isSaving) return;
      feedback.replaceChildren();
      let draft;
      try { draft = readDraft(true); }
      catch (error) { feedback.replaceChildren(errorText(error.message)); return; }
      isSaving = true;
      saveButton.disabled = preview.disabled = cancel.disabled = true;
      draftStatus.textContent = 'Saving…';
      try {
        const detail = await api.save(draft, cloneId);
        clearTimeout(storageTimer);
        draftStorage(draftKey, null);
        dirty = false;
        isSaving = false;
        await app.read(detail.skill.name, detail);
        const sync = deployment(detail.skill, installedProviders(snapshot.providers));
        notify(sync.key === 'issue' ? 'Saved. A deployment needs attention.' : 'Skill saved.');
      } catch (error) {
        feedback.replaceChildren(errorText(error.message));
        draftStatus.textContent = 'Draft · save failed';
      } finally {
        isSaving = false;
        saveButton.disabled = preview.disabled = cancel.disabled = false;
      }
    }
    root.replaceChildren(heading(clone ? 'Customize skill' : detail ? 'Edit skill' : 'New skill'), form);
    if (restored?.fields) {
      if (restored.revision === (baseline.revision ?? 0)) {
        restore(restored.fields);
        edited();
        feedback.textContent = 'Recovered your unsaved draft.';
      } else {
        feedback.replaceChildren(h('p', { class: 'warning' }, 'This skill changed after your draft was saved.'),
          button('Compare saved draft', () => {
            const current = raw();
            const labels = { title: 'Title', description: 'Description', when_to_use: 'When to use',
              instructions_markdown: 'Instructions', tags: 'Tags', summary_short: 'Short description',
              tools: 'Tools', globs: 'File patterns', always: 'Always apply', targets: 'Destinations', files: 'Supporting files' };
            const display = value => typeof value === 'string' ? value : JSON.stringify(value, null, 2);
            const changed = Object.keys(labels).filter(key => display(current[key]) !== display(restored.fields[key]));
            const comparison = h('div', { class: 'stack' }, changed.map(key => disclosure(labels[key],
              h('div', { class: 'columns review-compare' },
                h('section', {}, h('h3', {}, 'Current'), h('pre', { class: 'file-code', tabindex: '0' }, display(current[key]))),
                h('section', {}, h('h3', {}, 'Saved draft'), h('pre', { class: 'file-code', tabindex: '0' }, display(restored.fields[key])))))));
            if (!changed.length) comparison.append(h('p', {}, 'The draft matches this revision.'));
            const recover = button('Use draft in editor', () => {
              restore(restored.fields);
              edited();
              dialog.close();
              feedback.textContent = 'Draft loaded. Save to apply these changes.';
            }, 'primary');
            const dialog = modal('Draft from an earlier revision', comparison, changed.length ? recover : null);
          }));
      }
    }
    root.saveDraft = save;
  }
  return {
    start,
    get isDirty() { return dirty; },
    get isSaving() { return isSaving; },
    discard() { clearTimeout(storageTimer); draftStorage(draftKey, null); dirty = false; },
    save() { root.saveDraft?.(); }
  };
}
