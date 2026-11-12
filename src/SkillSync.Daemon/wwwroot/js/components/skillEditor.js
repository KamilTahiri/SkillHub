/**
 * Dedicated Cohesive Vertical Skill Editor (Write / Live Formatted Markdown Preview)
 */
import { api } from "../api.js?v=20261112-v6";
import { store, slugifyTitle, humanizeSlug, showToast } from "../state.js?v=20261112-v6";
import { renderMarkdown, escapeHtml } from "../markdown.js?v=20261112-v6";

export function initSkillEditor({ onCancelEdit, onPreviewFromEdit, onSkillSaved }) {
  const el = {
    btnCancelEdit: document.getElementById("btn-cancel-edit"),
    editorHeading: document.getElementById("editor-heading"),
    slugPreviewBadge: document.getElementById("slug-preview-badge"),
    btnPreviewFromEdit: document.getElementById("btn-preview-from-edit"),
    btnSaveSkill: document.getElementById("btn-save-skill"),
    fieldTitle: document.getElementById("field-title"),
    fieldDescription: document.getElementById("field-description"),
    fieldWhenToUse: document.getElementById("field-when-to-use"),
    activeTagsList: document.getElementById("active-tags-list"),
    tagInputField: document.getElementById("tag-input-field"),
    editorAvailableTags: document.getElementById("editor-available-tags"),
    btnMdWrite: document.getElementById("btn-md-write"),
    btnMdPreview: document.getElementById("btn-md-preview"),
    fieldInstructions: document.getElementById("field-instructions"),
    editorMdLivePreview: document.getElementById("editor-md-live-preview"),
    toggleClaude: document.getElementById("toggle-claude"),
    toggleGemini: document.getElementById("toggle-gemini"),
    toggleCodex: document.getElementById("toggle-codex"),
    toggleCursor: document.getElementById("toggle-cursor"),
    fieldAllowedTools: document.getElementById("field-allowed-tools"),
    fieldSummaryShort: document.getElementById("field-summary-short"),
    codexCharCounter: document.getElementById("codex-char-counter"),
    fieldCursorGlobs: document.getElementById("field-cursor-globs"),
    fieldCursorAlways: document.getElementById("field-cursor-always"),
    btnAddSupportingFile: document.getElementById("btn-add-supporting-file"),
    supportingFilesContainer: document.getElementById("supporting-files-container")
  };

  let currentTags = [];
  let previewTimer = null;

  function autoResizeTextarea(textarea) {
    if (!textarea) return;
    textarea.style.height = "auto";
    textarea.style.height = `${Math.max(68, textarea.scrollHeight)}px`;
  }

  function autoResizeAllTextareas() {
    autoResizeTextarea(el.fieldDescription);
    autoResizeTextarea(el.fieldWhenToUse);
  }

  function setMarkdownTab(mode) {
    if (mode === "preview") {
      el.btnMdWrite.classList.remove("active");
      el.btnMdPreview.classList.add("active");
      el.fieldInstructions.classList.add("hidden");
      el.editorMdLivePreview.classList.remove("hidden");
      el.editorMdLivePreview.innerHTML = renderMarkdown(el.fieldInstructions.value || "");
    } else {
      el.btnMdPreview.classList.remove("active");
      el.btnMdWrite.classList.add("active");
      el.editorMdLivePreview.classList.add("hidden");
      el.fieldInstructions.classList.remove("hidden");
    }
  }

  function renderAvailableTagsHelper() {
    if (!el.editorAvailableTags) return;
    const allKnown = store.getAllKnownUserTags();
    const unassigned = allKnown.filter((t) => !currentTags.includes(t));
    el.editorAvailableTags.innerHTML = "";
    if (unassigned.length === 0) return;

    const label = document.createElement("span");
    label.className = "field-hint";
    label.textContent = "Click to add existing tag:";
    el.editorAvailableTags.appendChild(label);

    unassigned.forEach((tag) => {
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "tag-chip";
      chip.innerHTML = `+ #${escapeHtml(tag)}`;
      chip.addEventListener("click", () => {
        if (!currentTags.includes(tag)) {
          currentTags.push(tag);
          renderTagsPills();
          schedulePreviewUpdate();
        }
      });
      el.editorAvailableTags.appendChild(chip);
    });
  }

  function renderTagsPills() {
    el.activeTagsList.innerHTML = "";
    currentTags.forEach((tag) => {
      const pill = document.createElement("span");
      pill.className = "tag-pill";
      pill.textContent = tag;

      const removeBtn = document.createElement("button");
      removeBtn.type = "button";
      removeBtn.textContent = "×";
      removeBtn.title = `Remove tag '${tag}'`;
      removeBtn.addEventListener("click", () => {
        currentTags = currentTags.filter((t) => t !== tag);
        renderTagsPills();
        schedulePreviewUpdate();
      });

      pill.appendChild(removeBtn);
      el.activeTagsList.appendChild(pill);
    });
    renderAvailableTagsHelper();
  }

  function addTagFromInput(rawVal) {
    const cleaned = String(rawVal || "")
      .split(",")
      .map((s) => s.trim().toLowerCase().replace(/^#+/, ""))
      .filter(Boolean);

    let changed = false;
    cleaned.forEach((t) => {
      store.addCustomTag(t);
      if (!currentTags.includes(t)) {
        currentTags.push(t);
        changed = true;
      }
    });

    if (changed) {
      renderTagsPills();
      schedulePreviewUpdate();
    }
  }

  function computeEffectiveSlug() {
    const { isCreatingNew, selectedSkillName } = store.getState();
    const titleVal = el.fieldTitle.value.trim();
    if (!isCreatingNew && selectedSkillName) {
      return selectedSkillName;
    }
    return slugifyTitle(titleVal) || "new-skill";
  }

  function updateHeaderAndSlugBadge() {
    const { isCreatingNew } = store.getState();
    const titleVal = el.fieldTitle.value.trim();
    const slug = computeEffectiveSlug();

    el.editorHeading.textContent = titleVal || (isCreatingNew ? "New Skill" : "Edit Skill");
    el.slugPreviewBadge.textContent = slug;
  }

  function updateCodexCounter() {
    const len = (el.fieldSummaryShort.value || "").trim().length;
    el.codexCharCounter.textContent = len > 0 ? `${len} chars` : "Auto-generated if blank";
  }

  function addSupportingFileRow(relPath = "scripts/helper.sh", content = "#!/usr/bin/env bash\n") {
    const wrapper = document.createElement("div");
    wrapper.className = "supporting-file-item";

    const top = document.createElement("div");
    top.className = "supporting-file-top";

    const pathInput = document.createElement("input");
    pathInput.type = "text";
    pathInput.className = "supporting-path-input";
    pathInput.value = relPath;
    pathInput.placeholder = "scripts/my-script.py or references/guide.md";

    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "btn btn-sm btn-ghost-danger";
    removeBtn.textContent = "Remove";
    removeBtn.addEventListener("click", () => {
      wrapper.remove();
      schedulePreviewUpdate();
    });

    top.appendChild(pathInput);
    top.appendChild(removeBtn);

    const contentArea = document.createElement("textarea");
    contentArea.className = "supporting-content-input";
    contentArea.rows = 4;
    contentArea.value = content;

    pathInput.addEventListener("input", () => schedulePreviewUpdate());
    contentArea.addEventListener("input", () => schedulePreviewUpdate());

    wrapper.appendChild(top);
    wrapper.appendChild(contentArea);
    el.supportingFilesContainer.appendChild(wrapper);
  }

  function populateForm() {
    const { currentSkillModel: skill, isCreatingNew } = store.getState();
    if (!skill) return;

    setMarkdownTab("write");

    const displayTitle = isCreatingNew ? skill.title || "" : skill.title || humanizeSlug(skill.name);
    el.fieldTitle.value = displayTitle;
    el.fieldDescription.value = skill.description || "";
    el.fieldWhenToUse.value = skill.when_to_use || "";

    currentTags = [...(skill.tags || [])];
    el.tagInputField.value = "";
    renderTagsPills();

    el.fieldInstructions.value = skill.instructions_markdown || "";

    el.toggleClaude.checked = skill.targets?.claude?.enabled !== false;
    el.toggleGemini.checked = skill.targets?.gemini?.enabled !== false;
    el.toggleCodex.checked = skill.targets?.codex?.enabled !== false;
    el.toggleCursor.checked = skill.targets?.cursor?.enabled !== false;

    el.fieldAllowedTools.value = (skill.targets?.claude?.allowed_tools || []).join(", ");
    el.fieldSummaryShort.value = skill.summary_short || "";
    updateCodexCounter();

    el.fieldCursorGlobs.value = (skill.targets?.cursor?.globs || ["**/*"]).join(", ");
    el.fieldCursorAlways.checked = Boolean(skill.targets?.cursor?.always_apply);

    el.supportingFilesContainer.innerHTML = "";
    const files = skill.supporting_files || {};
    Object.keys(files)
      .sort()
      .forEach((relPath) => {
        addSupportingFileRow(relPath, files[relPath]);
      });

    updateHeaderAndSlugBadge();
    requestAnimationFrame(autoResizeAllTextareas);
  }

  function buildPayloadFromForm() {
    if (el.tagInputField.value.trim()) {
      addTagFromInput(el.tagInputField.value);
      el.tagInputField.value = "";
    }

    const parseList = (val) =>
      (val || "")
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);

    const supportingFiles = {};
    el.supportingFilesContainer.querySelectorAll(".supporting-file-item").forEach((item) => {
      const p = item.querySelector(".supporting-path-input").value.trim().replace(/\\/g, "/");
      const c = item.querySelector(".supporting-content-input").value;
      if (p) supportingFiles[p] = c;
    });

    const globs = parseList(el.fieldCursorGlobs.value);
    const title = el.fieldTitle.value.trim();
    const slug = computeEffectiveSlug();

    return {
      name: slug,
      title: title || humanizeSlug(slug),
      version: store.getState().currentSkillModel?.version || "1.0.0",
      source_category: "user",
      origin_provider: "My Skill",
      description: el.fieldDescription.value.trim(),
      when_to_use: el.fieldWhenToUse.value.trim(),
      summary_short: el.fieldSummaryShort.value.trim(),
      tags: [...currentTags],
      targets: {
        claude: {
          enabled: el.toggleClaude.checked,
          allowed_tools: parseList(el.fieldAllowedTools.value)
        },
        gemini: {
          enabled: el.toggleGemini.checked
        },
        codex: {
          enabled: el.toggleCodex.checked
        },
        cursor: {
          enabled: el.toggleCursor.checked,
          globs: globs.length > 0 ? globs : ["**/*"],
          always_apply: el.fieldCursorAlways.checked
        }
      },
      instructions_markdown: el.fieldInstructions.value,
      supporting_files: supportingFiles
    };
  }

  function schedulePreviewUpdate(immediate = false) {
    updateHeaderAndSlugBadge();
    updateCodexCounter();
    if (previewTimer) clearTimeout(previewTimer);

    const run = async () => {
      try {
        const draft = buildPayloadFromForm();
        const res = await api.previewTranspilation(draft);
        store.update(
          {
            previews: res.transpiled_previews || {}
          },
          "previews:updated"
        );
      } catch (_) {}
    };

    if (immediate) {
      run();
    } else {
      previewTimer = setTimeout(run, 180);
    }
  }

  // Event Listeners
  el.btnCancelEdit.addEventListener("click", onCancelEdit);
  el.btnPreviewFromEdit.addEventListener("click", () => {
    schedulePreviewUpdate(true);
    onPreviewFromEdit();
  });

  el.btnMdWrite.addEventListener("click", () => setMarkdownTab("write"));
  el.btnMdPreview.addEventListener("click", () => setMarkdownTab("preview"));

  el.fieldDescription.addEventListener("input", () => autoResizeTextarea(el.fieldDescription));
  el.fieldWhenToUse.addEventListener("input", () => autoResizeTextarea(el.fieldWhenToUse));
  window.addEventListener("resize", autoResizeAllTextareas);

  el.tagInputField.addEventListener("keydown", (e) => {
    if (e.key === "Enter" || e.key === ",") {
      e.preventDefault();
      addTagFromInput(el.tagInputField.value);
      el.tagInputField.value = "";
    } else if (e.key === "Backspace" && !el.tagInputField.value && currentTags.length > 0) {
      currentTags.pop();
      renderTagsPills();
      schedulePreviewUpdate();
    }
  });

  el.tagInputField.addEventListener("blur", () => {
    if (el.tagInputField.value.trim()) {
      addTagFromInput(el.tagInputField.value);
      el.tagInputField.value = "";
    }
  });

  [
    el.fieldTitle,
    el.fieldDescription,
    el.fieldWhenToUse,
    el.fieldInstructions,
    el.toggleClaude,
    el.toggleGemini,
    el.toggleCodex,
    el.toggleCursor,
    el.fieldAllowedTools,
    el.fieldSummaryShort,
    el.fieldCursorGlobs,
    el.fieldCursorAlways
  ].forEach((input) => input.addEventListener("input", () => schedulePreviewUpdate()));

  el.btnAddSupportingFile.addEventListener("click", () => {
    addSupportingFileRow();
    schedulePreviewUpdate();
  });

  async function saveSkill() {
    try {
      const payload = buildPayloadFromForm();
      if (!payload.title || !payload.name) {
        showToast("Please enter a Skill Name.");
        el.fieldTitle.focus();
        return;
      }
      if (!payload.description) {
        showToast("Please describe what this skill does.");
        el.fieldDescription.focus();
        return;
      }
      const res = await api.upsertSkill(payload);
      showToast(`Saved '${res.skill.title || res.skill.name}' and synced across providers.`);
      await onSkillSaved(res);
    } catch (err) {
      showToast(`Save failed: ${err.message}`);
    }
  }

  el.btnSaveSkill.addEventListener("click", saveSkill);

  window.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && store.getState().activeScreen === "edit") {
      e.preventDefault();
      saveSkill();
    }
  });

  return {
    populateForm,
    schedulePreviewUpdate
  };
}
