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
