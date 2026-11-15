// Guards the single stylesheet hierarchy (base → layout → components): no selector may be declared twice in the same
// media context, and !important is reserved for the [hidden] utility and the motion kill switches.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const root = new URL('../../src/SkillSync.Daemon/wwwroot/css/', import.meta.url);
const files = ['base.css', 'layout.css', 'components.css'];

test('stylesheets declare each selector once per media context', async () => {
  for (const name of files) {
    const text = (await readFile(new URL(name, root), 'utf8')).replace(/\/\*[\s\S]*?\*\//g, '');
    const counts = new Map();
    const stack = [];
    const pattern = /([^{}]+)\{|\}/g;
    let match;
    while ((match = pattern.exec(text))) {
      if (match[0] === '}') { stack.pop(); continue; }
      const selector = match[1].trim().replace(/\s+/g, ' ');
      const context = stack.filter(item => item.startsWith('@')).join('|');
      stack.push(selector);
      if (selector.startsWith('@') || stack.some(item => item.startsWith('@keyframes'))) continue;
      const key = context + ' :: ' + selector;
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
    const duplicates = [...counts].filter(([, count]) => count > 1).map(([key]) => key);
    assert.deepEqual(duplicates, [], name + ' repeats selectors');
  }
});

test('!important is limited to utilities', async () => {
  const allowed = [/^\[hidden\]/, /prefers-reduced-motion/, /^\.theme-switching/];
  for (const name of files) {
    const text = await readFile(new URL(name, root), 'utf8');
    const lines = text.split('\n').filter(line => line.includes('!important'));
    for (const line of lines) {
      const context = text.slice(0, text.indexOf(line)).split('\n').slice(-3).join('\n') + '\n' + line;
      assert.ok(allowed.some(rule => rule.test(line.trim()) || rule.test(context)), name + ': unexpected !important in ' + line.trim());
    }
  }
});

test('index.html loads exactly the three stylesheets', async () => {
  const html = await readFile(new URL('../index.html', root), 'utf8');
  const sheets = [...html.matchAll(/href="\/css\/([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(sheets, files);
});
