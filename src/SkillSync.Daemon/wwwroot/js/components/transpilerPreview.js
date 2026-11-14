import { api } from '../api.js';
import { Latest, PROVIDERS } from '../state.js';
import { button, copy, errorText, field, h, modal, select } from '../ui.js';

export function showProviderPreview(draft, { saved, providers = [] } = {}) {
  const latest = new Latest();
  const choices = [['canonical', 'Canonical Markdown'], ...Object.entries(PROVIDERS)];
  const body = h('div', { class: 'stack', 'aria-busy': 'true' }, h('p', {}, 'Preparing output…'));
  const dialog = modal('Provider output · ' + (draft.title || draft.name), body);
  dialog.addEventListener('close', () => latest.cancel(), { once: true });
  const job = latest.begin();
  const pending = saved ? Promise.resolve(saved) : api.preview(draft, job.signal);
  pending.then(result => {
    if (!job.current()) return;
    const outputs = result.transpiled_previews ?? {};
    const chooser = select(choices, 'canonical');
    const path = h('p', { class: 'path' });
    const code = h('pre', { class: 'output-code', tabindex: '0' });
    const warning = h('p', { class: 'muted' });
    const copyOutput = button('Copy output', () => copy(code.textContent));
    function change() {
      const target = chooser.value;
      const suffix = target === 'cursor' ? draft.name + '.mdc' : draft.name + '/SKILL.md';
      const effective = result.effective_paths?.[target] ??
        draft.destinations?.filter(item => item.provider === target).map(item => item.path);
      const roots = providers.filter(provider => provider.provider === target).map(provider => provider.skills_root + '/' + suffix);
      path.textContent = (effective?.length ? effective : roots).join(' · ');
      code.textContent = outputs[target] ?? 'No output was returned for this target.';
      warning.textContent = target === 'codex' ? 'Codex output uses the concise description and may shorten it.'
        : target === 'gemini' ? 'Gemini combines the description and activation guidance.'
        : target === 'cursor' ? 'Cursor output uses rule metadata. Referenced files remain in the canonical bundle.'
        : target === 'claude' ? 'Claude output includes its allowed tools and model when set.' : 'This is the canonical skill document.';
    }
    chooser.addEventListener('change', change);
    body.setAttribute('aria-busy', 'false');
    body.replaceChildren(field('Target format', chooser), path, warning, code, copyOutput);
    change();
  }).catch(error => {
    if (job.current()) { body.setAttribute('aria-busy', 'false'); body.replaceChildren(errorText(error.message)); }
  });
}
