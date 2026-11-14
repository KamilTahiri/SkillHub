# SkillHub — Next-Level Roadmap

**14 November 2026 · v1.2.1 · HEAD `3e84218` · Source review (no code changed)**

> Scope: every file under `src/SkillSync.Daemon/` (C# + `wwwroot/`), `src/SkillHub.Core/`, `tests/`, plus `AUDIT_AND_ROADMAP.md` and `UI_UX_AUDIT.md` for history.
> Evidence: **Source** means traced in code. **Test** means run. `dotnet test SkillSync.sln` passed **57/57** (5 Core and 52 Daemon). I did **not** launch the installed app or take screenshots in this pass. Visual claims come from reading the CSS and DOM and are labelled where that matters.

---

## Status (updated 14 November 2026)

| Phase | State | Notes |
|---|---|---|
| A · Quick wins | Done (`fdaf1f9`) | Live status dot, Connections cards, inline deploy chips, importer input fixes, ETag caching |
| B · Repository Indexer & Explore v2 | Done | `RepositoryIndexer` (codeload tarball, SHA-keyed cache in `~/.skillhub/cache/github`, ETag refs, Trees API fallback), slash branches, all Markdown formats classified, root-level sibling files in bundles, repo card, kind tabs, bundle file tabs, pinned sources, multi-select import, non-blocking search |
| C · Design System v2 | Done | Surface and elevation tokens, `polish.css`, button press/hover/loading states, Library glyph rows with hover actions, sort and density, Ctrl+K command palette, toast stack, Markdown task lists/strikethrough/code highlighting/outline, external links via the desktop host, Mica title bar |
| D · Flagship features | Not started | |
| E · Launch polish | Not started | |

Live-verified indexing: `anthropics/skills` keeps `FORMS.md`, `REFERENCE.md` and `LICENSE.txt` with the pdf skill. `github/awesome-copilot` (113 MB archive) indexes 481 skills, 197 instructions and 231 agents.

## 0. Verdict

The engine is now in good shape. The two previous audits found data-loss, containment, and config-corruption bugs, and these are fixed and covered by tests (`SyncSafetyTests`, `LocalContractTests`, `LoopPreventionTests`). The loopback trust boundary is good: Host, Origin and Sec-Fetch checks, a per-session desktop token, and a strict CSP (`Api/LocalHttpAccess.cs`). Ownership tracking and conflict review are careful, more careful than most commercial sync tools.

The **product surface has not caught up with the engine**. Three things hold SkillHub back from feeling like a flagship app:

1. **Explore only sees part of each repository.** Discovery only recognizes `SKILL.md` and `*.mdc`. It **silently drops sibling Markdown files that SKILL.md links to**, so some imported skills are broken (details in §1.1). List rows have no descriptions until a per-item preview is fetched. This is the main cause of the "it only shows the repo" feeling.
2. **The visual language is a wireframe with a palette.** There is one shadow token, borders on everything, text-only buttons, no icon system beyond 9 hand-drawn glyphs, no motion vocabulary, and a single replace-on-write toast. It reads as "careful developer tool", not as Linear or Raycast.
3. **Ordering and emphasis follow data order, not user intent.** Connections shows Claude first because `InstallManager.DiscoverMcpClientConfigTargets()` hard-codes Claude Desktop as candidate #1. Featured Explore content is a hard-coded list of 9 items. The status pill uses the same `--ok` token as body text.

None of this needs a rewrite. It needs a **discovery indexer**, a **design-system v2 pass**, and **4–5 features that use data SkillHub already has**. The most obvious example: import provenance (`import_source`, `import_revision`) is already stored, but nothing uses it to offer upstream updates.

---

## 1. Your four issues: root cause and fix

### 1.1 Explore shows repo descriptions and READMEs instead of every skill file

**Root causes (Source):**

| # | Cause | Where |
|---|---|---|
| a | `IsSkillDocument` accepts only `SKILL.md` or `*.mdc`. It ignores `*.instructions.md`, `*.prompt.md`, `*.chatmode.md` and `*.agent.md` (most of `github/awesome-copilot`), Claude `commands/*.md` and `agents/*.md`, Cursor `rules/*.md`, and `AGENTS.md`, `CLAUDE.md` and `GEMINI.md`. | `Core/Engine/SkillImporterService.Plans.cs:216` |
| b | **Bundle truncation bug.** `SupportingPaths` keeps only files under the six whitelisted folders (`scripts, resources, references, examples, assets, agents`). It requires a `/` in the relative path, so **root-level sibling files like `reference.md`, `forms.md` and `LICENSE.txt` next to `SKILL.md` are dropped without a warning.** Several Anthropic skills use exactly this layout, with SKILL.md linking to sibling .md files. The result is an import whose instructions point at files that do not exist. | `SkillImporterService.Previews.cs:58-65`, `SkillPaths.cs:12,21-31` |
| c | GitHub list rows are built with `Description: string.Empty` and a title made from the folder name. Metadata appears only after a preview download. The frontend prefetches only the **first 12** rows, so most rows show a slug-title, a blank line and the repo name. | `SkillImporterService.cs:225-233`, `explore.js:101,166` |
| d | Files without frontmatter are rejected outright ("incomplete frontmatter"), so plain-Markdown rule files can never be previewed. | `SkillImporterService.cs:493-496` |
| e | The Source chooser only offers curated repos. Typing `owner/repo` is honoured **only while Source = Featured**. With any other source, the text is treated as a filter on that repo and returns nothing. | `Api/SkillRestEndpoints.cs:367-372`, `SkillImporterService.cs:281-286` |
| f | `npx skills add owner/repo --skill foo` parses the `--skill` filter, then throws it away during browse (`out _`). Branch names containing `/` (`feature/x`) are mis-parsed because only `parts[3]` becomes the ref. | `SkillImporterService.cs:219,581-589` |
| g | "Featured" is a hard-coded list pinned to `HEAD`. One slug (`claude-3-7-sonnet-composition-patterns`) does not match its path (`composition-patterns`). | `SkillImporterService.cs:358-371` |
| h | No README or repo context is shown at all. Users see a repo name and blank rows, and fairly conclude that SkillHub "only sees the repo". | `explore.js` |

**Design: the Repository Indexer** (detail in §2). In short:
- Build one **immutable index per commit SHA**, holding every Markdown file classified by *kind*, plus parsed name and description.
- Treat **the whole skill directory** as the bundle, siblings included.
- Show skills first and companion docs (README, AGENTS.md) as context.
- Allow multi-select import.

### 1.2 Truncated "Deploy to…" control

**Root cause (Source, not visually verified):** three things combine.
1. The label is literally `'Deploy to…'` (`explore.js:261`). The trailing ellipsis character makes it *look* cut off, even when it isn't.
2. The control is a borderless `quiet small` button stacked under the primary in a right-aligned column (`components.css:171-172`). It reads like dangling, clipped text rather than a control.
3. It hides its own state. The chosen targets live only inside a modal, so the header can never show "Deploy to Codex, Gemini".

**Fix:** remove the modal.
- Put a **sticky review footer** in the preview with two parts:
  - an inline provider **toggle-chip group**: brand icon, label and check state for each detected provider, with "Library only" when none are selected;
  - a "Save as" slug field with live collision validation.
- Keep the primary **Add to My Skills** at the right of the footer.
- At < 768 px the chips wrap to a second row. Never use `white-space:nowrap` on a label that can grow.
- Label it **"Also deploy to"** so it is clear that import is local-first and deployment is optional.

### 1.3 Dull green status dot

**Root cause:** `.provider-overview-dot` uses `--ok` (`layout.css:19`). That is a *text* token. In light theme it is forest green `#1c684c` (`base.css:33`), chosen for 4.5:1 text contrast, which is the wrong goal for a 7 px status light.

**Fix:** add dedicated status tokens and a breathing glow that runs only when the app is healthy.

```css
:root            { --live: #10b981; --live-glow: 16 185 129; }   /* dark  */
[data-theme=light]{ --live: #059669; --live-glow: 5 150 105; }    /* ≥3:1 on white, non-text */
.provider-overview-dot { width: 8px; height: 8px; background: var(--live);
  box-shadow: 0 0 0 0 rgb(var(--live-glow) / .55); animation: live-breathe 2.6s ease-in-out infinite; }
@keyframes live-breathe {
  0%,100% { box-shadow: 0 0 0 0 rgb(var(--live-glow) / .50); }
  50%     { box-shadow: 0 0 0 5px rgb(var(--live-glow) / 0); } }
.provider-overview.has-issues .provider-overview-dot { background: var(--warn); animation: none; }
.provider-overview:is(.offline,.no-providers) .provider-overview-dot { background: var(--muted); animation: none; }
```

`layout.css:79` already disables animation under `prefers-reduced-motion`. Add a `has-issues` (amber) state as well, so the dot carries meaning beyond "a folder exists". It needs `renderProviderButton` to count `deployment().issues` (`main.js:404`).

### 1.4 Settings → Connections is upside-down and bland

**Root cause:** `renderConnections` groups clients with `Object.groupBy`, which keeps the backend's insertion order. The backend hard-codes Claude Desktop → Claude Code → Gemini CLI → Antigravity → Cursor → Codex (`InstallManager.cs:153-180`). There is no sort by availability, no icons and no counts, and the actions are plain `small` and `small danger` text buttons (`statusBar.js:40-65`).

**Redesign:**
- **Sort groups** by `registered > detected > unavailable`, then by synced-skill count.
- Show **active providers as elevated cards**, each with:
  - the brand mark, 28 px, in a tinted squircle;
  - the logical provider name with its client sub-rows (Gemini CLI and Antigravity under one card, each with its own status);
  - a status badge: `Connected` (emerald), `Restart required` (amber, with "Restart Codex to load SkillHub"), `Needs attention` (red, with an inline reason);
  - a meta line such as "**14 skills synced** · 1 needs review" that links to the filtered Library;
  - actions: an icon + label secondary button **Repair** (wrench), and **Disconnect** (unplug icon) in a `⋯` overflow menu. "Remove" is ambiguous; it sounds like removing the tool or its skills.
  - config and skill paths moved into a "Details" popover with **Copy path** and **Reveal in Explorer**. Reveal needs a small native message, as in §4.
- **Uninstalled providers** go in `<details class="muted-group">Other supported AI tools not installed on this device (2)</details>`, collapsed, each with a one-line "Install X, then SkillHub connects automatically" and a docs link.
- `restartRequired` lives in a JS `Set`, so a page reload loses it (`statusBar.js:9`). Move it into the daemon, keyed by config mtime versus last registration, so the badge survives.
- Brand icons: `icons.js` currently draws Claude as six crossing lines and Cursor as a generic cube. Use the official marks in line with each vendor's brand guidelines, monochrome `currentColor`, with a tinted container per provider.

---

## 2. Community discovery architecture (Explore v2)

### 2.1 Goals
List **every** relevant Markdown file in a repo, each with title and description, using **≤ 2 GitHub REST calls per repo per commit**. Cache results **permanently by SHA**, work offline after first view, and stay inside the zero-database invariant.

### 2.2 Fetch strategy (rate-limit safe)

| Step | Call | Quota impact |
|---|---|---|
| Resolve ref → SHA | `GET /repos/{o}/{r}/commits/{ref}` with **`If-None-Match` ETag** | A 304 response does not count against the REST quota |
| Content | **`https://codeload.github.com/{o}/{r}/tar.gz/{sha}`**, streamed through `GZipStream` and `System.Formats.Tar` (in the box on .NET 8) | Not a REST API call. Extract only text entries ≤ 256 KB with allowed extensions. Cap at ~40 MB compressed and 5 000 entries. |
| Fallback | Today's `git/trees?recursive=1` + `raw.githubusercontent.com` | Keep for repos where the tarball is too large or blocked |

- **Optional auth.** With user opt-in, read a token from `GH_TOKEN`/`GITHUB_TOKEN` or `gh auth token`, or accept a pasted PAT. Store it with **DPAPI (`ProtectedData`, CurrentUser)** in `~/.skillhub/secrets.bin`, never in JSON. That raises the limit from 60/h to 5 000/h and unlocks code search.
- **Quota UX.** Parse `X-RateLimit-Remaining` and `X-RateLimit-Reset` and show "GitHub limit resets at 14:32 · browsing cached repos" instead of a dead error.

### 2.3 On-disk index (no DB)

```
~/.skillhub/cache/github/{owner}/{repo}/
    refs.json                 # ref -> {sha, etag, checkedAt}
    {sha}/index.json          # immutable; written atomically via AtomicFileWriter
    {sha}/blobs/{sha256}.md   # optional text cache for previews
```

`index.json` holds `{ repo, sha, license, readme: {path, excerpt}, entries: [{ path, kind, name, title, description, when_to_use, dir, files: [...], bytes, warnings: [...] }] }`. Evict by LRU with a total size cap (200 MB). Because a SHA never changes, entries never go stale. Only `refs.json` expires (TTL 10 minutes, revalidated with ETag).

### 2.4 Classification (`SkillDocumentKind`)

| Kind | Match | Import as |
|---|---|---|
| `skill` | `**/SKILL.md` | bundle = the **entire directory** (all text files, size-capped), not just the 6 whitelisted folders |
| `cursor-rule` | `*.mdc`, `.cursor/rules/**/*.md` | single-file skill |
| `claude-command` | `commands/**/*.md`, `.claude/commands/**` | skill with `kind: command` passthrough |
| `agent` | `agents/**/*.md` or `*.agent.md` with `name` + `description` frontmatter | skill with `kind: agent` |
| `copilot-instructions` / `prompt` / `chatmode` | `*.instructions.md`, `*.prompt.md`, `*.chatmode.md` | skill; carry `applyTo` into passthrough and map it to Cursor `globs` |
| `agents-md` | `AGENTS.md`, `CLAUDE.md`, `GEMINI.md` | "Project rules". Importable, but labelled as such |
| `doc` | `README*`, other `*.md` | Context only. Readable, not importable by default |

Frontmatter-less files get a name from the filename, a title from the first H1, and a description from the first paragraph. Instead of rejecting them, add a **warning badge** ("No frontmatter: metadata inferred").

**Schema impact:** canonical storage must allow root-level sibling files. Today `SkillPaths.SupportingPath` throws for them. Accept any safe relative path except `SKILL.md` itself, keep the reparse-point and segment validation, and add round-trip tests. Cursor's single-file `.mdc` cannot carry siblings. Surface that as a per-provider warning ("Cursor receives SKILL.md only") instead of losing the files silently.

### 2.5 Explore UI v2
- **Source = anything.** "Add a repository…" pins `owner/repo` into a user source list (`~/.skillhub/sources.json`) next to Featured, Local and the curated repos. Pasting a GitHub URL anywhere opens that repo.
- **Repo view**:
  - a header card with owner avatar initial, stars if authed, license, short SHA, an "Updated 3 days ago" note and a collapsible README excerpt;
  - a left list **grouped by kind with counts** (Skills 17 · Commands 4 · Rules 9 · Docs 3), each row with title, a 2-line description, and a file-count chip ("+5 files");
  - a right preview with **tabs for every file in the bundle** (a file tree, not a disclosure).
- **Multi-select** checkboxes, with a sticky "Import 5 selected" bar sharing one deploy-target choice.
- **"Installed" rows** get an **Open** action. When `import_revision` ≠ latest SHA: an **Update available** badge (see §5.1).
- **Featured** is generated, not hard-coded: union the curated repos' indexes and rank by a small embedded editorial list plus local-plugin popularity. Pin every featured item to a SHA.
- **Search**: a local fuzzy search (title > name > description > path, with a word-prefix bonus) across all cached indexes. "Search GitHub" (code search, needs a token) is an explicit second tier.

### 2.6 Smaller Explore bugs to fix along the way
- `InferTopic` uses raw `Contains`. `"ai"` matches *maintain/email/detail*, `"ci"` matches *decision*, `"ui"` matches *build/guide*, `"cd"` matches anything with "cd". Topics are therefore often wrong (`SkillImporterService.cs:112-126`). Tokenize on word boundaries and prefer frontmatter `tags`/`category`.
- `listScroll = window.scrollY` (`explore.js:162`), but at ≥ 768 px results scroll inside `.explore-results`, so Back restores the wrong position.
- Search typing clears results to "Searching…" on every keystroke (`explore.js:38-46`). Keep the old results dimmed until new ones arrive. That avoids a flash, and Linear does the same.
- The preview cache is duplicated: client-side `previewCache` holds 48 items / 8 MB and server-side `_previewCache` holds 64 / 8 MB. With SHA-keyed disk indexes, the client cache can go.

---

## 3. UI/UX critique and Design System v2

### 3.1 What still looks developer-built (Source, CSS read)

| Area | Today | Effect |
|---|---|---|
| Elevation | One `--shadow`. Panels, cards, inputs, chips, dialogs and disclosures are all `1px solid var(--line)` | Flat "wireframe" look. Hierarchy comes only from borders. |
| Buttons | `.btn` has 4 variants (default, primary, quiet, danger), text-only, `filter: brightness` hover, `opacity:.62` disabled | No icons, no pressed state, no loading spinner (`busy()` only disables, `ui.js:74`). Destructive actions look like links. |
| Typography | 15 px Segoe UI. Metadata at .78–.82 rem. Same weight (650) for h1, h2, h3 and titles | Little contrast between levels. Row titles look the same as section headings. |
| Color | Lavender accent on near-black, with a single accent | Nice base, but no semantic surface tints (success, warn and info backgrounds) and no provider tints |
| Motion | 2 keyframes (120 ms collection fade, 160 ms preview fade) | No route transitions, no hover lift, no list-item enter, no skeletons |
| Feedback | `notify()` replaces the previous toast, hides after 4.8 s, has no action and no stacking (`ui.js:33-39`) | Undo is impossible from a toast, and fast actions overwrite each other |
| Empty states | Dashed box + h2 + muted line | No illustration, no primary action, no keyboard hint |
| Loading | Text such as "Loading skills…" | Content jumps when it lands. No skeleton rows. |
| Icons | 9 inline outlines. Claude and Cursor glyphs are not their brands. | Weak recognition in the most important spot (the header pill and the rows) |
| Markdown | Hand-rolled renderer: no images, task lists, strikethrough, hard breaks, nested quotes, autolinks or code highlighting. External links *copy* instead of open (`markdown.js:28-32`). | Skills look worse in SkillHub than on GitHub. That matters when the product's job is reading skills. |
| App bar | Brand and Library go to the same place. Nav underline at 2 px. Labels hidden at ≤ 600 px (`layout.css:66-70`) | Fine, but generic. No command entry point. |
| Settings | Three text tabs plus `settings-row` lists | Utilitarian. See §1.4. |

### 3.2 Design System v2 (still no build step)
1. **Tokens.** Split `base.css` into `tokens.css` with these scales:
   - **surface**: `--bg`, `--surface-1`, `--surface-2`, `--surface-3`, `--overlay`;
   - **elevation**: `--e1` (hairline + 1 px shadow), `--e2` (cards), `--e3` (popovers), `--e4` (dialogs), each with a light and a dark recipe;
   - **radius**: 6 / 10 / 14;
   - **space**: 4-pt;
   - **type**: 12 / 13 / 14 / 16 / 20 / 28, with weights 450 / 550 / 650;
   - **status**: `--live`, `--warn`, `--danger`, `--info`, each with `-bg` and `-fg`;
   - **provider tints**: `--p-codex`, `--p-gemini`, `--p-claude`, `--p-cursor`.

   Prefer `Segoe UI Variable Text/Display` (Windows 11), with `font-variant-numeric: tabular-nums` on counts.
2. **Borders → fills.** Sibling rows use dividers. Selected states use a quiet `--surface-2` fill plus a 2 px accent inset bar. Borders stay only on inputs and real containers. That one change removes most of the "wireframe" feel.
3. **Buttons.** Sizes `sm`, `md`, `lg`, with an optional leading icon and a `kbd` hint slot. Variants:
   - `primary`: gradient-free, with a 1 px inner highlight;
   - `secondary`: `surface-2` fill;
   - `ghost`;
   - `danger`: red text, turning red-bg on hover;
   - `icon`: square, with a tooltip.

   States: hover lifts `translateY(-0.5px)` and raises `--e2`. Active is `scale(.98)`. **Loading** shows a spinner replacing the icon, plus `aria-busy`, and keeps the width fixed. `busy()` should toggle this.
4. **Rows and cards.** Library rows get a 32 px leading glyph (a provider-tinted squircle with the skill's initial or kind icon), the title at 14/550, a 1-line description in `--secondary`, a trailing compact provider dots cluster, and a hover reveal of row actions (Open · Edit · ⋯), as in Raycast.
5. **Motion.** One easing (`cubic-bezier(.2,.8,.2,1)`), durations 120 / 180 / 240 ms:
   - route cross-fade with a 4 px slide;
   - stagger list enter (first 12 rows, 12 ms each);
   - dialog scale from .98;
   - skeleton shimmer.

   All of it sits under the existing reduced-motion kill switch.
6. **Toasts v2.** A stack of up to 3 with an action button (`Undo`, `Open`, `Retry`) and a progress bar. Hovering pauses the timer, and errors persist until dismissed.
7. **Command palette (Ctrl+K).** Ctrl+K currently just focuses Library search. Make it a Raycast-style palette: skills, "New skill", "Import from link…", "Open Settings › Connections", "Toggle theme", "Reconcile now", recent items, with fuzzy matching and `↵` / `⌘↵` alternative actions. This is the biggest single "feels premium" upgrade.
8. **Markdown.** Either embed a vetted, dependency-free renderer as a vendored ES module (for example `marked` + DOMPurify, pinned and checked into `wwwroot/vendor/`, which stays build-free) or extend the hand-rolled one with task lists, images (`data:` / `https:` blocked by CSP until proxied), strikethrough and hard breaks. Add lightweight code highlighting and a per-heading outline in the Reader inspector. External links: open in the user's default browser through a WebView2 `postMessage → Process.Start` **only for `https://` URLs outside the app origin, after a click**. The AGENTS.md "never open a browser" rule is about the app UI itself, not about outbound documentation links.
9. **Empty states.** Use a small monochrome illustration (an inline SVG built from the brand prism), one sentence and one primary action, with a keyboard hint. Examples: "No skills yet: **New skill** (N) or **Explore** (E)".
10. **Window chrome.** Mica/Acrylic backdrop on Windows 11 (`DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)`) with a transparent WebView2 background on the app bar gives an Arc/Win11-native depth cheaply. Fall back to solid colors under high contrast.

### 3.3 Screen-level notes not covered by `UI_UX_AUDIT.md`
- **Library:** collection tabs double as counts and filters. Merge them into a single segmented control with a scope selector, and move tag admin to a dedicated "Tags" side sheet. Add **sort** (name, recently edited, issues first) and **density** (comfortable or compact) toggles.
- **Reader:** "Actions" opens a modal for 4 commands (`skillReader.js:106-120`). Make it a popover menu, as in Raycast's action panel. Add a "Copy as prompt" quick action, since that is the most common real-world use.
- **Editor:** add a split **Write | Preview** view with a synced scroll, a live provider-output tab per provider, and inline lint (§5.2).
- **Diagnostics:** "Watching N folders" (`statusBar.js:137-138`) is jargon. Show a health summary ("All 47 destinations healthy · last reconcile 2 min ago") with a sparkline of the last 24 h of operations.
- **Onboarding:** there is no first-run flow. On first launch, run a 3-step sheet: (1) detected providers and connect them, (2) import existing provider skills, (3) install the bootstrap skill and show the "ask your AI to create a skill" demo.

---

## 4. Code quality and architecture review

| Sev | Finding | Where | Recommendation |
|---|---|---|---|
| P1 | Bundle sibling files dropped on import (§1.1 b) | `SkillImporterService.Previews.cs:58` | Fix with the indexer, with a regression test using an Anthropic-style layout |
| P1 | Arbitrary repo input ignored unless Source=Featured (§1.1 e) | `SkillRestEndpoints.cs:367` | Detect a repo-shaped `q` regardless of source |
| P2 | `InferTopic` substring false positives | `SkillImporterService.cs:112` | Word-boundary tokens, frontmatter first |
| P2 | Branch refs with `/` mis-parsed; `--skill` filter discarded in browse | `SkillImporterService.cs:219,581` | Resolve the longest matching ref via `git/matching-refs` or the tarball fallback; honor the filter |
| P2 | MCP `resources/list`, `resources/templates/list` and `prompts/list` return empty arrays | `Mcp/McpJsonRpcHandler.cs:114-116` | Implement (§5.4) |
| P2 | Sync-over-async in the preferences store (`.GetAwaiter().GetResult()` on an atomic write) | `Desktop/DesktopPreferences.cs:71` | Make the store async, or use a synchronous atomic write. Avoid blocking the WinForms thread. |
| P2 | `async void OnFormLoadAsync` | `Desktop/SkillHubDesktopShell.cs:326` | Wrap the full body in try/catch and log. An unhandled exception there terminates the process. |
| P3 | Silent `catch { }` around file deletes | `InstallManager.cs:408-469`, `Program.cs:138` | Log at debug. Silent failures make support harder. |
| P3 | Manual `?v=` cache-busting across ~15 files (AGENTS.md even documents it as a chore) | `wwwroot/**` | Serve static files with `Cache-Control: no-cache` + ETag from the embedded resource hash, or generate an import map at startup. Drop manual bumps. |
| P3 | **AGENTS.md drift:** it lists `Api/McpHttpEndpoints.cs` (actually `Mcp/HttpMcpEndpoints.cs`) and `css/variables.css` (actually `base.css`), and omits `src/SkillHub.Core`, `tests/SkillSync.Frontend`, `tests/packaging`, `installer/` and the Core test project | `AGENTS.md` | Update it. AI agents read this file first, so wrong paths waste every session. |
| P3 | Connection "restart required" state is lost on reload | `statusBar.js:9` | Keep it in the daemon |
| P3 | Duplicate preview caches (client + server) | `explore.js:12`, `SkillImporterService.Previews.cs:10` | Use one server-side cache keyed by SHA |
| P3 | Frontend views rebuild via `replaceChildren` and patch focus back by `data-*` keys (Reader, Settings, Library) | various | Adopt a ~60-line keyed-reconcile helper or tiny signals, still with no build step. This removes a whole class of focus and scroll bugs. |
| P3 | `notify()` single-slot toast | `ui.js:33` | Toast stack (§3.2.6) |
| P3 | Curated repo list and featured skills hard-coded in C# | `SkillImporterService.cs:88-110,358-371` | Move to an embedded `featured.json`, updatable without a release |
| Info | Tests: 57 .NET + a Chromium workbench harness. No coverage of Explore classification, bundle completeness, rate-limit paths or Connections ordering | `tests/` | Add `RepositoryIndexerTests` with a canned tarball fixture, and Explore/Connections cases in `workbench.test.mjs` |

What is good and should stay: atomic writes plus the 3-layer loop guard, per-destination ownership and review, SHA-pinned import plans with expiry, the `Latest` generation/abort pattern in the frontend, and the strict CSP and loopback guard.

---

## 5. Five high-impact features

### 5.1 Upstream Update Inbox ("Your imported skills have updates")
SkillHub already stores `import_source`, `import_revision` and `import_path` (`SkillImporterService.Plans.cs:147-150`). A background check, at most every 6 h and ETag-revalidated (often costing 0 quota), compares the stored revision with the repo's current SHA and the file's blob hash.
- **UI:** a badge on the row, an "Updates (3)" chip in Library, and a **3-way diff** (upstream old → upstream new → your local edits) with **Take update / Keep mine / Merge**.
- **Why it matters:** this turns SkillHub from a copier into a *package manager for skills*. No competitor does this locally.

### 5.2 Skill Doctor (lint and quality score)
A per-skill checklist in the Editor and Reader:
- **frontmatter rules per provider**: name ≤ 64 lowercase-hyphen, description length limits, Cursor `globs` validity;
- **broken relative links and missing referenced files** (catches the §1.1 b class of bug);
- a token estimate against each provider's progressive-disclosure budget;
- a duplicate trigger description across skills ("These 2 skills will compete");
- secrets detection (API-key patterns) before sync.

It produces a score badge in the Library, and adds a `skillhub_lint` MCP tool so AIs fix their own skills.

### 5.3 Project Workspaces (per-repo skill sets)
Every provider supports **project-level** dirs (`<repo>/.claude/skills`, `.cursor/rules`, `.agents/skills`, `.gemini/skills`). Let users add a project folder and choose which skills apply there. SkillHub projects them with the same ownership ledger, scoped per workspace (`~/.skillhub/workspaces/<id>.json`). Add a "Profiles" switcher (Work / Personal / Client-X) in the app bar. This is the feature teams ask for first. The earlier roadmap's Phase 5 called for it once ownership was stable, and it now is.

### 5.4 MCP Resources and Prompts
- **Resources:** `skillhub://skills/{slug}` (canonical Markdown) and `skillhub://skills/{slug}/files/{path}`, with `resources/templates/list` for discovery and subscriptions so clients see live edits.
- **Prompts:** expose each enabled skill as an MCP prompt (`/skillhub:<slug>`). Clients **without native skill folders**, such as Claude Desktop, other MCP hosts and future tools, then get every skill as a slash command at no extra cost.
- **Tools:** `skillhub_suggest(task)` returns the 3 most relevant skills using local BM25 over title, description and when_to_use, so any agent can pull the right skill on demand.

### 5.5 Revision History and Time Machine
Write each canonical revision to `~/.skillhub/history/<slug>/<rev>-<utc>.md`. That is plain files, so the zero-DB rule holds. Rotate at 50 revisions or 30 days.
- The Reader gets a **History** tab with a side-by-side diff and **Restore this version**. Conflict review reuses the same diff component.
- Pair it with **Skill Packs**: export selected skills as `*.skillpack.zip` with a `skillpack.json` manifest, and register a per-user `skillhub://install?repo=owner/repo&path=…` protocol handler. "Add to SkillHub" links in READMEs then open the review screen directly. This gives one-click sharing without any server.

---

## 6. Phased implementation plan

### Phase A · Quick wins (≈ 3–4 days)
1. Status dot: `--live` tokens, breathing glow, amber issue state (§1.3).
2. Connections: sort, provider cards, brand marks, collapsed "not installed" group, icon buttons, `Disconnect` rename, persistent restart flag (§1.4).
3. Explore review footer with inline deploy chips; remove the "Deploy to…" modal (§1.2).
4. Accept `owner/repo` input from any source; honor `--skill`; fix the featured slug; word-boundary `InferTopic`.
5. Fix AGENTS.md drift; drop manual `?v=` bumps in favour of ETag/no-cache.

**Gate:** Chromium workbench cases for Connections order, deploy chips at 480 px and 1280 px, and the dot states in light and dark.

### Phase B · Repository Indexer (≈ 1.5–2 weeks)
1. `RepositoryIndexer`: tarball fetch, kind classification, `index.json` per SHA, ETag ref cache, optional DPAPI token, quota UX.
2. Canonical schema: allow root-level sibling files. Add per-provider capability warnings. Add round-trip tests.
3. Explore v2 UI: grouped kinds, rows with metadata, README card, bundle file tabs, multi-select import, pinned user sources.

**Gate:** importing an Anthropic-style skill with sibling `.md` files keeps every referenced file. `awesome-copilot` lists its instructions and prompts. A cold browse of 3 repos uses ≤ 3 REST calls; a warm one uses 0.

### Phase C · Design System v2 (≈ 2 weeks)
Tokens and elevation, buttons with loading state, rows with hover actions, motion, toast stack, skeletons, empty states, Markdown upgrade, external links, Mica backdrop, and the command palette.

**Gate:** the existing contrast checks, plus a 3:1 check for non-text status indicators, plus reduced-motion and forced-colors snapshots.

### Phase D · Flagship features (≈ 3–5 weeks, independently shippable)
In order: 5.2 Skill Doctor → 5.1 Update Inbox → 5.4 MCP Resources/Prompts → 5.5 History + Packs → 5.3 Workspaces. Workspaces goes last because it extends the ledger model.

### Phase E · Open-source launch polish
- README: hero screenshot/GIF, a 30-second demo ("ask Codex to create a skill → appears in Claude, Gemini, Cursor"), an architecture diagram.
- Signed installer (the scripts already support `-SignCommand`), a **winget** manifest, and an in-app update check against GitHub Releases (opt-in, ETag).
- First-run onboarding sheet (§3.3).
- `CONTRIBUTING.md`: a "good first issue" list built from the P3 items above.

---

## 7. What this review did not verify
- No live run of the installed app this session. The "Deploy to…" truncation cause and the visual critique come from CSS and DOM reading, not screenshots. Confirm with the workbench harness at 768–900 px widths before coding the fix.
- I did not fetch real repositories. The anthropics/skills sibling-file layout and the awesome-copilot file kinds are stated from knowledge of those repos and should be confirmed against current trees in the indexer tests.
- The codeload tarball approach (no REST quota, and the commit SHA in the pax header) should be spiked before relying on it. The tree + raw fallback stays in place either way.
