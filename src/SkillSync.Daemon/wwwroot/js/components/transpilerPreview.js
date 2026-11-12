/**
 * Provider Transpiler Output Preview Screen Component
 */
import { store, humanizeSlug, showToast } from "../state.js?v=20261112-v7";

export function initTranspilerPreview({ onClosePreview }) {
  const el = {
    btnClosePreview: document.getElementById("btn-close-preview"),
    previewHeading: document.getElementById("preview-heading"),
    previewSlugBadge: document.getElementById("preview-slug-badge"),
    previewPathLabel: document.getElementById("preview-path-label"),
    previewCodeBox: document.getElementById("preview-code-box"),
    btnCopyPreview: document.getElementById("btn-copy-preview"),
    tabs: document.querySelectorAll(".preview-tab")
  };

  function renderPreview() {
    const { activePreviewTab, previews, currentSkillModel } = store.getState();
    const slug = (currentSkillModel?.name || "skill").trim().toLowerCase();
    const title = currentSkillModel?.title || humanizeSlug(slug);

    el.previewHeading.textContent = `Provider Output — ${title}`;
    el.previewSlugBadge.textContent = slug;

    const pathMap = {
      gemini: `~/.gemini/skills/${slug}/SKILL.md`,
      codex: `~/.codex/skills/${slug}/SKILL.md`,
      claude: `~/.claude/skills/${slug}/SKILL.md`,
      cursor: `~/.cursor/rules/${slug}.mdc`,
      canonical: `~/.skillhub/skills/${slug}/SKILL.md`
    };

    el.previewPathLabel.textContent = pathMap[activePreviewTab] || "";
    el.previewCodeBox.textContent = (previews && previews[activePreviewTab]) || "";
  }

  el.btnClosePreview.addEventListener("click", onClosePreview);

  el.tabs.forEach((btn) => {
    btn.addEventListener("click", () => {
      el.tabs.forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      const target = btn.getAttribute("data-target");
      store.update({ activePreviewTab: target }, "previews:updated");
    });
  });

  el.btnCopyPreview.addEventListener("click", async () => {
    const { activePreviewTab, previews } = store.getState();
    const text = (previews && previews[activePreviewTab]) || "";
    await navigator.clipboard.writeText(text);
    showToast("Copied transpiled file content to clipboard.");
  });

  store.subscribe("previews:updated", renderPreview);
  store.subscribe("skill:selected", renderPreview);
}
