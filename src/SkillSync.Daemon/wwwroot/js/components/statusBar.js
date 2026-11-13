import { api } from '../api.js?v=20261113-v12';
import { Latest } from '../state.js?v=20261113-v12';
import { h, button, heading, panel, field, select, disclosure, confirmAction, errorText, busy, notify } from '../ui.js?v=20261113-v12';

export function createSettings(root, app) {
  let tab = 'Connections';
  const jobs = new Latest();
  const diagnosticsJob = new Latest();
  const restartRequired = new Set();
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
    const providers = { codex: 'codex', 'gemini-cli': 'gemini', antigravity: 'gemini', 'claude-code': 'claude', 'claude-desktop': 'claude', cursor: 'cursor' };
    const grouped = Object.groupBy(clients, client => providers[client.client_id] ?? 'other');
    content.replaceChildren(...Object.entries(grouped).map(([group, items]) => h('section', { class: 'settings-group' },
      h('h2', {}, group === 'gemini' ? 'Gemini and Antigravity' : group === 'codex' ? 'Codex' : group === 'claude' ? 'Claude' : 'Cursor'),
      items.map(client => {
        const available = client.detected ?? (client.file_exists || client.parent_exists);
        const connection = client.error ? 'Needs attention' : restartRequired.has(client.client_id) ? 'Restart required'
          : client.is_registered ? 'Registered' : available ? 'Detected' : 'Unavailable';
        const action = client.is_registered ? 'repair' : 'connect';
        const feedback = h('div', { role: 'status' });
        const update = button(client.is_registered ? 'Repair' : 'Connect', () => updateClient(client, action, update, feedback), 'small',
          { disabled: !available && !client.is_registered, 'aria-label': action + ' ' + client.display_name });
        const remove = client.is_registered && button('Remove', () => updateClient(client, 'remove', remove, feedback), 'small danger');
        const provider = app.snapshot.providers.find(item => item.provider === group);
        const folder = client.client_id === 'antigravity' && provider?.provider_root
          ? provider.provider_root + '/antigravity/skills' : provider?.skills_root;
        return h('div', { class: 'settings-row stack' }, h('div', { class: 'spread' },
          h('div', {}, h('h3', {}, client.display_name), h('p', { class: client.error ? 'warning' : 'muted' }, connection)),
          h('div', { class: 'actions' }, update, remove)),
          h('details', {}, h('summary', {}, 'Configuration and skill folders'),
            h('code', { class: 'path' }, client.config_path), folder && h('p', { class: 'path' }, folder)),
          client.error && errorText(client.error), feedback);
      }))));
  }
  async function updateClient(client, action, control, feedback) {
    const label = action[0].toUpperCase() + action.slice(1);
    if (!(await confirmAction(label + ' ' + client.display_name,
      label + ' SkillHub in this client configuration? An existing file is backed up before a change.', label, action === 'remove'))) return;
    busy(control, async () => {
      const result = await api.client(client.client_id, action);
      if (result.error) throw new Error(result.error);
      restartRequired.add(client.client_id);
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
