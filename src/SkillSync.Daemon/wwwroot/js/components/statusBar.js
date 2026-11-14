import { api } from '../api.js';
import { Latest, deployment, installedProviders } from '../state.js';
import { icon } from '../icons.js';
import { h, button, heading, panel, field, select, disclosure, confirmAction, errorText, busy, notify, copy } from '../ui.js';

const CONNECTION_GROUPS = { codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity', claude: 'Claude', cursor: 'Cursor' };
const CLIENT_PROVIDERS = { codex: 'codex', 'gemini-cli': 'gemini', antigravity: 'gemini', 'claude-code': 'claude', 'claude-desktop': 'claude', cursor: 'cursor' };

export function createSettings(root, app) {
  let tab = 'Connections';
  const jobs = new Latest();
  const diagnosticsJob = new Latest();
  let themeInput;
  let diagnosticsTab = 'Needs Review';
  let diagnosticsFilter = 'all';
  let updateDiagnostics;
  const tabs = h('nav', { class: 'settings-tabs', 'aria-label': 'Settings sections' });
  const content = h('div', { class: 'stack' });
  root.append(heading('Settings'), tabs, content);
  function renderTabs() {
    tabs.replaceChildren(...['Connections', 'Appearance & Startup', 'Diagnostics'].map(name =>
      button(name, () => { tab = name; renderTabs(); load(); }, '', { 'aria-pressed': String(name === tab) })));
  }

  async function load() {
    const job = jobs.begin();
    const current = tab;
    themeInput = undefined;
    updateDiagnostics = undefined;
    content.replaceChildren(h('p', { role: 'status' }, 'Loading ' + current.toLowerCase() + '…'));
    try {
      const data = current === 'Connections' ? await api.status(job.signal)
        : current === 'Appearance & Startup' ? await api.preferences(null, job.signal) : await api.diagnostics(job.signal);
      if (!job.current()) return;
      if (current === 'Connections') renderConnections(data);
      else if (current === 'Appearance & Startup') renderPreferences(data);
      else renderDiagnostics(data);
    } catch (error) {
      if (job.current()) content.replaceChildren(panel(errorText(error.message), button('Retry', load)));
    }
  }

  function renderConnections(status) {
    const clients = status.install?.mcp_clients ?? [];
    const installed = installedProviders(app.snapshot.providers);
    const skills = app.snapshot.skills.filter(skill => skill.source_category !== 'system').map(skill => deployment(skill, installed));
    const groups = Object.entries(CONNECTION_GROUPS).map(([id, label]) => {
      const items = clients.filter(client => CLIENT_PROVIDERS[client.client_id] === id).map(client => ({
        client, available: Boolean(client.detected ?? (client.file_exists || client.parent_exists))
      }));
      const provider = app.snapshot.providers.find(item => item.provider === id);
      const synced = skills.filter(skill => skill.synced.includes(id)).length;
      const issues = skills.filter(skill => skill.issues.includes(id)).length;
      const rank = items.some(item => item.client.is_registered) ? 0
        : items.some(item => item.available) || provider?.is_installed ? 1 : 2;
      return { id, label, items, provider, synced, issues, rank };
    }).filter(group => group.items.length || group.provider)
      .sort((a, b) => a.rank - b.rank || b.synced - a.synced);
    const active = groups.filter(group => group.rank < 2);
    const missing = groups.filter(group => group.rank === 2);
    content.replaceChildren(
      active.length ? h('div', { class: 'connection-list' }, active.map(connectionCard))
        : h('div', { class: 'empty' }, h('h2', {}, 'No AI tools detected'),
          h('p', { class: 'muted' }, 'Install Codex, Gemini CLI, Antigravity, Claude, or Cursor. SkillHub detects them automatically.')),
      missing.length > 0 && h('details', { class: 'connections-other' },
        h('summary', {}, 'Other supported AI tools not installed on this device (' + missing.length + ')'),
        h('ul', {}, missing.map(group => h('li', { class: 'connection-other-row' },
          h('span', { class: 'provider-mark', 'data-provider': group.id }, icon(group.id)),
          h('div', {}, h('strong', {}, group.label),
            h('p', { class: 'muted' }, 'Install ' + group.label + ' and SkillHub can connect it from here.')))))));
  }

  function groupState(group) {
    if (group.items.some(item => item.client.error)) return ['Needs attention', 'warn'];
    if (group.items.some(item => item.client.restart_required)) return ['Restart required', 'warn'];
    if (group.items.some(item => item.client.is_registered)) return ['Connected', 'ok'];
    return ['Detected', 'muted'];
  }

  function connectionCard(group) {
    const [state, tone] = groupState(group);
    const summary = [group.synced + (group.synced === 1 ? ' skill synced' : ' skills synced'),
      group.issues > 0 && group.issues + (group.issues === 1 ? ' needs' : ' need') + ' review'].filter(Boolean).join(' · ');
    return h('section', { class: 'connection-card', 'data-state': tone, 'aria-label': group.label },
      h('header', { class: 'connection-head' },
        h('span', { class: 'provider-mark', 'data-provider': group.id }, icon(group.id)),
        h('div', { class: 'connection-title' }, h('h2', {}, group.label), h('p', { class: 'muted' }, summary)),
        h('span', { class: 'status-badge ' + tone }, h('span', { class: 'status-badge-dot', 'aria-hidden': 'true' }), state)),
      group.items.length > 0 && h('ul', { class: 'connection-clients' }, group.items.map(item => clientRow(group, item))));
  }

  function clientRow(group, { client, available }) {
    const feedback = h('div', { role: 'status' });
    const [state, tone] = client.error ? ['Needs attention', 'warn'] : client.restart_required ? ['Restart required', 'warn']
      : client.is_registered ? ['Connected', 'ok'] : available ? ['Not connected', 'muted'] : ['Not installed', 'muted'];
    const action = client.is_registered ? 'repair' : 'connect';
    const update = button([icon(client.is_registered ? 'wrench' : 'plug'), client.is_registered ? 'Repair' : 'Connect'],
      () => updateClient(client, action, update, feedback), client.is_registered ? 'small' : 'primary small',
      { disabled: !available && !client.is_registered, 'aria-label': (client.is_registered ? 'Repair ' : 'Connect ') + client.display_name });
    const folder = client.client_id === 'antigravity' && group.provider?.provider_root
      ? group.provider.provider_root + '/antigravity/skills' : group.provider?.skills_root;
    const menu = h('details', { class: 'action-menu' },
      h('summary', { class: 'btn small icon-only', 'aria-label': 'More actions for ' + client.display_name, title: 'More actions' }, icon('more')),
      h('div', { class: 'action-menu-list' },
        button([icon('copy'), 'Copy config path'], () => { menu.open = false; copy(client.config_path); }, 'quiet small'),
        folder && button([icon('copy'), 'Copy skills folder'], () => { menu.open = false; copy(folder); }, 'quiet small'),
        client.is_registered && button([icon('unplug'), 'Disconnect'], () => {
          menu.open = false;
          updateClient(client, 'remove', update, feedback);
        }, 'quiet small danger')));
    menu.addEventListener('keydown', event => {
      if (event.key === 'Escape' && menu.open) { event.stopPropagation(); menu.open = false; menu.firstElementChild.focus(); }
    });
    menu.addEventListener('focusout', event => { if (!menu.contains(event.relatedTarget)) menu.open = false; });
    const restart = client.restart_required && !client.error && h('div', { class: 'restart-hint' }, icon('restart'),
      h('span', {}, 'Restart ' + client.display_name + ' so it loads SkillHub.'),
      button([icon('check'), 'Done'], event => busy(event.currentTarget, async () => {
        await api.client(client.client_id, 'acknowledge');
        await load();
      }, error => feedback.replaceChildren(errorText(error.message))), 'quiet small', { 'aria-label': 'I restarted ' + client.display_name }));
    return h('li', { class: 'connection-client' },
      h('div', { class: 'connection-client-main' },
        h('div', { class: 'connection-client-name' }, h('strong', {}, client.display_name),
          h('span', { class: 'status-badge small ' + tone }, h('span', { class: 'status-badge-dot', 'aria-hidden': 'true' }), state)),
        h('div', { class: 'actions' }, update, menu)),
      restart, client.error && errorText(client.error), feedback);
  }

  async function updateClient(client, action, control, feedback) {
    const label = action === 'remove' ? 'Disconnect' : action[0].toUpperCase() + action.slice(1);
    const text = action === 'remove'
      ? 'Remove SkillHub from the ' + client.display_name + ' configuration? Your skills stay in place. The file is backed up first.'
      : label + ' SkillHub in the ' + client.display_name + ' configuration? An existing file is backed up before a change.';
    if (!(await confirmAction(label + ' ' + client.display_name, text, label, action === 'remove'))) return;
    busy(control, async () => {
      const result = await api.client(client.client_id, action);
      if (result.error) throw new Error(result.error);
      if (tab === 'Connections' && root.isConnected && !root.hidden) await load();
      app.refresh();
    }, error => feedback.replaceChildren(errorText(error.message)));
  }

  function renderPreferences(data) {
    const feedback = h('span', { role: 'status', class: 'muted' });
    let pending = 0;
    const theme = select([['system', 'System'], ['light', 'Light'], ['dark', 'Dark']], app.theme, {
      onchange: async () => {
        const current = ++pending;
        feedback.textContent = 'Saving theme…';
        try {
          await app.setTheme(theme.value);
          if (current === pending) feedback.textContent = 'Theme saved.';
        } catch (error) {
          if (current === pending) feedback.replaceChildren(errorText(error.message));
        }
      }
    });
    themeInput = theme;
    function toggle(label, name, value) {
      const status = h('span', { class: 'muted', role: 'status' });
      const control = h('input', { type: 'checkbox', checked: value, onchange: () => busy(control, async () => {
        status.textContent = 'Saving…';
        const result = await api.setPreference(name, control.checked);
        control.checked = result.enabled;
        status.textContent = result.enabled ? 'Enabled' : 'Disabled';
      }, error => {
        control.checked = !control.checked;
        status.replaceChildren(errorText(error.message));
      }) });
      return h('div', { class: 'settings-row spread' }, h('label', { class: 'actions' }, control, label), status);
    }
    content.replaceChildren(h('section', { class: 'settings-group' }, h('h2', {}, 'Appearance'),
      h('div', { class: 'settings-row spread' }, field('Theme', theme), feedback)),
      h('section', { class: 'settings-group' }, h('h2', {}, 'Startup and window behavior'),
        toggle('Start SkillHub when I sign in', 'startup', data.auto_start),
        toggle('Keep SkillHub running in the tray', 'tray', data.close_to_tray)));
  }

  function renderDiagnostics(data) {
    const feedback = h('div', { role: 'status' });
    const body = h('div', { class: 'stack' });
    const subnav = h('nav', { class: 'settings-tabs', 'aria-label': 'Diagnostics sections' });
    let current = data;
    let showSnapshots = false;
    const reconcile = button('Reconcile destinations', () => busy(reconcile, async () => {
      const response = await api.resync();
      const report = response.report ?? {};
      feedback.textContent = [report.projected + ' updated', report.syncedDestinations + ' synced',
        report.conflictedDestinations + ' conflicts', report.missingDestinations + ' missing',
        report.failedDestinations + ' failed'].join(' · ');
      await refreshDiagnostics();
      app.refresh();
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const sections = ['Needs Review', 'Recent Operations', 'Trash'];
    subnav.append(...sections.map(name => button(name, event => {
      diagnosticsTab = name;
      for (const tabButton of subnav.querySelectorAll('button')) tabButton.setAttribute('aria-pressed', String(tabButton === event.currentTarget));
      fill();
    }, '', { 'aria-pressed': String(name === diagnosticsTab) })));
    const watcher = data.watcher ?? {};
    content.replaceChildren(h('div', { class: 'spread' }, h('p', { class: 'muted' },
      watcher.running ? 'Watching ' + watcher.roots + ' folders' : 'Watcher is starting or offline'), reconcile),
      watcher.last_error && errorText(watcher.last_error), feedback, subnav, body);
    updateDiagnostics = next => { current = next; fill(); };
    function fill() {
      const focused = body.contains(document.activeElement) ? document.activeElement.dataset.key : null;
      if (diagnosticsTab === 'Needs Review') {
        const issues = current.issues ?? [];
        body.replaceChildren(...issues.map(issue => h('div', { class: 'settings-row spread' },
          h('div', {}, h('strong', {}, issue.title), h('p', { class: 'warning' }, issue.label + ' · ' + issue.state)),
          h('div', { class: 'actions' }, button('Open skill', () => app.read(issue.skill), 'quiet small'),
            button('Review ' + issue.label, () => app.review(issue.skill, issue), 'small', { 'data-key': issue.path })))));
        if (!issues.length) body.append(h('p', { class: 'muted' }, 'No destinations need review.'));
        const backups = current.conflicts ?? [];
        if (backups.length) {
          const retained = disclosure('Retained snapshots (' + backups.length + ')', backups.map(entry =>
          h('div', { class: 'settings-row spread' }, h('div', {}, h('strong', {}, entry.skill), h('p', { class: 'path' }, entry.destination)),
            h('div', { class: 'actions' }, entry.can_undo && button('Undo replacement', async event => {
              const control = event.currentTarget;
              if (!await confirmAction('Undo replacement', 'Restore the saved provider files and leave this copy provider-managed?', 'Undo replacement')) return;
              busy(control, async () => {
                await api.undoConflict(entry.id, entry.revision);
                notify('Restored the provider snapshot.');
                app.refresh();
                await refreshDiagnostics();
              }, error => feedback.replaceChildren(errorText(error.message)));
            }, 'small'),
            button('Recover inactive copy', async event => {
              const control = event.currentTarget;
              if (!await confirmAction('Recover snapshot', 'Save an inactive copy in My Skills?', 'Recover copy', false)) return;
              busy(control, async () => {
                await api.conflict(entry.id, entry.revision);
                notify('Recovered an inactive copy in My Skills.');
                app.refresh();
              });
            }, 'small')))));
          retained.open = showSnapshots;
          retained.addEventListener('toggle', () => { showSnapshots = retained.open; });
          body.append(retained);
        }
      } else if (diagnosticsTab === 'Recent Operations') {
        const all = current.recent_activity ?? [];
        const kinds = [...new Set(all.map(event => event.event_type ?? event.action ?? 'activity'))];
        const filter = select([['all', 'All operations'], ...kinds.map(kind => [kind, kind])], diagnosticsFilter, {
          'aria-label': 'Filter operations', 'data-key': 'operation-filter',
          onchange: event => { diagnosticsFilter = event.target.value; fill(); }
        });
        const events = all.filter(event => diagnosticsFilter === 'all' || (event.event_type ?? event.action ?? 'activity') === diagnosticsFilter);
        body.replaceChildren(filter, events.length ? h('ul', { class: 'activity' }, events.map(event =>
          h('li', {}, h('small', { class: 'muted' }, new Date(event.timestamp_utc).toLocaleString()),
            h('p', {}, event.details)))) : h('p', { class: 'muted' }, 'No matching operations.'));
      } else {
        const trash = current.trash ?? [];
        body.replaceChildren(...trash.map(item => {
          const restore = button('Restore', async () => {
            const impact = item.restore_targets?.length ? 'Enabled destinations: ' + item.restore_targets.join(', ') + '.' : 'The skill will return to My Skills.';
            if (!await confirmAction('Restore ' + item.title, impact, 'Restore', false)) return;
            busy(restore, async () => {
              await api.restore(item.name);
              await refreshDiagnostics();
              app.refresh();
              subnav.lastElementChild.focus();
              notify('Restored ' + item.title + '.');
            });
          }, 'small');
          return h('div', { class: 'settings-row spread' }, h('div', {}, h('strong', {}, item.title),
            h('p', { class: 'muted' }, [item.origin, item.deleted_at && new Date(item.deleted_at).toLocaleString()].filter(Boolean).join(' · ')),
            item.restore_targets?.length && h('p', {}, 'Restore to ' + item.restore_targets.join(', '))), restore);
        }));
        if (!trash.length) body.append(h('p', { class: 'muted' }, 'Trash is empty.'));
      }
      if (focused) body.querySelector('[data-key="' + CSS.escape(focused) + '"]')?.focus({ preventScroll: true });
    }
    fill();
  }

  async function refreshDiagnostics() {
    if (tab !== 'Diagnostics' || !updateDiagnostics || root.hidden) return;
    const job = diagnosticsJob.begin();
    try {
      const next = await api.diagnostics(job.signal);
      if (job.current() && tab === 'Diagnostics') updateDiagnostics?.(next);
    } catch (error) { if (job.current()) notify(error.message); }
  }
  renderTabs();
  return {
    show(section = tab) {
      if (section !== tab) {
        tab = section;
        renderTabs();
      }
      return load();
    },
    syncTheme(value) { if (themeInput) themeInput.value = value; },
    refreshDiagnostics,
    leave() { jobs.cancel(); diagnosticsJob.cancel(); updateDiagnostics = undefined; }
  };
}
