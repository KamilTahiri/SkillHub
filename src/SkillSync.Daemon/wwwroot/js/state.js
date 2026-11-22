export class Latest {
  #generation = 0;
  #controller;

  cancel() {
    this.#generation++;
    this.#controller?.abort();
  }

  begin() {
    this.cancel();
    const generation = this.#generation;
    const controller = new AbortController();
    this.#controller = controller;
    return { signal: controller.signal, current: () => generation === this.#generation && !controller.signal.aborted };
  }
}

export const PROVIDERS = { codex: 'Codex', gemini: 'Gemini and Antigravity', claude: 'Claude', cursor: 'Cursor',
  copilot: 'GitHub Copilot', windsurf: 'Windsurf', cline: 'Cline', roocode: 'Roo Code' };
// Cline and Roo Code load every rule into every conversation, so skills reach them only when turned on per skill.
export const OPT_IN_PROVIDERS = new Set(['cline', 'roocode']);
export const COLLECTIONS = { user: 'My Skills', synced: 'Active & Synced', template: 'Built-in & Templates', system: 'System' };
export const skillKey = skill => skill.is_catalog_only ? skill.catalog_id ?? skill.name : skill.name;

export function slugify(value) {
  return value.trim().toLowerCase().replaceAll('c#', 'csharp').replaceAll('f#', 'fsharp')
    .replaceAll('c++', 'cpp').replaceAll('.net', 'dotnet').replace(/[^\p{L}\p{Nd}-]+/gu, '-')
    .replace(/-+/g, '-').replace(/^-|-$/g, '');
}

export function tagSlug(value) {
  const tag = slugify(value);
  if (!tag || tag.length > 64 || ['system', 'skillhub', 'meta-skill'].includes(tag)) {
    throw new Error('Use a tag of 1 to 64 letters, numbers, or hyphens. System tags are reserved.');
  }
  return tag;
}

export const destinationLabel = state => ({
  synced: 'Synced', disabled: 'Disabled', withdrawn: 'Disabled', missing: 'Missing · needs repair',
  conflict: 'Conflict · needs review', error: 'Deployment failed', external: 'Provider-owned', pending: 'Pending'
})[state] ?? 'Pending';

export function installedProviders(providers) {
  return Object.keys(PROVIDERS).filter(id => providers.some(provider => provider.provider === id && provider.is_installed));
}

export const isTargetEnabled = (skill, id) => skill.targets?.[id]?.enabled ?? !OPT_IN_PROVIDERS.has(id);

export function targetIssues(skill, installed) {
  return (skill.destinations ?? []).filter(target => installed.includes(target.provider) &&
    isTargetEnabled(skill, target.provider) && ['error', 'conflict', 'missing'].includes(target.state))
    .map(target => ({ ...target, label: target.provider === 'gemini'
      ? (/[\\/]\.gemini[\\/]config[\\/]skills[\\/]/i.test(target.path) ? 'Antigravity IDE'
        : /[\\/]antigravity[\\/]/i.test(target.path) ? 'Antigravity' : 'Gemini') : PROVIDERS[target.provider] }));
}

export function deployment(skill, installed) {
  const synced = [];
  const issues = [];
  if (skill.is_catalog_only) return { key: 'catalog', label: 'Catalog · not synced', synced, issues };
  const enabled = installed.filter(id => isTargetEnabled(skill, id));
  const targets = (skill.destinations ?? []).filter(target => enabled.includes(target.provider));
  for (const id of enabled) {
    const destinations = targets.filter(target => target.provider === id);
    if (destinations.some(target => target.state === 'synced' && target.applied_revision === skill.revision)) synced.push(id);
    if (destinations.some(target => ['error', 'conflict', 'missing'].includes(target.state))) issues.push(id);
  }
  if (issues.length) return { key: 'issue', label: 'Needs attention', tone: 'warning', synced, issues };
  if (synced.length) return { key: 'synced', label: 'Synced', tone: 'ok', synced, issues };
  // Project-only and Off skills are never written to the tools' global folders, so nothing is pending.
  if (skill.scope === 'project') return { key: 'project', label: 'Project-only · not synced globally', synced, issues };
  if (skill.scope === 'off') return { key: 'off', label: 'Off · not synced', synced, issues };
  if (Object.values(skill.targets ?? {}).every(target => target.enabled === false) || installed.length && !enabled.length)
    return { key: 'disabled', label: 'Disabled', synced, issues };
  if (targets.some(target => target.state === 'external')) return { key: 'pending', label: 'Provider-owned', synced, issues };
  return { key: 'pending', label: installed.length ? targets.length ? 'Pending sync' : 'Not deployed' : 'No providers detected', synced, issues };
}

export const BOOTSTRAP_SKILL = '00-skillhub-bootstrap';

/** SkillHub's own protocol skill: read-only and shipped with the app, but listed with the user's skills because it is always loaded. */
export const isBuiltInProtocol = skill => skill.source_category === 'system' && skill.name === BOOTSTRAP_SKILL;

export function inCollection(skill, collection, sync) {
  if (collection === 'all') return skill.source_category !== 'system' || isBuiltInProtocol(skill);
  if (collection === 'synced') return (skill.source_category !== 'system' || isBuiltInProtocol(skill)) && sync.synced.length > 0;
  if (collection === 'user') return skill.source_category === 'user' || isBuiltInProtocol(skill);
  return skill.source_category === collection;
}

export function matchesFilters(skill, filters, sync) {
  const tags = new Set(skill.tags?.map(slugify));
  const tagMatch = !filters.tags.length || (filters.tagMode === 'any'
    ? filters.tags.some(tag => tags.has(tag)) : filters.tags.every(tag => tags.has(tag)));
  return tagMatch &&
    (!filters.origin || skill.origin_provider === filters.origin) &&
    (!filters.provider || sync.synced.includes(filters.provider) || sync.issues.includes(filters.provider)) &&
    (!filters.status || sync.key === filters.status);
}
