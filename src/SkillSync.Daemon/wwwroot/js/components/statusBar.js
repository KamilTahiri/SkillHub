/**
 * Top Navigation Bar, "My AI Providers" Popover, Theme Toggle (with DWM Window Sync),
 * Interactive Community Skill Browser (skills.sh / GitHub / Local .md),
 * Settings Modal & Activity Log Modal
 */
import { api } from "../api.js?v=20261112-v7";
import { store, getProviderSvgIcon, showToast, showConfirmDialog } from "../state.js?v=20261112-v7";
import { renderMarkdown, escapeHtml } from "../markdown.js?v=20261112-v7";

export function initStatusBar({
  onRefreshAll,
  onNewSkill,
  onNavigateLibrary,
  onSelectImportedSkill
}) {
  const el = {
    btnBrandHome: document.getElementById("btn-brand-home"),
    btnMyProviders: document.getElementById("btn-my-providers"),
    sseDot: document.getElementById("sse-dot"),
    myProvidersLabel: document.getElementById("my-providers-label"),
    myProvidersLabelCompact: document.getElementById("my-providers-label-compact"),
    providersPopover: document.getElementById("providers-popover"),
    btnPopoverOpenSettings: document.getElementById("btn-popover-open-settings"),
    popoverActiveProviders: document.getElementById("popover-active-providers"),
    popoverUninstalledDetails: document.getElementById("popover-uninstalled-details"),
    popoverUninstalledProviders: document.getElementById("popover-uninstalled-providers"),
    btnOpenImport: document.getElementById("btn-open-import"),
    importModal: document.getElementById("import-modal"),
    btnCloseImport: document.getElementById("btn-close-import"),
    btnImportTabBrowse: document.getElementById("btn-import-tab-browse"),
    btnImportTabDirect: document.getElementById("btn-import-tab-direct"),
    panelImportBrowse: document.getElementById("panel-import-browse"),
    panelImportDirect: document.getElementById("panel-import-direct"),
    communityRepoPills: document.getElementById("community-repo-pills"),
    communityTopicChips: document.getElementById("community-topic-chips"),
    communitySearchInput: document.getElementById("community-search-input"),
    btnCommunitySearch: document.getElementById("btn-community-search"),
    communityListTitle: document.getElementById("community-list-title"),
    communityListCount: document.getElementById("community-list-count"),
    communitySkillsList: document.getElementById("community-skills-list"),
    communityPreviewEmpty: document.getElementById("community-preview-empty"),
    communityPreviewContent: document.getElementById("community-preview-content"),
    communityPreviewTitle: document.getElementById("community-preview-title"),
    communityPreviewSource: document.getElementById("community-preview-source"),
    communityPreviewTriggerRow: document.getElementById("community-preview-trigger-row"),
    communityPreviewTrigger: document.getElementById("community-preview-trigger"),
    btnImportPreviewedSkill: document.getElementById("btn-import-previewed-skill"),
    communityPreviewDesc: document.getElementById("community-preview-desc"),
    communityPreviewMarkdown: document.getElementById("community-preview-markdown"),
    importUrlInput: document.getElementById("import-url-input"),
    btnSubmitImportUrl: document.getElementById("btn-submit-import-url"),
    importPresetChips: document.querySelectorAll(".import-preset-chip"),
    btnChooseImportFile: document.getElementById("btn-choose-import-file"),
    importFileInput: document.getElementById("import-file-input"),
    importFileStatus: document.getElementById("import-file-status"),
    btnNewSkill: document.getElementById("btn-new-skill"),
    btnThemeToggle: document.getElementById("btn-theme-toggle"),
    iconSun: document.getElementById("icon-sun"),
    iconMoon: document.getElementById("icon-moon"),
    btnOpenSettings: document.getElementById("btn-open-settings"),
    settingsModal: document.getElementById("settings-modal"),
    btnCloseSettings: document.getElementById("btn-close-settings"),
    settingsProvidersGrid: document.getElementById("settings-providers-grid"),
    settingsUninstalledDetails: document.getElementById("settings-uninstalled-details"),
    settingsUninstalledSummary: document.getElementById("settings-uninstalled-summary"),
    settingsUninstalledGrid: document.getElementById("settings-uninstalled-grid"),
    btnResync: document.getElementById("btn-resync"),
    btnAutoInjectMcp: document.getElementById("btn-auto-inject-mcp"),
    btnTunnel: document.getElementById("btn-tunnel"),
    tunnelBtnLabel: document.getElementById("tunnel-btn-label"),
    tunnelBanner: document.getElementById("tunnel-banner"),
    tunnelPublicUrl: document.getElementById("tunnel-public-url"),
    btnCopyTunnel: document.getElementById("btn-copy-tunnel"),
    btnOpenActivityModal: document.getElementById("btn-open-activity-modal"),
    activityModal: document.getElementById("activity-modal"),
    btnCloseActivityModal: document.getElementById("btn-close-activity-modal"),
    activityList: document.getElementById("activity-list")
  };

  let activeCommunityRepo = "featured/all";
  let activeCommunityTopic = "all";
  let cachedCommunitySkills = [];
  let currentPreviewedSkill = null;
  let communityLoadedOnce = false;

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    localStorage.setItem("skillhub_theme", theme);
    if (theme === "light") {
      el.iconSun.classList.add("hidden");
      el.iconMoon.classList.remove("hidden");
    } else {
      el.iconSun.classList.remove("hidden");
      el.iconMoon.classList.add("hidden");
    }
    try {
      window.chrome?.webview?.postMessage(JSON.stringify({ type: "theme", theme }));
    } catch (_) {}
    api.setWindowTheme(theme).catch(() => {});
  }

  applyTheme(store.getState().theme);

  function countSyncedSkillsForProvider(providerKey, skills) {
    const syncedSkills = (skills || []).filter(
      (s) => !s.is_catalog_only && (s.source_category || "user") !== "system"
    );
    return syncedSkills.filter((s) => s.targets?.[providerKey]?.enabled !== false).length;
  }

  function renderProvidersAndSummary() {
    const { providers, skills } = store.getState();
    const external = (providers || []).filter((p) => p.provider !== "canonical");
    const installed = external.filter((p) => p.is_installed);
    const uninstalled = external.filter((p) => !p.is_installed);

    if (el.myProvidersLabel) {
      el.myProvidersLabel.textContent = `My AI Providers (${installed.length})`;
    }
    if (el.myProvidersLabelCompact) {
      el.myProvidersLabelCompact.textContent = `${installed.length} AIs`;
    }

    if (el.popoverActiveProviders) {
      el.popoverActiveProviders.innerHTML = "";
      if (installed.length > 0) {
        installed.forEach((p) => {
          const syncedCount = countSyncedSkillsForProvider(p.provider, skills);
          const item = document.createElement("div");
          item.className = "popover-provider-item";
          item.innerHTML = `
            <div class="provider-brand-left">
              <span class="provider-svg-box">${getProviderSvgIcon(p.provider, 16)}</span>
              <div class="provider-brand-meta">
                <span class="provider-brand-name">${escapeHtml(p.display_name)}</span>
                <span class="provider-brand-sub">${syncedCount} skill${syncedCount === 1 ? "" : "s"} synced</span>
              </div>
            </div>
            <span class="status-badge-active">Active &amp; Synced</span>
          `;
          el.popoverActiveProviders.appendChild(item);
        });
      } else {
        const empty = document.createElement("div");
        empty.className = "field-hint";
        empty.textContent = "Local SkillHub store active.";
        el.popoverActiveProviders.appendChild(empty);
      }
    }

    if (el.popoverUninstalledDetails && el.popoverUninstalledProviders) {
      el.popoverUninstalledProviders.innerHTML = "";
      if (uninstalled.length > 0) {
        el.popoverUninstalledDetails.classList.remove("hidden");
        uninstalled.forEach((p) => {
          const row = document.createElement("div");
          row.className = "uninstalled-provider-row";
          row.innerHTML = `
            <div class="provider-brand-left">
              <span style="color:var(--text-muted); display:inline-flex;">${getProviderSvgIcon(p.provider, 14)}</span>
              <span style="font-size:12px; color:var(--text-secondary);">${escapeHtml(p.display_name)}</span>
            </div>
            <span class="status-badge-inactive">Not installed</span>
          `;
          el.popoverUninstalledProviders.appendChild(row);
        });
      } else {
        el.popoverUninstalledDetails.classList.add("hidden");
      }
    }

    if (el.settingsProvidersGrid) {
      el.settingsProvidersGrid.innerHTML = "";
      installed.forEach((p) => {
        const syncedCount = countSyncedSkillsForProvider(p.provider, skills);
        const card = document.createElement("div");
        card.className = "settings-provider-card";
        card.innerHTML = `
          <div class="settings-provider-top">
            <div class="provider-brand-left">
              <span class="provider-svg-box">${getProviderSvgIcon(p.provider, 16)}</span>
              <span class="provider-brand-name">${escapeHtml(p.display_name)}</span>
            </div>
            <span class="status-badge-active">Active &amp; Synced</span>
          </div>
          <div class="settings-provider-foot">
            <span>${syncedCount} skill${syncedCount === 1 ? "" : "s"} synced</span>
            <code>${escapeHtml(p.provider === "codex" ? "~/.codex/skills" : p.provider === "gemini" ? "~/.gemini/skills" : p.file_pattern || "")}</code>
          </div>
        `;
        el.settingsProvidersGrid.appendChild(card);
      });
    }

    if (el.settingsUninstalledDetails && el.settingsUninstalledGrid) {
      el.settingsUninstalledGrid.innerHTML = "";
      if (uninstalled.length > 0) {
        el.settingsUninstalledDetails.classList.remove("hidden");
        if (el.settingsUninstalledSummary) {
          el.settingsUninstalledSummary.textContent = `Supported providers not installed on this device (${uninstalled.length})`;
        }
        uninstalled.forEach((p) => {
          const row = document.createElement("div");
          row.className = "uninstalled-provider-row";
          row.innerHTML = `
            <div class="provider-brand-left">
              <span class="provider-svg-box" style="color:var(--text-muted);">${getProviderSvgIcon(p.provider, 16)}</span>
              <span style="font-size:12.5px; color:var(--text-secondary);">${escapeHtml(p.display_name)}</span>
            </div>
            <span class="status-badge-inactive">Not installed on this PC</span>
          `;
          el.settingsUninstalledGrid.appendChild(row);
        });
      } else {
        el.settingsUninstalledDetails.classList.add("hidden");
      }
    }
  }

  function renderStatus() {
    const { status } = store.getState();
    if (!status) return;

    const tunnel = status.tunnel;
    if (tunnel) {
      const active = tunnel.status === "running" || tunnel.status === "starting";
      el.tunnelBtnLabel.textContent = active ? "Disable Web Tunnel" : "Enable Web Tunnel";
      if (active) {
        el.tunnelBanner.classList.remove("hidden");
        el.tunnelPublicUrl.textContent = tunnel.public_mcp_url || "Starting tunnel...";
      } else {
        el.tunnelBanner.classList.add("hidden");
      }
    }
  }

  function appendActivity(act) {
    if (!el.activityList) return;
    const row = document.createElement("div");
    row.className = "activity-row";
    const time = act.timestamp_utc
      ? new Date(act.timestamp_utc).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })
      : new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    row.innerHTML = `<span class="field-hint">${escapeHtml(time)}</span> &mdash; ${escapeHtml(act.details || "")}`;
    el.activityList.prepend(row);
    while (el.activityList.children.length > 40) {
      el.activityList.lastChild.remove();
    }
  }

  function setRecentActivities(list) {
    if (!el.activityList) return;
    el.activityList.innerHTML = "";
    if (!list || list.length === 0) {
      const empty = document.createElement("div");
      empty.className = "field-hint";
      empty.textContent = "No sync events recorded yet in this session.";
      el.activityList.appendChild(empty);
      return;
    }
    list.slice().reverse().forEach(appendActivity);
  }

  function setSseConnected(online) {
    if (online) {
      el.sseDot.classList.add("online");
    } else {
      el.sseDot.classList.remove("online");
    }
  }

  function closePopover() {
    el.providersPopover?.classList.add("hidden");
    el.btnMyProviders?.classList.remove("open");
  }

  el.btnBrandHome?.addEventListener("click", onNavigateLibrary);
  el.btnNewSkill?.addEventListener("click", onNewSkill);

  el.btnThemeToggle.addEventListener("click", () => {
    const next = store.getState().theme === "dark" ? "light" : "dark";
    store.update({ theme: next }, "theme:changed");
    applyTheme(next);
  });

  // Toggle "My AI Providers" Popover
  el.btnMyProviders?.addEventListener("click", (e) => {
    e.stopPropagation();
    const isHidden = el.providersPopover.classList.contains("hidden");
    if (isHidden) {
      el.providersPopover.classList.remove("hidden");
      el.btnMyProviders.classList.add("open");
    } else {
      closePopover();
    }
  });

  el.btnPopoverOpenSettings?.addEventListener("click", () => {
    closePopover();
    el.settingsModal.classList.remove("hidden");
  });

  document.addEventListener("click", (e) => {
    if (
      el.providersPopover &&
      !el.providersPopover.classList.contains("hidden") &&
      !el.providersPopover.contains(e.target) &&
      !el.btnMyProviders.contains(e.target)
    ) {
      closePopover();
    }
  });

  // =========================================================================
  // Community Skill Browser & Direct URL/File Import Modal
  // =========================================================================
  function setImportModalTab(tab) {
    const isBrowse = tab === "browse";
    el.btnImportTabBrowse?.classList.toggle("active", isBrowse);
    el.btnImportTabDirect?.classList.toggle("active", !isBrowse);
    el.panelImportBrowse?.classList.toggle("hidden", !isBrowse);
    el.panelImportDirect?.classList.toggle("hidden", isBrowse);
    if (!isBrowse) {
      el.importUrlInput?.focus();
    }
  }

  el.btnImportTabBrowse?.addEventListener("click", () => setImportModalTab("browse"));
  el.btnImportTabDirect?.addEventListener("click", () => setImportModalTab("direct"));

  async function loadCommunitySkills({ repo = "", q = "" } = {}) {
    if (!el.communitySkillsList) return;
    el.communitySkillsList.innerHTML = `<div class="field-hint" style="padding:18px;">Loading discovery hub skills...</div>`;

    try {
      const res = await api.getCommunitySkills(repo, q);
      cachedCommunitySkills = res.skills || [];
      renderCommunitySkillsList();
    } catch (err) {
      el.communitySkillsList.innerHTML = `<div class="field-hint" style="padding:18px;">Could not load community skills: ${escapeHtml(err.message)}</div>`;
    }
  }

  function renderCommunitySkillsList() {
    if (!el.communitySkillsList) return;
    let list = cachedCommunitySkills;

    if (activeCommunityTopic && activeCommunityTopic !== "all") {
      list = list.filter((item) => (item.topic || "coding").toLowerCase() === activeCommunityTopic.toLowerCase());
    }

    if (el.communityListCount) {
      el.communityListCount.textContent = `${list.length} skill${list.length === 1 ? "" : "s"}`;
    }

    el.communitySkillsList.innerHTML = "";

    if (list.length === 0) {
      el.communitySkillsList.innerHTML = `<div class="field-hint" style="padding:18px;">No skills matched this filter. Try selecting another topic or repository above.</div>`;
      return;
    }

    list.forEach((item, idx) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "community-skill-item";
      if (currentPreviewedSkill && currentPreviewedSkill.repo === item.repo && currentPreviewedSkill.path === item.path) {
        btn.classList.add("active");
      }

      const displayRepo = item.repo === "local/catalog" ? "Local Catalog" : item.repo;
      btn.innerHTML = `
        <div class="community-skill-item-top">
          <span class="community-skill-item-title">${escapeHtml(item.title || item.slug)}</span>
          <div style="display:flex;align-items:center;gap:5px;">
            ${item.topic ? `<span class="community-skill-topic-badge">${escapeHtml(item.topic)}</span>` : ""}
            <span class="community-skill-item-repo">${escapeHtml(displayRepo)}</span>
          </div>
        </div>
        <div class="community-skill-item-desc">${escapeHtml(item.description || item.path)}</div>
      `;

      btn.addEventListener("click", () => {
        el.communitySkillsList
          .querySelectorAll(".community-skill-item")
          .forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        previewCommunitySkillItem(item);
      });

      el.communitySkillsList.appendChild(btn);

      // Auto-preview the first item if nothing is previewed or preview changed
      if (idx === 0 && (!currentPreviewedSkill || !list.some(s => s.repo === currentPreviewedSkill.repo && s.path === currentPreviewedSkill.path))) {
        btn.classList.add("active");
        previewCommunitySkillItem(item);
      }
    });
  }

  async function previewCommunitySkillItem(item) {
    if (!el.communityPreviewContent || !el.communityPreviewEmpty) return;

    // Redundant re-render guard: If clicking already-selected community skill, return immediately!
    if (
      currentPreviewedSkill &&
      currentPreviewedSkill.repo === item.repo &&
      currentPreviewedSkill.path === item.path &&
      currentPreviewedSkill.instructions_markdown
    ) {
      return;
    }

    el.communityPreviewEmpty.classList.add("hidden");
    el.communityPreviewContent.classList.remove("hidden");

    const fullPath = `${item.repo}/${item.path}`;
    el.communityPreviewTitle.textContent = item.title || item.slug;
    if (el.communityPreviewSource) {
      el.communityPreviewSource.textContent = fullPath;
    }
    el.communityPreviewDesc.textContent = "Loading skill documentation...";
    if (el.communityPreviewTriggerRow) {
      el.communityPreviewTriggerRow.classList.add("hidden");
    }
    el.communityPreviewMarkdown.innerHTML = `
      <div style="padding: 24px 0; text-align: center; color: var(--text-muted);">
        <span class="sync-dot online" style="margin-right: 8px;"></span>
        Fetching <code>${escapeHtml(item.path)}</code>...
      </div>
    `;
    currentPreviewedSkill = item;

    try {
      const preview = await api.getCommunitySkillPreview(
        item.repo,
        item.path,
        item.git_ref || "HEAD"
      );
      currentPreviewedSkill = { ...item, ...preview };
      el.communityPreviewTitle.textContent = preview.title || item.title || item.slug;
      if (el.communityPreviewSource) {
        el.communityPreviewSource.textContent = `${preview.repo}/${preview.path}`;
      }
      el.communityPreviewDesc.textContent =
        preview.description || "Community skill ready to import into My Skills.";

      if (preview.when_to_use && preview.when_to_use.trim()) {
        if (el.communityPreviewTriggerRow) el.communityPreviewTriggerRow.classList.remove("hidden");
        if (el.communityPreviewTrigger) el.communityPreviewTrigger.textContent = preview.when_to_use;
      } else {
        if (el.communityPreviewTriggerRow) el.communityPreviewTriggerRow.classList.add("hidden");
      }

      el.communityPreviewMarkdown.innerHTML = renderMarkdown(
        preview.instructions_markdown || preview.raw_markdown || "",
        "",
        { stripLeadingH1: true }
      );
    } catch (err) {
      el.communityPreviewDesc.textContent = `Could not load preview: ${err.message}`;
      el.communityPreviewMarkdown.innerHTML = `
        <div class="field-hint" style="padding: 16px 0;">
          Failed to fetch documentation (<code>${escapeHtml(fullPath)}</code>).
          You can still click <strong>+ Import to My Skills</strong> to import it directly.
        </div>
      `;
    }
  }

  el.communityRepoPills?.querySelectorAll("[data-community-repo]").forEach((pill) => {
    pill.addEventListener("click", () => {
      const repo = pill.getAttribute("data-community-repo") || "featured/all";
      activeCommunityRepo = repo;
      if (el.communitySearchInput) el.communitySearchInput.value = "";
      el.communityRepoPills
        .querySelectorAll("[data-community-repo]")
        .forEach((p) => p.classList.toggle("active", p === pill));
      loadCommunitySkills({ repo, q: "" });
    });
  });

  document.querySelectorAll("[data-community-topic]").forEach((chip) => {
    chip.addEventListener("click", () => {
      activeCommunityTopic = chip.getAttribute("data-community-topic") || "all";
      document.querySelectorAll("[data-community-topic]").forEach((c) => c.classList.toggle("active", c === chip));
      renderCommunitySkillsList();
    });
  });

  function runCommunitySearch() {
    const q = el.communitySearchInput?.value.trim() || "";
    if (!q) {
      loadCommunitySkills({ repo: activeCommunityRepo, q: "" });
      return;
    }
    // Check if the user typed a repo shorthand like owner/repo
    if (/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(q)) {
      activeCommunityRepo = q;
      el.communityRepoPills
        ?.querySelectorAll("[data-community-repo]")
        .forEach((p) => p.classList.toggle("active", p.getAttribute("data-community-repo") === q));
      loadCommunitySkills({ repo: q, q: "" });
    } else {
      loadCommunitySkills({ repo: "", q });
    }
  }

  el.btnCommunitySearch?.addEventListener("click", runCommunitySearch);
  el.communitySearchInput?.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      runCommunitySearch();
    }
  });

  el.btnImportPreviewedSkill?.addEventListener("click", async () => {
    if (!currentPreviewedSkill) return;
    const origText = el.btnImportPreviewedSkill.textContent;
    el.btnImportPreviewedSkill.disabled = true;
    el.btnImportPreviewedSkill.textContent = "Importing...";

    try {
      const payload = currentPreviewedSkill.raw_markdown
        ? {
            file_name: `${currentPreviewedSkill.slug || "skill"}.SKILL.md`,
            raw_markdown: currentPreviewedSkill.raw_markdown
          }
        : {
            source:
              currentPreviewedSkill.source ||
              `${currentPreviewedSkill.repo}/${currentPreviewedSkill.path.replace(/\/SKILL\.md$/i, "")}`
          };
      const res = await api.importSkill(payload);
      el.importModal.classList.add("hidden");
      showToast(
        res.message ||
          `Imported '${currentPreviewedSkill.title || currentPreviewedSkill.slug}' to My Skills and synced to your AI providers!`
      );
      if (onSelectImportedSkill && res.primary_skill?.name) {
        await onSelectImportedSkill(res.primary_skill.name);
      } else {
        await onRefreshAll();
      }
    } catch (err) {
      showToast(`Import failed: ${err.message}`);
    } finally {
      el.btnImportPreviewedSkill.disabled = false;
      el.btnImportPreviewedSkill.textContent = origText;
    }
  });

  el.btnOpenImport?.addEventListener("click", () => {
    closePopover();
    el.importModal.classList.remove("hidden");
    if (!communityLoadedOnce) {
      communityLoadedOnce = true;
      loadCommunitySkills({ repo: activeCommunityRepo, q: "" });
    }
  });

  el.btnCloseImport?.addEventListener("click", () => {
    el.importModal.classList.add("hidden");
  });

  el.importModal?.addEventListener("click", (e) => {
    if (e.target === el.importModal) el.importModal.classList.add("hidden");
  });

  el.importPresetChips?.forEach((chip) => {
    chip.addEventListener("click", () => {
      const val = chip.getAttribute("data-import-example") || "";
      if (el.importUrlInput) {
        el.importUrlInput.value = val;
        el.importUrlInput.focus();
      }
    });
  });

  async function handleUrlImport() {
    const source = el.importUrlInput?.value.trim();
    if (!source) {
      showToast("Please enter a GitHub URL or skills.sh repository shorthand.");
      el.importUrlInput?.focus();
      return;
    }

    const origText = el.btnSubmitImportUrl.textContent;
    el.btnSubmitImportUrl.disabled = true;
    el.btnSubmitImportUrl.textContent = "Importing...";

    try {
      const res = await api.importSkill({ source });
      el.importUrlInput.value = "";
      el.importModal.classList.add("hidden");
      showToast(res.message || `Imported ${res.count || 1} skill(s) and synced to your AI providers!`);
      if (onSelectImportedSkill && res.primary_skill?.name) {
        await onSelectImportedSkill(res.primary_skill.name);
      } else {
        await onRefreshAll();
      }
    } catch (err) {
      showToast(`Import failed: ${err.message}`);
    } finally {
      el.btnSubmitImportUrl.disabled = false;
      el.btnSubmitImportUrl.textContent = origText;
    }
  }

  el.btnSubmitImportUrl?.addEventListener("click", handleUrlImport);
  el.importUrlInput?.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      handleUrlImport();
    }
  });

  el.btnChooseImportFile?.addEventListener("click", () => {
    el.importFileInput?.click();
  });

  el.importFileInput?.addEventListener("change", async () => {
    const file = el.importFileInput.files?.[0];
    if (!file) return;
    if (el.importFileStatus) {
      el.importFileStatus.textContent = `Importing ${file.name}...`;
    }

    try {
      const markdownContent = await file.text();
      const res = await api.importSkill({
        file_name: file.name,
        raw_markdown: markdownContent
      });
      el.importFileInput.value = "";
      if (el.importFileStatus) {
        el.importFileStatus.textContent = "Supports SKILL.md, Cursor .mdc, or standard Markdown files";
      }
      el.importModal.classList.add("hidden");
      showToast(res.message || `Imported '${file.name}' and synced across your AI providers!`);
      if (onSelectImportedSkill && res.primary_skill?.name) {
        await onSelectImportedSkill(res.primary_skill.name);
      } else {
        await onRefreshAll();
      }
    } catch (err) {
      if (el.importFileStatus) {
        el.importFileStatus.textContent = "Supports SKILL.md, Cursor .mdc, or standard Markdown files";
      }
      showToast(`File import failed: ${err.message}`);
    }
  });

  // Settings Modal Open / Close
  el.btnOpenSettings.addEventListener("click", () => {
    closePopover();
    el.settingsModal.classList.remove("hidden");
  });
  el.btnCloseSettings.addEventListener("click", () => el.settingsModal.classList.add("hidden"));
  el.settingsModal.addEventListener("click", (e) => {
    if (e.target === el.settingsModal) el.settingsModal.classList.add("hidden");
  });

  // Activity Log Modal Open / Close
  el.btnOpenActivityModal?.addEventListener("click", () => {
    el.activityModal.classList.remove("hidden");
  });
  el.btnCloseActivityModal?.addEventListener("click", () => {
    el.activityModal.classList.add("hidden");
  });
  el.activityModal?.addEventListener("click", (e) => {
    if (e.target === el.activityModal) el.activityModal.classList.add("hidden");
  });

  // Maintenance Actions
  el.btnResync.addEventListener("click", async () => {
    const ok = await showConfirmDialog({
      title: "Run Full Resync",
      message:
        "Run a full resync across all connected AI providers? This will re-scan your skill folders and rewrite transpiled files to every installed provider.",
      confirmText: "Run Full Resync"
    });
    if (!ok) return;

    try {
      const res = await api.forceResync();
      await onRefreshAll();
      showToast(`Synchronized ${res.count} skill(s) across your installed AI providers.`);
    } catch (err) {
      showToast(`Sync failed: ${err.message}`);
    }
  });

  el.btnAutoInjectMcp.addEventListener("click", async () => {
    const ok = await showConfirmDialog({
      title: "Repair MCP Configurations",
      message:
        "Re-register the SkillHub MCP server in your AI client configuration files? Use this if you recently updated or reinstalled Codex, Gemini CLI, Antigravity, Claude, or Cursor.",
      confirmText: "Repair MCP Configs"
    });
    if (!ok) return;

    try {
      await api.injectMcpClients();
      await onRefreshAll();
      showToast("Re-registered SkillHub MCP server with your installed AI tools.");
    } catch (err) {
      showToast(`Connection failed: ${err.message}`);
    }
  });

  el.btnTunnel.addEventListener("click", async () => {
    try {
      const currentStatus = store.getState().status?.tunnel?.status;
      const isRunning = currentStatus === "running" || currentStatus === "starting";
      const res = await api.toggleTunnel(!isRunning);
      showToast(res.message || "Tunnel updated.");
      setTimeout(async () => {
        const status = await api.getStatus();
        store.update({ status }, "status:updated");
      }, 1400);
    } catch (err) {
      showToast(`Tunnel error: ${err.message}`);
    }
  });

  el.btnCopyTunnel.addEventListener("click", async () => {
    const url = store.getState().status?.tunnel?.public_mcp_url;
    if (!url) return;
    await navigator.clipboard.writeText(url);
    showToast("Copied tunnel URL to clipboard.");
  });

  store.subscribe("providers:updated", renderProvidersAndSummary);
  store.subscribe("skills:updated", renderProvidersAndSummary);
  store.subscribe("status:updated", renderStatus);

  return {
    appendActivity,
    setRecentActivities,
    setSseConnected
  };
}
