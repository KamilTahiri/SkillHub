import { h } from '../ui.js';
import { icon } from '../icons.js';
import { skillKey } from '../state.js';

/**
 * Scores how well a query matches a label as a subsequence. Matches at word starts and consecutive
 * characters score higher. Returns -1 when the query does not match.
 */
export function fuzzyScore(query, label) {
  const q = query.trim().toLowerCase();
  if (!q) return 0;
  const text = label.toLowerCase();
  const direct = text.indexOf(q);
  if (direct >= 0) return 1000 - direct * 4 - (text.length - q.length) + (direct === 0 || /[\s/:#-]/.test(text[direct - 1]) ? 200 : 0);
  let score = 0;
  let position = -1;
  let streak = 0;
  for (const character of q) {
    if (character === ' ') continue;
    const next = text.indexOf(character, position + 1);
    if (next < 0) return -1;
    streak = next === position + 1 ? streak + 1 : 0;
    score += 10 + streak * 6 + (next === 0 || /[\s/:#-]/.test(text[next - 1]) ? 14 : 0) - Math.min(next - position, 12);
    position = next;
  }
  return score;
}

let open = null;

/** Opens the Raycast-style command palette. Commands come from the app; skills come from the current snapshot. */
export function openPalette(app, commands) {
  if (open) { open.querySelector('input')?.focus(); return; }
  const opener = document.activeElement;
  const input = h('input', { class: 'palette-input', type: 'text', placeholder: 'Search skills and commands…', spellcheck: 'false',
    role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'palette-list', 'aria-autocomplete': 'list', 'aria-label': 'Command palette' });
  const list = h('ul', { class: 'palette-list', id: 'palette-list', role: 'listbox', 'aria-label': 'Results' });
  const dialog = h('dialog', { class: 'palette', 'aria-label': 'Command palette' },
    h('div', { class: 'palette-search' }, icon('search'), input, h('kbd', {}, 'Esc')),
    list,
    h('footer', { class: 'palette-foot' }, h('span', {}, h('kbd', {}, '↑'), h('kbd', {}, '↓'), ' to move'),
      h('span', {}, h('kbd', {}, '↵'), ' to run'), h('span', {}, h('kbd', {}, 'Ctrl'), h('kbd', {}, 'K'), ' to toggle')));
  let active = 0;
  let entries = [];

  const skillEntries = () => app.snapshot.skills.filter(skill => skill.source_category !== 'system').map(skill => ({
    id: 'skill:' + skillKey(skill), group: 'Skills', label: skill.title || skill.name, hint: skill.name,
    icon: 'library', keywords: (skill.description ?? '') + ' ' + (skill.tags ?? []).join(' '),
    run: () => app.read(skillKey(skill))
  }));

  function render() {
    const query = input.value;
    const scored = [...commands, ...skillEntries()].map(entry => {
      const score = Math.max(fuzzyScore(query, entry.label), fuzzyScore(query, entry.hint ?? '') - 40,
        query.trim().length > 2 ? fuzzyScore(query, entry.keywords ?? '') - 300 : -1);
      return { entry, score };
    }).filter(item => !query.trim() ? item.entry.group !== 'Skills' || item.entry.recent : item.score >= 0);
    if (query.trim()) scored.sort((a, b) => b.score - a.score);
    entries = scored.slice(0, query.trim() ? 40 : 30).map(item => item.entry);
    if (!query.trim()) entries = [...entries, ...skillEntries().slice(0, 6)];
    active = Math.min(active, Math.max(entries.length - 1, 0));
    let lastGroup = '';
    const nodes = [];
    entries.forEach((entry, index) => {
      if (entry.group !== lastGroup) {
        nodes.push(h('li', { class: 'palette-group', role: 'presentation' }, entry.group));
        lastGroup = entry.group;
      }
      nodes.push(h('li', { class: 'palette-item', role: 'option', id: 'palette-' + index, 'aria-selected': String(index === active),
        onmousemove: () => { if (active !== index) { active = index; highlight(); } },
        onclick: () => run(index) },
        h('span', { class: 'palette-icon', 'data-icon': entry.icon }, icon(entry.icon ?? 'palette')),
        h('span', { class: 'palette-label' }, entry.label),
        entry.hint && h('span', { class: 'palette-hint' }, entry.hint),
        entry.shortcut && h('kbd', {}, entry.shortcut)));
    });
    if (!entries.length) nodes.push(h('li', { class: 'palette-empty', role: 'presentation' }, 'No matching skills or commands'));
    list.replaceChildren(...nodes);
    highlight();
  }

  function highlight() {
    for (const node of list.querySelectorAll('.palette-item')) node.setAttribute('aria-selected', String(node.id === 'palette-' + active));
    input.setAttribute('aria-activedescendant', entries.length ? 'palette-' + active : '');
    list.querySelector('#palette-' + active)?.scrollIntoView({ block: 'nearest' });
  }

  function run(index) {
    const entry = entries[index];
    if (!entry) return;
    dialog.close();
    // Let the dialog finish closing before the command moves focus or opens another dialog.
    setTimeout(() => entry.run(), 0);
  }

  input.addEventListener('input', () => { active = 0; render(); });
  dialog.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!entries.length) return;
      active = (active + (event.key === 'ArrowDown' ? 1 : -1) + entries.length) % entries.length;
      highlight();
    } else if (event.key === 'Enter') {
      event.preventDefault();
      run(active);
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      dialog.close();
    }
  });
  dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
  dialog.addEventListener('close', () => {
    dialog.remove();
    open = null;
    if (opener?.isConnected && document.activeElement === document.body) opener.focus({ preventScroll: true });
  }, { once: true });
  document.body.append(dialog);
  open = dialog;
  dialog.showModal();
  render();
  input.focus();
  return dialog;
}
