/**
 * SkillHub SPA Entrypoint
 * Manages multi-screen navigation:
 *   - "library": Full-Window Skill Library & Search View (Default)
 *   - "detail":  Centered Single-Column Document Skill Reading View
 *   - "edit":    Dedicated Cohesive Vertical Edit / Create View
 *   - "preview": Provider Transpiler Output Preview
 */
import { api } from "./api.js?v=20261112-v6";
import { store, showToast } from "./state.js?v=20261112-v6";
import { initStatusBar } from "./components/statusBar.js?v=20261112-v6";
import { initSkillList } from "./components/skillList.js?v=20261112-v6";
import { initSkillReader } from "./components/skillReader.js?v=20261112-v6";
import { initSkillEditor } from "./components/skillEditor.js?v=20261112-v6";
import { initTranspilerPreview } from "./components/transpilerPreview.js?v=20261112-v6";

const screens = {
  library: document.getElementById("screen-library"),
  detail: document.getElementById("screen-detail"),
  edit: document.getElementById("screen-edit"),
  preview: document.getElementById("screen-preview")
};

function navigateToScreen(targetScreen) {
  const current = store.getState().activeScreen;
  const previous = current !== targetScreen ? current : store.getState().previousScreen;

  Object.entries(screens).forEach(([key, el]) => {
    if (!el) return;
    if (key === targetScreen) {
      el.classList.remove("hidden");
    } else {
      el.classList.add("hidden");
    }
  });

  store.update({ activeScreen: targetScreen, previousScreen: previous }, "screen:changed");
}

async function bootstrap() {
  let editorApi = null;

  const statusBar = initStatusBar({
    onRefreshAll: refreshSkillsAndProviders,
    onNewSkill: () => startNewSkillDraft(editorApi),
    onNavigateLibrary: () => navigateToScreen("library"),
    onSelectImportedSkill: async (skillName) => {
      await refreshSkillsAndProviders();
      if (skillName) {
        await selectSkillByName(skillName);
        navigateToScreen("detail");
      }
    }
  });

  initSkillList({
    onSelectSkill: async (name) => {
      await selectSkillByName(name);
      navigateToScreen("detail");
    },
    onRefreshAll: refreshSkillsAndProviders,
    onQuickSyncSkill: async (skill) => {
      try {
        const fullData = await api.getSkill(skill.name);
        const model = fullData.skill || skill;
        const payload = {
          ...model,
          source_category: model.source_category || "template",
          origin_provider: model.origin_provider || "Built-in Template",
          is_catalog_only: false
        };
        const res = await api.upsertSkill(payload);
        await refreshSkillsAndProviders();
        showToast(
          `Synced '${res.skill.title || res.skill.name}' across your installed AI providers (view in Active & Synced)!`
        );
      } catch (err) {
        showToast(`Failed to sync skill: ${err.message}`);
      }
    }
  });

  initSkillReader({
    onBackToLibrary: () => navigateToScreen("library"),
    onEditSkill: () => {
      if (editorApi) editorApi.populateForm();
      navigateToScreen("edit");
    },
    onPreviewSkill: () => navigateToScreen("preview"),
    onSkillPromoted: async (res) => {
      await refreshSkillsAndProviders();
      store.update(
        {
          selectedSkillName: res.skill.name,
          currentSkillModel: res.skill,
          isCreatingNew: false,
          previews: res.transpiled_previews || {}
        },
        "skill:selected"
      );
    },
    onSkillDeleted: async () => {
      store.update({ selectedSkillName: null, currentSkillModel: null }, "skill:selected");
      await refreshSkillsAndProviders();
      navigateToScreen("library");
    }
  });

  editorApi = initSkillEditor({
    onCancelEdit: () => {
      const { isCreatingNew, selectedSkillName } = store.getState();
      if (isCreatingNew || !selectedSkillName) {
        navigateToScreen("library");
      } else {
        navigateToScreen("detail");
      }
    },
    onPreviewFromEdit: () => navigateToScreen("preview"),
    onSkillSaved: async (res) => {
      await refreshSkillsAndProviders();
      store.update(
        {
          selectedSkillName: res.skill.name,
          currentSkillModel: res.skill,
          isCreatingNew: false,
          previews: res.transpiled_previews || {}
        },
        "skill:selected"
      );
      navigateToScreen("detail");
    }
  });

  initTranspilerPreview({
    onClosePreview: () => {
      const { previousScreen, selectedSkillName } = store.getState();
      if (previousScreen === "edit") {
        navigateToScreen("edit");
      } else if (selectedSkillName) {
        navigateToScreen("detail");
      } else {
        navigateToScreen("library");
      }
    }
  });

  try {
    const [status, providers, skills] = await Promise.all([
      api.getStatus(),
      api.getProviders(),
      api.listSkills()
    ]);

    store.update({ status }, "status:updated");
    store.update({ providers }, "providers:updated");
    store.update({ skills }, "skills:updated");

    statusBar.setRecentActivities(status.recent_activity || []);
    navigateToScreen("library");
  } catch (err) {
    showToast(`Failed to load initial state: ${err.message}`);
  }

  api.connectEventStream({
    onConnected: () => statusBar.setSseConnected(true),
    onActivity: async (act) => {
      statusBar.appendActivity(act);
      await refreshSkillsAndProviders();
    },
    onError: () => statusBar.setSseConnected(false)
  });
}

async function refreshSkillsAndProviders() {
  const [providers, skills, status] = await Promise.all([
    api.getProviders(),
    api.listSkills(),
    api.getStatus()
  ]);
  store.update({ status }, "status:updated");
  store.update({ providers }, "providers:updated");
  store.update({ skills }, "skills:updated");
}

async function selectSkillByName(name) {
  try {
    const data = await api.getSkill(name);
    store.update(
      {
        selectedSkillName: data.skill.name,
        currentSkillModel: data.skill,
        isCreatingNew: false,
        previews: data.transpiled_previews || {}
      },
      "skill:selected"
    );
  } catch (err) {
    showToast(`Error loading skill '${name}': ${err.message}`);
  }
}

function startNewSkillDraft(editorApi) {
  const draft = {
    name: "new-skill",
    title: "",
    version: "1.0.0",
    source_category: "user",
    origin_provider: "My Skill",
    description: "",
    when_to_use: "",
    summary_short: "",
    tags: [],
    targets: {
      claude: { enabled: true, allowed_tools: [] },
      gemini: { enabled: true },
      codex: { enabled: true },
      cursor: { enabled: true, globs: ["**/*"], always_apply: false }
    },
    instructions_markdown: "# Goal\n\nDescribe step-by-step how the AI should execute this skill...\n",
    supporting_files: {}
  };

  store.update(
    {
      selectedSkillName: null,
      currentSkillModel: draft,
      isCreatingNew: true
    },
    "skill:selected"
  );

  if (editorApi) {
    editorApi.populateForm();
    editorApi.schedulePreviewUpdate(true);
  }
  navigateToScreen("edit");
}

bootstrap();
