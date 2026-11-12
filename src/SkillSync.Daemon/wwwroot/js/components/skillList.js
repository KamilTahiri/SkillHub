/**
 * Full-Window Skill Library, 5 Category Tabs (My Skills, Active & Synced, Built-in & Templates, System, Plugin Catalog),
 * Tag Scope Toggle ([Current Tab | All Skills]), Smart Auto-Tab Switching, Tag CRUD Management,
 * and High-Visibility Empty States
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
import {
  highlightText,
  extractInstructionMatchSnippet,
  escapeHtml
} from "../markdown.js?v=20261112-v7";

export function initSkillList({ onSelectSkill, onQuickSyncSkill, onRefreshAll }) {
  const el = {
    librarySearchInput: document.getElementById("library-search-input"),
    btnClearSearch: document.getElementById("btn-clear-search"),
    catTabs: document.querySelectorAll(".cat-tab"),
    countUser: document.getElementById("count-user"),
    countSynced: document.getElementById("count-synced"),
    countTemplate: document.getElementById("count-template"),
    countSystem: document.getElementById("count-system"),
    countPlugin: document.getElementById("count-plugin"),
    overviewTotalCount: document.getElementById("overview-total-count"),
    statCountPersonal: document.getElementById("stat-count-personal"),
    statCountSynced: document.getElementById("stat-count-synced"),
    statSubdetailSynced: document.getElementById("stat-subdetail-synced"),
    statCountTemplates: document.getElementById("stat-count-templates"),
    statCountPlugins: document.getElementById("stat-count-plugins"),
    statCountSystem: document.getElementById("stat-count-system"),
    statPills: document.querySelectorAll(".stat-pill"),
    libraryGlobalSummary: document.getElementById("library-global-summary"),
    libraryTabExplanation: document.getElementById("library-tab-explanation"),
    activeFilterIndicator: document.getElementById("active-filter-indicator"),
    btnToggleFilters: document.getElementById("btn-toggle-filters"),
    collapsibleFilterDrawer: document.getElementById("collapsible-filter-drawer"),
    originFilterRow: document.getElementById("origin-filter-row"),
    originFilterBar: document.getElementById("origin-filter-bar"),
    btnTagScopeCurrent: document.getElementById("btn-tag-scope-current"),
    btnTagScopeAll: document.getElementById("btn-tag-scope-all"),
    btnToggleManageTags: document.getElementById("btn-toggle-manage-tags"),
    tagFilterBar: document.getElementById("tag-filter-bar"),
    newCustomTagInput: document.getElementById("new-custom-tag-input"),
    btnCreateCustomTag: document.getElementById("btn-create-custom-tag"),
    manageTagsPanel: document.getElementById("manage-tags-panel"),
    manageTagsList: document.getElementById("manage-tags-list"),
    libraryGrid: document.getElementById("library-grid")
  };

  const CATEGORY_LABELS = {
    user: "My Skills",
    synced: "Active & Synced",
    template: "Built-in & Templates",
    system: "System",
    plugin: "Plugin Catalog"
  };

  function isReservedSystemTag(tag) {
    const lower = (tag || "").toLowerCase();
    return lower === "meta-skill" || lower === "skillhub" || lower === "system";
  }

  function skillBelongsToCategory(skill, category) {
    const cat = (skill.source_category || "user").toLowerCase();
    if (category === "synced") {
      return !skill.is_catalog_only && cat !== "system";
    }
    return cat === category;
  }

  function matchesSearch(skill, query) {
    if (!query) return true;
    const q = query.toLowerCase();
    const displayTitle = (skill.title || humanizeSlug(skill.name)).toLowerCase();
    const slug = (skill.name || "").toLowerCase();
    const desc = (skill.description || "").toLowerCase();
    const trigger = (skill.when_to_use || "").toLowerCase();
    const origin = (skill.origin_provider || "").toLowerCase();
    const instructions = (skill.instructions_markdown || "").toLowerCase();
    const tagsMatch = (skill.tags || []).some((t) => t.toLowerCase().includes(q));

    if (
      displayTitle.includes(q) ||
      slug.includes(q) ||
      desc.includes(q) ||
      trigger.includes(q) ||
      origin.includes(q) ||
      instructions.includes(q) ||
      tagsMatch
    ) {
      return true;
    }

    const tokens = q.split(/\s+/).filter((t) => t.length >= 2);
    if (tokens.length > 1) {
      const combined = `${displayTitle} ${slug} ${desc} ${trigger} ${origin} ${(skill.tags || []).join(" ")} ${instructions}`;
      return tokens.every((tok) => combined.includes(tok));
    }

    return false;
  }

  function updateCategoryCounts(skills, searchQuery) {
    const queryFiltered = (skills || []).filter((s) => matchesSearch(s, searchQuery));
    const userCount = queryFiltered.filter((s) => skillBelongsToCategory(s, "user")).length;
    const syncedCount = queryFiltered.filter((s) => skillBelongsToCategory(s, "synced")).length;
    const templateCount = queryFiltered.filter((s) => skillBelongsToCategory(s, "template")).length;
    const systemCount = queryFiltered.filter((s) => skillBelongsToCategory(s, "system")).length;
    const pluginCount = queryFiltered.filter((s) => skillBelongsToCategory(s, "plugin")).length;

    if (el.countUser) el.countUser.textContent = String(userCount);
    if (el.countSynced) el.countSynced.textContent = String(syncedCount);
    if (el.countTemplate) el.countTemplate.textContent = String(templateCount);
    if (el.countSystem) el.countSystem.textContent = String(systemCount);
    if (el.countPlugin) el.countPlugin.textContent = String(pluginCount);

    // Multi-Tab Overview Global Summary & Stat Strip Breakdown
    const totalUnique = (skills || []).length;
    const totalPersonal = (skills || []).filter((s) => (s.source_category || "user") === "user" && !s.is_catalog_only).length;
    const totalSynced = (skills || []).filter((s) => !s.is_catalog_only && s.source_category !== "system").length;
    const totalTemplates = (skills || []).filter((s) => s.source_category === "template").length;
    const syncedTemplates = (skills || []).filter((s) => s.source_category === "template" && !s.is_catalog_only).length;
    const totalPlugins = (skills || []).filter((s) => s.source_category === "plugin").length;
    const totalSystem = (skills || []).filter((s) => s.source_category === "system").length;

    if (el.overviewTotalCount) el.overviewTotalCount.textContent = String(totalUnique);
    if (el.statCountPersonal) el.statCountPersonal.textContent = String(totalPersonal);
    if (el.statCountSynced) el.statCountSynced.textContent = String(totalSynced);
    if (el.statSubdetailSynced) el.statSubdetailSynced.textContent = `(${totalPersonal} Personal + ${syncedTemplates} Templates)`;
    if (el.statCountTemplates) el.statCountTemplates.textContent = String(totalTemplates);
    if (el.statCountPlugins) el.statCountPlugins.textContent = String(totalPlugins);
    if (el.statCountSystem) el.statCountSystem.textContent = String(totalSystem);

    if (el.libraryGlobalSummary) {
      el.libraryGlobalSummary.textContent = `${totalPersonal} Personal · ${totalSynced} Synced · ${totalTemplates} Built-in`;
    }

    if (el.libraryTabExplanation) {
      const { categoryFilter, tagScope, selectedTag } = store.getState();
      if (selectedTag && tagScope === "all") {
        el.libraryTabExplanation.textContent = `All Skills Scope — Showing all skills matching #${selectedTag} across Personal, Synced, Templates, and Plugins.`;
      } else if (categoryFilter === "user") {
        el.libraryTabExplanation.textContent = `My Skills — Showing ${userCount} personal skills authored by you or imported. All active skills here synchronize automatically across your AI tools.`;
      } else if (categoryFilter === "synced") {
        el.libraryTabExplanation.textContent = `Active & Synced — Showing all ${totalSynced} skills currently being synchronized across your AI providers (${totalPersonal} Personal + ${syncedTemplates} Templates).`;
      } else if (categoryFilter === "template") {
        el.libraryTabExplanation.textContent = `Built-in & Templates — Curated agent skills from OpenAI Codex, Antigravity, Claude, and Cursor. Click "+ Sync to My AIs" on any skill to activate sync.`;
      } else if (categoryFilter === "system") {
        el.libraryTabExplanation.textContent = `System — Internal meta-skills, bootstrap protocol, and core system utilities.`;
      } else if (categoryFilter === "plugin") {
        el.libraryTabExplanation.textContent = `Plugin Catalog — External skills discovered in AI agent plugin directories.`;
      }
    }

    return {
      userCount,
      syncedCount,
      templateCount,
      systemCount,
      pluginCount,
      total: queryFiltered.length
    };
  }

  function syncActiveCategoryTabUi() {
    const { categoryFilter, tagScope } = store.getState();
    el.catTabs.forEach((tab) => {
      const cat = tab.getAttribute("data-category");
      tab.classList.toggle("active", cat === categoryFilter);
    });
    el.statPills?.forEach((pill) => {
      const cat = pill.getAttribute("data-stat-category");
      pill.classList.toggle("active", cat === categoryFilter);
    });
    el.btnTagScopeCurrent?.classList.toggle("active", tagScope === "current");
    el.btnTagScopeAll?.classList.toggle("active", tagScope === "all");
  }

  function findBestTabForTag(allSkills, tag) {
    const order = ["user", "synced", "template", "plugin"];
    for (const cat of order) {
      const hasMatch = (allSkills || []).some(
        (s) =>
          skillBelongsToCategory(s, cat) &&
          (s.tags || []).some((t) => t.toLowerCase() === tag.toLowerCase())
      );
      if (hasMatch) return cat;
    }
    return null;
  }

  function renderManageTagsPanel(allSkills) {
    if (!el.manageTagsPanel || el.manageTagsPanel.classList.contains("hidden") || !el.manageTagsList) {
      return;
    }
    const { customTags } = store.getState();
    const globalCounts = new Map();
    (customTags || []).forEach((ct) => {
      if (!isReservedSystemTag(ct)) {
        globalCounts.set(ct.toLowerCase(), 0);
      }
    });

    (allSkills || []).forEach((s) => {
      if ((s.source_category || "user") === "system") return;
      (s.tags || []).forEach((t) => {
        const lower = t.toLowerCase();
        if (!isReservedSystemTag(lower)) {
          globalCounts.set(lower, (globalCounts.get(lower) || 0) + 1);
        }
      });
    });

    el.manageTagsList.innerHTML = "";
    const sorted = Array.from(globalCounts.entries()).sort((a, b) => a[0].localeCompare(b[0]));

    if (sorted.length === 0) {
      el.manageTagsList.innerHTML = `<span class="field-hint">No tags created yet.</span>`;
      return;
    }

    sorted.forEach(([tag, count]) => {
      const item = document.createElement("div");
      item.className = "manage-tag-item";
      item.innerHTML = `
        <span class="manage-tag-name">#${escapeHtml(tag)}</span>
        <span class="manage-tag-count">${count} skill${count === 1 ? "" : "s"}</span>
        <div class="manage-tag-actions">
          <button type="button" class="btn btn-secondary btn-sm btn-rename-tag">Rename</button>
          <button type="button" class="btn btn-ghost-danger btn-sm btn-delete-tag">Delete</button>
        </div>
      `;

      const renameBtn = item.querySelector(".btn-rename-tag");
      const delBtn = item.querySelector(".btn-delete-tag");

      renameBtn.addEventListener("click", () => {
        // Toggle inline editing mode
        item.innerHTML = `
          <input type="text" class="manage-tag-inline-input" value="${escapeHtml(tag)}" />
          <div class="manage-tag-actions">
            <button type="button" class="btn btn-primary btn-sm btn-save-rename">Save</button>
            <button type="button" class="btn btn-secondary btn-sm btn-cancel-rename">Cancel</button>
          </div>
        `;
        const input = item.querySelector(".manage-tag-inline-input");
        const saveBtn = item.querySelector(".btn-save-rename");
        const cancelBtn = item.querySelector(".btn-cancel-rename");

        input.focus();
        input.select();

        async function doSave() {
          const nextRaw = input.value.trim();
          const nextClean = slugifyTitle(nextRaw);
          if (!nextClean || nextClean === tag) {
            renderManageTagsPanel(allSkills);
            return;
          }
          try {
            saveBtn.disabled = true;
            saveBtn.textContent = "Saving...";
            await api.renameTag(tag, nextClean);
            store.renameCustomTag(tag, nextClean);
            if (onRefreshAll) await onRefreshAll();
            showToast(`Renamed tag #${tag} to #${nextClean}`);
          } catch (err) {
            showToast(`Rename failed: ${err.message}`);
            renderManageTagsPanel(allSkills);
          }
        }

        saveBtn.addEventListener("click", doSave);
        cancelBtn.addEventListener("click", () => renderManageTagsPanel(allSkills));
        input.addEventListener("keydown", (e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            doSave();
          } else if (e.key === "Escape") {
            renderManageTagsPanel(allSkills);
          }
        });
      });

      delBtn.addEventListener("click", async () => {
        const ok = await showConfirmDialog({
          title: "Delete Tag",
          message: `Delete tag #${tag} from all ${count} skill(s)? This will remove #${tag} from their metadata.`,
          confirmText: "Delete Tag",
          isDanger: true
        });
        if (!ok) return;

        try {
          await api.deleteTag(tag);
          store.removeCustomTag(tag);
          if (onRefreshAll) await onRefreshAll();
          showToast(`Deleted tag #${tag}`);
        } catch (err) {
          showToast(`Delete failed: ${err.message}`);
        }
      });

      el.manageTagsList.appendChild(item);
    });
  }

  function renderOriginAndTagFilters(allSkills, categoryMatchedSkills) {
    const { originFilter, selectedTag, customTags, categoryFilter, tagScope } = store.getState();

    const activeFilters = [];
    if (originFilter) activeFilters.push(originFilter);
    if (selectedTag) activeFilters.push(`#${selectedTag}`);
    if (el.activeFilterIndicator) {
      if (activeFilters.length > 0) {
        el.activeFilterIndicator.classList.remove("hidden");
        el.activeFilterIndicator.textContent = activeFilters.join(" • ");
      } else {
        el.activeFilterIndicator.classList.add("hidden");
      }
    }

    // 1. Source Filter Chips
    const originsMap = new Map();
    categoryMatchedSkills.forEach((s) => {
      const badge = getBadgeVariant(s);
      originsMap.set(badge.label, (originsMap.get(badge.label) || 0) + 1);
    });

    if (el.originFilterBar && el.originFilterRow) {
      el.originFilterBar.innerHTML = "";
      if (originsMap.size > 1) {
        el.originFilterRow.classList.remove("hidden");
        const topOrigins = Array.from(originsMap.entries())
          .sort((a, b) => b[1] - a[1])
          .slice(0, 6);

        topOrigins.forEach(([originLabel, count]) => {
          const btn = document.createElement("button");
          btn.type = "button";
          btn.className = "tag-chip" + (originFilter === originLabel ? " active" : "");
          btn.textContent = `${originLabel} (${count})`;
          btn.addEventListener("click", () => {
            const next = store.getState().originFilter === originLabel ? "" : originLabel;
            store.update({ originFilter: next }, "skills:filterChanged");
          });
          el.originFilterBar.appendChild(btn);
        });
      } else {
        el.originFilterRow.classList.add("hidden");
      }
    }

    // 2. Tag Counts (Current Tab vs All Skills) — strictly excluding "system", "skillhub", "meta-skill" unless System tab is open
    const currentTabCounts = new Map();
    const globalCounts = new Map();

    (customTags || []).forEach((ct) => {
      const lower = ct.toLowerCase();
      if (!isReservedSystemTag(lower)) {
        currentTabCounts.set(lower, 0);
        globalCounts.set(lower, 0);
      }
    });

    (allSkills || []).forEach((s) => {
      const isSysSkill = (s.source_category || "user") === "system";
      if (isSysSkill && categoryFilter !== "system") return;

      const inCurrentTab = skillBelongsToCategory(s, categoryFilter);
      (s.tags || []).forEach((t) => {
        const lower = t.toLowerCase();
        if (categoryFilter !== "system" && isReservedSystemTag(lower)) return;

        globalCounts.set(lower, (globalCounts.get(lower) || 0) + 1);
        if (inCurrentTab) {
          currentTabCounts.set(lower, (currentTabCounts.get(lower) || 0) + 1);
        } else if (!currentTabCounts.has(lower)) {
          currentTabCounts.set(lower, 0);
        }
      });
    });

    const displayCounts = tagScope === "all" ? globalCounts : currentTabCounts;

    if (el.tagFilterBar) {
      el.tagFilterBar.innerHTML = "";
      const sortedTags = Array.from(displayCounts.entries())
        .filter(([tag, count]) =>
          tagScope === "current" ? count > 0 : count > 0 || (customTags || []).includes(tag)
        )
        .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

      if (sortedTags.length === 0) {
        const hint = document.createElement("span");
        hint.className = "field-hint";
        hint.textContent =
          tagScope === "current"
            ? "No tags assigned to skills in this tab yet."
            : "No custom tags yet. Create a tag on the right or inside any skill.";
        el.tagFilterBar.appendChild(hint);
      } else {
        sortedTags.forEach(([tag, count]) => {
          const chip = document.createElement("button");
          chip.type = "button";
          chip.className = "tag-chip" + (selectedTag === tag ? " active" : "");

          const labelSpan = document.createElement("span");
          labelSpan.textContent = `#${tag} (${count})`;
          chip.appendChild(labelSpan);

          chip.addEventListener("click", () => {
            const isDeselecting = store.getState().selectedTag === tag;
            if (isDeselecting) {
              store.update({ selectedTag: "" }, "skills:filterChanged");
              return;
            }

            // In "Current Tab" mode: Smart Auto-Tab Switching if 0 matches in current tab
            if (tagScope === "current") {
              const currentCount = currentTabCounts.get(tag) || 0;
              if (currentCount === 0 && (globalCounts.get(tag) || 0) > 0) {
                const targetCat = findBestTabForTag(allSkills, tag);
                if (targetCat && targetCat !== categoryFilter) {
                  store.update(
                    { selectedTag: tag, categoryFilter: targetCat, originFilter: "" },
                    "skills:filterChanged"
                  );
                  showToast(
                    `Switched to ${CATEGORY_LABELS[targetCat] || targetCat} to show skills tagged #${tag}`
                  );
                  return;
                }
              }
            }

            // In "All Skills" mode: keep all-skills scope and filter across all categories!
            store.update({ selectedTag: tag }, "skills:filterChanged");
          });
          el.tagFilterBar.appendChild(chip);
        });
      }
    }

    renderManageTagsPanel(allSkills);
  }

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

  function getFilteredSkills() {
    const { skills, categoryFilter, originFilter, selectedTag, searchQuery, tagScope } = store.getState();
    const q = (searchQuery || "").trim();

    updateCategoryCounts(skills, q);
    syncActiveCategoryTabUi();

    let pool;
    if (tagScope === "all" && selectedTag) {
      // In "All Skills" scope with an active tag, pool across all categories (excluding system unless in system tab)
      pool = (skills || []).filter(
        (s) => (s.source_category || "user") !== "system" || categoryFilter === "system"
      );
    } else {
      pool = (skills || []).filter((s) => skillBelongsToCategory(s, categoryFilter));
    }

    renderOriginAndTagFilters(skills, pool);

    return pool.filter((s) => {
      if (originFilter) {
        const badge = getBadgeVariant(s);
        if (badge.label !== originFilter) return false;
      }
      if (selectedTag && !(s.tags || []).some((t) => t.toLowerCase() === selectedTag.toLowerCase())) {
        return false;
      }
      return matchesSearch(s, q);
    });
  }

  function renderLibraryGrid() {
    const { skills, searchQuery, selectedTag, originFilter, categoryFilter } = store.getState();
    const q = (searchQuery || "").trim();
    const counts = updateCategoryCounts(skills, q);
    const filtered = getFilteredSkills();
    const installedProviders = getInstalledProviderSet();

    el.btnClearSearch.classList.toggle("hidden", !q);
    el.libraryGrid.innerHTML = "";

    if (filtered.length === 0) {
      const empty = document.createElement("div");
      empty.className = "library-empty-state";

      if (selectedTag) {
        const targetCat = findBestTabForTag(skills, selectedTag);
        if (targetCat && targetCat !== categoryFilter) {
          empty.innerHTML = `
            <div class="library-empty-title">No skills tagged #${escapeHtml(selectedTag)} in ${escapeHtml(CATEGORY_LABELS[categoryFilter] || categoryFilter)}</div>
            <div class="library-empty-desc">Skills with the tag <strong>#${escapeHtml(selectedTag)}</strong> were found in <strong>${escapeHtml(CATEGORY_LABELS[targetCat])}</strong>.</div>
            <div class="library-empty-actions"></div>
          `;
          const actions = empty.querySelector(".library-empty-actions");
          const switchBtn = document.createElement("button");
          switchBtn.type = "button";
          switchBtn.className = "btn btn-primary btn-sm";
          switchBtn.textContent = `Switch to ${CATEGORY_LABELS[targetCat]}`;
          switchBtn.addEventListener("click", () => {
            store.update({ categoryFilter: targetCat, originFilter: "" }, "skills:filterChanged");
          });
          const clearBtn = document.createElement("button");
          clearBtn.type = "button";
          clearBtn.className = "btn btn-secondary btn-sm";
          clearBtn.textContent = `Clear #${selectedTag} Filter`;
          clearBtn.addEventListener("click", () => {
            store.update({ selectedTag: "" }, "skills:filterChanged");
          });
          actions.appendChild(switchBtn);
          actions.appendChild(clearBtn);
        } else {
          empty.innerHTML = `
            <div class="library-empty-title">No skills assigned to #${escapeHtml(selectedTag)} yet</div>
            <div class="library-empty-desc">Open any skill and click <strong>+ Add tag</strong> below its title to assign <strong>#${escapeHtml(selectedTag)}</strong>.</div>
            <div class="library-empty-actions"></div>
          `;
          const actions = empty.querySelector(".library-empty-actions");
          const clearBtn = document.createElement("button");
          clearBtn.type = "button";
          clearBtn.className = "btn btn-secondary btn-sm";
          clearBtn.textContent = `Clear #${selectedTag} Filter`;
          clearBtn.addEventListener("click", () => {
            store.update({ selectedTag: "" }, "skills:filterChanged");
          });
          actions.appendChild(clearBtn);
        }
      } else if (q && counts.total > 0) {
        let suggestedCat = "template";
        let suggestedLabel = "Built-in & Templates";
        if (counts.userCount > 0) {
          suggestedCat = "user";
          suggestedLabel = `My Skills (${counts.userCount})`;
        } else if (counts.syncedCount > 0) {
          suggestedCat = "synced";
          suggestedLabel = `Active & Synced (${counts.syncedCount})`;
        } else if (counts.templateCount > 0) {
          suggestedCat = "template";
          suggestedLabel = `Built-in & Templates (${counts.templateCount})`;
        } else if (counts.systemCount > 0) {
          suggestedCat = "system";
          suggestedLabel = `System (${counts.systemCount})`;
        } else if (counts.pluginCount > 0) {
          suggestedCat = "plugin";
          suggestedLabel = `Plugin Catalog (${counts.pluginCount})`;
        }

        empty.innerHTML = `
          <div class="library-empty-title">No matches in ${escapeHtml(CATEGORY_LABELS[categoryFilter] || "this tab")} for "${escapeHtml(q)}"</div>
          <div class="library-empty-desc">We found <strong>${counts.total}</strong> matching skill${counts.total === 1 ? "" : "s"} in other tabs.</div>
          <div class="library-empty-actions"></div>
        `;
        const actions = empty.querySelector(".library-empty-actions");
        const switchBtn = document.createElement("button");
        switchBtn.type = "button";
        switchBtn.className = "btn btn-primary btn-sm";
        switchBtn.textContent = `View matches in ${suggestedLabel}`;
        switchBtn.addEventListener("click", () => {
          store.update({ categoryFilter: suggestedCat, originFilter: "" }, "skills:filterChanged");
        });
        actions.appendChild(switchBtn);
      } else {
        empty.innerHTML = `
          <div class="library-empty-title">${q ? `No skills matched "${escapeHtml(q)}"` : `No skills in ${escapeHtml(CATEGORY_LABELS[categoryFilter] || "this category")} yet`}</div>
          <div class="library-empty-desc">${
            q || originFilter
              ? "Try clearing your search query or source filter."
              : categoryFilter === "user"
                ? "Create a new personal skill or import one from skills.sh / GitHub."
                : "Enable skills from Built-in & Templates or My Skills to see them here."
          }</div>
        `;
      }

      el.libraryGrid.appendChild(empty);
      return;
    }

    filtered.forEach((skill) => {
      const card = document.createElement("article");
      card.className = "library-card";

      // 1. Header: Human-Readable Title + Origin Badge + Synced Pill
      const top = document.createElement("div");
      top.className = "library-card-top";

      const titleEl = document.createElement("h3");
      titleEl.className = "library-card-title";
      const displayTitle = skill.title || humanizeSlug(skill.name);
      titleEl.innerHTML = highlightText(displayTitle, q);

      const badgesWrap = document.createElement("div");
      badgesWrap.className = "card-badges-wrap";

      const badgeInfo = getBadgeVariant(skill);
      const badgeEl = document.createElement("span");
      badgeEl.className = `source-badge ${badgeInfo.css}`;
      badgeEl.innerHTML = `${getProviderSvgIcon(badgeInfo.iconKey, 12)}<span>${escapeHtml(badgeInfo.label)}</span>`;
      badgesWrap.appendChild(badgeEl);

      // Subtle ● Synced status badge on active synced skills
      if (!skill.is_catalog_only && skill.source_category !== "system") {
        const syncedBadge = document.createElement("span");
        if (categoryFilter === "synced") {
          syncedBadge.className = "status-badge-synced-pill live-synced-pulse";
          syncedBadge.title = "Currently active & synchronized across your installed AI providers";
          syncedBadge.innerHTML = `<span class="synced-dot">●</span><span>Live in Active & Synced</span>`;
        } else {
          syncedBadge.className = "status-badge-synced-pill";
          syncedBadge.title = "Currently active & synced across your installed AI providers";
          syncedBadge.innerHTML = `<span class="synced-dot">●</span><span>Synced</span>`;
        }
        badgesWrap.appendChild(syncedBadge);
      }

      // If in "All Skills" tag mode and viewing cross-category results, show the category name
      const { tagScope: activeTagScope } = store.getState();
      if (activeTagScope === "all" && selectedTag) {
        const catBadge = document.createElement("span");
        catBadge.className = "card-user-tag";
        catBadge.textContent = CATEGORY_LABELS[skill.source_category] || skill.source_category;
        badgesWrap.appendChild(catBadge);
      }

      top.appendChild(titleEl);
      top.appendChild(badgesWrap);
      card.appendChild(top);

      // 2. Description ("What it does")
      if (skill.description) {
        const descEl = document.createElement("div");
        descEl.className = "library-card-desc";
        descEl.innerHTML = highlightText(skill.description, q);
        card.appendChild(descEl);
      }

      // 3. Compact Lightning Trigger Pill ("When to use")
      if (skill.when_to_use && skill.when_to_use.trim()) {
        const trigEl = document.createElement("div");
        trigEl.className = "card-trigger-pill";
        trigEl.title = `Trigger: ${skill.when_to_use}`;
        trigEl.innerHTML = `
          <svg class="trigger-bolt" viewBox="0 0 24 24" fill="currentColor">
            <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
          </svg>
          <span>${highlightText(skill.when_to_use, q)}</span>
        `;
        card.appendChild(trigEl);
      }

      // 4. Live Instructions Match Excerpt (when searching!)
      if (q) {
        const snippetHtml = extractInstructionMatchSnippet(skill.instructions_markdown, q);
        if (snippetHtml) {
          const matchBox = document.createElement("div");
          matchBox.className = "library-card-match-snippet";
          matchBox.innerHTML =
            `<div class="library-card-match-label">Matched in instructions</div>` +
            `<div>${snippetHtml}</div>`;
          card.appendChild(matchBox);
        }
      }

      // 5. Card Footer: User Tags (left) + Synced Installed Provider Pills OR 1-Click "+ Sync to My AIs" (right)
      const footer = document.createElement("div");
      footer.className = "library-card-footer";

      const tagsWrap = document.createElement("div");
      tagsWrap.className = "library-card-tags";
      const userTags = (skill.tags || []).filter((t) => !isReservedSystemTag(t));
      userTags.slice(0, 3).forEach((t) => {
        const tagSpan = document.createElement("span");
        tagSpan.className = "card-user-tag";
        tagSpan.innerHTML = `#${highlightText(t, q)}`;
        tagsWrap.appendChild(tagSpan);
      });

      const providersWrap = document.createElement("div");
      providersWrap.className = "library-card-providers";

      if (!skill.is_catalog_only) {
        const providerDefs = [
          {
            key: "gemini",
            label: "Gemini & Antigravity",
            shortLabel: "Gemini",
            enabled: skill.targets?.gemini?.enabled !== false
          },
          {
            key: "codex",
            label: "ChatGPT / Codex",
            shortLabel: "Codex",
            enabled: skill.targets?.codex?.enabled !== false
          },
          {
            key: "claude",
            label: "Claude",
            shortLabel: "Claude",
            enabled: skill.targets?.claude?.enabled !== false
          },
          {
            key: "cursor",
            label: "Cursor",
            shortLabel: "Cursor",
            enabled: skill.targets?.cursor?.enabled !== false
          }
        ];

        providerDefs
          .filter((p) => installedProviders.has(p.key))
          .forEach((p) => {
            const tag = document.createElement("span");
            if (p.enabled) {
              tag.className = "provider-pill-active";
              tag.innerHTML = `${getProviderSvgIcon(p.key, 12)}<span>${escapeHtml(p.label)}</span>`;
            } else {
              tag.className = "provider-pill-off";
              tag.title = `Sync disabled for ${p.label}`;
              tag.innerHTML = `${getProviderSvgIcon(p.key, 11)}<span>${escapeHtml(p.shortLabel)} • Off</span>`;
            }
            providersWrap.appendChild(tag);
          });
      } else {
        const syncBtn = document.createElement("button");
        syncBtn.type = "button";
        syncBtn.className = "btn-card-sync";
        syncBtn.title = "Enable and sync this template across all your installed AI providers";
        syncBtn.innerHTML = `<span>+ Sync to My AIs</span>`;
        syncBtn.addEventListener("click", async (e) => {
          e.stopPropagation();
          if (!onQuickSyncSkill) return;
          syncBtn.disabled = true;
          syncBtn.textContent = "Syncing...";
          await onQuickSyncSkill(skill);
        });
        providersWrap.appendChild(syncBtn);
      }

      footer.appendChild(tagsWrap);
      footer.appendChild(providersWrap);
      card.appendChild(footer);

      card.addEventListener("click", () => {
        const cur = store.getState();
        if (cur.selectedSkillName === skill.name && cur.activeScreen === "detail") {
          return;
        }
        onSelectSkill(skill.name);
      });
      el.libraryGrid.appendChild(card);
    });
  }

  function handleCreateCustomTag() {
    if (!el.newCustomTagInput) return;
    const val = el.newCustomTagInput.value.trim();
    if (!val) return;
    const created = store.addCustomTag(val);
    el.newCustomTagInput.value = "";
    if (created) {
      showToast(`Created tag #${created}`);
    }
  }

  // Event Listeners
  el.librarySearchInput.addEventListener("input", () => {
    store.update({ searchQuery: el.librarySearchInput.value }, "skills:filterChanged");
  });

  el.btnClearSearch.addEventListener("click", () => {
    el.librarySearchInput.value = "";
    store.update({ searchQuery: "" }, "skills:filterChanged");
    el.librarySearchInput.focus();
  });

  el.catTabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const category = tab.getAttribute("data-category") || "user";
      store.update({ categoryFilter: category, originFilter: "" }, "skills:filterChanged");
    });
  });

  el.statPills?.forEach((pill) => {
    pill.addEventListener("click", () => {
      const category = pill.getAttribute("data-stat-category") || "user";
      store.update({ categoryFilter: category, originFilter: "" }, "skills:filterChanged");
    });
  });

  el.btnToggleFilters?.addEventListener("click", () => {
    const isHidden = el.collapsibleFilterDrawer.classList.contains("hidden");
    el.collapsibleFilterDrawer.classList.toggle("hidden", !isHidden);
    el.btnToggleFilters.classList.toggle("open", isHidden);
  });

  el.btnTagScopeCurrent?.addEventListener("click", () => {
    store.update({ tagScope: "current" }, "skills:filterChanged");
  });

  el.btnTagScopeAll?.addEventListener("click", () => {
    store.update({ tagScope: "all" }, "skills:filterChanged");
  });

  el.btnToggleManageTags?.addEventListener("click", () => {
    if (!el.manageTagsPanel) return;
    const isHidden = el.manageTagsPanel.classList.contains("hidden");
    el.manageTagsPanel.classList.toggle("hidden", !isHidden);
    renderManageTagsPanel(store.getState().skills || []);
  });

  el.btnCreateCustomTag?.addEventListener("click", handleCreateCustomTag);
  el.newCustomTagInput?.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      handleCreateCustomTag();
    }
  });

  window.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      if (store.getState().activeScreen !== "library") {
        store.update({ activeScreen: "library" }, "screen:changed");
      }
      el.librarySearchInput.focus();
      el.librarySearchInput.select();
    } else if (
      e.key === "/" &&
      store.getState().activeScreen === "library" &&
      document.activeElement !== el.librarySearchInput &&
      !["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName || "")
    ) {
      e.preventDefault();
      el.librarySearchInput.focus();
    }
  });

  store.subscribe("skills:updated", renderLibraryGrid);
  store.subscribe("providers:updated", renderLibraryGrid);
  store.subscribe("skills:filterChanged", renderLibraryGrid);
}
