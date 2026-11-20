import { api } from '../api.js';
import { icon } from '../icons.js';
import { h, button, field, select, modal, busy, notify, errorText } from '../ui.js';

/**
 * Library health: the always-loaded token meter, scope (Global / Project-only / Off) with bulk actions,
 * the library-wide Skill Doctor and the trigger tester.
 */
export const SCOPES = { global: 'Global', project: 'Project-only', off: 'Off' };
const SCOPE_HELP = {
  global: 'Synced to every AI tool’s global folder. Its description is in every chat.',
  project: 'Only written into projects that choose it. Costs nothing in other chats.',
  off: 'Not delivered anywhere.'
};
const formatTokens = tokens => tokens >= 1000 ? (tokens / 1000).toFixed(1) + 'k' : String(tokens);

export function scopeBadge(skill) {
  if (skill.is_catalog_only || !skill.scope || skill.scope === 'global') return null;
  return h('span', { class: 'scope-badge', 'data-scope': skill.scope, title: SCOPE_HELP[skill.scope] }, SCOPES[skill.scope]);
}

/** Changes scope for names, a tag or a source, then refreshes the app and reports the new budget. */
export async function applyScope(app, selection, scope) {
  const result = await api.setScope({ scope, ...selection });
  const changed = result.result?.changed ?? [];
  const skipped = result.result?.skipped ?? [];
  notify(changed.length
    ? 'Set ' + changed.length + (changed.length === 1 ? ' skill' : ' skills') + ' to ' + SCOPES[scope] + '. Always-loaded: ~' + formatTokens(result.budget.tokens) + ' tokens per chat.'
    : 'Nothing to change' + (skipped.length ? ': ' + skipped.map(item => item.name + ' (' + item.reason + ')').join(', ') : '.'),
  { tone: changed.length ? 'success' : 'info' });
  await app.refresh();
  app.refreshBudget?.();
  return result;
}

export function createBudgetMeter(app) {
  const label = h('span', { class: 'ctx-label' }, 'Always-loaded: …');
  const bar = h('span', { class: 'ctx-bar', 'aria-hidden': 'true' }, h('span', { class: 'ctx-fill' }));
  const meter = button([label, bar], () => showBudget(app), 'ctx-meter', {
    id: 'context-budget-meter', 'data-state': 'loading', title: 'Tokens of skill descriptions loaded into every chat. Click for the breakdown.'
  });
  let last;
  async function refresh() {
    try {
      last = await api.contextBudget();
      meter.dataset.state = last.state;
      label.textContent = 'Always-loaded: ~' + formatTokens(last.tokens) + ' tokens / chat';
      bar.firstChild.style.width = Math.min(100, Math.round(last.tokens / last.limit_tokens * 100)) + '%';
      meter.setAttribute('aria-label', label.textContent + '. ' + last.message);
    } catch {
      meter.dataset.state = 'error';
      label.textContent = 'Always-loaded: unavailable';
    }
  }
  app.refreshBudget = refresh;
  refresh();
  return { element: meter, refresh, get last() { return last; } };
}

