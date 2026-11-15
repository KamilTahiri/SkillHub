import { api } from '../api.js';
import { icon } from '../icons.js';
import { h, button, busy, notify, errorText, copy } from '../ui.js';

const PROVIDER_LABELS = { codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity', claude: 'Claude', cursor: 'Cursor',
  copilot: 'GitHub Copilot', windsurf: 'Windsurf', cline: 'Cline', roocode: 'Roo Code' };
const EXAMPLES = [
  'Use SkillHub to find or create a skill for writing Playwright tests in this project.',
  'Use SkillHub to search skills.sh and GitHub for skills that fit this repository, preview the best three, and tell me which to import.',
  'Use SkillHub to turn what we just did into a reusable skill and sync it to all my AI tools.'
];

/**
 * The three-step welcome sheet: detected AI tools (with one-click MCP connection), the skills already on disk,
 * and how to use SkillHub from chat. Closing it at any step marks onboarding as done.
 */
export async function showOnboarding(app) {
  document.querySelector('dialog.onboarding')?.close();
  let step = 0;
  let status;
  try { status = await api.status(); } catch { status = { install: { mcp_clients: [] } }; }
  const dialog = h('dialog', { class: 'onboarding', 'aria-modal': 'true', 'aria-labelledby': 'onboarding-title' });
  const title = h('h2', { id: 'onboarding-title', tabindex: '-1' });
  const dots = h('ol', { class: 'onboarding-steps', 'aria-label': 'Steps' });
  const content = h('div', { class: 'onboarding-body' });
  const back = button('Back', () => go(step - 1), 'quiet');
  const next = button('Next', undefined, 'primary');
  const skip = button('Skip guide', () => dialog.close(), 'quiet');
  dialog.append(h('header', { class: 'onboarding-head' }, h('span', { class: 'brand-mark', 'aria-hidden': 'true' }), title, skip),
    dots, content, h('footer', { class: 'onboarding-foot' }, back, next));
  dialog.addEventListener('close', () => {
    dialog.remove();
    api.onboarding(true).catch(() => {});
  }, { once: true });
  dialog.addEventListener('cancel', () => api.onboarding(true).catch(() => {}));

  const steps = [
    { title: 'Your AI tools', render: tools },
    { title: 'Skills already on this PC', render: skills },
    { title: 'Use SkillHub from chat', render: usage }
  ];

  function tools() {
    const installed = app.snapshot.providers.filter(item => item.is_installed && item.provider !== 'canonical');
    const clients = status.install?.mcp_clients ?? [];
    const rows = clients.filter(client => client.detected ?? (client.file_exists || client.parent_exists));
    const feedback = h('div', { role: 'status' });
    return [
      h('p', { class: 'muted' }, installed.length
        ? 'SkillHub found ' + installed.length + (installed.length === 1 ? ' AI tool' : ' AI tools') + ' and keeps their skill folders in sync.'
        : 'No AI tools were found yet. Install one and SkillHub picks it up automatically.'),
      h('ul', { class: 'onboarding-tools' }, installed.map(item => h('li', {},
        h('span', { class: 'provider-mark', 'data-provider': item.provider }, icon(item.provider)),
        h('strong', {}, PROVIDER_LABELS[item.provider] ?? item.display_name ?? item.provider),
        h('span', { class: 'status-badge small ok' }, h('span', { class: 'status-badge-dot', 'aria-hidden': 'true' }), 'Skills sync')))),
      rows.length > 0 && h('div', { class: 'stack' }, h('h3', {}, 'Let these tools create and edit skills (MCP)'),
        h('ul', { class: 'onboarding-clients' }, rows.map(client => {
          const connect = !client.is_registered && button([icon('plug'), 'Connect'], () => busy(connect, async () => {
            const result = await api.client(client.client_id, 'connect');
            if (result.error) throw new Error(result.error);
            connect.replaceWith(h('span', { class: 'status-badge small ok' }, 'Connected · restart ' + client.display_name));
          }, error => feedback.replaceChildren(errorText(error.message))), 'small primary', { 'aria-label': 'Connect ' + client.display_name });
          return h('li', {}, h('strong', {}, client.display_name),
            client.is_registered ? h('span', { class: 'status-badge small ok' }, 'Connected') : connect);
        }))),
      feedback
    ];
  }

  function skills() {
    const all = app.snapshot.skills;
    const personal = all.filter(skill => skill.source_category === 'user').length;
    const synced = all.filter(skill => !skill.is_catalog_only && (skill.destinations ?? []).some(target => target.state === 'synced')).length;
    const builtIn = all.filter(skill => ['template', 'system', 'plugin'].includes(skill.source_category));
    const origins = Object.entries(builtIn.reduce((map, skill) => {
      const origin = skill.origin_provider || 'Other';
      map[origin] = (map[origin] ?? 0) + 1;
      return map;
    }, {})).sort((a, b) => b[1] - a[1]);
    const bundles = all.filter(skill => (skill.file_count ?? 0) > 0).length;
    const stat = (value, label) => h('div', { class: 'onboarding-stat' }, h('strong', {}, String(value)), h('span', {}, label));
    return [
      h('div', { class: 'onboarding-stats' }, stat(personal, 'personal skills'), stat(synced, 'synced to your tools'),
        stat(builtIn.length, 'built-in & templates'), stat(bundles, 'bundles with files')),
      origins.length > 0 && h('div', { class: 'stack' }, h('h3', {}, 'Built-in skills by source'),
        h('ul', { class: 'onboarding-origins' }, origins.slice(0, 8).map(([origin, count]) => h('li', {}, h('span', {}, origin), h('strong', {}, String(count)))))),
      h('p', { class: 'muted' }, 'Everything lives once in ~/.skillhub/skills. Edit a skill anywhere and SkillHub carries the change to every tool.')
    ];
  }

  function usage() {
    return [
      h('p', { class: 'muted' }, 'In any connected AI tool, ask in plain words. The assistant uses SkillHub’s tools to search, preview, create and sync skills.'),
      h('ul', { class: 'onboarding-prompts' }, EXAMPLES.map(text => h('li', {}, h('code', {}, text),
        button([icon('copy'), 'Copy'], () => copy(text), 'small quiet', { 'aria-label': 'Copy example prompt' })))),
      h('div', { class: 'actions wrap' },
        button([icon('explore'), 'Open Explore'], () => { dialog.close(); app.navigate('explore'); }),
        button([icon('library'), 'Add a project'], () => { dialog.close(); app.navigate('projects'); }))
    ];
  }

  function go(index) {
    step = Math.max(0, Math.min(steps.length - 1, index));
    title.textContent = steps[step].title;
    dots.replaceChildren(...steps.map((item, position) => h('li', { 'aria-current': position === step ? 'step' : null,
      'data-done': String(position < step) }, h('span', {}, String(position + 1)), item.title)));
    content.replaceChildren(...[steps[step].render()].flat().filter(child => child instanceof Node));
    back.hidden = step === 0;
    next.replaceChildren(step === steps.length - 1 ? 'Get started' : 'Next');
    next.onclick = step === steps.length - 1 ? () => { dialog.close(); notify('Welcome to SkillHub.', { tone: 'success' }); } : () => go(step + 1);
    title.focus({ preventScroll: true });
  }

  document.body.append(dialog);
  dialog.showModal();
  go(0);
  return dialog;
}
