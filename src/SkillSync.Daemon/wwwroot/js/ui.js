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

let noticeTimer;
export function notify(message) {
  const region = document.getElementById('notifications');
  region.replaceChildren(h('p', { class: 'notice' }, message));
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => region.replaceChildren(), 4800);
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

export function busy(control, run, onError = error => notify(error.message)) {
  if (control.disabled) return;
  control.disabled = true;
  Promise.resolve().then(run).catch(onError).finally(() => { control.disabled = false; });
}

export async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    notify('Copied to clipboard.');
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
