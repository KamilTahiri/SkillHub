import { h, openExternal } from './ui.js';

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

function externalLink(url, label) {
  return h('a', { class: 'md-link', href: url, title: 'Open in your browser · ' + url, rel: 'noreferrer', onclick: event => {
    event.preventDefault();
    openExternal(url);
  } }, label);
}

const KEYWORDS = new Set(('const let var function return if else for while do switch case break continue new class extends import from export ' +
  'default async await try catch finally throw typeof instanceof in of null undefined true false this super def elif lambda pass yield with ' +
  'as None True False and or not is public private protected static void int string bool using namespace fn pub impl struct enum match ' +
  'mut use mod trait where interface type readonly echo then fi done esac local param foreach').split(' '));
const HASH_COMMENT_LANGUAGES = new Set(['py', 'python', 'sh', 'bash', 'shell', 'zsh', 'yaml', 'yml', 'toml', 'ps1', 'powershell', 'rb', 'ruby',
  'dockerfile', 'r', 'perl', 'makefile']);
const SLASH_COMMENT = String.raw`\/\/[^\n]*|\/\*[\s\S]*?\*\/|<!--[\s\S]*?-->`;
const HASH_COMMENT = String.raw`#[^\n]*`;
const CODE_TOKENS = String.raw`("(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'|` + '`' + String.raw`(?:\\.|[^` + '`' + String.raw`\\])*` + '`' +
  String.raw`)|\b(\d+(?:\.\d+)?)\b|([A-Za-z_$][\w$]*)`;

/** Lightweight, dependency-free syntax highlighting for fenced code blocks. */
function highlightCode(code, language) {
  const fragment = document.createDocumentFragment();
  const lang = (language || '').toLowerCase();
  if (!lang || ['text', 'txt', 'plain', 'markdown', 'md'].includes(lang) || code.length > 60000) {
    fragment.append(code);
    return fragment;
  }
  const pattern = new RegExp('(' + (HASH_COMMENT_LANGUAGES.has(lang) ? HASH_COMMENT : SLASH_COMMENT) + ')|' + CODE_TOKENS, 'g');
  let end = 0;
  for (const match of code.matchAll(pattern)) {
    const [text, comment, string, number, word] = match;
    const kind = comment ? 'comment' : string ? 'string' : number ? 'number' : word && KEYWORDS.has(word) ? 'keyword' : null;
    if (!kind) continue;
    fragment.append(code.slice(end, match.index), h('span', { class: 'tok-' + kind }, text));
    end = match.index + text.length;
  }
  fragment.append(code.slice(end));
  return fragment;
}

