import { api } from '../api.js';
import { h, button, modal, busy, errorText, confirmAction, notify } from '../ui.js';

const choices = {
  recognize: ['Recognize existing mirror', 'Register this identical copy without writing its contents.'],
  keep: ['Keep SkillHub version', 'Replace the reviewed provider files with the SkillHub projection. A snapshot will remain available.'],
  accept: ['Accept provider version', 'Merge the reviewed provider changes into this skill and reconcile its enabled destinations.'],
  copy: ['Keep separate copy', 'Save an inactive copy in My Skills and retain the current conflict.'],
  leave: ['Leave provider-managed', 'Leave the provider files in place. A later change will reopen review.'],
  retry: ['Retry deployment', 'Try applying the current SkillHub version to this destination again.']
};

function markdownParts(markdown) {
  const header = /^---\n([\s\S]*?)\n---(?:\n|$)/.exec(markdown);
  return header ? [header[1], markdown.slice(header[0].length)] : ['', markdown];
}

export function showDestinationReview(app, skill, target) {
  const body = h('div', { class: 'stack' });
  const footer = h('div', { class: 'actions' });
  const dialog = modal('Review ' + target.label, body, footer);
  const request = new AbortController();
  dialog.addEventListener('close', () => request.abort(), { once: true });
  async function load() {
    body.replaceChildren(h('p', { role: 'status' }, 'Comparing current files…'));
    footer.replaceChildren();
    try {
      const review = await api.reviewDestination(skill, target.path, request.signal);
      if (!dialog.open) return;
      const selectFile = h('select', { class: 'select', 'aria-label': 'Compare file' },
        review.files.map((file, index) => h('option', { value: index }, file.path + ' · ' + file.change)));
      const compare = h('div', { class: 'columns review-compare' });
      const feedback = h('div', { role: 'status' });
      const fileDetail = h('p', { class: 'muted', role: 'status' });
      function showFile() {
        const file = review.files[Number(selectFile.value)];
        fileDetail.textContent = file.change;
        if (file.path === 'SKILL.md' && file.skill_hub != null && file.provider != null) {
          const [expectedHeader, expectedBody] = markdownParts(file.skill_hub);
          const [actualHeader, actualBody] = markdownParts(file.provider);
          fileDetail.textContent = (expectedHeader === actualHeader ? 'Frontmatter unchanged' : 'Frontmatter changed') +
            ' · ' + (expectedBody === actualBody ? 'Instructions unchanged' : 'Instructions changed') +
            (file.change === 'Different' && expectedHeader === actualHeader && expectedBody === actualBody ? ' · File bytes differ' : '');
        }
        compare.hidden = file.skill_hub == null && file.provider == null;
        compare.replaceChildren(...[['SkillHub projection', file.skill_hub], ['On provider disk', file.provider]].map(([label, text]) =>
          h('section', { class: 'stack' }, h('h3', {}, label), h('pre', { class: 'file-code', tabindex: '0' }, text ?? 'File absent'))));
      }
      selectFile.addEventListener('change', showFile);
      showFile();
      const summary = review.reason === 'identical' ? 'All reviewed files match byte-for-byte.'
        : review.reason === 'different' ? 'The provider bundle differs from the current SkillHub projection.'
        : review.reason === 'missing' ? 'The managed provider copy is missing.' : 'The last deployment failed.';
      body.replaceChildren(h('h3', {}, review.issue.title), h('p', {}, summary),
        review.issue.detail && h('details', {}, h('summary', {}, 'Technical details'), h('code', { class: 'path' }, review.issue.path),
          h('p', {}, review.issue.detail)),
        selectFile, fileDetail, compare, feedback);
      footer.replaceChildren(...review.actions.map((action, index) => {
        const [label, detail] = choices[action];
        const control = button(label, async () => {
          if (['keep', 'accept', 'copy'].includes(action) && !await confirmAction(label, detail, label, action !== 'copy')) return;
          busy(control, async () => {
            const result = await api.resolveDestination(skill, target.path, review.version, action);
            if (dialog.open) dialog.close();
            app.updateDetail(result);
            app.refresh();
            app.refreshDiagnostics();
            notify(action === 'copy' ? 'Saved an inactive copy in My Skills.' : 'Destination review updated.');
          }, error => feedback.replaceChildren(errorText(error.message), button('Refresh comparison', load, 'small')));
        }, index === 0 ? 'primary' : '', { title: detail });
        return h('div', { class: 'review-action' }, control, h('p', { class: 'muted' }, detail));
      }));
    } catch (error) {
      if (dialog.open) body.replaceChildren(errorText(error.message), button('Refresh comparison', load));
    }
  }
  load();
}
