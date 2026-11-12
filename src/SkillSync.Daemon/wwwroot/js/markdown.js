/**
 * Zero-Dependency Markdown Renderer & Search Highlighting Engine for SkillHub
 */

export function escapeHtml(str) {
  return String(str ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function escapeRegExp(str) {
  return String(str).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function buildSearchRegex(query) {
  const q = String(query ?? "").trim();
  if (!q) return null;

  // Match full phrase first, then individual tokens >= 2 chars
  const tokens = q
    .split(/\s+/)
    .map((t) => t.trim())
    .filter((t) => t.length >= 2);

  const patterns = [escapeRegExp(q)];
  tokens.forEach((t) => {
    const esc = escapeRegExp(t);
    if (!patterns.includes(esc)) {
      patterns.push(esc);
    }
  });

  return new RegExp(`(${patterns.join("|")})`, "gi");
}

/**
 * Escapes HTML and wraps any matches of `query` in <mark class="search-mark">
 */
export function highlightText(text, query) {
  const raw = String(text ?? "");
  if (!raw) return "";
  const regex = buildSearchRegex(query);
  if (!regex) return escapeHtml(raw);

  const parts = raw.split(regex);
  return parts
    .map((part, idx) => {
      // Capturing group splits place matched substrings at odd indices
      if (idx % 2 === 1) {
        return `<mark class="search-mark">${escapeHtml(part)}</mark>`;
      }
      return escapeHtml(part);
    })
    .join("");
}

/**
 * Extracts a contextual snippet around the first match inside Markdown instructions
 */
export function extractInstructionMatchSnippet(markdown, query, maxLength = 190) {
  const raw = String(markdown ?? "").trim();
  const q = String(query ?? "").trim();
  if (!raw || !q) return null;

  const lowerRaw = raw.toLowerCase();
  const lowerQ = q.toLowerCase();

  let matchIndex = lowerRaw.indexOf(lowerQ);
  if (matchIndex < 0) {
    const tokens = lowerQ.split(/\s+/).filter((t) => t.length >= 2);
    for (const token of tokens) {
      matchIndex = lowerRaw.indexOf(token);
      if (matchIndex >= 0) break;
    }
  }

  if (matchIndex < 0) return null;

  // Clean markdown syntax gently around the match window
  const start = Math.max(0, matchIndex - 55);
  const end = Math.min(raw.length, matchIndex + maxLength - 55);
  let slice = raw
    .slice(start, end)
    .replace(/```[\s\S]*?```/g, " ")
    .replace(/^#+\s+/gm, "")
    .replace(/[*_`>-]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();

  if (start > 0) slice = "…" + slice;
  if (end < raw.length) slice = slice + "…";

  return highlightText(slice, q);
}

/**
 * Renders inline Markdown formatting (inline code, bold, italic, links) + optional search highlight
 */
function renderInline(text, query = "") {
  if (!text) return "";

  // Split by inline code spans first so code spans are not mangled
  const segments = String(text).split(/(`[^`]+`)/g);
  return segments
    .map((seg) => {
      if (seg.startsWith("`") && seg.endsWith("`") && seg.length >= 2) {
        const inner = seg.slice(1, -1);
        return `<code class="md-inline-code">${highlightText(inner, query)}</code>`;
      }

      // Process links, bold, italic on non-code text
      let html = highlightText(seg, query);

      // Markdown links: [text](url)
      html = html.replace(
        /\[([^\]]+)\]\(([^)]+)\)/g,
        (_, label, href) => `<span class="md-link" title="${href}">${label}</span>`
      );

      // Bold + Italic: ***text***
      html = html.replace(/\*\*\*([^*]+)\*\*\*/g, "<strong><em>$1</em></strong>");
      // Bold: **text**
      html = html.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
      // Italic: *text*
      html = html.replace(/(^|[\s(])\*([^*\n]+)\*(?=[\s.,;:!?)]|$)/g, "$1<em>$2</em>");

      return html;
    })
    .join("");
}

/**
 * Full Markdown-to-HTML document renderer for SkillHub Reading View
 */
export function renderMarkdown(markdown, query = "", options = { stripLeadingH1: true }) {
  const normalized = String(markdown ?? "").replace(/\r\n/g, "\n").trim();
  if (!normalized) {
    return `<p class="md-empty">No instructions provided for this skill.</p>`;
  }

  const lines = normalized.split("\n");
  const out = [];
  let i = 0;

  // Strip leading # H1 title so it never duplicates the page's main document heading
  if (options?.stripLeadingH1 !== false) {
    while (i < lines.length && !lines[i].trim()) {
      i++;
    }
    if (i < lines.length && /^#\s+.+$/.test(lines[i].trim())) {
      i++;
    }
  }

  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();

    // 1. Empty line
    if (!trimmed) {
      i++;
      continue;
    }

    // 2. Fenced Code Block (```lang ... ```)
    if (trimmed.startsWith("```")) {
      const lang = trimmed.slice(3).trim() || "text";
      const codeLines = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith("```")) {
        codeLines.push(lines[i]);
        i++;
      }
      if (i < lines.length) i++; // skip closing ```
      const rawCode = codeLines.join("\n");
      const highlightedCode = highlightText(rawCode, query);
      out.push(
        `<div class="md-code-block">` +
          `<div class="md-code-header">` +
            `<span class="md-code-lang">${escapeHtml(lang)}</span>` +
            `<button type="button" class="md-code-copy" data-copy-code="${escapeHtml(rawCode)}">Copy</button>` +
          `</div>` +
          `<pre><code>${highlightedCode}</code></pre>` +
        `</div>`
      );
      continue;
    }

    // 3. Horizontal Rule
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      out.push(`<hr class="md-hr" />`);
      i++;
      continue;
    }

    // 4. Headings (# to ####)
    const headingMatch = trimmed.match(/^(#{1,4})\s+(.+)$/);
    if (headingMatch) {
      const level = headingMatch[1].length;
      const content = renderInline(headingMatch[2], query);
      out.push(`<h${level} class="md-h${level}">${content}</h${level}>`);
      i++;
      continue;
    }

    // 5. Markdown Table (| ... |)
    if (
      trimmed.startsWith("|") &&
      trimmed.endsWith("|") &&
      i + 1 < lines.length &&
      /^\s*\|[\s:-]+\|\s*$/.test(lines[i + 1].replace(/[^|\s:-]/g, ""))
    ) {
      const parseCells = (rowStr) =>
        rowStr
          .trim()
          .replace(/^\||\|$/g, "")
          .split("|")
          .map((c) => c.trim());

      const headers = parseCells(trimmed);
      i += 2; // skip header and separator row
      const rows = [];
      while (i < lines.length && lines[i].trim().startsWith("|")) {
        rows.push(parseCells(lines[i]));
        i++;
      }

      const thead =
        `<thead><tr>` +
        headers.map((h) => `<th>${renderInline(h, query)}</th>`).join("") +
        `</tr></thead>`;
      const tbody =
        `<tbody>` +
        rows
          .map(
            (r) =>
              `<tr>` + r.map((c) => `<td>${renderInline(c, query)}</td>`).join("") + `</tr>`
          )
          .join("") +
        `</tbody>`;
      out.push(`<div class="md-table-wrap"><table class="md-table">${thead}${tbody}</table></div>`);
      continue;
    }

    // 6. Blockquote (> ...)
    if (trimmed.startsWith(">")) {
      const quoteLines = [];
      while (i < lines.length && lines[i].trim().startsWith(">")) {
        quoteLines.push(lines[i].trim().replace(/^>\s?/, ""));
        i++;
      }
      out.push(`<blockquote class="md-blockquote">${renderInline(quoteLines.join(" "), query)}</blockquote>`);
      continue;
    }

    // 7. Unordered List (- or * )
    if (/^[-*]\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
        const itemText = lines[i].replace(/^\s*[-*]\s+/, "").trim();
        items.push(`<li>${renderInline(itemText, query)}</li>`);
        i++;
      }
      out.push(`<ul class="md-list">${items.join("")}</ul>`);
      continue;
    }

    // 8. Ordered List (1. )
    if (/^\d+\.\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) {
        const itemText = lines[i].replace(/^\s*\d+\.\s+/, "").trim();
        items.push(`<li>${renderInline(itemText, query)}</li>`);
        i++;
      }
      out.push(`<ol class="md-list md-list-ordered">${items.join("")}</ol>`);
      continue;
    }

    // 9. Standard Paragraph (merge consecutive plain lines)
    const paraLines = [];
    while (
      i < lines.length &&
      lines[i].trim() !== "" &&
      !lines[i].trim().startsWith("```") &&
      !lines[i].trim().startsWith("#") &&
      !lines[i].trim().startsWith(">") &&
      !/^[-*]\s+/.test(lines[i].trim()) &&
      !/^\d+\.\s+/.test(lines[i].trim()) &&
      !/^(-{3,}|\*{3,}|_{3,})$/.test(lines[i].trim()) &&
      !(lines[i].trim().startsWith("|") && lines[i].trim().endsWith("|"))
    ) {
      paraLines.push(lines[i].trim());
      i++;
    }

    if (paraLines.length > 0) {
      out.push(`<p class="md-p">${renderInline(paraLines.join(" "), query)}</p>`);
    } else {
      i++;
    }
  }

  return out.join("\n");
}

