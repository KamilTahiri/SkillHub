import { api } from './api.js';
import { skillKey } from './state.js';
import { h, button, modal, busy, notify, errorText } from './ui.js';

// Skill badge icons: simple 24px outline glyphs drawn for SkillHub. They use currentColor, so they follow the theme
// and the badge tint exactly like the two-letter monogram they replace.
const NS = 'http://www.w3.org/2000/svg';
const p = d => ['path', { d }];
const c = (cx, cy, r) => ['circle', { cx, cy, r }];
export const BADGE_ICONS = {
  pencil: { label: 'Edit / writing', shapes: [p('M4 20h4L19 9l-4-4L4 16v4Zm9-13 4 4')] },
  terminal: { label: 'Terminal', shapes: [p('M4 5h16v14H4zM7.5 9.5 10 12l-2.5 2.5M12.5 15H16')] },
  code: { label: 'Code', shapes: [p('m8 7-5 5 5 5m8-10 5 5-5 5M13.5 4.5l-3 15')] },
  'git-branch': { label: 'Git', shapes: [c(6, 5.5, 2), c(6, 18.5, 2), c(18, 7, 2), p('M6 7.5v9M18 9a6 6 0 0 1-6 6H8')] },
  cube: { label: '3D / cube', shapes: [p('M12 3 4 7.5v9L12 21l8-4.5v-9L12 3ZM4 7.5l8 4.5 8-4.5M12 12v9')] },
  gamepad: { label: 'Games', shapes: [p('M7.5 8h9a4.5 4.5 0 0 1 4.5 4.5v1a3.5 3.5 0 0 1-6.2 2.2L14 15h-4l-.8.7A3.5 3.5 0 0 1 3 13.5v-1A4.5 4.5 0 0 1 7.5 8ZM7.5 10.5v3M6 12h3M15.5 11.5h.01M17.5 13.5h.01')] },
  globe: { label: 'Web', shapes: [c(12, 12, 9), p('M3 12h18M12 3c2.4 2.5 3.6 5.5 3.6 9s-1.2 6.5-3.6 9c-2.4-2.5-3.6-5.5-3.6-9S9.6 5.5 12 3Z')] },
  shield: { label: 'Security', shapes: [p('M12 3 5 6v5.5c0 4.2 2.9 7.8 7 9.5 4.1-1.7 7-5.3 7-9.5V6l-7-3ZM9 12l2 2 4-4')] },
  database: { label: 'Database', shapes: [['ellipse', { cx: 12, cy: 6, rx: 7, ry: 2.8 }], p('M5 6v12c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8V6M5 12c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8')] },
  sparkles: { label: 'AI', shapes: [p('M10 3.5 11.6 8l4.4 1.6-4.4 1.6L10 15.7l-1.6-4.5L4 9.6 8.4 8 10 3.5ZM18 14l.8 2.2 2.2.8-2.2.8L18 20l-.8-2.2-2.2-.8 2.2-.8L18 14Z')] },
  wrench: { label: 'Tools', shapes: [p('M15 3.6a4.6 4.6 0 0 0-4.3 6.1L3.8 16.6a1.9 1.9 0 0 0 2.7 2.7l6.9-6.9a4.6 4.6 0 0 0 6.1-4.3l-2.8 1.6-2.4-1.4V5.6L17 4.1a4.6 4.6 0 0 0-2-.5Z')] },
  book: { label: 'Docs', shapes: [p('M5 5.5A2.5 2.5 0 0 1 7.5 3H19v14H7.5A2.5 2.5 0 0 0 5 19.5v-14ZM5 19.5A2.5 2.5 0 0 0 7.5 22H19v-5M9 7.5h6')] },
  layers: { label: 'Layers', shapes: [p('m12 3 9 5-9 5-9-5 9-5ZM3 12l9 5 9-5M3 16l9 5 9-5')] },
  palette: { label: 'Design / UI', shapes: [p('M12 3a9 9 0 1 0 0 18c1 0 1.7-.8 1.7-1.7 0-.4-.2-.8-.5-1.1a1.7 1.7 0 0 1 1.3-2.9H17a4 4 0 0 0 4-4C21 6.8 17 3 12 3Z'), c(7.5, 11.5, 1.2), c(10, 7.5, 1.2), c(15, 7.5, 1.2)] },
  layout: { label: 'Layout', shapes: [p('M4 4h16v16H4zM4 9h16M9 9v11')] },
  bug: { label: 'Debugging', shapes: [p('M9 7.5a3 3 0 0 1 6 0V9H9V7.5ZM7 9h10v5a5 5 0 0 1-10 0V9ZM12 9v10M3 13h4m10 0h4M4.5 7.5 7 9m12.5-1.5L17 9M4.5 19.5 7 17m12.5 2.5L17 17')] },
  rocket: { label: 'Launch / deploy', shapes: [p('M14 15.5 8.5 10C10.5 5.5 14 3.5 20.5 3.5 20.5 10 18.5 13.5 14 15.5ZM8.5 10 5 10.5 3 13l4 .7M14 15.5l-.5 3.5-2.5 2-.7-4M5.5 18.5c.3-1.6 1.2-2.6 2.5-3'), c(15.5, 8.5, 1.4)] },
  folder: { label: 'Files', shapes: [p('M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z')] },
  chart: { label: 'Data / charts', shapes: [p('M4 4v16h16M8 16v-4m4 4V8m4 8v-6')] },
  cloud: { label: 'Cloud', shapes: [p('M7 18.5a4.5 4.5 0 0 1-.6-9 6 6 0 0 1 11.6 1.6 3.7 3.7 0 0 1-.5 7.4H7Z')] },
  lock: { label: 'Privacy', shapes: [p('M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3M12 14.5v2')] },
  chat: { label: 'Chat / writing', shapes: [p('M5 5h14v10H10l-4 4v-4H5V5ZM8.5 9h7M8.5 12h4')] },
  zap: { label: 'Automation', shapes: [p('M13 3 5 13.5h6L10 21l8-10.5h-6L13 3Z')] },
  flask: { label: 'Testing', shapes: [p('M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.8 3h10.4a2 2 0 0 0 1.8-3l-5-9V3M7.3 15h9.4')] },
  puzzle: { label: 'Plugins', shapes: [p('M5 5h4a2 2 0 1 1 4 0h4v4a2 2 0 1 1 0 4v6h-5a2 2 0 1 0-4 0H5v-5a2 2 0 1 0 0-4V5Z')] },
  cpu: { label: 'Hardware', shapes: [p('M7 7h10v10H7zM10 10h4v4h-4zM10 3.5V7m4-3.5V7m-4 10v3.5m4-3.5v3.5M3.5 10H7m-3.5 4H7m10-4h3.5M17 14h3.5')] },
  image: { label: 'Images', shapes: [p('M4 5h16v14H4zM4 16l4.5-4.5 3.5 3.5 2.5-2.5L20 18'), c(15.5, 9, 1.5)] },
  music: { label: 'Audio', shapes: [p('M9 17.5V6l11-2v11.5'), c(6.5, 17.5, 2.5), c(17.5, 15.5, 2.5)] },
  compass: { label: 'Navigation', shapes: [c(12, 12, 9), p('m15.5 8.5-2 5-5 2 2-5 5-2Z')] },
  robot: { label: 'Agents', shapes: [p('M7 9h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2ZM12 5.5V9M3 13v2m18-2v2M9.5 14h.01M14.5 14h.01'), c(12, 4.5, 1)] },
  mail: { label: 'Email', shapes: [p('M4 6h16v12H4zM4 7l8 6 8-6')] },
  calendar: { label: 'Planning', shapes: [p('M4 6h16v14H4zM4 10h16M8 3.5V7m8-3.5V7')] },
  search: { label: 'Research', shapes: [c(11, 11, 6.5), p('m20 20-4.4-4.4')] },
  heart: { label: 'Favourites', shapes: [p('M12 20s-7.5-4.5-7.5-10A4.3 4.3 0 0 1 12 7.4 4.3 4.3 0 0 1 19.5 10c0 5.5-7.5 10-7.5 10Z')] },
  star: { label: 'Highlight', shapes: [p('m12 3.5 2.6 5.3 5.9.8-4.3 4.1 1 5.8L12 16.8l-5.2 2.7 1-5.8-4.3-4.1 5.9-.8L12 3.5Z')] },
  box: { label: 'Package', shapes: [p('M4 8h16v12H4zM3 4h18v4H3zM10 12h4')] }
};

