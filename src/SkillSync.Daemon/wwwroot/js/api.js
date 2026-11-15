export class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

export async function request(path, { method = 'GET', data, signal } = {}) {
  const response = await fetch('/api/v1' + path, {
    method, signal, cache: 'no-store',
    ...(data === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) })
  });
  if (!response.ok) {
    let detail;
    try { detail = await response.json(); } catch { /* The host can return a response without a JSON body. */ }
    throw new ApiError(detail?.error ?? detail?.detail ?? ('Request failed (' + response.status + ').'), response.status);
  }
  return response.status === 204 ? null : response.json();
}

const encode = encodeURIComponent;
const pathFor = name => '/skills/' + encode(name);
export const api = {
  reviewDestination: (name, path, signal) => request(pathFor(name) + '/destination-review?' + new URLSearchParams({ path }), { signal }),
  resolveDestination: (name, path, version, action) => request(pathFor(name) + '/destination-review', { method: 'POST', data: { path, version, action } }),
  setPreference: (key, enabled) => request('/preferences/' + key, { method: 'PATCH', data: { enabled } }),
  status: signal => request('/status', { signal }),
  providers: signal => request('/providers', { signal }),
  tags: signal => request('/tags', { signal }),
  skills: (query, signal) => request('/skills' + (query ? '?query=' + encode(query) : ''), { signal }),
  detail: (name, signal) => request(pathFor(name), { signal }),
  save: (draft, cloneId) => request(cloneId ? '/catalog/' + encode(cloneId) + '/clone'
    : draft.revision ? pathFor(draft.name) : '/skills', { method: draft.revision && !cloneId ? 'PUT' : 'POST', data: draft }),
  preview: (draft, signal) => request('/preview', { method: 'POST', data: draft, signal }),
  doctor: (draft, signal) => request('/doctor', { method: 'POST', data: draft, signal }),
  skillDoctor: (name, signal) => request(pathFor(name) + '/doctor', { signal }),
  history: (name, signal) => request(pathFor(name) + '/history', { signal }),
  revision: (name, id, signal) => request(pathFor(name) + '/history/' + encode(id), { signal }),
  restoreRevision: (name, id, revision) => request(pathFor(name) + '/history/' + encode(id) + '/restore', { method: 'POST', data: { revision } }),
  updates: (force, signal) => request('/updates' + (force ? '?force=true' : ''), { signal }),
  updateDiff: (name, signal) => request(pathFor(name) + '/update', { signal }),
  resolveUpdate: (name, action, revision) => request(pathFor(name) + '/update', { method: 'POST', data: { action, revision } }),
  activate: id => request('/catalog/' + encode(id) + '/activate', { method: 'POST' }),
  remove: name => request(pathFor(name), { method: 'DELETE' }),
  restore: name => request(pathFor(name) + '/restore', { method: 'POST' }),
  provider: (name, provider, enabled) => request(pathFor(name) + '/providers/' + encode(provider), { method: 'PATCH', data: { enabled } }),
  repair: (name, provider) => request(pathFor(name) + '/providers/' + encode(provider) + '/repair', { method: 'POST' }),
  skillTag: (name, tag, add) => request(pathFor(name) + '/tags', { method: 'PATCH', data: { tag, add } }),
  createTag: tag => request('/tags', { method: 'POST', data: { tag } }),
  renameTag: (oldTag, newTag) => request('/tags/rename', { method: 'POST', data: { old_tag: oldTag, new_tag: newTag } }),
  deleteTag: tag => request('/tags/delete', { method: 'POST', data: { tag } }),
  undoTag: id => request('/tags/undo/' + encode(id), { method: 'POST' }),
  repositories: signal => request('/community/repos', { signal }),
  pinSource: repo => request('/community/sources', { method: 'POST', data: { repo } }),
  unpinSource: repo => request('/community/sources/remove', { method: 'POST', data: { repo } }),
  community: (repo, q, signal) => request('/community/skills?' + new URLSearchParams({ repo, q }), { signal }),
  communityPreview: ({ repo, path, git_ref }, signal) =>
    request('/community/preview?' + new URLSearchParams({ repo, path, git_ref }), { signal }),
  previewFile: ({ repo, path, revision }, file, signal) =>
    request('/community/preview-file?' + new URLSearchParams({ repo, path, revision, file }), { signal }),
  prepare: (selection, signal) => request('/imports/prepare', { method: 'POST', data: selection, signal }),
  import: (plan, name, providers) => request('/imports/' + encode(plan.plan_id) + '/apply', {
    method: 'POST', data: { revision: plan.revision, name, providers }
  }),
  client: (id, action) => request('/mcp-clients/' + encode(id), { method: 'POST', data: { action } }),
  theme: value => request('/preferences/theme', { method: 'PATCH', data: { theme: value } }),
  preferences: (data, signal) => request('/preferences', data ? { method: 'PUT', data } : { signal }),
  diagnostics: signal => request('/diagnostics', { signal }),
  resync: () => request('/resync', { method: 'POST' }),
  undoConflict: (id, revision) => request('/diagnostics/conflicts/undo', { method: 'POST', data: { id, revision } }),
  conflict: (id, revision) => request('/diagnostics/conflicts/recover', { method: 'POST', data: { id, revision } })
};
