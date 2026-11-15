export function h(tag, attributes = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(attributes)) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith('on')) element.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'value' || key === 'checked') element[key] = value;
    else element.setAttribute(key, value === true ? '' : String(value));
  }
  const append = child => {
    if (Array.isArray(child)) child.forEach(append);
    else if (child instanceof Node) element.append(child);
    else if (child !== null && child !== undefined && child !== false) element.append(document.createTextNode(String(child)));
  };
  children.forEach(append);
  return element;
}

/** Replaces an element's children, skipping null, undefined and false the way h() does (native replaceChildren prints them). */
export function fill(element, ...children) {
  element.replaceChildren(...h('div', {}, ...children).childNodes);
  return element;
}

/** "just now", "5 minutes ago", "3 days ago"; falls back to the date for anything older than a month. */
export function relativeTime(value) {
  const time = new Date(value).getTime();
  if (!Number.isFinite(time)) return 'at an unknown time';
  const seconds = Math.round((Date.now() - time) / 1000);
  if (seconds < 45) return 'just now';
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  if (seconds < 3600) return format.format(-Math.round(seconds / 60), 'minute');
  if (seconds < 86400) return format.format(-Math.round(seconds / 3600), 'hour');
  if (seconds < 30 * 86400) return format.format(-Math.round(seconds / 86400), 'day');
  return new Date(time).toLocaleDateString(undefined, { dateStyle: 'medium' });
}

const SCRIPT_FILE = /\.(py|sh|bash|zsh|ps1|psm1|bat|cmd|js|mjs|cjs|ts|tsx|jsx|rb|go|rs|java|kt|php|pl|lua)$/i;
const DOC_FILE = /\.(md|mdx|markdown|txt|rst|adoc)$/i;

/** "2 scripts + 1 doc" for a list of companion file paths. */
export function describeCompanions(paths = []) {
  const scripts = paths.filter(path => SCRIPT_FILE.test(path)).length;
  const docs = paths.filter(path => !SCRIPT_FILE.test(path) && DOC_FILE.test(path)).length;
  const other = paths.length - scripts - docs;
  const part = (count, one, many) => count > 0 && count + ' ' + (count === 1 ? one : many);
  return [part(scripts, 'script', 'scripts'), part(docs, 'doc', 'docs'), part(other, 'other file', 'other files')].filter(Boolean).join(' + ');
}

/**
 * Says plainly whether a skill is a single SKILL.md or a bundle. `count` is the number of companion files besides SKILL.md.
 * With `verbose`, a bundle also says what it contains: "📦 Bundle · 4 files: SKILL.md + 3 scripts".
 */
export function bundleBadge(count, { known = true, paths, verbose = false, main = 'SKILL.md' } = {}) {
  if (!known) return h('span', { class: 'files-badge unknown', title: 'Open the preview to see every file in this skill' }, 'Files listed in preview');
  const companions = Math.max(0, Number(count) || 0);
  if (companions === 0) return h('span', { class: 'files-badge single', title: 'This skill is a single ' + main + ' file with no companion files' }, '1 file (' + main + ')');
  const total = companions + 1;
  const detail = paths?.length ? describeCompanions(paths) : companions + (companions === 1 ? ' companion file' : ' companion files');
  return h('span', { class: 'files-badge bundle', title: main + ' + ' + detail + (paths?.length ? ': ' + paths.join(', ') : '') },
    '📦 Bundle · ' + total + ' files' + (verbose ? ': ' + main + ' + ' + detail : ''));
}

/**
 * Infinite scroll for long result lists. A sentinel after `list` loads the next page as it scrolls into view; the
 * sentinel is also a real button, so keyboard and screen reader users can load more without scrolling.
 * `append(pageSize)` adds rows and returns the index of the first new row.
 */
export function autoLoadMore(container, total, shown, append, list, noun = 'results', pageSize = 50) {
  const remaining = () => total - shown();
  if (remaining() <= 0) return null;
  const label = () => 'Show ' + Math.min(pageSize, remaining()) + ' more ' + noun + ' (' + remaining() + ' left)';
  const more = h('button', { type: 'button', class: 'btn quiet load-more' }, label());
  const sentinel = h('div', { class: 'load-more-sentinel' }, more);
  let observer;
  const load = focus => {
    if (remaining() <= 0) return;
    const index = append(pageSize);
    if (remaining() <= 0) { observer?.disconnect(); sentinel.remove(); }
    else more.textContent = label();
    if (focus) list.querySelectorAll(':scope > li')[index]?.querySelector('button, input')?.focus();
  };
  more.addEventListener('click', () => load(true));
  list.after(sentinel);
  if ('IntersectionObserver' in window) {
    // The list scrolls inside its container on wide layouts and with the page on narrow ones.
    const scrolls = getComputedStyle(container).overflowY !== 'visible';
    container.loadMoreObserver?.disconnect();
    observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting) && sentinel.isConnected) load(false);
      if (!sentinel.isConnected) observer.disconnect();
    }, { root: scrolls ? container : null, rootMargin: '0px 0px 600px 0px' });
    observer.observe(sentinel);
    container.loadMoreObserver = observer;
  }
  return sentinel;
}

/** Uses a shorter placeholder on compact windows so it never reads as cut off. */
export function responsivePlaceholder(input, full, compact, query = '(max-width: 720px)') {
  const media = matchMedia(query);
  const apply = () => { input.placeholder = media.matches ? compact : full; };
  media.addEventListener('change', apply);
  apply();
  return input;
}