export async function showBudget(app) {
  const body = h('div', { class: 'stack' }, h('p', { role: 'status' }, 'Measuring…'));
  const dialog = modal('Always-loaded context budget', body);
  dialog.classList.add('wide-dialog');
  let data;
  const selected = new Set();
  const render = () => {
    const state = h('div', { class: 'budget-summary', 'data-state': data.state },
      h('strong', {}, '~' + formatTokens(data.tokens) + ' tokens in every chat'),
      h('span', {}, data.global_skills + ' global · ' + data.project_only_skills + ' Project-only · ' + data.off_skills + ' Off'),
      h('p', {}, data.message),
      h('small', { class: 'muted' }, 'Comfortable below ' + formatTokens(data.safe_tokens) + '; above ' + formatTokens(data.limit_tokens) +
        ' Claude Code starts listing skills by name only, so their descriptions stop triggering.'));
    const bulk = h('div', { class: 'actions budget-bulk' },
      h('span', { class: 'muted' }, 'Selected skills:'),
      ...Object.entries(SCOPES).map(([scope, label]) => button(label, event => busy(event.currentTarget, async () => {
        if (!selected.size) { notify('Select skills first.'); return; }
        await applyScope(app, { names: [...selected] }, scope);
        selected.clear();
        await load();
      }), 'small', { 'data-bulk-scope': scope })));
    const tags = [...new Set(app.snapshot.skills.filter(skill => !skill.is_catalog_only).flatMap(skill => skill.tags ?? []))].sort();
    const tagPick = select([['', 'Choose a tag'], ...tags.map(tag => [tag, '#' + tag])], '', { 'aria-label': 'Tag to change' });
    const tagScope = select(Object.entries(SCOPES), 'project', { 'aria-label': 'Scope for the tag' });
    const byTag = tags.length > 0 && h('div', { class: 'actions budget-bulk' }, h('span', { class: 'muted' }, 'By tag:'), tagPick, tagScope,
      button('Apply', event => busy(event.currentTarget, async () => {
        if (!tagPick.value) { notify('Choose a tag.'); return; }
        await applyScope(app, { tag: tagPick.value }, tagScope.value);
        await load();
      }), 'small'));
    const packs = h('ul', { class: 'budget-packs' }, data.packs.map(pack => {
      const share = data.tokens ? Math.round(pack.tokens / data.tokens * 100) : 0;
      const canMove = pack.kind !== 'system';
      const packSelection = pack.kind === 'personal' ? { source: 'personal' } : { source: pack.pack };
      const list = h('ul', { class: 'budget-skills' }, pack.skills.map(skill => h('li', {},
        h('label', {}, h('input', { type: 'checkbox', disabled: !canMove, checked: selected.has(skill.name), 'data-skill': skill.name,
          onchange: event => { if (event.target.checked) selected.add(skill.name); else selected.delete(skill.name); } }),
          h('span', {}, skill.name)),
        h('span', { class: 'scope-badge', 'data-scope': skill.scope }, SCOPES[skill.scope]),
        h('span', { class: 'muted budget-tokens' }, skill.tokens ? '~' + skill.tokens : '0'))));
      return h('li', { class: 'budget-pack', 'data-pack': pack.pack },
        h('div', { class: 'spread' },
          h('div', {}, h('strong', {}, pack.label),
            h('small', { class: 'muted' }, ' ' + pack.skills.length + (pack.skills.length === 1 ? ' skill' : ' skills') +
              ' · ' + pack.global_skills + ' global' + (pack.project_only_skills ? ' · ' + pack.project_only_skills + ' Project-only' : '') +
              (pack.off_skills ? ' · ' + pack.off_skills + ' Off' : ''))),
          h('div', { class: 'actions' },
            h('span', { class: 'budget-share' }, '~' + formatTokens(pack.tokens) + ' (' + share + '%)'),
            canMove && pack.global_skills > 0 && button('Set pack to Project-only', event => busy(event.currentTarget, async () => {
              await applyScope(app, packSelection, 'project');
              await load();
            }), 'small primary', { 'data-pack-action': 'project' }),
            canMove && pack.global_skills < pack.skills.length && button('Set pack to Global', event => busy(event.currentTarget, async () => {
              await applyScope(app, packSelection, 'global');
              await load();
            }), 'small', { 'data-pack-action': 'global' }))),
        h('details', {}, h('summary', {}, 'Skills'), list));
    }));
    body.replaceChildren(state, h('h3', {}, 'By source'), packs, bulk, byTag || '',
      h('p', { class: 'muted' }, 'Project-only skills are written into a project only when you tick them in the Projects tab, or ask your assistant to use them in a repository (set_project_skills).'));
  };
  async function load() {
    try {
      data = await api.contextBudget();
      if (dialog.open) render();
    } catch (error) { body.replaceChildren(errorText(error.message)); }
  }
  await load();
}

