import { api } from '../api.js';
import { renderMarkdown } from '../markdown.js';
import { icon } from '../icons.js';
import { PROVIDERS, installedProviders } from '../state.js';
import { h, button, errorText } from '../ui.js';

const DESCRIPTION_LIMIT = 1024;
const BODY_TOKEN_GUIDE = 5000;
const DESCRIPTION_TOKEN_GUIDE = 256;
const tokens = text => Math.ceil(String(text ?? '').length / 4);
const PANES = [['preview', 'Live preview'], ['output', 'Provider output'], ['doctor', 'Skill Doctor']];

/**
 * Editor 2.0: a Write pane with a formatting toolbar and budget meters, beside Live preview, Provider output and
 * Skill Doctor tabs. Wide windows show both panes with synchronised scrolling; narrow windows switch with tabs.
 */
export function createEditorWorkspace({ instructions, description, trigger, title, getDraft, snapshot }) {
  let pane = 'preview';
  let narrowView = 'write';
  let outputProvider = installedProviders(snapshot.providers)[0] ?? 'claude';
  let pending;
  let request;
  const narrow = matchMedia('(width < 960px)');

  // ---- Toolbar ---------------------------------------------------------------------------------------------
  function wrap(before, after = before, placeholder = 'text') {
    const { selectionStart: start, selectionEnd: end, value } = instructions;
    const selected = value.slice(start, end) || placeholder;
    instructions.setRangeText(before + selected + after, start, end, 'end');
    instructions.selectionStart = start + before.length;
    instructions.selectionEnd = start + before.length + selected.length;
    changed();
  }
  function linePrefix(prefix) {
    const { selectionStart: start, selectionEnd: end, value } = instructions;
    const lineStart = value.lastIndexOf('\n', start - 1) + 1;
    const block = value.slice(lineStart, end);
    const updated = block.split('\n').map(line => prefix + line).join('\n');
    instructions.setRangeText(updated, lineStart, end, 'end');
    changed();
  }
  function insertBlock(text) {
    const { selectionStart: start, value } = instructions;
    const needsBreak = start > 0 && value[start - 1] !== '\n' ? '\n\n' : '';
    instructions.setRangeText(needsBreak + text, start, instructions.selectionEnd, 'end');
    changed();
  }
  function changed() {
    instructions.focus();
    instructions.dispatchEvent(new Event('input', { bubbles: true }));
  }
  const tool = (label, glyph, run, shortcut) => h('button', { type: 'button', class: 'editor-tool', title: label + (shortcut ? ' (' + shortcut + ')' : ''),
    'aria-label': label, onclick: run }, glyph);
  const toolbar = h('div', { class: 'editor-toolbar', role: 'toolbar', 'aria-label': 'Formatting' },
    tool('Heading', 'H', () => linePrefix('## ')),
    tool('Bold', h('strong', {}, 'B'), () => wrap('**'), 'Ctrl+B'),
    tool('Italic', h('em', {}, 'I'), () => wrap('_'), 'Ctrl+I'),
    tool('Inline code', h('code', {}, '`'), () => wrap('`', '`', 'code')),
    h('span', { class: 'editor-tool-gap', 'aria-hidden': 'true' }),
    tool('Bulleted list', '•', () => linePrefix('- ')),
    tool('Checklist', '☐', () => linePrefix('- [ ] ')),
    tool('Code block', '{ }', () => insertBlock('```bash\n' + (instructions.value.slice(instructions.selectionStart, instructions.selectionEnd) || 'command') + '\n```\n')),
    tool('Table', '▦', () => insertBlock('| Column | Notes |\n| --- | --- |\n| Value | Detail |\n')),
    tool('Link to a bundle file', '🔗', () => wrap('[', '](references/file.md)', 'link text')),
    tool('Variable placeholder', '{{x}}', () => wrap('{{', '}}', 'variable')));
  instructions.addEventListener('keydown', event => {
    if (!(event.ctrlKey || event.metaKey)) return;
    if (event.key.toLowerCase() === 'b') { event.preventDefault(); wrap('**'); }
    else if (event.key.toLowerCase() === 'i') { event.preventDefault(); wrap('_'); }
  });

  // ---- Budget meters --------------------------------------------------------------------------------------
  const meters = h('div', { class: 'budget-meters', 'aria-live': 'polite' });
  function meter(label, used, limit, unit, hint) {
    const ratio = Math.min(used / limit, 1.25);
    const tone = used > limit ? 'over' : used > limit * 0.8 ? 'near' : 'ok';
    return h('div', { class: 'budget-meter', 'data-tone': tone, title: hint },
      h('span', { class: 'budget-label' }, label),
      h('span', { class: 'budget-track' }, h('span', { class: 'budget-fill', style: 'width:' + Math.round(ratio / 1.25 * 100) + '%' })),
      h('span', { class: 'budget-value' }, used.toLocaleString() + ' / ' + limit.toLocaleString() + ' ' + unit));
  }
  function updateMeters() {
    const descriptionText = description.value.trim() + (trigger.value.trim() ? ' ' + trigger.value.trim() : '');
    meters.replaceChildren(
      meter('Description', description.value.length, DESCRIPTION_LIMIT, 'chars', 'Claude, Codex and Copilot accept up to 1,024 characters.'),
      meter('Always loaded', tokens(descriptionText), DESCRIPTION_TOKEN_GUIDE, 'tokens', 'Description and “When to use” sit in every conversation of every enabled tool.'),
      meter('Instructions', tokens(instructions.value), BODY_TOKEN_GUIDE, 'tokens', 'Loaded when the skill triggers. Move long reference material into bundle files.'));
  }

  // ---- Right pane -----------------------------------------------------------------------------------------
  const previewBody = h('article', { class: 'markdown editor-preview', 'aria-label': 'Live preview' });
  const outputSelect = h('select', { class: 'select', 'aria-label': 'Provider format', onchange: event => { outputProvider = event.target.value; refresh(); } },
    Object.entries(PROVIDERS).map(([id, label]) => h('option', { value: id }, label)));
  outputSelect.value = outputProvider;
  const outputPath = h('p', { class: 'path muted' });
  const outputCode = h('pre', { class: 'file-code output-code', tabindex: '0' });
  const outputBody = h('div', { class: 'stack' }, h('div', { class: 'spread' }, outputSelect, outputPath), outputCode);
  const doctorBody = h('div', { class: 'doctor-report', 'aria-live': 'polite' }, h('p', { class: 'muted' }, 'Checking…'));
  const bodies = { preview: previewBody, output: outputBody, doctor: doctorBody };
  const paneTabs = h('div', { class: 'editor-pane-tabs', role: 'tablist', 'aria-label': 'Editor views' });
  const writeTab = h('button', { type: 'button', role: 'tab', class: 'editor-pane-tab editor-write-tab', onclick: () => { narrowView = 'write'; layout(); } }, 'Write');
  function renderTabs() {
    paneTabs.replaceChildren(writeTab, ...PANES.map(([id, label]) => h('button', {
      type: 'button', role: 'tab', class: 'editor-pane-tab', 'data-pane': id,
      'aria-selected': String(pane === id && (!narrow.matches || narrowView !== 'write')),
      onclick: () => { pane = id; narrowView = 'side'; layout(); refresh(); }
    }, id === 'doctor' ? [label, h('span', { class: 'doctor-score-chip', hidden: true })] : label)));
    writeTab.setAttribute('aria-selected', String(narrow.matches && narrowView === 'write'));
  }
  const side = h('div', { class: 'editor-side' });
  const writePane = h('div', { class: 'editor-write' }, toolbar, instructions, meters);
  const element = h('section', { class: 'editor-workspace', 'aria-label': 'Instructions editor' }, paneTabs, writePane, side);
  function layout() {
    element.dataset.narrow = String(narrow.matches);
    element.dataset.view = narrowView;
    side.replaceChildren(bodies[pane]);
    renderTabs();
  }
  narrow.addEventListener('change', layout);

  // Synchronised scrolling between the source and the preview, by relative position.
  let syncing = false;
  instructions.addEventListener('scroll', () => {
    if (syncing || pane !== 'preview') return;
    const ratio = instructions.scrollTop / Math.max(1, instructions.scrollHeight - instructions.clientHeight);
    syncing = true;
    side.scrollTop = ratio * (side.scrollHeight - side.clientHeight);
    requestAnimationFrame(() => { syncing = false; });
  });
  side.addEventListener('scroll', () => {
    if (syncing || pane !== 'preview') return;
    const ratio = side.scrollTop / Math.max(1, side.scrollHeight - side.clientHeight);
    syncing = true;
    instructions.scrollTop = ratio * (instructions.scrollHeight - instructions.clientHeight);
    requestAnimationFrame(() => { syncing = false; });
  });

  function renderDoctor(report) {
    const chip = paneTabs.querySelector('.doctor-score-chip');
    if (chip) {
      chip.hidden = false;
      chip.textContent = report.score;
      chip.dataset.tone = report.score >= 85 ? 'ok' : report.score >= 60 ? 'near' : 'over';
    }
    const grouped = ['error', 'warning', 'info'].map(severity => report.findings.filter(finding => finding.severity === severity));
    doctorBody.replaceChildren(...[
      h('div', { class: 'doctor-score', 'data-tone': report.score >= 85 ? 'ok' : report.score >= 60 ? 'near' : 'over' },
        h('strong', {}, report.score), h('span', {}, report.score >= 85 ? 'Looks healthy' : report.score >= 60 ? 'A few things to fix' : 'Needs work')),
      report.findings.length === 0 && h('p', { class: 'muted' }, 'No problems found: provider rules, links, overlap, secrets and scripts all check out.'),
      ...grouped.flat().map(finding => h('div', { class: 'doctor-finding', 'data-severity': finding.severity },
        icon(finding.severity === 'error' ? 'close' : finding.severity === 'warning' ? 'info' : 'check'),
        h('div', {}, h('p', {}, finding.message),
          finding.file && h('p', { class: 'muted path' }, finding.file + (finding.line ? ':' + finding.line : ''))))),
      report.scripts?.length > 0 && h('div', { class: 'doctor-scripts' }, h('h3', {}, 'Executable content'),
        report.scripts.map(script => h('p', {}, h('code', {}, script.path), ' · ', script.language,
          script.concerns.length ? h('span', { class: 'warning' }, ' · ' + script.concerns.join(', ')) : '')))].filter(Boolean));
  }

  async function refresh() {
    clearTimeout(pending);
    updateMeters();
    if (pane === 'preview') {
      previewBody.replaceChildren(renderMarkdown(instructions.value, { title: title.value }));
      if (!instructions.value.trim()) previewBody.append(h('p', { class: 'muted' }, 'Start writing to see the preview.'));
      return;
    }
    // Server-backed panes wait for a pause in typing.
    pending = setTimeout(async () => {
      request?.abort();
      request = new AbortController();
      let draft;
      try { draft = getDraft(); }
      catch (error) { bodies[pane].replaceChildren(errorText(error.message)); return; }
      try {
        if (pane === 'output') {
          const result = await api.preview(draft, request.signal);
          const previews = result.transpiled_previews ?? {};
          const key = Object.keys(previews).find(name => name.toLowerCase() === outputProvider) ?? outputProvider;
          outputCode.textContent = previews[key] ?? 'This provider has no output for the current draft.';
          const paths = result.effective_paths ?? {};
          const pathKey = Object.keys(paths).find(name => name.toLowerCase() === outputProvider);
          outputPath.textContent = pathKey ? [].concat(paths[pathKey]).join(' · ') : '';
        } else {
          renderDoctor(await api.doctor(draft, request.signal));
        }
      } catch (error) {
        if (error.name !== 'AbortError') bodies[pane].replaceChildren(errorText(error.message));
      }
    }, pane === 'doctor' ? 650 : 450);
  }

  for (const input of [instructions, description, trigger, title]) input.addEventListener('input', () => refresh());
  layout();
  refresh();
  // Score the draft once so the Doctor tab shows its badge before it is opened.
  queueMicrotask(async () => {
    try { renderDoctor(await api.doctor(getDraft())); } catch { /* The Doctor tab retries when opened. */ }
  });
  return { element, refresh, focusWrite: () => instructions.focus() };
}