export const button = (label, run, kind = '', extra = {}) =>
  h('button', { type: 'button', class: ('btn ' + kind).trim(), onclick: run, ...extra }, label);
export const field = (label, control, hint) => h('label', { class: 'field' }, h('span', {}, label), control, hint && h('small', { class: 'field-help' }, hint));
export const heading = (title, actions, extra) => h('header', { class: 'page-heading' },
  h('div', {}, h('h1', { tabindex: '-1' }, title), extra), actions);
export const panel = (...children) => h('div', { class: 'panel stack' }, children);
export const disclosure = (label, ...children) => h('details', { class: 'disclosure' }, h('summary', {}, label), children);
export const errorText = message => h('p', { class: 'error', role: 'alert' }, message);
export const select = (entries, value, attrs = {}) => {
  const node = h('select', { class: 'select', ...attrs }, entries.map(([key, label]) => h('option', { value: key }, label)));
  node.value = value;
  return node;
};
export const optionPairs = object => Object.entries(object);

const MAX_TOASTS = 3;

/**
 * Shows a toast. Up to three stack in the corner; hovering pauses their timers.
 * options: { action: 'Undo', run: () => {}, tone: 'warning' | 'error' | 'success', duration: ms }
 */
export function notify(message, options = {}) {
  const region = document.getElementById('notifications');
  if (!region) return;
  const tone = options.tone ?? (/fail|could not|unavailable|error/i.test(message) ? 'error' : 'info');
  let remaining = options.duration ?? (tone === 'error' ? 8000 : 5200);
  let started = Date.now();
  let timer;
  const toast = h('div', { class: 'notice', 'data-tone': tone },
    h('span', { class: 'notice-text' }, message));
  const dismiss = () => {
    clearTimeout(timer);
    toast.classList.add('leaving');
    setTimeout(() => toast.remove(), 160);
  };
  if (options.action && options.run) {
    toast.append(h('button', { type: 'button', class: 'notice-action', onclick: () => { dismiss(); options.run(); } }, options.action));
  }
  toast.append(h('button', { type: 'button', class: 'notice-close', 'aria-label': 'Dismiss notification', onclick: dismiss }, '×'));
  const progress = h('span', { class: 'notice-progress', 'aria-hidden': 'true' });
  progress.style.animationDuration = remaining + 'ms';
  toast.append(progress);
  const schedule = () => { started = Date.now(); timer = setTimeout(dismiss, remaining); };
  toast.addEventListener('mouseenter', () => { clearTimeout(timer); remaining -= Date.now() - started; toast.classList.add('paused'); });
  toast.addEventListener('mouseleave', () => { toast.classList.remove('paused'); schedule(); });
  region.append(toast);
  while (region.children.length > MAX_TOASTS) region.firstElementChild.remove();
  schedule();
  return dismiss;
}

let dialogNumber = 0;
export function modal(title, body, footer) {
  const opener = document.activeElement;
  const titleId = 'dialog-title-' + (++dialogNumber);
  const dialog = h('dialog', { role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId });
  const close = button('Close', () => dialog.close(), 'quiet', { 'aria-label': 'Close ' + title });
  dialog.append(h('header', { class: 'dialog-head spread' }, h('h2', { id: titleId }, title), close),
    h('div', { class: 'dialog-body stack' }, body));
  if (footer) dialog.append(h('footer', { class: 'dialog-foot actions' }, footer));
  dialog.addEventListener('close', () => {
    dialog.remove();
    if (opener?.isConnected) opener.focus({ preventScroll: true });
  }, { once: true });
  document.body.append(dialog);
  dialog.showModal();
  return dialog;
}

export function confirmAction(title, text, action, dangerous = true) {
  return new Promise(resolve => {
    let approved = false;
    const cancel = button('Cancel', () => dialog.close(), '', { autofocus: true });
    const accept = button(action, () => { approved = true; dialog.close(); }, dangerous ? 'danger' : 'primary');
    const dialog = modal(title, h('p', {}, text), [cancel, accept]);
    dialog.addEventListener('close', () => resolve(approved), { once: true });
    cancel.focus();
  });
}

export function busy(control, run, onError = error => notify(error.message, { tone: 'error' })) {
  if (control.disabled) return;
  control.disabled = true;
  // Keep the button's width while its label is replaced by a spinner.
  const width = control.getBoundingClientRect?.().width;
  if (width) control.style.minWidth = width + 'px';
  control.classList.add('is-loading');
  control.setAttribute('aria-busy', 'true');
  Promise.resolve().then(run).catch(onError).finally(() => {
    control.disabled = false;
    control.classList.remove('is-loading');
    control.removeAttribute('aria-busy');
    control.style.minWidth = '';
  });
}

export async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    notify('Copied to clipboard.', { tone: 'success', duration: 2600 });
  } catch {
    notify('Clipboard access is unavailable. Select and copy the text.');
  }
}

export function download(name, content, type = 'text/plain') {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = h('a', { href: url, download: name });
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Opens an https link in the user's default browser through the desktop host, or copies it outside the app. */
export function openExternal(url) {
  let parsed;
  try { parsed = new URL(url); } catch { return; }
  if (parsed.protocol !== 'https:') { copy(url); return; }
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ type: 'open-external', url: parsed.href }));
    notify('Opened ' + parsed.host + ' in your browser.', { tone: 'success', duration: 2600 });
  } else copy(url);
}