function inline(text, options, depth = 0) {
  if (depth >= 6) return highlighted(text, options.query);
  const result = document.createDocumentFragment();
  const tokens = /(\x60+)([^\n]+?)\1|\[([^\]\n]+)\]\((<?(?:[^()\s]|\([^()\n]*\))+>?)\)|(\*\*|__)(.+?)\5|(\*|_)([^\n]+?)\7|~~([^\n]+?)~~|(https:\/\/[^\s<>()]*[^\s<>().,;:!?'"])/g;
  let end = 0;
  for (const match of text.matchAll(tokens)) {
    result.append(highlighted(text.slice(end, match.index), options.query));
    if (match[1]) result.append(h('code', {}, highlighted(match[2], options.query)));
    else if (match[3]) {
      const url = match[4].replace(/^<|>$/g, '');
      const label = inline(match[3], options, depth + 1);
      if (/^https?:\/\//i.test(url)) {
        result.append(externalLink(url, label));
      } else if (options.openFile && /^(?:\.\/)?[^:#?\s/][^:?\s]*$/.test(url)) {
        // Relative links such as reference.md or scripts/run.py open the bundled file.
        result.append(h('button', { type: 'button', class: 'md-link', onclick: () => options.openFile(url.replace(/^\.\//, '').split('#')[0]) }, label));
      } else result.append(label);
    } else if (match[9]) result.append(h('del', {}, inline(match[9], options, depth + 1)));
    else if (match[10]) result.append(externalLink(match[10], match[10]));
    else result.append(h(match[5] ? 'strong' : 'em', {}, inline(match[6] ?? match[8], options, depth + 1)));
    end = match.index + match[0].length;
  }
  result.append(highlighted(text.slice(end), options.query));
  return result;
}

const cells = line => line.trim().replace(/^\||(?<!\\)\|$/g, '').split(/(?<!\\)\|/).map(cell => cell.trim().replace(/\\\|/g, '|'));
const alignment = cell => /^:-+:$/.test(cell) ? 'center' : /-:$/.test(cell) ? 'right' : null;
const listPattern = /^(\s*)([-+*]|\d+[.)])\s+(.*)$/;
const headingPattern = /^\s*(#{1,6})\s+(.+?)\s*#*\s*$/;
const plainHeading = text => text.replace(/[\x60*_~]/g, '').trim().replace(/\s+/g, ' ').toLowerCase();
const separator = line => line && line.includes('|') && cells(line).every(cell => /^:?-{3,}:?$/.test(cell));

export function renderMarkdown(source, options = {}) {
  const fragment = document.createDocumentFragment();
  const lines = String(source ?? '').replace(/\r\n?/g, '\n').split('\n');
  let index = 0;
  let first = true;
  const headingIds = new Map();

  function listItem(text) {
    const task = /^\[([ xX])\]\s+(.*)$/.exec(text);
    if (!task) return h('li', {}, inline(text, options));
    const done = task[1] !== ' ';
    return h('li', { class: 'task-item' + (done ? ' done' : '') },
      h('input', { type: 'checkbox', disabled: true, checked: done, 'aria-label': done ? 'Done' : 'Not done' }),
      h('span', {}, inline(task[2], options)));
  }

  function list(indent, ordered, depth) {
    const firstMatch = listPattern.exec(lines[index]);
    const start = ordered ? Number.parseInt(firstMatch[2], 10) : undefined;
    const node = h(ordered ? 'ol' : 'ul', { start: start === 1 ? undefined : start });
    while (index < lines.length) {
      const match = listPattern.exec(lines[index]);
      if (!match || match[1].length !== indent || /^\d/.test(match[2]) !== ordered) break;
      index++;
      const item = listItem(match[3]);
      node.append(item);
      while (index < lines.length) {
        const next = listPattern.exec(lines[index]);
        if (!next || next[1].length <= indent || depth >= 12) break;
        item.append(list(next[1].length, /^\d/.test(next[2]), depth + 1));
      }
    }
    if ([...node.children].some(item => item.classList.contains('task-item'))) node.classList.add('task-list');
    return node;
  }

  while (index < lines.length) {
    const line = lines[index];
    if (!line.trim()) { index++; continue; }
    const fence = /^\s*(\x60{3,}|~{3,})\s*([\w+#.-]*)/.exec(line);
    const title = headingPattern.exec(line);
    const item = listPattern.exec(line);
    if (fence) {
      const content = [];
      index++;
      while (index < lines.length && !lines[index].trim().startsWith(fence[1])) content.push(lines[index++]);
      index++;
      const code = content.join('\n');
      fragment.append(h('pre', { tabindex: '0', 'data-language': fence[2] || undefined },
        h('code', {}, options.query ? highlighted(code, options.query) : highlightCode(code, fence[2]))));
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
      const aligns = cells(lines[index + 1]).map(alignment);
      const style = column => aligns[column] ? 'text-align:' + aligns[column] : undefined;
      const table = h('table', {}, h('thead', {}, h('tr', {}, header.map((cell, column) => h('th', { scope: 'col', style: style(column) }, inline(cell, options))))));
      const body = h('tbody');
      index += 2;
      while (index < lines.length && lines[index].trim() && lines[index].includes('|')) {
        const row = cells(lines[index++]);
        body.append(h('tr', {}, header.map((_, column) => h('td', { style: style(column) }, inline(row[column] ?? '', options)))));
      }
      table.append(body);
      fragment.append(h('div', { class: 'table-scroll', tabindex: '0', role: 'region', 'aria-label': 'Table' }, table));
    } else if (item) {
      fragment.append(list(item[1].length, /^\d/.test(item[2]), 0));
    } else if (/^\s*>/.test(line)) {
      const quote = [];
      while (index < lines.length && /^\s*>/.test(lines[index])) quote.push(lines[index++].replace(/^\s*>\s?/, ''));
      fragment.append(h('blockquote', {}, renderMarkdown(quote.join('\n'), { ...options, title: undefined })));
    } else if (/^\s*(?:---+|\*\*\*+|___+)\s*$/.test(line)) {
      fragment.append(h('hr'));
      index++;
    } else {
      const paragraph = h('p');
      let firstLine = true;
      while (index < lines.length && lines[index].trim() &&
        (firstLine || !headingPattern.test(lines[index]) && !listPattern.test(lines[index]) &&
          !/^\s*(?:\x60{3,}|~{3,}|>|---+)/.test(lines[index]) && !(lines[index].includes('|') && separator(lines[index + 1])))) {
        const raw = lines[index++];
        // Two trailing spaces or a backslash mark a hard line break.
        const hardBreak = / {2,}$|\\$/.test(raw);
        if (!firstLine) paragraph.append(' ');
        paragraph.append(inline(raw.trim().replace(/\\$/, ''), options));
        if (hardBreak && index < lines.length && lines[index].trim()) paragraph.append(h('br'));
        firstLine = false;
      }
      fragment.append(paragraph);
    }
    first = false;
  }
  return fragment;
}

/** The section headings of a rendered document, for an outline. */
export function outline(article) {
  return [...article.querySelectorAll('h2[id], h3[id]')].map(node => ({ id: node.id, level: Number(node.tagName[1]), text: node.textContent }));
}
