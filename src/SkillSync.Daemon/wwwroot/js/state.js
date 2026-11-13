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

export function deployment(skill) {
  if (skill.is_catalog_only) return { key: 'catalog', label: 'Catalog · not synced' };
  const targets = skill.destinations ?? [];
  const issue = targets.find(target => ['error', 'conflict', 'missing'].includes(target.state));
  const synced = targets.filter(target => target.state === 'synced' && target.applied_revision === skill.revision).length;
  if (issue) return { key: 'issue', label: synced ? 'Synced to ' + synced + ' · needs attention' : destinationLabel(issue.state), tone: 'warning', synced };
  if (synced) return { key: 'synced', label: 'Synced to ' + synced, tone: 'ok', synced };
  if (Object.values(skill.targets ?? {}).every(target => target.enabled === false)) return { key: 'disabled', label: 'Disabled' };
  if (targets.some(target => target.state === 'external')) return { key: 'pending', label: 'Provider-owned' };
  return { key: 'pending', label: targets.length ? 'Pending sync' : 'Not deployed' };
}

export function inCollection(skill, collection) {
  if (collection === 'all') return skill.source_category !== 'system';
  if (collection === 'synced') return skill.source_category !== 'system' && Boolean(deployment(skill).synced);
  return skill.source_category === collection;
}

export function filterSkills(skills, filters) {
  return skills.filter(skill =>
    (!filters.tag || skill.tags?.some(tag => slugify(tag) === filters.tag)) &&
    (!filters.origin || skill.origin_provider === filters.origin) &&
    (!filters.provider || skill.destinations?.some(target => target.provider === filters.provider)) &&
    (!filters.status || deployment(skill).key === filters.status));
}
