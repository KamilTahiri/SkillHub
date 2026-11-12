/**
 * SkillHub REST API & Server-Sent Events Client
 */

async function request(path, options = {}) {
  const response = await fetch(path, {
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    },
    ...options
  });

  if (!response.ok) {
    let errorMessage = response.statusText;
    try {
      const errorBody = await response.json();
      if (errorBody && errorBody.error) {
        errorMessage = errorBody.error;
      }
    } catch (_) {
      // Ignore non-JSON error body
    }
    throw new Error(errorMessage);
  }

  return response.json();
}

export const api = {
  getStatus: () => request("/api/v1/status"),
  getProviders: () => request("/api/v1/providers"),
  setWindowTheme: (theme) =>
    request("/api/v1/window/theme", {
      method: "POST",
      body: JSON.stringify({ theme })
    }),
  initializeProvider: (providerName) =>
    request(`/api/v1/providers/${encodeURIComponent(providerName)}/initialize`, {
      method: "POST"
    }),
  injectMcpClients: () =>
    request("/api/v1/mcp-clients/inject", {
      method: "POST"
    }),
  listSkills: (query = "", tag = "", category = "") => {
    const params = new URLSearchParams();
    if (query) params.set("query", query);
    if (tag) params.set("tag", tag);
    if (category) params.set("category", category);
    const qs = params.toString();
    return request(`/api/v1/skills${qs ? `?${qs}` : ""}`);
  },
  getSkill: (name) => request(`/api/v1/skills/${encodeURIComponent(name)}`),
  upsertSkill: (skillPayload) =>
    request("/api/v1/skills", {
      method: "POST",
      body: JSON.stringify(skillPayload)
    }),
  importSkill: (importPayload) =>
    request("/api/v1/skills/import", {
      method: "POST",
      body: JSON.stringify(importPayload)
    }),
  getCommunityRepos: () => request("/api/v1/community/repos"),
  getCommunitySkills: (repo = "", query = "") => {
    const params = new URLSearchParams();
    if (repo) params.set("repo", repo);
    if (query) params.set("q", query);
    const qs = params.toString();
    return request(`/api/v1/community/skills${qs ? `?${qs}` : ""}`);
  },
  getCommunitySkillPreview: (repo, path, gitRef = "HEAD") => {
    const params = new URLSearchParams({ repo, path, git_ref: gitRef || "HEAD" });
    return request(`/api/v1/community/preview?${params.toString()}`);
  },
  renameTag: (oldTag, newTag) =>
    request("/api/v1/tags/rename", {
      method: "POST",
      body: JSON.stringify({ old_tag: oldTag, new_tag: newTag })
    }),
  deleteTag: (tag) =>
    request("/api/v1/tags/delete", {
      method: "POST",
      body: JSON.stringify({ tag })
    }),
  toggleSkillProvider: (name, providerName, enabled) =>
    request(`/api/v1/skills/${encodeURIComponent(name)}/providers/${encodeURIComponent(providerName)}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled })
    }),
  deleteSkill: (name) =>
    request(`/api/v1/skills/${encodeURIComponent(name)}`, {
      method: "DELETE"
    }),
  previewTranspilation: (draftSkillPayload) =>
    request("/api/v1/preview", {
      method: "POST",
      body: JSON.stringify(draftSkillPayload)
    }),
  forceResync: () =>
    request("/api/v1/resync", {
      method: "POST"
    }),
  toggleTunnel: (enable) =>
    request("/api/v1/tunnel/toggle", {
      method: "POST",
      body: JSON.stringify({ enable })
    }),
  connectEventStream: ({ onConnected, onActivity, onError }) => {
    const es = new EventSource("/api/v1/events");
    es.addEventListener("connected", () => {
      if (onConnected) onConnected();
    });
    es.addEventListener("activity", (event) => {
      if (onActivity) {
        try {
          const parsed = JSON.parse(event.data);
          onActivity(parsed);
        } catch (_) {}
      }
    });
    es.onerror = () => {
      if (onError) onError();
    };
    return es;
  }
};
