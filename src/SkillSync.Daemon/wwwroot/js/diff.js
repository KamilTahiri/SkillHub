import { h } from './ui.js';

/** Line diff using the longest common subsequence. Returns [{ type: 'same' | 'add' | 'remove', text }]. */
export function diffLines(before, after) {
  const a = String(before ?? '').replace(/\r\n?/g, '\n').split('\n');
  const b = String(after ?? '').replace(/\r\n?/g, '\n').split('\n');
  // Trim the shared head and tail first so large mostly-identical documents stay cheap.
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length, endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }
  const midA = a.slice(start, endA), midB = b.slice(start, endB);
  const head = a.slice(0, start).map(text => ({ type: 'same', text }));
  const tail = a.slice(endA).map(text => ({ type: 'same', text }));
  if (midA.length * midB.length > 4_000_000) {
    return [...head, ...midA.map(text => ({ type: 'remove', text })), ...midB.map(text => ({ type: 'add', text })), ...tail];
  }
  const table = Array.from({ length: midA.length + 1 }, () => new Uint32Array(midB.length + 1));
  for (let i = midA.length - 1; i >= 0; i--)
    for (let j = midB.length - 1; j >= 0; j--)
      table[i][j] = midA[i] === midB[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const body = [];
  let i = 0, j = 0;
  while (i < midA.length && j < midB.length) {
    if (midA[i] === midB[j]) { body.push({ type: 'same', text: midA[i] }); i++; j++; }
    else if (table[i + 1][j] >= table[i][j + 1]) body.push({ type: 'remove', text: midA[i++] });
    else body.push({ type: 'add', text: midB[j++] });
  }
  while (i < midA.length) body.push({ type: 'remove', text: midA[i++] });
  while (j < midB.length) body.push({ type: 'add', text: midB[j++] });
  return [...head, ...body, ...tail];
}

/** Renders a unified diff with a few lines of context around each change; unchanged runs collapse. */
export function renderDiff(before, after, { context = 3, labels = ['Before', 'After'] } = {}) {
  const lines = diffLines(before, after);
  const changed = lines.map(line => line.type !== 'same');
  const keep = lines.map((_, index) => changed.slice(Math.max(0, index - context), index + context + 1).some(Boolean));
  const added = lines.filter(line => line.type === 'add').length;
  const removed = lines.filter(line => line.type === 'remove').length;
  const rows = [];
  let skipped = 0;
  lines.forEach((line, index) => {
    if (!keep[index]) { skipped++; return; }
    if (skipped) { rows.push(h('div', { class: 'diff-line diff-skip' }, '⋯ ' + skipped + ' unchanged lines')); skipped = 0; }
    rows.push(h('div', { class: 'diff-line diff-' + line.type },
      h('span', { class: 'diff-sign', 'aria-hidden': 'true' }, line.type === 'add' ? '+' : line.type === 'remove' ? '−' : ' '),
      h('span', { class: 'sr-only' }, line.type === 'add' ? 'Added: ' : line.type === 'remove' ? 'Removed: ' : ''),
      h('span', { class: 'diff-text' }, line.text || ' ')));
  });
  if (skipped) rows.push(h('div', { class: 'diff-line diff-skip' }, '⋯ ' + skipped + ' unchanged lines'));
  return h('div', { class: 'diff' },
    h('p', { class: 'diff-summary' }, h('span', { class: 'diff-legend diff-remove' }, '− ' + labels[0]), ' ',
      h('span', { class: 'diff-legend diff-add' }, '+ ' + labels[1]), ' · ',
      added || removed ? `${added} added, ${removed} removed` : 'No differences'),
    h('div', { class: 'diff-body', tabindex: '0', role: 'region', 'aria-label': 'Differences' }, rows));
}