export function badgeIcon(name) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', 'badge-icon');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  for (const [key, value] of Object.entries({ fill: 'none', stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }))
    svg.setAttribute(key, value);
  for (const [element, attrs] of BADGE_ICONS[name]?.shapes ?? []) {
    const child = document.createElementNS(NS, element);
    for (const [key, value] of Object.entries(attrs)) child.setAttribute(key, value);
    svg.append(child);
  }
  return svg;
}

export function initials(title) {
  const words = String(title).replace(/[^\p{L}\p{Nd}\s-]/gu, ' ').split(/[\s-]+/).filter(Boolean);
  return ((words[0]?.[0] ?? '?') + (words[1]?.[0] ?? '')).toUpperCase();
}

/** The saved badge choice for a skill: { icon } or { image } (a data URL), or undefined for initials. */
export function appearanceFor(skill, appearance = {}) {
  const choice = appearance[skillKey(skill)] ?? appearance[skill.name];
  if (choice?.image) return { image: choice.image };
  if (choice?.icon && BADGE_ICONS[choice.icon]) return { icon: choice.icon };
  return undefined;
}

/** A skill's badge: its uploaded image, its chosen icon or its two-letter monogram. */
export function skillBadge(skill, appearance, { tint, className = '' } = {}) {
  const choice = appearance && 'image' in appearance || appearance && 'icon' in appearance ? appearance : undefined;
  const kind = choice?.image ? 'image' : choice?.icon ? 'icon' : 'initials';
  return h('span', { class: ('skill-glyph ' + className).trim(), 'data-badge': kind, 'data-provider': tint, 'aria-hidden': 'true' },
    kind === 'image' ? h('img', { src: choice.image, alt: '', draggable: 'false' })
      : kind === 'icon' ? badgeIcon(choice.icon)
        : initials(skill.title || skill.name));
}

