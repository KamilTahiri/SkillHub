import { api } from '../api.js';
import { Latest, skillKey } from '../state.js';
import { icon } from '../icons.js';
import { h, button, heading, field, modal, busy, notify, errorText, confirmAction, copy, fill, relativeTime } from '../ui.js';
import { applyScope, scopeBadge } from './libraryHealth.js';

/**
 * Projects: give a repository its own skill set. SkillHub writes the chosen skills into the project's
 * .claude/skills, .agents/skills, .gemini/skills and .cursor/rules folders (plus .github/skills, .windsurf/skills, .clinerules
 * or .roo/rules when chosen) and keeps them current.
 */
export function createWorkspaces(root, app) {
  const jobs = new Latest();
  let data = { workspaces: [], targets: [] };
  let selectedId = null;
  const body = h('div', { class: 'stack' });
  const add = button([icon('plus'), 'Add project'], () => addProject(), 'primary');
  root.append(heading('Projects', h('div', { class: 'actions' }, add), [
    h('p', { class: 'muted' }, 'Give a project folder its own set of skills. SkillHub copies them into the project’s AI folders, so everyone who opens the repository gets them.'),
    h('p', { class: 'muted', 'data-umbrella-note': '' }, 'Tip: for a folder that groups several projects (such as Roblox Games), add the umbrella folder once instead of every project. ' +
      'Claude Code reads parent folders; IDE tools opened on a project that has its own .git may stop there, so add that repository too if you work on it in Cursor, Copilot, Windsurf or Antigravity.')]),
  body);

  async function load(focusId) {
    const job = jobs.begin();
    if (!data.workspaces.length) body.replaceChildren(h('p', { role: 'status' }, 'Loading projects…'));
    try {
      const next = await api.workspaces(job.signal);
      if (!job.current()) return;
      data = next;
      if (focusId) selectedId = focusId;
      if (!data.workspaces.some(item => item.id === selectedId)) selectedId = data.workspaces[0]?.id ?? null;
      render();
    } catch (error) {
      if (job.current()) body.replaceChildren(errorText(error.message), button('Retry', () => load()));
    }
  }

  function render() {
    if (!data.workspaces.length) {
      body.replaceChildren(h('div', { class: 'empty workspaces-empty' }, icon('library'),
        h('h2', {}, 'No projects yet'),
        h('p', { class: 'muted' }, 'Add a project folder, choose the skills it needs, and SkillHub writes them into ' +
          '.claude/skills, .agents/skills, .gemini/skills and .cursor/rules inside that project (.github/skills and others are optional). ' +
          'Niche packs work best as Project-only skills: they stay out of your other chats and appear only here.'),
        button([icon('plus'), 'Add your first project'], () => addProject(), 'primary')));
      return;
    }
    const list = h('nav', { class: 'workspace-list', 'aria-label': 'Projects' }, data.workspaces.map(item =>
      h('button', { type: 'button', class: 'workspace-tab', 'aria-pressed': String(item.id === selectedId), 'data-workspace': item.id,
        onclick: () => { selectedId = item.id; render(); } },
        h('strong', {}, item.name),
        h('small', { class: 'muted' }, item.skills.length + (item.skills.length === 1 ? ' skill' : ' skills') + (item.issues.length ? ' · needs attention' : '')))));
    const current = data.workspaces.find(item => item.id === selectedId);
    body.replaceChildren(h('div', { class: 'workspace-layout' }, list, current ? detail(current) : null));
  }

  function detail(workspace) {
    const feedback = h('div', { role: 'status' });
    const library = app.snapshot.skills.filter(skill => skill.source_category !== 'system' || skill.name === '00-skillhub-bootstrap')
      .sort((a, b) => (a.title || a.name).localeCompare(b.title || b.name));
    const chosen = new Set(workspace.skills);
    const targets = new Set(workspace.targets);
    let keepLocal = workspace.keep_local !== false;
    const isGlobal = skill => (skill.always_loaded_tokens ?? 0) > 0;
    const search = h('input', { class: 'input', type: 'search', placeholder: 'Filter skills', 'aria-label': 'Filter skills for ' + workspace.name });
    const count = h('span', { class: 'muted workspace-count' });
    const skillList = h('ul', { class: 'workspace-skills' });
    const dirty = () => {
      const changed = [...chosen].sort().join() !== [...workspace.skills].sort().join() || [...targets].sort().join() !== [...workspace.targets].sort().join() ||
        keepLocal !== (workspace.keep_local !== false);
      save.disabled = !changed;
      count.textContent = chosen.size + (chosen.size === 1 ? ' skill selected' : ' skills selected');
    };
    function renderSkills() {
      const query = search.value.trim().toLowerCase();
      const visible = library.filter(skill => !query || (skill.title + ' ' + skill.name + ' ' + (skill.description ?? '')).toLowerCase().includes(query));
      skillList.replaceChildren(...visible.map(skill => h('li', {},
        h('label', { class: 'workspace-skill' },
          h('input', { type: 'checkbox', checked: chosen.has(skillKey(skill)), 'data-skill': skillKey(skill),
            onchange: event => { if (event.target.checked) chosen.add(skillKey(skill)); else chosen.delete(skillKey(skill)); dirty(); } }),
          h('span', { class: 'workspace-skill-text' }, h('strong', {}, skill.title || skill.name,
            skill.is_catalog_only && h('small', { class: 'muted' }, ' · ' + (skill.origin_provider || 'Built-in'))),
            h('small', { class: 'muted' }, skill.description ?? ''))),
        flags(skill))));
      for (const missing of workspace.missing_skills) if (!query) skillList.append(h('li', { class: 'warning' }, missing + ' is no longer in your library.'));
      // Chosen skills first, so a project's set is visible without scrolling.
      skillList.replaceChildren(...[...skillList.children].sort((a, b) => Number(Boolean(b.querySelector('input:checked'))) - Number(Boolean(a.querySelector('input:checked')))));
    }
    // A skill chosen here that is also synced globally reaches the tools twice; offer the one-click fix.
    function flags(skill) {
      const key = skillKey(skill);
      const redundant = chosen.has(key) && !skill.is_catalog_only && isGlobal(skill);
      const off = chosen.has(key) && skill.scope === 'off';
      const badge = scopeBadge(skill);
      if (!redundant && !off && !badge) return null;
      return h('div', { class: 'workspace-skill-flags' }, badge,
        redundant && h('span', { class: 'redundant-badge', 'data-redundant': skill.name,
          title: 'Also synced to the tools\u2019 global folders: the AI sees it twice and pays its description tokens in every chat.' }, 'Also global \u2014 redundant here'),
        redundant && button('Switch to Project-only', event => busy(event.currentTarget, async () => {
          await applyScope(app, { names: [skill.name] }, 'project');
          await load(workspace.id);
        }), 'small', { 'data-make-project-only': skill.name }),
        off && h('span', { class: 'redundant-badge' }, 'Off \u2014 not written'),
        off && button('Set to Project-only', event => busy(event.currentTarget, async () => {
          await applyScope(app, { names: [skill.name] }, 'project');
          await load(workspace.id);
        }), 'small'));
    }
    search.addEventListener('input', renderSkills);
    renderSkills();
    const targetBoxes = h('div', { class: 'workspace-targets', role: 'group', 'aria-label': 'Project folders to write' },
      data.targets.map(target => h('label', { class: 'chip-toggle' },
        h('input', { type: 'checkbox', checked: targets.has(target.id), 'data-target': target.id,
          onchange: event => { if (event.target.checked) targets.add(target.id); else targets.delete(target.id); dirty(); } }),
        target.label, target.experimental && h('span', { class: 'tier-badge-inline', title: 'Experimental: documented path, not yet verified against an installed copy' }, ' · experimental'))));
    const redundantAll = library.filter(skill => (workspace.also_global ?? []).includes(skillKey(skill)) && !skill.is_catalog_only);
    const fixAll = redundantAll.length > 1 && button('Switch all ' + redundantAll.length + ' to Project-only', event => busy(event.currentTarget, async () => {
      await applyScope(app, { names: redundantAll.map(skill => skill.name) }, 'project');
      await load(workspace.id);
    }), 'small primary', { 'data-fix-all-redundant': '' });
    const keepLocalBox = h('input', { type: 'checkbox', checked: keepLocal, disabled: !workspace.git_repo, 'data-keep-local': '',
      onchange: event => { keepLocal = event.target.checked; dirty(); } });
    const save = button([icon('check'), 'Save & sync project'], () => busy(save, async () => {
      const next = await api.updateWorkspace(workspace.id, { skills: [...chosen], targets: [...targets], keep_local: keepLocal });
      notify('Synced ' + next.skills.length + (next.skills.length === 1 ? ' skill' : ' skills') + ' to ' + next.name + '.', { tone: 'success' });
      await load(workspace.id);
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    const sync = button([icon('sync'), 'Sync now'], () => busy(sync, async () => {
      await api.syncWorkspace(workspace.id);
      await load(workspace.id);
      notify('Project files are up to date.');
    }, error => feedback.replaceChildren(errorText(error.message))));
    const remove = button('Remove project', async () => {
      const deleteFiles = await confirmAction('Remove ' + workspace.name,
        'Stop managing this project? SkillHub will delete the unedited skill files it wrote there. Files you edited in the project stay.', 'Remove project');
      if (!deleteFiles) return;
      busy(remove, async () => {
        const result = await api.removeWorkspace(workspace.id, true);
        notify('Removed ' + workspace.name + (result.kept?.length ? '. ' + result.kept.length + ' edited files were kept.' : '.'));
        selectedId = null;
        await load();
      }, error => feedback.replaceChildren(errorText(error.message)));
    }, 'quiet danger');
    dirty();
    return h('section', { class: 'workspace-detail', 'aria-label': workspace.name },
      h('header', { class: 'workspace-head' },
        h('div', {}, h('h2', {}, workspace.name),
          h('p', { class: 'path muted' }, workspace.path, ' ', button([icon('copy')], () => copy(workspace.path), 'small icon-only quiet', { 'aria-label': 'Copy project path', title: 'Copy path' })),
          h('p', { class: 'muted' }, workspace.exists ? workspace.file_count + ' files written · ' +
            (workspace.last_synced_at ? 'synced ' + relativeTime(workspace.last_synced_at) : 'not synced yet') : 'The folder was not found.')),
        h('div', { class: 'actions' }, sync, remove)),
      workspace.issues.length > 0 && h('div', { class: 'workspace-issues' }, h('strong', {}, 'Needs attention'),
        h('ul', {}, workspace.issues.map(issue => h('li', {}, h('code', { class: 'path' }, issue.path), ' — ', issue.problem)))),
      redundantAll.length > 0 && h('div', { class: 'workspace-issues', 'data-redundant-summary': '' },
        h('strong', {}, redundantAll.length + (redundantAll.length === 1 ? ' skill is' : ' skills are') + ' also synced globally'),
        h('p', { class: 'muted' }, 'The AI tools load them from your global folders and from this project. Make them Project-only so they appear only in projects that choose them.'),
        fixAll || ''),
      h('div', { class: 'settings-group' }, h('h3', {}, 'Write skills to'), targetBoxes,
        h('small', { class: 'field-help' }, 'Claude, Codex/Antigravity, Gemini and Cursor are written by default. Copilot, Windsurf, Cline and Roo Code are experimental and opt-in; Cline and Roo Code load every rule into every task.')),
      h('div', { class: 'settings-group' }, h('h3', {}, 'Git'),
        h('label', { class: 'checkbox-row' }, keepLocalBox, ' Keep local (add generated skill folders to .git/info/exclude)'),
        h('small', { class: 'field-help' }, workspace.git_repo
          ? 'On: the generated folders stay out of git status and commits, which suits solo work. Off: commit them so everyone who clones the repository gets the skills.'
          : 'This folder is not a Git repository, so there is nothing to exclude.')),
      h('div', { class: 'settings-group' }, h('div', { class: 'spread' }, h('h3', {}, 'Skills for this project'), count), search, skillList),
      h('footer', { class: 'workspace-foot' }, feedback, save,
        h('small', { class: 'field-help' }, 'Edit skills in SkillHub; project copies update automatically. Copies edited inside the project are never overwritten.')));
  }

  function addProject() {
    const path = h('input', { class: 'input', type: 'text', placeholder: 'C:\\Users\\you\\code\\my-project', autofocus: true });
    const name = h('input', { class: 'input', type: 'text', placeholder: 'Optional, defaults to the folder name' });
    const feedback = h('div', { role: 'status' });
    const create = button('Add project', () => busy(create, async () => {
      const created = await api.addWorkspace(path.value.trim(), name.value.trim() || null);
      dialog.close();
      await load(created.id);
      notify('Added ' + created.name + '. Choose its skills, then Save & sync.');
    }, error => feedback.replaceChildren(errorText(error.message))), 'primary');
    path.addEventListener('keydown', event => { if (event.key === 'Enter') create.click(); });
    const dialog = modal('Add a project', [field('Project folder', path, 'Paste the full path of the repository folder.'), field('Name', name), feedback], create);
  }

  return {
    show: () => load(),
    leave: () => jobs.cancel(),
    addProject
  };
}
