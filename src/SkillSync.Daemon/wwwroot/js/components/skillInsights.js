import { api } from '../api.js';
import { icon } from '../icons.js';
import { renderDiff } from '../diff.js';
import { h, button, modal, errorText, busy, notify, copy, confirmAction, disclosure, fill } from '../ui.js';

/** The text pasted into any chat to make an assistant follow a skill, matching SkillHub's MCP prompt. */
export function skillPrompt(skill) {
  const parts = [`Follow the skill “${skill.title || skill.name}” for this request.`];
  if (skill.when_to_use?.trim()) parts.push('When to use: ' + skill.when_to_use.trim());
  parts.push((skill.instructions_markdown ?? '').trim());
  const files = Object.keys(skill.supporting_files ?? {});
  if (files.length) parts.push('Bundled files (in ~/.skillhub/skills/' + skill.name + '/):\n' + files.map(path => '- ' + path).join('\n'));
  return parts.join('\n\n') + '\n';
}

export async function copySkillPrompt(app, key) {
  const detail = await api.detail(key);
  await copy(skillPrompt(detail.skill));
}

const when = value => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const tone = score => score >= 85 ? 'ok' : score >= 60 ? 'near' : 'over';

/** Compact Skill Doctor summary for the Reader inspector. */
export function doctorPanel(skill) {
  const body = h('div', { class: 'doctor-mini', 'aria-live': 'polite' }, h('p', { class: 'muted' }, 'Checking…'));
  const panel = h('section', { class: 'doctor-panel', 'aria-label': 'Skill Doctor' }, h('h3', {}, 'Skill Doctor'), body);
  api.skillDoctor(skill.name).then(report => {
    const top = report.findings.filter(finding => finding.severity !== 'info').slice(0, 4);
    body.replaceChildren(...[
      h('div', { class: 'doctor-score small', 'data-tone': tone(report.score) }, h('strong', {}, report.score),
        h('span', {}, report.findings.length ? report.findings.length + (report.findings.length === 1 ? ' finding' : ' findings') : 'No problems')),
      ...top.map(finding => h('p', { class: 'doctor-line', 'data-severity': finding.severity }, finding.message)),
      report.findings.length > top.length && button('Show all findings', () => modal('Skill Doctor · ' + (skill.title || skill.name),
        h('div', { class: 'stack' }, report.findings.map(finding => h('div', { class: 'doctor-finding', 'data-severity': finding.severity },
          icon(finding.severity === 'error' ? 'close' : finding.severity === 'warning' ? 'info' : 'check'),
          h('div', {}, h('p', {}, finding.message), finding.file && h('p', { class: 'muted path' }, finding.file + (finding.line ? ':' + finding.line : ''))))))),
        'quiet small')].filter(Boolean));
  }).catch(error => body.replaceChildren(h('p', { class: 'muted' }, error.message)));
  return panel;
}

/** History tab: saved revisions with a diff against the current version and one-click restore. */
export function historySection(app, detail) {
  const skill = detail.skill;
  const current = detail.transpiled_previews?.canonical ?? skill.instructions_markdown ?? '';
  const list = h('div', { class: 'history-list' }, h('p', { class: 'muted' }, 'Loading history…'));
  const viewer = h('div', { class: 'history-viewer' });
  const readOnly = skill.is_catalog_only || skill.source_category === 'system';
  api.history(skill.name).then(({ revisions }) => {
    if (!revisions.length) {
      list.replaceChildren(h('p', { class: 'muted' }, 'No saved revisions yet. Every save from now on is kept here (up to 30).'));
      return;
    }
    list.replaceChildren(...revisions.map((revision, index) => {
      const row = h('button', { type: 'button', class: 'history-row', 'aria-pressed': 'false', onclick: () => open(revision, row) },
        h('strong', {}, index === 0 ? 'Current version' : when(revision.saved_at)),
        h('span', { class: 'muted' }, 'Revision ' + revision.revision + ' · ' + revision.file_count + (revision.file_count === 1 ? ' file' : ' files') +
          (index === 0 ? ' · ' + when(revision.saved_at) : '')));
      return row;
    }));
    const first = list.querySelectorAll('.history-row')[Math.min(1, revisions.length - 1)];
    first?.click();
  }).catch(error => list.replaceChildren(errorText(error.message)));

  async function open(revision, row) {
    for (const other of list.querySelectorAll('.history-row')) other.setAttribute('aria-pressed', String(other === row));
    viewer.replaceChildren(h('p', { class: 'muted' }, 'Loading revision…'));
    try {
      const snapshot = await api.revision(skill.name, revision.id);
      const currentFiles = skill.supporting_files ?? {};
      const paths = [...new Set([...Object.keys(snapshot.files), ...Object.keys(currentFiles)])].sort();
      const fileChanges = paths.filter(path => snapshot.files[path] !== currentFiles[path]);
      const restore = !readOnly && button([icon('undo'), 'Restore this version'], async event => {
        const control = event.currentTarget;
        if (!await confirmAction('Restore this version', 'Replace the current skill with the version from ' + when(revision.saved_at) +
          '? The current version stays in history, so you can undo this.', 'Restore', false)) return;
        busy(control, async () => {
          const response = await api.restoreRevision(skill.name, revision.id, skill.revision);
          notify('Restored the version from ' + when(revision.saved_at) + '.', { tone: 'success' });
          await app.read(response.skill.name);
        });
      }, 'primary small');
      fill(viewer,
        h('div', { class: 'spread' }, h('h3', {}, 'Changes since ' + when(revision.saved_at)), restore),
        renderDiff(snapshot.markdown, current, { labels: ['This version', 'Current'] }),
        fileChanges.length > 0 && disclosure('Bundle files that differ (' + fileChanges.length + ')',
          h('div', { class: 'stack' }, fileChanges.map(path => disclosure(path,
            renderDiff(snapshot.files[path] ?? '', currentFiles[path] ?? '', { labels: ['This version', 'Current'] }))))));
    } catch (error) {
      viewer.replaceChildren(errorText(error.message));
    }
  }
  return h('div', { class: 'history-section' }, list, viewer);
}