const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/webp'];
const IMAGE_SIZE = 128;

/** Center-crops and scales an uploaded image to a 128 px square so only a few KB are stored. */
export async function resizeImage(file) {
  if (!IMAGE_TYPES.includes(file.type)) throw new Error('Choose a PNG, JPG or WebP image.');
  if (file.size > 15 * 1024 * 1024) throw new Error('Choose an image smaller than 15 MB.');
  let bitmap;
  try { bitmap = await createImageBitmap(file); } catch { throw new Error('That image could not be read. Try another file.'); }
  const side = Math.min(bitmap.width, bitmap.height);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = IMAGE_SIZE;
  const context = canvas.getContext('2d');
  context.imageSmoothingQuality = 'high';
  context.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, IMAGE_SIZE, IMAGE_SIZE);
  bitmap.close?.();
  const webp = canvas.toDataURL('image/webp', 0.9);
  return webp.startsWith('data:image/webp') ? webp : canvas.toDataURL('image/png');
}

/** Icon picker: built-in icons, an uploaded image, or back to initials. Saved only in ~/.skillhub/appearance. */
export function showIconPicker(app, skill) {
  const key = skillKey(skill);
  const current = appearanceFor(skill, app.snapshot.appearance);
  let pending = current;
  const name = skill.title || skill.name;
  const preview = h('div', { class: 'icon-picker-preview' });
  const feedback = h('div', { role: 'status', class: 'icon-picker-feedback' });
  const grid = h('div', { class: 'icon-picker-grid', role: 'group', 'aria-label': 'Built-in icons' });
  const noMatch = h('p', { class: 'muted icon-picker-empty', hidden: true }, 'No icon matches. Try another word.');
  const filter = h('input', { class: 'input', type: 'search', placeholder: 'Filter icons (e.g. code, game, data)', 'aria-label': 'Filter icons',
    'data-control': 'icon-filter', oninput: () => {
      const query = filter.value.trim().toLowerCase();
      let shown = 0;
      for (const choice of grid.children) {
        const match = !query || (choice.dataset.icon + ' ' + choice.title).toLowerCase().includes(query);
        choice.hidden = !match;
        if (match) shown++;
      }
      noMatch.hidden = shown > 0;
    } });
  const choose = next => { pending = next; feedback.replaceChildren(); render(); };
  for (const [id, item] of Object.entries(BADGE_ICONS)) {
    grid.append(h('button', { type: 'button', class: 'icon-choice', 'data-icon': id, title: item.label, 'aria-label': item.label,
      'aria-pressed': 'false', onclick: () => choose({ icon: id }) }, badgeIcon(id)));
  }
  const file = h('input', { type: 'file', accept: IMAGE_TYPES.join(','), class: 'sr-only', tabindex: '-1', 'aria-hidden': 'true' });
  const upload = button('Upload image…', () => file.click(), 'small', { 'data-control': 'upload-image', title: 'PNG, JPG or WebP; cropped to a square' });
  file.addEventListener('change', () => {
    const chosen = file.files[0];
    file.value = '';
    if (chosen) busy(upload, async () => choose({ image: await resizeImage(chosen) }), error => feedback.replaceChildren(errorText(error.message)));
  });
  const reset = button('Reset to initials', () => choose(undefined), 'quiet', { 'data-control': 'reset-initials' });
  const save = button('Save icon', () => busy(save, async () => {
    if (!pending) await api.setAppearance({ key, reset: true });
    else await api.setAppearance({ key, ...pending });
    dialog.close();
    notify(pending ? 'Icon updated for ' + name + '.' : name + ' shows its initials again.', { tone: 'success', duration: 2800 });
    await app.refresh();
  }, error => feedback.replaceChildren(errorText(error.message))), 'primary', { 'data-control': 'save-icon' });
  function render() {
    preview.replaceChildren(skillBadge(skill, pending, { className: 'large' }),
      h('div', {}, h('strong', {}, name),
        h('p', { class: 'muted' }, pending?.image ? 'Custom image' : pending?.icon ? BADGE_ICONS[pending.icon].label + ' icon' : 'Initials (default)')));
    for (const choice of grid.children) choice.setAttribute('aria-pressed', String(choice.dataset.icon === pending?.icon));
    reset.disabled = !pending;
    save.disabled = JSON.stringify(pending ?? null) === JSON.stringify(current ?? null);
  }
  render();
  const dialog = modal('Skill icon', [preview,
    h('section', { class: 'stack icon-picker-section' }, h('h3', {}, 'Built-in icons'), filter, grid, noMatch),
    h('section', { class: 'stack icon-picker-section' }, h('h3', {}, 'Your own image'),
      h('div', { class: 'actions' }, upload, file,
        h('small', { class: 'muted' }, 'Stored only in SkillHub (~/.skillhub/appearance). AI tool folders never change.'))),
    feedback], [reset, button('Cancel', () => dialog.close()), save]);
  dialog.classList.add('icon-picker');
  return dialog;
}
