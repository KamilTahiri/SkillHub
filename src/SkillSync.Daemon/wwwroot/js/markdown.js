import { h, copy } from './ui.js?v=20261113-v12';

export function highlighted(text, query = '') {
  const fragment = document.createDocumentFragment();
  const term = query.trim();
  if (!term) return document.createTextNode(text);
  const expression = new RegExp(term.replace(/[.*+?^$(){}|[\]\\]/g, '\\$&'), 'giu');
  let offset = 0;
  for (const match of text.matchAll(expression)) {
    fragment.append(text.slice(offset, match.index), h('mark', {}, match[0]));
    offset = match.index + match[0].length;
  }
  fragment.append(text.slice(offset));
  return fragment;
}

function inline(text, options, depth = 0) {
  if (depth >= 6) return highlighted(text, options.query);
  const result = document.createDocumentFragment();
  const tokens = /(\x60+)([^\n]+?)\1|\[([^\]\n]+)\]\((<?(?:[^()\s]|\([^()\n]*\))+>?)\)|(\*\*|__)(.+?)\5|(\*|_)([^\n]+?)\7/g;
  let end = 0;
  for (const match of text.matchAll(tokens)) {
    result.append(highlighted(text.slice(end, match.index), options.query));
    if (match[1]) result.append(h('code', {}, highlighted(match[2], options.query)));
    else if (match[3]) {
      const url = match[4].replace(/^<|>$/g, '');
      const label = inline(match[3], options, depth + 1);
      if (/^https?:\/\//i.test(url)) {
        result.append(h('a', { class: 'md-link', href: url, title: 'Copy link', onclick: event => {
          event.preventDefault();
          copy(url);
        } }, label));
      } else if (options.openFile && /^(?:\.\/)?(?:scripts|assets|references|resources|agents|examples)\//i.test(url)) {
        result.append(h('button', { type: 'button', class: 'md-link', onclick: () => options.openFile(url.replace(/^\.\//, '')) }, label));
      } else result.append(label);
    } else result.append(h(match[5] ? 'strong' : 'em', {}, inline(match[6] ?? match[8], options, depth + 1)));
    end = match.index + match[0].length;
  }
  result.append(highlighted(text.slice(end), options.query));
  return result;
}

const cells = line => line.trim().replace(/^\||(?<!\\)\|$/g, '').split(/(?<!\\)\|/).map(cell => cell.trim().replace(/\\\|/g, '|'));
const listPattern = /^(\s*)([-+*]|\d+[.)])\s+(.*)$/;
const headingPattern = /^\s*(#{1,6})\s+(.+?)\s*#*\s*$/;
const plainHeading = text => text.replace(/[\x60*_]/g, '').trim().replace(/\s+/g, ' ').toLowerCase();
const separator = line => line && line.includes('|') && cells(line).every(cell => /^:?-{3,}:?$/.test(cell));

export function renderMarkdown(source, options = {}) {
  const fragment = document.createDocumentFragment();
  const lines = String(source ?? '').replace(/\r\n?/g, '\n').split('\n');
  let index = 0;
  let first = true;
  const headingIds = new Map();

  function list(indent, ordered, depth) {
    const firstMatch = listPattern.exec(lines[index]);
    const start = ordered ? Number.parseInt(firstMatch[2], 10) : undefined;
    const node = h(ordered ? 'ol' : 'ul', { start: start === 1 ? undefined : start });
    while (index < lines.length) {
      const match = listPattern.exec(lines[index]);
      if (!match || match[1].length !== indent || /^\d/.test(match[2]) !== ordered) break;
      index++;
      const item = h('li', {}, inline(match[3], options));
      node.append(item);
      while (index < lines.length) {
        const next = listPattern.exec(lines[index]);
        if (!next || next[1].length <= indent || depth >= 12) break;
        item.append(list(next[1].length, /^\d/.test(next[2]), depth + 1));
      }
    }
    return node;
  }

  while (index < lines.length) {
    const line = lines[index];
    if (!line.trim()) { index++; continue; }
    const fence = /^\s*(\x60{3,}|~{3,})/.exec(line);
    const title = headingPattern.exec(line);
    const item = listPattern.exec(line);
    if (fence) {
      const content = [];
      index++;
      while (index < lines.length && !lines[index].trim().startsWith(fence[1])) content.push(lines[index++]);
      index++;
      fragment.append(h('pre', { tabindex: '0' }, h('code', {}, highlighted(content.join('\n'), options.query))));
    } else if (title) {
      const level = title[1].length;
      if (!(first && level === 1 && options.title && plainHeading(title[2]) === plainHeading(options.title))) {
        const base = plainHeading(title[2]).replace(/[^\p{L}\p{Nd}-]+/gu, '-');
        const count = (headingIds.get(base) ?? 0) + 1;
        headingIds.set(base, count);
        fragment.append(h('h' + level, { id: 'md-' + base + (count > 1 ? '-' + count : '') }, inline(title[2], options)));
      }
      index++;
    } else if (line.includes('|') && separator(lines[index + 1]) && cells(line).length === cells(lines[index + 1]).length) {
      const header = cells(line);
      const table = h('table', {}, h('thead', {}, h('tr', {}, header.map(cell => h('th', { scope: 'col' }, inline(cell, options))))));
      const body = h('tbody');
      index += 2;
      while (index < lines.length && lines[index].trim() && lines[index].includes('|')) {
        const row = cells(lines[index++]);
        body.append(h('tr', {}, header.map((_, column) => h('td', {}, inline(row[column] ?? '', options)))));
      }
      table.append(body);
      fragment.append(h('div', { class: 'table-scroll', tabindex: '0', role: 'region', 'aria-label': 'Table' }, table));
    } else if (item) {
      fragment.append(list(item[1].length, /^\d/.test(item[2]), 0));
    } else if (/^\s*>/.test(line)) {
      const quote = [];
      while (index < lines.length && /^\s*>/.test(lines[index])) quote.push(lines[index++].replace(/^\s*>\s?/, ''));
      fragment.append(h('blockquote', {}, inline(quote.join(' '), options)));
    } else if (/^\s*(?:---+|\*\*\*+|___+)\s*$/.test(line)) {
      fragment.append(h('hr'));
      index++;
    } else {
      const paragraph = [lines[index++].trim()];
      while (index < lines.length && lines[index].trim() && !headingPattern.test(lines[index]) &&
        !listPattern.test(lines[index]) && !/^\s*(?:\x60{3,}|~{3,}|>|---+)/.test(lines[index]) &&
        !(lines[index].includes('|') && separator(lines[index + 1]))) {
        paragraph.push(lines[index++].trim());
      }
      fragment.append(h('p', {}, inline(paragraph.join(' '), options)));
    }
    first = false;
  }
  return fragment;
}
