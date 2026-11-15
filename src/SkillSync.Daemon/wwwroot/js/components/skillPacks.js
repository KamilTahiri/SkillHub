import { api } from '../api.js';
import { icon } from '../icons.js';
import { h, button, modal, busy, notify, errorText, download, bundleBadge, describeCompanions } from '../ui.js';

/** Downloads a .skillpack.zip of the named skills, or of every personal skill when `names` is empty. */
export async function exportPack(names = []) {
  const { name, blob } = await api.exportPack(names);
  download(name, blob, 'application/zip');
  notify('Saved ' + name + ' to your Downloads folder.', { tone: 'success' });
}

/** Lets the user tick personal skills and export them as one pack. */
export function exportPicker(app) {
  const skills = app.snapshot.skills.filter(skill => skill.source_category === 'user' && !skill.is_catalog_only)
    .sort((a, b) => (a.title || a.name).localeCompare(b.title || b.name));
  const feedback = h('div', { role: 'status' });
  const boxes = skills.map(skill => h('input', { type: 'checkbox', value: skill.name, checked: true }));
  const count = h('span', { class: 'muted' });
  const update = () => { count.textContent = boxes.filter(box => box.checked).length + ' of ' + boxes.length + ' selected'; };
  boxes.forEach(box => box.addEventListener('change', update));
  update();
  const all = (checked) => { boxes.forEach(box => { box.checked = checked; }); update(); };
  const save = button([icon('download'), 'Export Skill Pack'], () => busy(save, async () => {
    const names = boxes.filter(box => box.checked).map(box => box.value);
    if (!names.length) throw new Error('Tick at least one skill.');
    await exportPack(names);
    dialog.close();
  }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
  const dialog = modal('Export skills as a pack', h('div', { class: 'stack' },
    h('p', { class: 'muted' }, 'A .skillpack.zip holds each SKILL.md, every companion file and a skillpack.json manifest. Import it on any PC with SkillHub.'),
    h('div', { class: 'actions' }, button('Select all', () => all(true), 'quiet small'), button('Select none', () => all(false), 'quiet small'), count),
    skills.length ? h('ul', { class: 'pack-pick-list' }, skills.map((skill, index) => h('li', {},
      h('label', { class: 'pack-pick' }, boxes[index], h('span', {}, h('strong', {}, skill.title || skill.name),
        h('small', { class: 'muted' }, ' ' + skill.name)), bundleBadge(skill.file_count ?? 0, { paths: skill.supporting_paths })))))
      : h('p', { class: 'muted' }, 'You have no personal skills yet.'),
    feedback), save);
}

const fileToBase64 = file => new Promise((resolve, reject) => {
  const reader = new FileReader();
  reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '');
  reader.onerror = () => reject(new Error('The file could not be read.'));
  reader.readAsDataURL(file);
});

/** Reviews a .skillpack.zip: every skill, its files, name conflicts and executable content, then imports the chosen ones. */
export async function reviewPack(file, app) {
  if (file.size > 20 * 1024 * 1024) throw new Error('Skill packs can be at most 20 MB.');
  const preview = await api.previewPack(file.name, await fileToBase64(file));
  const feedback = h('div', { role: 'status' });
  const rows = preview.skills.map(skill => {
    const choice = h('select', { class: 'select', 'aria-label': 'What to do with ' + skill.title },
      h('option', { value: 'import' }, skill.name_exists ? 'Import as a new skill' : 'Import'),
      skill.name_exists && h('option', { value: 'replace' }, 'Replace my current skill'),
      h('option', { value: 'skip' }, 'Skip'));
    choice.value = skill.same_as_existing ? 'skip' : 'import';
    const rename = h('input', { class: 'input', type: 'text', value: skill.name_exists ? skill.suggested_name : skill.name,
      'aria-label': 'Save ' + skill.title + ' as' });
    const renameField = h('label', { class: 'field pack-rename' }, h('span', {}, 'Save as'), rename);
    const sync = () => { renameField.hidden = choice.value !== 'import'; };
    choice.addEventListener('change', sync);
    sync();
    const risky = skill.scripts.filter(script => script.concerns.length);
    const row = h('li', { class: 'pack-skill', 'data-name': skill.name },
      h('div', { class: 'pack-skill-head' }, h('strong', {}, skill.title),
        bundleBadge(skill.files.length, { paths: skill.files }),
        skill.same_as_existing ? h('span', { class: 'status-badge small ok' }, 'Already identical in your library')
          : skill.name_exists && h('span', { class: 'status-badge small warn' }, 'Name in use: ' + skill.name)),
      skill.description && h('p', { class: 'muted' }, skill.description),
      skill.scripts.length > 0 && h('p', { class: risky.length ? 'warning' : 'muted' }, icon('info'),
        ' ' + skill.scripts.length + (skill.scripts.length === 1 ? ' item' : ' items') + ' an agent may run (' +
        skill.scripts.map(script => script.path).slice(0, 4).join(', ') + ')' + (risky.length ? ' · ' + risky.length + ' with risky patterns' : '')),
      h('details', { class: 'disclosure' }, h('summary', {}, 'Read SKILL.md' + (skill.files.length ? ' and file list' : '')),
        skill.files.length > 0 && h('p', { class: 'muted' }, 'SKILL.md + ' + describeCompanions(skill.files) + ': ' + skill.files.join(', ')),
        h('pre', { class: 'file-code' }, skill.instructions_markdown)),
      h('div', { class: 'pack-skill-actions' }, choice, renameField));
    row.choice = () => ({ name: skill.name, action: choice.value, new_name: choice.value === 'import' ? rename.value.trim() : null });
    return row;
  });
  const keep = h('input', { type: 'checkbox' });
  const apply = button('Import selected skills', () => busy(apply, async () => {
    const choices = rows.map(row => row.choice());
    if (choices.every(choice => choice.action === 'skip')) throw new Error('Every skill is set to Skip.');
    const result = await api.applyPack(preview.pack_id, choices, keep.checked);
    dialog.close();
    await app.refresh();
    notify([result.imported.length && result.imported.length + ' imported', result.replaced.length && result.replaced.length + ' replaced',
      result.skipped.length && result.skipped.length + ' skipped'].filter(Boolean).join(' · ') + '.', { tone: 'success' });
    const first = result.imported[0] ?? result.replaced[0];
    if (first) app.read(first);
  }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
  const dialog = modal('Review skill pack', h('div', { class: 'stack pack-review' },
    h('p', { class: 'muted' }, preview.file_name + ' · ' + preview.skills.length + (preview.skills.length === 1 ? ' skill' : ' skills') +
      (preview.created_by ? ' · made with ' + preview.created_by : '') +
      (preview.created_at ? ' · ' + new Date(preview.created_at).toLocaleDateString() : '')),
    h('ul', { class: 'pack-skills' }, rows),
    preview.skipped.length > 0 && h('details', { class: 'disclosure' }, h('summary', {}, preview.skipped.length + ' entries will not be imported'),
      h('ul', {}, preview.skipped.map(item => h('li', { class: 'path' }, item)))),
    h('label', { class: 'actions' }, keep, 'Keep the pack’s sync settings (otherwise imported skills stay in SkillHub until you turn sync on)'),
    feedback), apply);
  return dialog;
}
