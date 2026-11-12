/**
 * Reactive State Store & Brand Icon Utilities for SkillHub
 */

const listeners = new Map();

const savedTheme = localStorage.getItem("skillhub_theme") || "dark";
const savedTagScope = localStorage.getItem("skillhub_tag_scope") || "all";

function loadSavedCustomTags() {
  try {
    const raw = localStorage.getItem("skillhub_custom_tags");
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed)
      ? parsed.filter((t) => !["system", "skillhub", "meta-skill"].includes(String(t).toLowerCase()))
      : [];
  } catch (_) {
    return [];
  }
}

export const SYSTEM_RESERVED_TAGS = new Set(["system", "skillhub", "meta-skill"]);

const state = {
  theme: savedTheme,
  activeScreen: "library", // "library" | "detail" | "edit" | "preview"
  previousScreen: "library",
  status: null,
  providers: [],
  skills: [],
  selectedSkillName: null,
  currentSkillModel: null,
  isCreatingNew: false,
  searchQuery: "",
  categoryFilter: "user", // "user" ("My Skills") | "synced" ("Active & Synced") | "template" | "system" | "plugin"
  originFilter: "",
  selectedTag: "",
  tagScope: savedTagScope, // "current" | "all"
  customTags: loadSavedCustomTags(),
  activePreviewTab: "gemini",
  previews: {
    canonical: "",
    claude: "",
    gemini: "",
    codex: "",
    cursor: ""
  }
};

export const store = {
  getState() {
    return state;
  },

  update(partial, eventTopic = "state:changed") {
    Object.assign(state, partial);
    this.emit(eventTopic, state);
  },

  setTagScope(scope) {
    const clean = scope === "current" ? "current" : "all";
    state.tagScope = clean;
    localStorage.setItem("skillhub_tag_scope", clean);
    this.emit("skills:filterChanged", state);
  },

  addCustomTag(rawTag) {
    const clean = slugifyTitle(rawTag);
    if (!clean || SYSTEM_RESERVED_TAGS.has(clean)) return null;
    if (!state.customTags.includes(clean)) {
      state.customTags = [...state.customTags, clean];
      localStorage.setItem("skillhub_custom_tags", JSON.stringify(state.customTags));
    }
    this.emit("skills:filterChanged", state);
    return clean;
  },

  renameCustomTag(oldTag, newTag) {
    const cleanOld = slugifyTitle(oldTag);
    const cleanNew = slugifyTitle(newTag);
    if (!cleanOld || !cleanNew || SYSTEM_RESERVED_TAGS.has(cleanNew)) return null;
    state.customTags = Array.from(
      new Set(state.customTags.map((t) => (t === cleanOld ? cleanNew : t)).concat([cleanNew]))
    );
    localStorage.setItem("skillhub_custom_tags", JSON.stringify(state.customTags));
    if (state.selectedTag === cleanOld) {
      state.selectedTag = cleanNew;
    }
    this.emit("skills:filterChanged", state);
    return cleanNew;
  },

  removeCustomTag(tagToRemove) {
    const clean = slugifyTitle(tagToRemove);
    state.customTags = state.customTags.filter((t) => t !== clean);
    localStorage.setItem("skillhub_custom_tags", JSON.stringify(state.customTags));
    if (state.selectedTag === clean) {
      state.selectedTag = "";
    }
    this.emit("skills:filterChanged", state);
  },

  getAllKnownUserTags() {
    const tagSet = new Set();
    (state.customTags || []).forEach((t) => {
      const lower = String(t).toLowerCase();
      if (!SYSTEM_RESERVED_TAGS.has(lower)) tagSet.add(lower);
    });
    (state.skills || []).forEach((s) => {
      if ((s.source_category || "user").toLowerCase() === "system") return;
      (s.tags || []).forEach((t) => {
        const lower = String(t).toLowerCase();
        if (!SYSTEM_RESERVED_TAGS.has(lower)) tagSet.add(lower);
      });
    });
    return Array.from(tagSet).sort((a, b) => a.localeCompare(b));
  },

  subscribe(topic, callback) {
    if (!listeners.has(topic)) {
      listeners.set(topic, new Set());
    }
    listeners.get(topic).add(callback);
    return () => listeners.get(topic)?.delete(callback);
  },

  emit(topic, payload) {
    const set = listeners.get(topic);
    if (!set) return;
    for (const cb of set) {
      try {
        cb(payload);
      } catch (err) {
        console.error(`Error in store listener [${topic}]:`, err);
      }
    }
  }
};