export async function showLibraryAudit(app) {
  const body = h('div', { class: 'stack' }, h('p', { role: 'status' }, 'Checking every skill…'));
  const dialog = modal('Library audit', body);
  dialog.classList.add('wide-dialog');
  try {
    const report = await api.libraryDoctor();
    const open = name => { dialog.close(); app.read(name); };
    const skillLink = name => button(name, () => open(name), 'link-button small');
    body.replaceChildren(
      h('div', { class: 'audit-score', 'data-state': report.score >= 85 ? 'ok' : report.score >= 65 ? 'warning' : 'over' },
        h('strong', {}, report.score + '/100'),
        h('span', {}, report.skills_checked + ' skills · ' + report.errors + ' errors · ' + report.warnings + ' warnings · ~' +
          formatTokens(report.budget.tokens) + ' always-loaded tokens'),
        report.budget.state !== 'ok' && button('Fix the budget…', () => { dialog.close(); showBudget(app); }, 'small')),
      h('h3', {}, 'Trigger collisions'),
      report.collisions.length
        ? h('ul', { class: 'audit-list' }, report.collisions.map(cluster => h('li', {},
          h('div', { class: 'chips' }, cluster.skills.map(skillLink)),
          h('small', { class: 'muted' }, 'Shared triggers: ' + (cluster.shared_terms.join(', ') || '—') + (cluster.all_global ? '' : ' · some are not global')))))
        : h('p', { class: 'muted' }, 'No skills compete for the same requests.'),
      h('h3', {}, 'Missing companion skills'),
      report.missing_references.length
        ? h('ul', { class: 'audit-list' }, report.missing_references.map(item => h('li', {}, skillLink(item.skill),
          h('small', { class: 'muted' }, ' refers to ' + item.missing.join(', ')))))
        : h('p', { class: 'muted' }, 'Every referenced skill is installed.'),
      h('h3', {}, 'Skills with findings'),
      h('ul', { class: 'audit-list' }, report.skills.filter(skill => skill.findings.length).map(skill => h('li', {},
        h('div', { class: 'spread' }, skillLink(skill.name), h('span', { class: 'audit-skill-score' }, skill.score)),
        h('ul', {}, skill.findings.map(finding => h('li', { class: 'finding', 'data-severity': finding.severity }, finding.message)))))));
  } catch (error) {
    body.replaceChildren(errorText(error.message));
  }
}

export function showTriggerTester(app, initial = '') {
  const prompt = h('input', { class: 'input', type: 'search', value: initial, placeholder: 'Make a Roblox HUD', 'aria-label': 'Sample prompt', autofocus: true });
  const projects = h('input', { type: 'checkbox' });
  const results = h('div', { class: 'stack', role: 'status', 'aria-live': 'polite' });
  let timer;
  let request = 0;
  async function run() {
    const text = prompt.value.trim();
    if (!text) { results.replaceChildren(h('p', { class: 'muted' }, 'Type a request to see which skills would load.')); return; }
    const id = ++request;
    try {
      const result = await api.triggerTest(text, projects.checked);
      if (id !== request) return;
      results.replaceChildren(h('p', { class: result.collision ? 'trigger-summary warning' : 'trigger-summary' }, result.summary),
        h('ol', { class: 'trigger-results' }, result.candidates.map(candidate => h('li', { 'data-verdict': candidate.verdict },
          h('div', { class: 'spread' },
            button(candidate.title, () => { dialog.close(); app.read(candidate.name); }, 'link-button'),
            h('span', { class: 'trigger-verdict' }, candidate.verdict + ' · ' + Math.round(candidate.relative * 100) + '%')),
          h('span', { class: 'trigger-bar', 'aria-hidden': 'true' }, h('span', { style: 'width:' + Math.round(candidate.relative * 100) + '%' })),
          h('small', { class: 'muted' }, candidate.name + (candidate.scope !== 'global' ? ' · ' + SCOPES[candidate.scope] : '') +
            ' · matched: ' + candidate.matched_terms.join(', '))))));
    } catch (error) {
      if (id === request) results.replaceChildren(errorText(error.message));
    }
  }
  const schedule = () => { clearTimeout(timer); timer = setTimeout(run, 180); };
  prompt.addEventListener('input', schedule);
  projects.addEventListener('change', run);
  const dialog = modal('Trigger tester', [
    field('Sample prompt', prompt, 'Ranks skills by how well their name, description and “when to use” match. Ties mean the agent may pick any of them.'),
    h('label', { class: 'checkbox-row' }, projects, ' Include Project-only skills (as inside a project that uses them)'),
    results]);
  dialog.classList.add('wide-dialog');
  run();
  return dialog;
}
