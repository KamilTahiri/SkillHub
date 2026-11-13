import { api } from '../api.js?v=20261113-v10';
import { deployment, installedProviders, PROVIDERS, slugify, tagSlug } from '../state.js?v=20261113-v10';
import { renderMarkdown } from '../markdown.js?v=20261113-v10';
import { h, button, heading, field, disclosure, errorText, modal, notify } from '../ui.js?v=20261113-v10';
import { showProviderPreview } from './transpilerPreview.js?v=20261113-v10';

const blank = () => ({
  title: '', name: '', description: '', when_to_use: '', instructions_markdown: '', tags: [],
  targets: Object.fromEntries(Object.keys(PROVIDERS).map(name => [name, { enabled: true }])), supporting_files: {}
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
    const files = h('div', { class: 'stack' });
    function addFile(path, text) {
      const name = h('input', { class: 'input', value: path, placeholder: 'references/example.md' });
      const content = h('textarea', { rows: '4' }, text);
      const row = h('div', { class: 'panel stack' }, field('Relative path', name), field('File contents', content),
        button('Remove file', () => { row.remove(); edited(); }, 'danger small'));
      row.readFile = () => [name.value, content.value];
      row.writeFile = (p, c) => { name.value = p; content.value = c; };
      files.append(row);
      return name;
    }
    for (const [path, text] of Object.entries(baseline.supporting_files ?? {})) addFile(path, text);
    const deploymentSection = disclosure('Deployment and provider settings',
      h('div', { class: 'stack' }, h('fieldset', {}, h('legend', {}, 'Providers'),
        h('div', { class: 'checks' }, Object.entries(deploy).map(([key, item]) => h('label', {}, item.control,
          item.label + (snapshot.providers.some(provider => provider.provider === key && provider.is_installed) ? '' : ' · not detected'))))),
        field('Codex concise summary', concise), field('Claude allowed tools', tooling, 'Separate tool names with commas.'),
        field('Cursor file globs', globs, 'Separate glob patterns with commas.'), h('label', { class: 'actions' }, always, 'Always apply Cursor rule')));
    const fileSection = disclosure('Supporting files',
      h('div', { class: 'stack' }, files, button('Add file', () => { addFile('', '').focus(); edited(); })));
    const saveButton = h('button', { type: 'submit', class: 'btn primary' }, 'Save skill');
    const preview = button('Provider output', () => {
      try { showProviderPreview(readDraft(true), { providers: snapshot.providers }); }
      catch (error) { feedback.replaceChildren(errorText(error.message)); }
    });
    const cancel = button('Cancel', () => app.cancelEditor(returnKey));
    const draftStatus = h('span', { class: 'muted', role: 'status' });
    const markdownPreview = button('Preview Markdown', () => modal('Markdown preview',
      h('article', { class: 'markdown' }, renderMarkdown(instructions.value, { title: formTitle.value }))));
    form.append(h('div', { class: 'editor-actions spread' }, h('div', { class: 'actions' }, saveButton, preview, cancel), draftStatus),
      feedback, field('Title', formTitle), h('div', { class: 'columns' }, field('Description', summary), field('When to use', trigger)),
      field('Instructions', instructions), markdownPreview, field('Tags', tags, 'Separate tags with commas.'), suggestionList, deploymentSection, fileSection);
    const list = text => text.split(',').map(item => item.trim()).filter(Boolean);
    const raw = () => ({
      title: formTitle.value, description: summary.value, when_to_use: trigger.value, instructions_markdown: instructions.value,
      tags: tags.value, targets: Object.fromEntries(Object.entries(deploy).map(([key, item]) => [key, item.control.checked])),
      summary_short: concise.value, tools: tooling.value, globs: globs.value, always: always.checked,
      files: Array.from(files.children, child => child.readFile())
    });
    const restore = draft => {
      formTitle.value = draft.title; summary.value = draft.description; trigger.value = draft.when_to_use;
      instructions.value = draft.instructions_markdown; tags.value = draft.tags; concise.value = draft.summary_short;
      tooling.value = draft.tools; globs.value = draft.globs; always.checked = draft.always;
      for (const key of Object.keys(deploy)) deploy[key].control.checked = draft.targets[key];
      files.replaceChildren();
      for (const [path, text] of draft.files) addFile(path, text);
    };
    readDraft = validate => {
      const values = raw();
      const name = baseline.revision ? baseline.name : slugify(values.title);
      if (validate && (!values.title.trim() || !name || name.length > 64 || windowsDevice.test(name))) {
        formTitle.focus();
        throw new Error('Enter a title that creates a valid skill name of up to 64 characters.');
      }
      const bundle = Object.create(null);
      const paths = new Set();
      for (const [path, contents] of values.files) {
        const safe = validatePath(path);
        if (paths.has(safe.toLowerCase())) throw new Error('Supporting files need unique paths.');
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
      dirty = JSON.stringify(raw()) !== initial;
      draftStatus.textContent = dirty ? 'Draft · unsaved changes' : '';
      clearTimeout(storageTimer);
      storageTimer = setTimeout(() => draftStorage(draftKey, dirty ? { revision: baseline.revision ?? 0, fields: raw() } : null), 100);
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
          button('Recover draft text', () => modal('Draft from an earlier revision', h('pre', { class: 'file-code' }, restored.fields.instructions_markdown))));
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
