/**
 * Centered Single-Column Skill Document Reading View Component
 * Renders a clean Notion/Linear-style document view without nested boxes,
 * strips duplicate H1 headings, supports 1-click per-provider sync toggling,
 * interactive "+ Add tag" dropdown popover, and exports portable SKILL.md files.
 */
import { api } from "../api.js?v=20261112-v7";
import {
  store,
  slugifyTitle,
  humanizeSlug,
  getBadgeVariant,
  getProviderSvgIcon,
  showToast,
  showConfirmDialog
} from "../state.js?v=20261112-v7";
import { renderMarkdown, highlightText, escapeHtml } from "../markdown.js?v=20261112-v7";

export function initSkillReader({
  onBackToLibrary,
  onEditSkill,
  onPreviewSkill,
  onSkillPromoted,
  onSkillDeleted
}) {
  let lastRenderedKey = "";
  const el = {
    btnBackToLibrary: document.getElementById("btn-back-to-library"),
    readCategoryBadge: document.getElementById("read-category-badge"),
    readSlugBadge: document.getElementById("read-slug-badge"),
    btnPromoteSkill: document.getElementById("btn-promote-skill"),
    btnUnsyncTemplate: document.getElementById("btn-unsync-template"),
    btnPreviewFromRead: document.getElementById("btn-preview-from-read"),
    btnExportSkill: document.getElementById("btn-export-skill"),
    btnEditFromRead: document.getElementById("btn-edit-from-read"),
    btnEditFromReadLabel: document.getElementById("btn-edit-from-read-label"),
    btnDeleteFromRead: document.getElementById("btn-delete-from-read"),
    detailScrollContainer: document.getElementById("detail-scroll-container"),
    readSearchBanner: document.getElementById("read-search-banner"),
    readSearchBannerText: document.getElementById("read-search-banner-text"),
    btnClearReadSearch: document.getElementById("btn-clear-read-search"),
    readTitle: document.getElementById("read-title"),
    readProvidersRow: document.getElementById("read-providers-row"),
    readTagsRow: document.getElementById("read-tags-row"),
    readTagPickerContainer: document.getElementById("read-tag-picker-container"),
    btnOpenTagPicker: document.getElementById("btn-open-tag-picker"),
    readTagPickerPopover: document.getElementById("read-tag-picker-popover"),
    readTagPickerInput: document.getElementById("read-tag-picker-input"),
    readTagPickerList: document.getElementById("read-tag-picker-list"),
    readSourcePath: document.getElementById("read-source-path"),
    readDescription: document.getElementById("read-description"),
    readTriggerBlock: document.getElementById("read-trigger-block"),
    readWhenToUse: document.getElementById("read-when-to-use"),
    readMarkdownBody: document.getElementById("read-markdown-body"),
    readSupportingSection: document.getElementById("read-supporting-section"),
    readSupportingCount: document.getElementById("read-supporting-count"),
    readSupportingList: document.getElementById("read-supporting-list")
  };

  function getInstalledProviderSet() {
    const { providers } = store.getState();
    const set = new Set();
    (providers || []).forEach((p) => {
      if (p.is_installed && p.provider !== "canonical") {
        set.add(p.provider.toLowerCase());
      }
    });
    return set;
  }

  async function saveUpdatedSkillTags(nextTags) {
    const { currentSkillModel: skill } = store.getState();
    if (!skill) return;
    try {
      const payload = {
        ...skill,
        tags: nextTags,
        source_category: skill.source_category || "user",
        origin_provider: skill.origin_provider || "My Skill",
        is_catalog_only: false
      };
      const res = await api.upsertSkill(payload);
      await onSkillPromoted(res);
    } catch (err) {
      showToast(`Failed to update tags: ${err.message}`);
    }
  }

  function renderTagPickerOptions(filterText = "") {
    if (!el.readTagPickerList) return;
    const { currentSkillModel: skill } = store.getState();
    if (!skill) return;

    const assignedSet = new Set((skill.tags || []).map((t) => t.toLowerCase()));
    const allKnown = store.getAllKnownUserTags();
    const q = (filterText || "").trim().toLowerCase().replace(/^#+/, "");

    const candidates = allKnown.filter((t) => !assignedSet.has(t) && (!q || t.includes(q)));
    el.readTagPickerList.innerHTML = "";

    if (q && !assignedSet.has(q) && !candidates.includes(q)) {
      const createBtn = document.createElement("button");
      createBtn.type = "button";
      createBtn.className = "tag-picker-option";
      createBtn.innerHTML = `<span>Create &amp; assign <strong>#${escapeHtml(slugifyTitle(q))}</strong></span><span>Enter ↵</span>`;
      createBtn.addEventListener("click", async () => {
        const clean = slugifyTitle(q);
        if (!clean) return;
        store.addCustomTag(clean);
        el.readTagPickerInput.value = "";
        el.readTagPickerPopover.classList.add("hidden");
        await saveUpdatedSkillTags([...(skill.tags || []), clean]);
        showToast(`Added tag #${clean}`);
      });
      el.readTagPickerList.appendChild(createBtn);
    }

    if (candidates.length === 0 && !q) {
      const hint = document.createElement("div");
      hint.className = "field-hint";
      hint.style.padding = "6px 8px";
      hint.textContent = "Type above and press Enter to create a new tag.";
      el.readTagPickerList.appendChild(hint);
      return;
    }

    candidates.forEach((tag) => {
      const opt = document.createElement("button");
      opt.type = "button";
      opt.className = "tag-picker-option";
      opt.innerHTML = `<span>#${escapeHtml(tag)}</span><span>+ Assign</span>`;
      opt.addEventListener("click", async () => {
        el.readTagPickerInput.value = "";
        el.readTagPickerPopover.classList.add("hidden");
        await saveUpdatedSkillTags([...(skill.tags || []), tag]);
        showToast(`Assigned tag #${tag}`);
      });
      el.readTagPickerList.appendChild(opt);
    });
  }

  function renderDocument() {
    const { currentSkillModel: skill, searchQuery } = store.getState();
    if (!skill) return;

    const q = (searchQuery || "").trim();
    const renderKey = `${skill.name}:${skill.instructions_markdown?.length || 0}:${(skill.tags || []).join(",")}:${q}:${skill.is_catalog_only}`;
    if (renderKey === lastRenderedKey && el.readTitle.textContent) {
      return;
    }
    lastRenderedKey = renderKey;

    const displayTitle = skill.title || humanizeSlug(skill.name);
    const badgeInfo = getBadgeVariant(skill);
    const installedProviders = getInstalledProviderSet();
    const isSyncedTemplate = !skill.is_catalog_only && skill.source_category === "template";

    el.readCategoryBadge.className = `source-badge ${badgeInfo.css}`;
    el.readCategoryBadge.innerHTML = `${getProviderSvgIcon(badgeInfo.iconKey, 12)}<span>${escapeHtml(badgeInfo.label)}</span>`;
    if (el.readSlugBadge) el.readSlugBadge.textContent = skill.name;

    // Button hierarchy based on whether this is a personal skill, synced template, or catalog item
    if (skill.is_catalog_only) {
      el.btnPromoteSkill.classList.remove("hidden");
      el.btnUnsyncTemplate?.classList.add("hidden");
      el.btnEditFromRead.className = "btn btn-secondary btn-sm";
      el.btnEditFromReadLabel.textContent = "Customize & Clone";
      el.btnDeleteFromRead.classList.add("hidden");
    } else if (isSyncedTemplate) {
      el.btnPromoteSkill.classList.add("hidden");
      el.btnUnsyncTemplate?.classList.remove("hidden");
      el.btnEditFromRead.className = "btn btn-secondary btn-sm";
      el.btnEditFromReadLabel.textContent = "Customize & Clone";
      el.btnDeleteFromRead.classList.add("hidden");
    } else {
      el.btnPromoteSkill.classList.add("hidden");
      el.btnUnsyncTemplate?.classList.add("hidden");
      el.btnEditFromRead.className = "btn btn-primary btn-sm";
      el.btnEditFromReadLabel.textContent = "Edit Skill";
      el.btnDeleteFromRead.classList.toggle("hidden", skill.name === "00-skillhub-bootstrap");
    }

    // Active search highlight notice
    if (q) {
      el.readSearchBanner.classList.remove("hidden");
      el.readSearchBannerText.innerHTML = `Highlighting search matches for <strong>"${escapeHtml(q)}"</strong>`;
    } else {
      el.readSearchBanner.classList.add("hidden");
    }

    // Title
    el.readTitle.innerHTML = highlightText(displayTitle, q);

    // Interactive Installed Provider Sync Pills (Click any installed provider pill to toggle sync!)
    el.readProvidersRow.innerHTML = "";
    if (!skill.is_catalog_only) {
      const providers = [
        { key: "gemini", label: "Gemini & Antigravity", on: skill.targets?.gemini?.enabled !== false },
        { key: "codex", label: "ChatGPT / Codex", on: skill.targets?.codex?.enabled !== false },
        { key: "claude", label: "Claude", on: skill.targets?.claude?.enabled !== false },
        { key: "cursor", label: "Cursor", on: skill.targets?.cursor?.enabled !== false }
      ];

      providers
        .filter((p) => installedProviders.has(p.key))
        .forEach((p) => {
          const pill = document.createElement("button");
          pill.type = "button";
          pill.className = p.on
            ? "provider-pill-active provider-pill-interactive"
            : "provider-pill-off provider-pill-interactive";
          pill.title = p.on
            ? `Synced to ${p.label} — click to disable on this provider`
            : `Not synced to ${p.label} — click to enable on this provider`;
          pill.innerHTML = p.on
            ? `${getProviderSvgIcon(p.key, 13)}<span>Synced: ${escapeHtml(p.label)}</span>`
            : `${getProviderSvgIcon(p.key, 12)}<span>${escapeHtml(p.label)} • Off</span>`;

          pill.addEventListener("click", async () => {
            try {
              pill.disabled = true;
              const nextEnabled = !p.on;
              const res = await api.toggleSkillProvider(skill.name, p.key, nextEnabled);
              showToast(
                `${nextEnabled ? "Enabled" : "Disabled"} '${displayTitle}' on ${p.label}.`
              );
              await onSkillPromoted(res);
            } catch (err) {
              showToast(`Failed to toggle provider: ${err.message}`);
            } finally {
              pill.disabled = false;
            }
          });

          el.readProvidersRow.appendChild(pill);
        });
    } else {
      const chip = document.createElement("span");
      chip.className = "provider-pill-off";
      chip.innerHTML = `<span>Read-only catalog skill • Click <strong>+ Sync to My AIs</strong> above to enable across your AI tools</span>`;
      el.readProvidersRow.appendChild(chip);
    }

    // Interactive User Tags
    el.readTagsRow.innerHTML = "";
    const visibleTags = (skill.tags || []).filter(
      (t) =>
        t.toLowerCase() !== "meta-skill" &&
        t.toLowerCase() !== "skillhub" &&
        t.toLowerCase() !== "system"
    );
    visibleTags.forEach((t) => {
      const tagEl = document.createElement("span");
      tagEl.className = "doc-tag-chip";
      const textSpan = document.createElement("span");
      textSpan.innerHTML = `#${highlightText(t, q)}`;
      tagEl.appendChild(textSpan);

      const removeBtn = document.createElement("button");
      removeBtn.type = "button";
      removeBtn.className = "doc-tag-remove-btn";
      removeBtn.title = `Remove tag #${t}`;
      removeBtn.textContent = "×";
      removeBtn.addEventListener("click", () => {
        const nextTags = (skill.tags || []).filter((existing) => existing !== t);
        saveUpdatedSkillTags(nextTags);
      });
      tagEl.appendChild(removeBtn);

      el.readTagsRow.appendChild(tagEl);
    });

    // Source file path (inside collapsed File & Sync Details section)
    if (el.readSourcePath) {
      el.readSourcePath.textContent =
        skill.source_path || `~/.skillhub/skills/${skill.name}/SKILL.md`;
    }

    // Left-Accented Summary Callout Strip (Purpose & Trigger)
    el.readDescription.innerHTML = skill.description
      ? highlightText(skill.description, q)
      : `<span class="field-hint">No summary description specified.</span>`;

    if (skill.when_to_use && skill.when_to_use.trim()) {
      el.readTriggerBlock.classList.remove("hidden");
      el.readWhenToUse.innerHTML = highlightText(skill.when_to_use, q);
    } else {
      el.readTriggerBlock.classList.add("hidden");
    }

    // Rich Rendered Markdown Instructions (stripLeadingH1 = true so we never show duplicate titles!)
    el.readMarkdownBody.innerHTML = renderMarkdown(skill.instructions_markdown || "", q, {
      stripLeadingH1: true
    });

    // Wire up Copy buttons on rendered code blocks
    el.readMarkdownBody.querySelectorAll(".md-code-copy").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const rawCode = btn.getAttribute("data-copy-code") || "";
        await navigator.clipboard.writeText(rawCode);
        btn.textContent = "Copied!";
        setTimeout(() => {
          btn.textContent = "Copy";
        }, 1500);
      });
    });

    // Supporting files (scripts/, references/, resources/, agents/)
    const files = skill.supporting_files || {};
    const fileKeys = Object.keys(files).sort();
    if (fileKeys.length > 0) {
      el.readSupportingSection.classList.remove("hidden");
      el.readSupportingCount.textContent = `${fileKeys.length} file${fileKeys.length === 1 ? "" : "s"}`;
      el.readSupportingList.innerHTML = "";

      fileKeys.forEach((relPath) => {
        const details = document.createElement("details");
        details.className = "read-supporting-card";

        const summary = document.createElement("summary");
        summary.className = "read-supporting-summary";
        summary.innerHTML = `<span>${escapeHtml(relPath)}</span><span class="field-hint">Click to view</span>`;

        const pre = document.createElement("pre");
        pre.className = "read-supporting-pre";
        pre.innerHTML = highlightText(files[relPath], q);

        details.appendChild(summary);
        details.appendChild(pre);
        el.readSupportingList.appendChild(details);
      });
    } else {
      el.readSupportingSection.classList.add("hidden");
    }
  }

  // Event Listeners
  el.btnBackToLibrary.addEventListener("click", onBackToLibrary);
  el.btnEditFromRead.addEventListener("click", onEditSkill);
  el.btnPreviewFromRead.addEventListener("click", onPreviewSkill);

  // Export .md Button
  el.btnExportSkill?.addEventListener("click", () => {
    const { currentSkillModel: skill, previews } = store.getState();
    if (!skill) return;

    const canonicalMd =
      previews?.canonical ||
      `---\nname: ${skill.name}\ntitle: "${(skill.title || skill.name).replace(/"/g, '\\"')}"\ndescription: "${(skill.description || "").replace(/"/g, '\\"')}"\nwhen_to_use: "${(skill.when_to_use || "").replace(/"/g, '\\"')}"\n---\n\n${skill.instructions_markdown || ""}\n`;

    const blob = new Blob([canonicalMd], { type: "text/markdown;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${skill.name}.SKILL.md`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    showToast(`Exported '${skill.name}.SKILL.md'`);
  });

  el.btnClearReadSearch.addEventListener("click", () => {
    const searchInput = document.getElementById("library-search-input");
    if (searchInput) searchInput.value = "";
    store.update({ searchQuery: "" }, "skills:filterChanged");
    renderDocument();
  });

  // Interactive "+ Add tag" Dropdown Popover
  el.btnOpenTagPicker?.addEventListener("click", (e) => {
    e.stopPropagation();
    if (!el.readTagPickerPopover) return;
    const isHidden = el.readTagPickerPopover.classList.contains("hidden");
    if (isHidden) {
      el.readTagPickerPopover.classList.remove("hidden");
      if (el.readTagPickerInput) {
        el.readTagPickerInput.value = "";
        el.readTagPickerInput.focus();
      }
      renderTagPickerOptions("");
    } else {
      el.readTagPickerPopover.classList.add("hidden");
    }
  });

  el.readTagPickerInput?.addEventListener("input", () => {
    renderTagPickerOptions(el.readTagPickerInput.value);
  });

  el.readTagPickerInput?.addEventListener("keydown", async (e) => {
    if (e.key === "Enter" || e.key === ",") {
      e.preventDefault();
      const raw = el.readTagPickerInput.value.trim();
      if (!raw) return;
      const clean = slugifyTitle(raw);
      el.readTagPickerInput.value = "";
      el.readTagPickerPopover.classList.add("hidden");
      if (!clean) return;

      store.addCustomTag(clean);
      const { currentSkillModel: skill } = store.getState();
      if (!skill) return;
      const currentTags = skill.tags || [];
      if (!currentTags.includes(clean)) {
        await saveUpdatedSkillTags([...currentTags, clean]);
        showToast(`Added tag #${clean}`);
      }
    } else if (e.key === "Escape") {
      el.readTagPickerPopover.classList.add("hidden");
    }
  });

  document.addEventListener("click", (e) => {
    if (
      el.readTagPickerPopover &&
      !el.readTagPickerPopover.classList.contains("hidden") &&
      !el.readTagPickerContainer?.contains(e.target)
    ) {
      el.readTagPickerPopover.classList.add("hidden");
    }
  });

  // "+ Sync to My AIs" on a Built-in Template preserves source_category = "template"
  el.btnPromoteSkill.addEventListener("click", async () => {
    const { currentSkillModel: skill } = store.getState();
    if (!skill) return;
    try {
      const payload = {
        ...skill,
        source_category: skill.source_category || "template",
        origin_provider: skill.origin_provider || "Built-in Template",
        is_catalog_only: false
      };
      const res = await api.upsertSkill(payload);
      showToast(
        `Synced '${res.skill.title || res.skill.name}' across your installed AI providers (view in Active & Synced).`
      );
      await onSkillPromoted(res);
    } catch (err) {
      showToast(`Failed to sync skill: ${err.message}`);
    }
  });

  // "Disable Sync" on a Synced Template reverts it back to catalog-only template
  el.btnUnsyncTemplate?.addEventListener("click", async () => {
    const { currentSkillModel: skill } = store.getState();
    if (!skill) return;
    const label = skill.title || skill.name;
    try {
      await api.deleteSkill(skill.name);
      const refreshed = await api.getSkill(skill.name);
      showToast(`Disabled sync for template '${label}'.`);
      await onSkillPromoted(refreshed);
    } catch (err) {
      showToast(`Failed to disable sync: ${err.message}`);
    }
  });

  el.btnDeleteFromRead.addEventListener("click", async () => {
    const { selectedSkillName, currentSkillModel } = store.getState();
    if (!selectedSkillName) return;
    const label = currentSkillModel?.title || selectedSkillName;
    const ok = await showConfirmDialog({
      title: "Delete Skill",
      message: `Delete '${label}' from SkillHub and all connected AI providers? This will remove the skill file from your disk.`,
      confirmText: "Delete Skill",
      isDanger: true
    });
    if (!ok) return;

    try {
      await api.deleteSkill(selectedSkillName);
      showToast(`Deleted '${label}'.`);
      await onSkillDeleted(selectedSkillName);
    } catch (err) {
      showToast(`Delete failed: ${err.message}`);
    }
  });

  store.subscribe("skill:selected", () => {
    renderDocument();
    if (el.detailScrollContainer) {
      el.detailScrollContainer.scrollTop = 0;
    }
  });
  store.subscribe("providers:updated", renderDocument);
  store.subscribe("skills:filterChanged", renderDocument);
}