export function slugifyTitle(titleOrSlug) {
  if (!titleOrSlug) return "";
  return String(titleOrSlug)
    .trim()
    .toLowerCase()
    .replace(/c#/g, "csharp")
    .replace(/f#/g, "fsharp")
    .replace(/c\+\+/g, "cpp")
    .replace(/\.net/g, "dotnet")
    .replace(/[^a-z0-9-]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

export function humanizeSlug(slug) {
  if (!slug) return "Untitled Skill";
  let cleaned = String(slug).trim();
  if (cleaned.toLowerCase().startsWith("artifact-template-")) {
    cleaned = cleaned.slice("artifact-template-".length);
  }

  const special = {
    csharp: "C#",
    fsharp: "F#",
    cpp: "C++",
    dotnet: ".NET",
    api: "API",
    ui: "UI",
    ux: "UX",
    sql: "SQL",
    mcp: "MCP",
    cli: "CLI",
    ai: "AI",
    llm: "LLM",
    json: "JSON",
    yaml: "YAML",
    html: "HTML",
    css: "CSS",
    js: "JS",
    ts: "TS",
    skillhub: "SkillHub",
    openai: "OpenAI",
    chatgpt: "ChatGPT",
    docx: "DOCX",
    pdf: "PDF",
    ios: "iOS",
    macos: "macOS",
    qa: "QA",
    sdk: "SDK",
    "2d": "2D",
    "3d": "3D",
    kpi: "KPI",
    kpis: "KPIs",
    dcf: "DCF",
    oauth: "OAuth"
  };

  return cleaned
    .split(/[-_]+/)
    .filter(Boolean)
    .map((word) => {
      const lower = word.toLowerCase();
      if (special[lower]) return special[lower];
      return lower.charAt(0).toUpperCase() + lower.slice(1);
    })
    .join(" ");
}

/**
 * Recognizable official-style SVG vector brand logos for ChatGPT/OpenAI, Gemini/Antigravity, Claude, Cursor, GitHub & SkillHub
 */
export function getProviderSvgIcon(providerKey, size = 14) {
  switch ((providerKey || "").toLowerCase()) {
    case "codex":
    case "openai":
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M22.282 9.821a5.985 5.985 0 0 0-.516-4.91 6.046 6.046 0 0 0-6.51-2.9A6.065 6.065 0 0 0 4.981 4.18a5.985 5.985 0 0 0-3.998 2.9 6.046 6.046 0 0 0 .743 7.097 5.98 5.98 0 0 0 .51 4.911 6.051 6.051 0 0 0 6.515 2.9A5.985 5.985 0 0 0 13.26 24a6.056 6.056 0 0 0 5.772-4.206 5.99 5.99 0 0 0 3.997-2.9 6.056 6.056 0 0 0-.747-7.073zM13.26 22.43a4.476 4.476 0 0 1-2.876-1.04l.141-.081 4.779-2.758a.795.795 0 0 0 .392-.681v-6.737l2.02 1.168a.071.071 0 0 1 .038.052v5.583a4.504 4.504 0 0 1-4.494 4.494zM3.6 18.304a4.47 4.47 0 0 1-.535-3.014l.142.085 4.783 2.759a.771.771 0 0 0 .78 0l5.843-3.369v2.332a.08.08 0 0 1-.033.062L9.74 19.95a4.5 4.5 0 0 1-6.14-1.646zM2.34 7.896a4.485 4.485 0 0 1 2.366-1.973V11.6a.766.766 0 0 0 .388.676l5.815 3.355-2.02 1.168a.076.076 0 0 1-.071 0l-4.83-2.786A4.504 4.504 0 0 1 2.34 7.872zm16.597 3.855l-5.833-3.387L15.119 7.2a.076.076 0 0 1 .071 0l4.83 2.791a4.494 4.494 0 0 1-.676 8.105v-5.678a.79.79 0 0 0-.407-.667zm2.01-3.023l-.141-.085-4.774-2.782a.776.776 0 0 0-.785 0L9.409 9.23V6.897a.066.066 0 0 1 .028-.061l4.83-2.787a4.5 4.5 0 0 1 6.68 4.66zm-12.64 4.135l-2.02-1.164a.08.08 0 0 1-.038-.057V6.075a4.5 4.5 0 0 1 7.375-3.453l-.142.08L8.704 5.46a.795.795 0 0 0-.393.681zm1.097-2.365l2.602-1.5 2.607 1.5v2.999l-2.597 1.5-2.607-1.5z"/></svg>`;
    case "gemini":
    case "antigravity":
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 1C12 7.075 16.925 12 23 12C16.925 12 12 16.925 12 23C12 16.925 7.075 12 1 12C7.075 12 12 7.075 12 1Z"/></svg>`;
    case "claude":
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="12" y1="2" x2="12" y2="22"/><line x1="2" y1="12" x2="22" y2="12"/><line x1="4.93" y1="4.93" x2="19.07" y2="19.07"/><line x1="19.07" y1="4.93" x2="4.93" y2="19.07"/><line x1="7.4" y1="2.9" x2="16.6" y2="21.1"/><line x1="2.9" y1="16.6" x2="21.1" y2="7.4"/></svg>`;
    case "cursor":
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polygon points="12 2 21 7 21 17 12 22 3 17 3 7 12 2"/><polyline points="3.3 7 12 12 20.7 7"/><line x1="12" y1="22" x2="12" y2="12"/></svg>`;
    case "github":
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2C6.477 2 2 6.484 2 12.017c0 4.425 2.865 8.18 6.839 9.504.5.092.682-.217.682-.483 0-.237-.008-.868-.013-1.703-2.782.605-3.369-1.343-3.369-1.343-.454-1.158-1.11-1.466-1.11-1.466-.908-.62.069-.608.069-.608 1.003.07 1.53 1.032 1.53 1.032.892 1.53 2.341 1.088 2.91.832.092-.647.35-1.088.636-1.338-2.22-.253-4.555-1.113-4.555-4.951 0-1.093.39-1.988 1.029-2.688-.103-.253-.446-1.272.098-2.65 0 0 .84-.27 2.75 1.026A9.564 9.564 0 0 1 12 6.844a9.59 9.59 0 0 1 2.504.337c1.909-1.296 2.747-1.027 2.747-1.027.546 1.379.202 2.398.1 2.651.64.7 1.028 1.595 1.028 2.688 0 3.848-2.339 4.695-4.566 4.943.359.309.678.92.678 1.855 0 1.338-.012 2.419-.012 2.747 0 .268.18.58.688.482A10.02 10.02 0 0 0 22 12.017C22 6.484 17.522 2 12 2z"/></svg>`;
    default:
      return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 12 12 17 22 12"/><polyline points="2 17 12 22 22 17"/></svg>`;
  }
}

export function getBadgeVariant(skill) {
  const cat = (skill?.source_category || "user").toLowerCase();
  const origin = skill?.origin_provider || "My Skill";

  if (cat === "user" && !skill?.is_catalog_only) {
    if (origin.startsWith("GitHub")) {
      return { label: origin, css: "badge-user", iconKey: "github" };
    }
    return { label: "My Skill", css: "badge-user", iconKey: "skillhub" };
  }
  if (cat === "template" && !skill?.is_catalog_only) {
    const iconKey = origin.includes("Antigravity") || origin.includes("Gemini") ? "gemini" : "codex";
    return { label: "Synced Template", css: "badge-template", iconKey };
  }
  if (cat === "system") {
    const iconKey = origin.includes("Codex") ? "codex" : origin.includes("Gemini") ? "gemini" : "skillhub";
    return { label: origin || "System Skill", css: "badge-system", iconKey };
  }
  if (cat === "plugin") {
    return { label: origin || "Plugin", css: "badge-plugin", iconKey: "codex" };
  }
  if (origin.includes("OpenAI Template")) {
    return { label: "From: OpenAI Template", css: "badge-template", iconKey: "codex" };
  }
  if (origin.includes("OpenAI Built-in")) {
    return { label: "From: OpenAI Codex", css: "badge-builtin", iconKey: "codex" };
  }
  if (origin.includes("Antigravity")) {
    return { label: "From: Antigravity", css: "badge-antigravity", iconKey: "gemini" };
  }
  if (origin.includes("Claude")) {
    return { label: "From: Claude", css: "badge-builtin", iconKey: "claude" };
  }
  if (origin.includes("Cursor")) {
    return { label: "From: Cursor", css: "badge-builtin", iconKey: "cursor" };
  }
  return { label: origin || "Built-in", css: "badge-builtin", iconKey: "skillhub" };
}

export function showToast(message) {
  const container = document.getElementById("toast-container");
  if (!container) return;
  const div = document.createElement("div");
  div.className = "toast";
  div.textContent = message;
  container.appendChild(div);
  setTimeout(() => div.remove(), 3400);
}

/**
 * Native in-app styled modal confirmation dialog (Zero browser window.confirm/prompt/alert)
 */
export function showConfirmDialog({
  title = "Confirm Action",
  message = "Are you sure you want to proceed?",
  confirmText = "Confirm",
  cancelText = "Cancel",
  isDanger = false
} = {}) {
  return new Promise((resolve) => {
    const modal = document.getElementById("confirm-modal");
    const titleEl = document.getElementById("confirm-modal-title");
    const msgEl = document.getElementById("confirm-modal-message");
    const actionBtn = document.getElementById("btn-action-confirm-modal");
    const cancelBtn = document.getElementById("btn-cancel-confirm-modal");
    const closeBtn = document.getElementById("btn-close-confirm-modal");

    if (!modal) {
      resolve(true);
      return;
    }

    if (titleEl) titleEl.textContent = title;
    if (msgEl) msgEl.textContent = message;
    if (cancelBtn) cancelBtn.textContent = cancelText;
    if (actionBtn) {
      actionBtn.textContent = confirmText;
      actionBtn.className = isDanger ? "btn btn-ghost-danger btn-sm" : "btn btn-primary btn-sm";
    }

    function cleanup(result) {
      modal.classList.add("hidden");
      actionBtn?.removeEventListener("click", onAction);
      cancelBtn?.removeEventListener("click", onCancel);
      closeBtn?.removeEventListener("click", onCancel);
      modal.removeEventListener("click", onBackdrop);
      window.removeEventListener("keydown", onKey);
      resolve(result);
    }

    function onAction() {
      cleanup(true);
    }
    function onCancel() {
      cleanup(false);
    }
    function onBackdrop(e) {
      if (e.target === modal) cleanup(false);
    }
    function onKey(e) {
      if (e.key === "Escape") cleanup(false);
      else if (e.key === "Enter") cleanup(true);
    }

    actionBtn?.addEventListener("click", onAction);
    cancelBtn?.addEventListener("click", onCancel);
    closeBtn?.addEventListener("click", onCancel);
    modal.addEventListener("click", onBackdrop);
    window.addEventListener("keydown", onKey);

    modal.classList.remove("hidden");
    actionBtn?.focus();
  });
}
