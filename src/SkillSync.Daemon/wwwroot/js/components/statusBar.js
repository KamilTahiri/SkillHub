import { api } from '../api.js';
import { Latest, deployment, installedProviders } from '../state.js';
import { icon } from '../icons.js';
import { h, button, heading, panel, field, select, disclosure, confirmAction, errorText, busy, notify, copy, fill, modal, download, relativeTime as when } from '../ui.js';
import { renderDiff } from '../diff.js';
import { exportPack, exportPicker } from './skillPacks.js';

const REAPPLY_HINT = 'Re-applies SkillHub’s MCP connection in this tool’s config file if an update overwrote it.';
const CONNECTION_GROUPS = { codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity', claude: 'Claude', cursor: 'Cursor',
  copilot: 'GitHub Copilot', windsurf: 'Windsurf', cline: 'Cline', roocode: 'Roo Code' };
// SkillHub syncs skills to these tools but does not write their MCP settings yet.
const FOLDER_ONLY = new Set(['copilot', 'windsurf', 'cline', 'roocode']);
const PROVIDER_LABELS = { codex: 'ChatGPT / Codex', gemini: 'Gemini & Antigravity', claude: 'Claude', cursor: 'Cursor',
  copilot: 'GitHub Copilot', windsurf: 'Windsurf', cline: 'Cline', roocode: 'Roo Code', canonical: 'SkillHub' };
const PROVIDER_ICONS = { codex: 'codex', gemini: 'gemini', claude: 'claude', cursor: 'cursor', copilot: 'copilot', windsurf: 'windsurf',
  cline: 'cline', roocode: 'roocode', canonical: 'library', home: 'explore' };
