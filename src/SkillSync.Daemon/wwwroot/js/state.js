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

export const PROVIDERS = { codex: 'Codex', gemini: 'Gemini and Antigravity', claude: 'Claude', cursor: 'Cursor' };
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

export function targetIssues(skill, installed) {
  return (skill.destinations ?? []).filter(target => installed.includes(target.provider) &&
    skill.targets?.[target.provider]?.enabled !== false && ['error', 'conflict', 'missing'].includes(target.state))
    .map(target => ({ ...target, label: target.provider === 'gemini'
      ? (/[\\/]antigravity[\\/]/i.test(target.path) ? 'Antigravity' : 'Gemini') : PROVIDERS[target.provider] }));
}

export function deployment(skill, installed) {
  const synced = [];
  const issues = [];
  if (skill.is_catalog_only) return { key: 'catalog', label: 'Catalog · not synced', synced, issues };
  const enabled = installed.filter(id => skill.targets?.[id]?.enabled !== false);
  const targets = (skill.destinations ?? []).filter(target => enabled.includes(target.provider));
  for (const id of enabled) {
    const destinations = targets.filter(target => target.provider === id);
    if (destinations.some(target => target.state === 'synced' && target.applied_revision === skill.revision)) synced.push(id);
    if (destinations.some(target => ['error', 'conflict', 'missing'].includes(target.state))) issues.push(id);
  }
  if (issues.length) return { key: 'issue', label: 'Needs attention', tone: 'warning', synced, issues };
  if (synced.length) return { key: 'synced', label: 'Synced', tone: 'ok', synced, issues };
  if (Object.values(skill.targets ?? {}).every(target => target.enabled === false) || installed.length && !enabled.length)
    return { key: 'disabled', label: 'Disabled', synced, issues };
  if (targets.some(target => target.state === 'external')) return { key: 'pending', label: 'Provider-owned', synced, issues };
  return { key: 'pending', label: installed.length ? targets.length ? 'Pending sync' : 'Not deployed' : 'No providers detected', synced, issues };
}

export function inCollection(skill, collection, sync) {
  if (collection === 'all') return skill.source_category !== 'system';
  if (collection === 'synced') return skill.source_category !== 'system' && sync.synced.length > 0;
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