/** Banner and review dialog for an upstream update of an imported skill. */
export function updateBanner(app, skill) {
  const update = app.updates?.get(skill.name);
  if (!update) return null;
  return h('div', { class: 'update-banner' }, icon('sync'),
    h('div', {}, h('strong', {}, 'Update available'),
      h('p', { class: 'muted' }, update.repo + ' moved from ' + update.current_revision.slice(0, 7) + ' to ' + update.latest_revision.slice(0, 7) +
        (update.local_modified ? '. You also edited this skill locally.' : '.'))),
    button('Review update', () => reviewUpdate(app, skill), 'small primary'));
}

export async function reviewUpdate(app, skill) {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, 'Comparing with upstream…'));
  const apply = button('Apply update', null, 'primary');
  const keep = button('Keep local');
  const dialog = modal('Update ' + (skill.title || skill.name), body, [keep, apply]);
  dialog.classList.add('wide-dialog');
  apply.disabled = keep.disabled = true;
  try {
    const diff = await api.updateDiff(skill.name);
    apply.disabled = keep.disabled = false;
    fill(body,
      h('p', {}, diff.update.repo, ' · ', h('code', {}, diff.update.path)),
      diff.update.local_modified && h('p', { class: 'warning' }, 'You changed this skill since importing it. Applying the update replaces those edits; the current version stays in History.'),
      renderDiff(diff.local, diff.upstream, { labels: ['Current local', 'New upstream'] }),
      diff.files.length > 0 && disclosure('Bundle files (' + diff.files.length + ' changed)', h('div', { class: 'stack' },
        diff.files.map(change => disclosure(change.change + ' · ' + change.path,
          renderDiff(diff.local_files[change.path] ?? '', diff.upstream_files[change.path] ?? '', { labels: ['Current local', 'New upstream'] }))))));
    const resolve = action => async event => {
      busy(event.currentTarget, async () => {
        const response = await api.resolveUpdate(skill.name, action, diff.update.latest_revision);
        app.updates?.delete(skill.name);
        dialog.close();
        notify(action === 'apply' ? 'Updated ' + (skill.title || skill.name) + ' from upstream.' : 'Kept your local version.', { tone: 'success' });
        await app.read(response.skill.name);
      });
    };
    apply.addEventListener('click', resolve('apply'));
    keep.addEventListener('click', resolve('keep'));
  } catch (error) {
    body.replaceChildren(errorText(error.message));
  }
}

/** Saved improvement proposals (continuous learning) for a skill: compare, apply (kept in History) or dismiss. */
export function proposalsBanner(app, skill) {
  if (skill.is_catalog_only) return null;
  const host = h('div', { class: 'proposals-host' });
  api.proposals(skill.name).then(({ proposals }) => {
    if (!proposals.length) return;
    fill(host, h('div', { class: 'proposal-banner' }, icon('edit'),
      h('div', { class: 'stack' },
        h('strong', {}, proposals.length === 1 ? 'A suggested improvement is waiting' : proposals.length + ' suggested improvements are waiting'),
        h('ul', {}, proposals.map(proposal => h('li', { class: 'proposal-row' },
          h('div', {}, h('p', {}, proposal.summary),
            h('small', { class: 'muted' }, 'Suggested ' + new Date(proposal.created_at).toLocaleString() + ' via ' + proposal.source)),
          h('div', { class: 'actions wrap' },
            proposal.instructions_markdown != null && button('Compare', () => modal('Suggested change · ' + (skill.title || skill.name),
              renderDiff(skill.instructions_markdown ?? '', proposal.instructions_markdown, { labels: ['Current', 'Suggested'] })), 'quiet small'),
            button('Apply', event => busy(event.currentTarget, async () => {
              await api.applyProposal(proposal.id);
              notify('Applied. The previous version is in History.', { tone: 'success' });
              await app.refresh();
              app.read(skill.name);
            }), 'primary small'),
            button('Dismiss', event => busy(event.currentTarget, async () => {
              await api.dismissProposal(proposal.id);
              event.target.closest('li')?.remove();
              if (!host.querySelector('li')) host.replaceChildren();
            }), 'quiet small'))))))));
  }).catch(() => {});
  return host;
}