// Engine activity kinds, shown as plain-language badges in Recent Operations.
const ACTIVITY = {
  synced: { label: 'Synced', icon: 'sync', tone: 'ok' },
  ingested: { label: 'Imported', icon: 'download', tone: 'info' },
  updated: { label: 'Updated', icon: 'edit', tone: 'info' },
  conflict: { label: 'Backup Created', icon: 'info', tone: 'warn' },
  review: { label: 'Reviewed', icon: 'check', tone: 'ok' },
  recognized: { label: 'Linked', icon: 'check', tone: 'ok' },
  withdrawn: { label: 'Removed copy', icon: 'unplug', tone: 'muted' },
  deleted: { label: 'Moved to Trash', icon: 'close', tone: 'muted' },
  error: { label: 'Problem', icon: 'close', tone: 'warn' },
  default: { label: 'Activity', icon: 'info', tone: 'muted' }
};
const CLIENT_PROVIDERS = { codex: 'codex', 'gemini-cli': 'gemini', antigravity: 'gemini', 'antigravity-ide': 'gemini', 'claude-code': 'claude', 'claude-desktop': 'claude', cursor: 'cursor' };

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
    fill(content,
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
    if (group.synced > 0) return ['Skills synced', 'info'];
    return ['Detected', 'muted'];
  }

  // Folder sync and MCP registration are separate: skills reach a tool through its skills folder,
  // while the MCP connection lets that tool create and edit skills through SkillHub.
  function connectionCard(group) {
    const [state, tone] = groupState(group);
    const registered = group.items.filter(item => item.client.is_registered).map(item => item.client.display_name);
    const evidence = group.items.map(item => item.client.install_evidence).find(Boolean);
    const folder = group.provider?.skills_root;
    const skillsLine = h('li', { class: 'capability', 'data-state': group.synced > 0 ? 'ok' : 'muted' }, icon('library'),
      h('span', {}, h('strong', {}, 'Skills folder'), ' ',
        group.synced > 0
          ? group.synced + (group.synced === 1 ? ' skill synced' : ' skills synced') + (folder ? ' to ' + folder.replace(/\\/g, '/').replace(/^.*?\/(\.[^/]+)/, '~/$1') : '')
          : 'No skills synced yet',
        group.issues > 0 && h('span', { class: 'warning' }, ' · ' + group.issues + (group.issues === 1 ? ' needs' : ' need') + ' review')));
    const mcpLine = h('li', { class: 'capability', 'data-state': registered.length ? 'ok' : 'muted' }, icon('plug'),
      h('span', {}, h('strong', {}, 'SkillHub tools (MCP)'), ' ',
        FOLDER_ONLY.has(group.id) ? 'Add SkillHub to this tool’s MCP settings yourself for now (command: SkillHub.exe mcp-stdio).'
        : registered.length ? 'Connected in ' + registered.join(' and ')
          : 'Not connected. Connect below so ' + group.label.split(' ')[0] + ' can create and update skills through SkillHub.'));
    return h('section', { class: 'connection-card', 'data-state': tone, 'aria-label': group.label },
      h('header', { class: 'connection-head' },
        h('span', { class: 'provider-mark', 'data-provider': group.id }, icon(group.id)),
        h('div', { class: 'connection-title' }, h('h2', {}, group.label), evidence && h('p', { class: 'muted' }, evidence)),
        h('span', { class: 'status-badge ' + tone }, h('span', { class: 'status-badge-dot', 'aria-hidden': 'true' }), state)),
      h('ul', { class: 'capabilities' }, skillsLine, mcpLine),
      group.id === 'claude' && claudeSkillsNote(),
      group.items.length > 0 && h('ul', { class: 'connection-clients' }, group.items.map(item => clientRow(group, item))));
  }

  // Claude Desktop's Skills screen lists skills saved in the claude.ai account. The app downloads them into a cache folder
  // and deletes anything else it finds there, so SkillHub cannot add skills to that screen from disk.
  function claudeSkillsNote() {
    const skills = app.snapshot.skills.filter(skill => skill.source_category === 'user' && !skill.is_catalog_only);
    const feedback = h('div', { role: 'status' });
    const zips = button([icon('download'), 'Download ZIPs for Claude upload'], () => busy(zips, async () => {
      for (const skill of skills) {
        const response = await fetch('/api/v1/skills/' + encodeURIComponent(skill.name) + '/bundle?format=claude');
        if (!response.ok) throw new Error('Could not prepare ' + skill.name + '.');
        download(skill.name + '.zip', await response.blob(), 'application/zip');
        await new Promise(resolve => setTimeout(resolve, 250));
      }
      feedback.replaceChildren(h('p', { class: 'muted' }, 'Saved ' + skills.length + ' ZIP files to Downloads. In Claude Desktop open Settings › Capabilities › Skills › Upload skill and choose each file.'));
    }, error => feedback.replaceChildren(errorText(error.message))), 'small', { disabled: !skills.length });
    return h('div', { class: 'claude-skills-note' }, icon('info'),
      h('div', { class: 'stack' },
        h('strong', {}, 'Where Claude reads your skills'),
        h('ul', {},
          h('li', {}, h('strong', {}, 'Claude Code and the Code tab in Claude Desktop'), ' load ~/.claude/skills, which SkillHub keeps in sync.'),
          h('li', {}, h('strong', {}, 'The Skills screen in Claude Desktop chat'), ' shows skills saved in your claude.ai account. Claude downloads those into its own cache and removes anything else from it, so they cannot be synced from disk. Upload a skill there once to use it in chat.')),
        h('div', { class: 'actions wrap' }, zips), feedback));
  }

  function clientRow(group, { client, available }) {
    const feedback = h('div', { role: 'status' });
    const [state, tone] = client.error ? ['Needs attention', 'warn'] : client.restart_required ? ['Restart required', 'warn']
      : client.is_registered ? ['MCP connected', 'ok'] : available ? ['MCP not connected', 'muted'] : ['Not installed', 'muted'];
    const action = client.is_registered ? 'repair' : 'connect';
    const update = button([icon(client.is_registered ? 'wrench' : 'plug'), client.is_registered ? 'Re-apply connection' : 'Connect'],
      () => updateClient(client, action, update, feedback), client.is_registered ? 'small' : 'primary small',
      { disabled: !available && !client.is_registered, title: client.is_registered ? REAPPLY_HINT : 'Add SkillHub to this tool’s MCP config file.',
        'aria-label': (client.is_registered ? 'Re-apply SkillHub connection for ' : 'Connect ') + client.display_name });
    const folder = client.client_id === 'antigravity' && group.provider?.provider_root
      ? group.provider.provider_root + '/antigravity/skills'
      : client.client_id === 'antigravity-ide' && group.provider?.provider_root
        ? group.provider.provider_root + '/config/skills' : group.provider?.skills_root;
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
    // Claude Desktop rewrites its config while it runs; applying the change needs Claude closed first.
    const claudeRestart = client.client_id === 'claude-desktop' && client.app_running && (!client.is_registered || client.restart_required) &&
      button([icon('restart'), 'Restart Claude to apply'], async event => {
        const control = event.currentTarget;
        if (!await confirmAction('Restart Claude Desktop',
          'SkillHub will close Claude Desktop, add its MCP connection while Claude is closed, check it, and start Claude again. Your chats are saved in your account. Continue?',
          'Restart Claude', false)) return;
        busy(control, async () => {
          const result = await api.restartClaude();
          if (result.error) throw new Error(result.error);
          notify(result.registered ? 'SkillHub is connected to Claude Desktop' + (result.relaunched ? ' and Claude is starting again.' : '.') : 'Claude restarted, but the connection was not saved.',
            { tone: result.registered ? 'success' : 'warning' });
          await load();
        }, error => feedback.replaceChildren(errorText(error.message)));
      }, 'primary small', { title: 'Close Claude, write the MCP setting, and start Claude again' });
    const runningHint = client.client_id === 'claude-desktop' && client.app_running && !client.is_registered &&
      h('p', { class: 'field-help' }, 'Claude Desktop is running. It overwrites its config when it saves, so connect with “Restart Claude to apply”.');
    return h('li', { class: 'connection-client' },
      h('div', { class: 'connection-client-main' },
        h('div', { class: 'connection-client-name' }, h('strong', {}, client.display_name),
          h('span', { class: 'status-badge small ' + tone }, h('span', { class: 'status-badge-dot', 'aria-hidden': 'true' }), state),
          client.is_registered && h('small', { class: 'field-help' }, REAPPLY_HINT)),
        h('div', { class: 'actions' }, claudeRestart, claudeRestart && !client.is_registered ? null : update, menu)),
      runningHint, claudeRestart ? null : restart, client.error && errorText(client.error), feedback);
  }

  async function updateClient(client, action, control, feedback) {
    const label = { remove: 'Disconnect', repair: 'Re-apply connection', connect: 'Connect' }[action] ?? action;
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
    const backup = button([icon('download'), 'Backup All My Skills'], () => busy(backup, () => exportPack([]),
      error => notify(error.message, { tone: 'error' })), 'primary');
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
        toggle('Keep SkillHub running in the tray', 'tray', data.close_to_tray)),
      h('section', { class: 'settings-group' }, h('h2', {}, 'Backup & skill packs'),
        h('p', { class: 'muted' }, 'A .skillpack.zip holds each SKILL.md, every companion file and a manifest. Import one from Explore › Import from link or file.'),
        h('div', { class: 'actions wrap' },
          backup,
          button('Export selected skills…', () => exportPicker(app)),
          button('Import a skill pack…', () => app.navigate('explore').then(done => done && app.directImport?.())))),
      h('section', { class: 'settings-group' }, h('h2', {}, 'Welcome guide'),
        h('div', { class: 'settings-row spread' }, h('p', { class: 'muted' }, 'See which AI tools SkillHub found, what is in your library, and how to use it from chat.'),
          button('Run Welcome Guide', () => app.showOnboarding()))));
  }

  function renderDiagnostics(data) {
    const feedback = h('div', { role: 'status' });
    const body = h('div', { class: 'stack' });
    const subnav = h('nav', { class: 'settings-tabs', 'aria-label': 'Diagnostics sections' });
    let current = data;
    let showSnapshots = true;
    const verify = button([icon('restart'), 'Verify & Sync All Folders'], () => busy(verify, async () => {
      const response = await api.resync();
      const report = response.report ?? {};
      const count = (value, one, many) => (value ?? 0) + ' ' + ((value ?? 0) === 1 ? one : many);
      feedback.replaceChildren(h('p', { class: 'muted' }, 'Checked every folder: ' + [count(report.projected, 'skill updated', 'skills updated'),
        count(report.syncedDestinations, 'copy in sync', 'copies in sync'), count(report.missingDestinations, 'missing copy restored', 'missing copies restored'),
        count(report.conflictedDestinations, 'needs review', 'need review'), count(report.failedDestinations, 'failed', 'failed')].join(' · ') + '.'));
      await refreshDiagnostics();
      app.refresh();
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const sections = ['Needs Review', 'Recent Operations', 'Trash'];
    subnav.append(...sections.map(name => button(name, event => {
      diagnosticsTab = name;
      for (const tabButton of subnav.querySelectorAll('button')) tabButton.setAttribute('aria-pressed', String(tabButton === event.currentTarget));
      renderSection();
    }, '', { 'aria-pressed': String(name === diagnosticsTab) })));
    const watcher = data.watcher ?? {};
    const folders = watcher.folders ?? [];
    const folderList = folders.length > 0 && disclosure('Show the ' + folders.length + ' watched ' + (folders.length === 1 ? 'folder' : 'folders'),
      h('ul', { class: 'watched-folders' }, folders.map(folder => h('li', { class: 'watched-folder' },
        h('span', { class: 'provider-mark', 'data-provider': folder.provider }, icon(PROVIDER_ICONS[folder.provider] ?? 'library')),
        h('div', { class: 'watched-folder-text' }, h('strong', {}, folder.label),
          h('code', { class: 'path' }, folder.path), folder.purpose && h('small', { class: 'muted' }, folder.purpose)),
        h('div', { class: 'actions' },
          button([icon('copy'), 'Copy path'], () => copy(folder.path), 'quiet small', { 'aria-label': 'Copy ' + folder.path }),
          button('Open folder', event => busy(event.currentTarget, () => api.openFolder(folder.path),
            error => feedback.replaceChildren(errorText(error.message))), 'quiet small', { 'aria-label': 'Open ' + folder.path }))))));
    fill(content,
      h('section', { class: 'settings-group' },
        h('div', { class: 'spread' },
          h('div', {}, h('p', { class: 'muted' }, watcher.running
            ? 'Watching ' + (watcher.roots ?? folders.length) + ' folders' : 'The folder watcher is starting or offline'),
            h('small', { class: 'field-help' }, 'SkillHub notices edits in these folders and copies them to your other AI tools.')),
          h('div', { class: 'verify-action' }, verify,
            h('small', { class: 'field-help' }, 'Checks all AI tool folders on disk, repairs missing copies, and checks for external edits.'))),
        folderList),
      watcher.last_error && errorText('The folder watcher reported: ' + watcher.last_error), feedback, subnav, body);
    updateDiagnostics = next => { current = next; renderSection(); };

    function backupRow(entry) {
      const restoreReplace = entry.skill_exists !== false && button('Restore & Replace Current Skill', async event => {
        const control = event.currentTarget;
        if (!await confirmAction('Restore & replace ' + entry.skill,
          'Replace the current ' + entry.skill + ' with this backup and sync it to your AI tools? The current version stays in the skill’s History, so you can undo this.',
          'Restore & replace')) return;
        busy(control, async () => {
          await api.restoreConflict(entry.id, entry.revision);
          notify('Restored the backup over ' + entry.skill + '.');
          app.refresh();
          await refreshDiagnostics();
        }, error => feedback.replaceChildren(errorText(error.message)));
      }, 'small');
      const restoreNew = button('Restore as New Separate Skill', async event => {
        const control = event.currentTarget;
        if (!await confirmAction('Restore as a new skill', 'Save this backup as a separate skill in My Skills? It starts with sync turned off, and the current skill is not changed.', 'Restore as new skill', false)) return;
        busy(control, async () => {
          await api.conflict(entry.id, entry.revision);
          notify('Saved the backup as a new skill in My Skills.');
          app.refresh();
        }, error => feedback.replaceChildren(errorText(error.message)));
      }, 'small');
      const undo = entry.can_undo && button('Undo last replacement', async event => {
        const control = event.currentTarget;
        if (!await confirmAction('Undo replacement', 'Put this tool’s saved files back and stop SkillHub from overwriting this copy?', 'Undo replacement')) return;
        busy(control, async () => {
          await api.undoConflict(entry.id, entry.revision);
          notify('Restored the tool’s saved files.');
          app.refresh();
          await refreshDiagnostics();
        }, error => feedback.replaceChildren(errorText(error.message)));
      }, 'small quiet');
      const compare = button('Preview / Compare Changes', event => busy(event.currentTarget, async () => {
        const preview = await api.conflictPreview(entry.id);
        const changed = [...new Set([...Object.keys(preview.backup_files ?? {}), ...Object.keys(preview.current_files ?? {})])]
          .filter(path => (preview.backup_files ?? {})[path] !== (preview.current_files ?? {})[path]);
        modal('Backup of ' + entry.skill, h('div', { class: 'stack' },
          h('p', { class: 'muted' }, (preview.provider ? (PROVIDER_LABELS[preview.provider] ?? preview.provider) + ' copy' : 'Tool copy') + ' saved ' + when(entry.saved_at) + '.'),
          preview.current_markdown == null
            ? h('div', { class: 'stack' }, h('p', { class: 'warning' }, 'The skill no longer exists in SkillHub. This is the full backup.'),
              h('pre', { class: 'diff-plain' }, preview.backup_markdown))
            : renderDiff(preview.current_markdown, preview.backup_markdown, { labels: ['Current skill', 'Backup'] }),
          changed.length > 0 && disclosure('Bundle files that differ (' + changed.length + ')', h('div', { class: 'stack' },
            changed.map(path => disclosure(path, renderDiff((preview.current_files ?? {})[path] ?? '', (preview.backup_files ?? {})[path] ?? '',
              { labels: ['Current skill', 'Backup'] })))))));
      }, error => feedback.replaceChildren(errorText(error.message))), 'quiet small');
      return h('div', { class: 'backup-row' },
        h('div', { class: 'backup-text' },
          h('div', { class: 'backup-title' }, h('strong', {}, entry.skill),
            entry.provider && h('span', { class: 'status-badge small muted' }, (PROVIDER_LABELS[entry.provider] ?? entry.provider) + ' copy'),
            h('small', { class: 'muted', title: entry.saved_at ? new Date(entry.saved_at).toLocaleString() : '' }, when(entry.saved_at))),
          h('p', {}, entry.reason ?? 'This tool’s copy differed from SkillHub’s version, so it was saved before syncing.'),
          h('p', { class: 'path muted' }, entry.destination + (entry.file_count > 1 ? ' · ' + entry.file_count + ' files' : ''))),
        h('div', { class: 'actions wrap' }, compare, restoreReplace, restoreNew, undo));
    }

    function activityRow(event) {
      const kind = ACTIVITY[event.action ?? event.event_type] ?? ACTIVITY.default;
      const provider = PROVIDER_LABELS[event.source];
      const slug = event.skill_slug;
      const skill = slug && app.snapshot.skills.find(item => item.name === slug);
      return h('li', { class: 'activity-row', 'data-tone': kind.tone },
        h('span', { class: 'activity-icon', 'aria-hidden': 'true' }, icon(kind.icon)),
        h('div', { class: 'activity-text' },
          h('div', { class: 'activity-head' },
            h('span', { class: 'status-badge small ' + kind.tone }, kind.label),
            slug ? (skill ? button(skill.title || slug, () => app.read(slug), 'link small') : h('strong', {}, slug)) : null,
            provider && h('span', { class: 'muted' }, (kind.label === 'Imported' ? 'from ' : '→ ') + provider)),
          event.details && h('small', { class: 'muted activity-detail' }, event.details)),
        h('time', { class: 'muted', datetime: event.timestamp_utc, title: new Date(event.timestamp_utc).toLocaleString() }, when(event.timestamp_utc)));
    }

    function renderSection() {
      const focused = body.contains(document.activeElement) ? document.activeElement.dataset.key : null;
      if (diagnosticsTab === 'Needs Review') {
        const issues = current.issues ?? [];
        body.replaceChildren(...issues.map(issue => h('div', { class: 'settings-row spread' },
          h('div', {}, h('strong', {}, issue.title), h('p', { class: 'warning' }, [issue.label, issue.state].filter(Boolean).join(' · '))),
          h('div', { class: 'actions' }, button('Open skill', () => app.read(issue.skill), 'quiet small'),
            button('Review ' + issue.label, () => app.review(issue.skill, issue), 'small', { 'data-key': issue.path })))));
        if (!issues.length) body.append(h('p', { class: 'muted' }, 'Nothing needs review. Every AI tool has the current version of your skills.'));
        const backups = current.conflicts ?? [];
        if (backups.length) {
          const retained = h('details', { class: 'disclosure backups' },
            h('summary', {}, 'Conflict & Safety Backups (' + backups.length + ')'),
            h('p', { class: 'field-help' }, 'When an AI tool’s copy of a skill differs from SkillHub’s, SkillHub saves that copy here before syncing, so nothing is lost.'),
            h('div', { class: 'stack' }, backups.map(backupRow)));
          retained.open = showSnapshots;
          retained.addEventListener('toggle', () => { showSnapshots = retained.open; });
          body.append(retained);
        }
      } else if (diagnosticsTab === 'Recent Operations') {
        const all = current.recent_activity ?? [];
        const kindOf = event => event.action ?? event.event_type ?? 'activity';
        const kinds = [...new Set(all.map(kindOf))];
        const filter = select([['all', 'All activity'], ...kinds.map(kind => [kind, (ACTIVITY[kind] ?? ACTIVITY.default).label])], diagnosticsFilter, {
          'aria-label': 'Filter activity', 'data-key': 'operation-filter',
          onchange: event => { diagnosticsFilter = event.target.value; renderSection(); }
        });
        const events = all.filter(event => diagnosticsFilter === 'all' || kindOf(event) === diagnosticsFilter);
        body.replaceChildren(filter, events.length ? h('ul', { class: 'activity' }, events.map(activityRow)) : h('p', { class: 'muted' }, 'No matching activity yet.'));
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
    renderSection();
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
