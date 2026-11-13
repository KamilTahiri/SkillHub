import { api } from '../api.js?v=20261112-v9';
import { Latest } from '../state.js?v=20261112-v9';
import { h, button, heading, panel, field, select, disclosure, confirmAction, errorText, busy, notify } from '../ui.js?v=20261112-v9';

export function createSettings(root, app) {
  let tab = 'Connections';
  const jobs = new Latest();
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
    const feedback = h('div', { role: 'status' });
    content.replaceChildren(h('p', { class: 'muted' }, 'Connect an AI client to SkillHub. Restart that client after changing its configuration.'),
      feedback, ...clients.map(client => {
        const connection = client.error ? 'Needs attention' : client.is_registered ? 'Registered'
          : client.detected ?? (client.file_exists || client.parent_exists) ? 'Available' : 'Not detected';
        const action = client.is_registered ? 'repair' : 'connect';
        const update = button(client.is_registered ? 'Repair' : 'Connect', () => updateClient(client, action, update, feedback), '',
          { disabled: connection === 'Not detected', 'aria-label': action + ' ' + client.display_name });
        const remove = client.is_registered && button('Remove', () => updateClient(client, 'remove', remove, feedback), 'danger',
          { 'aria-label': 'Remove ' + client.display_name });
        const provider = app.snapshot.providers.find(item => ({
          codex: ['codex'], gemini: ['gemini-cli', 'antigravity'], claude: ['claude-code', 'claude-desktop'], cursor: ['cursor']
        })[item.provider]?.includes(client.client_id));
        return panel(h('div', { class: 'spread' }, h('div', {},
          h('h2', {}, client.display_name), h('p', { class: client.is_registered ? 'ok' : 'muted' }, connection)),
          h('div', { class: 'actions' }, update, remove)),
          h('code', { class: 'path' }, client.config_path),
          provider && h('p', { class: 'muted' }, 'Skills · ', provider.skills_root),
          client.error && errorText(client.error));
      }));
  }
  async function updateClient(client, action, control, feedback) {
    const label = action[0].toUpperCase() + action.slice(1);
    if (!(await confirmAction(label + ' ' + client.display_name,
      label + ' SkillHub in this client configuration? An existing file is backed up before a change.', label, action === 'remove'))) return;
    busy(control, async () => {
      const result = await api.client(client.client_id, action);
      if (result.error) throw new Error(result.error);
      notify('Configuration updated. Restart ' + client.display_name + '.');
      await load();
      app.refresh();
    }, error => feedback.replaceChildren(errorText(error.message)));
  }

  function renderPreferences(data) {
    const theme = select([['system', 'Use Windows setting'], ['light', 'Light'], ['dark', 'Dark']], data.theme);
    const tray = h('input', { type: 'checkbox', checked: data.close_to_tray });
    const login = h('input', { type: 'checkbox', checked: data.auto_start });
    const feedback = h('div', { role: 'status' });
    const save = button('Save preferences', () => busy(save, async () => {
      const next = await api.preferences({ theme: theme.value, close_to_tray: tray.checked, auto_start: login.checked });
      app.applyTheme(next.theme);
      tray.checked = next.close_to_tray;
      login.checked = next.auto_start;
      feedback.textContent = 'Preferences saved.';
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    content.replaceChildren(panel(h('h2', {}, 'Appearance'), field('Theme', theme)),
      panel(h('h2', {}, 'Startup and window behavior'), h('div', { class: 'checks' },
        h('label', {}, login, 'Start SkillHub when I sign in'), h('label', {}, tray, 'Keep SkillHub running in the tray when the window closes')),
      h('div', { class: 'actions' }, save), feedback));
  }

  function renderDiagnostics(data) {
    const feedback = h('div', { role: 'status' });
    const reconcile = button('Rescan and reconcile', () => busy(reconcile, async () => {
      const response = await api.resync();
      app.refresh();
      const report = response.report ?? {};
      feedback.textContent = [response.count + ' managed skills',
        (report.projected ?? report.Projected ?? 0) + ' updated',
        (report.conflictedDestinations ?? report.conflicted_destinations ?? 0) + ' conflicts',
        (report.failedDestinations ?? report.failed_destinations ?? 0) + ' failed'].join(' · ');
      const current = await api.diagnostics();
      fillDiagnostics(current, log, conflicts, trash);
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const log = h('div');
    const conflicts = h('div', { class: 'stack' });
    const trash = h('div', { class: 'stack' });
    const watcher = data.watcher ?? {};
    content.replaceChildren(panel(h('div', { class: 'spread' }, h('div', {}, h('h2', {}, 'Sync health'),
      h('p', { class: 'muted' }, watcher.running ? watcher.roots + ' watched roots' : 'Watcher is starting or offline')), reconcile),
      watcher.last_error && errorText(watcher.last_error), feedback),
      panel(h('h2', {}, 'Conflict backups'), conflicts),
      panel(h('h2', {}, 'Trash'), trash),
      panel(h('h2', {}, 'Recent activity'), log));
    fillDiagnostics(data, log, conflicts, trash);
  }

  function fillDiagnostics(data, log, conflicts, trash) {
    const activity = data.recent_activity ?? [];
    log.replaceChildren(activity.length ? h('ul', { class: 'activity' }, activity.map(event =>
      h('li', {}, h('small', { class: 'muted' }, new Date(event.timestamp_utc).toLocaleString() + ' · '), event.details)))
      : h('p', { class: 'muted' }, 'No activity recorded.'));
    conflicts.replaceChildren(...(data.conflicts ?? []).map(entry => {
      const recover = button('Recover as copy', async () => {
        if (!(await confirmAction('Recover conflict', 'Create an inactive personal copy from this backup?', 'Recover copy', false))) return;
        busy(recover, async () => {
          const result = await api.conflict(entry.id, entry.revision);
          await app.read(result.skill.name, result);
          notify('Recovered a copy of the provider edit.');
        });
      }, 'small');
      return h('div', { class: 'target-row' }, h('div', {}, h('strong', {}, entry.skill), h('p', { class: 'path' }, entry.destination)), recover);
    }));
    if (!conflicts.childElementCount) conflicts.append(h('p', { class: 'muted' }, 'No conflict backups.'));
    trash.replaceChildren(...(data.trash ?? []).map(name => {
      const restore = button('Restore', async () => {
        if (!(await confirmAction('Restore ' + name, 'Restore the skill and its configured deployment targets?', 'Restore', false))) return;
        busy(restore, async () => {
          const result = await api.restore(name);
          await app.read(result.skill.name, result);
          notify('Skill restored.');
        });
      }, 'small');
      return h('div', { class: 'spread' }, name, restore);
    }));
    if (!trash.childElementCount) trash.append(h('p', { class: 'muted' }, 'Trash is empty.'));
  }
  renderTabs();
  return { show: load, leave: () => jobs.cancel() };
}
