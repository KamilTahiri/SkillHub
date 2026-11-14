# SkillHub — Project Evaluation

**14 November 2026 · `main` at `c7e9748` · after Phases A–C and the follow-up fixes**

The scores below are deliberately strict. A 10 means "best available anywhere for this job"; a 7 means "solid and usable, with real gaps"; a 5 means "works, but you would not recommend it yet". Competitor details come from my knowledge of those tools and may lag their latest releases. Please verify them before quoting.

---

## 1. Scores

| Dimension | Now | Ceiling | One-line reason |
|---|:-:|:-:|---|
| Core sync engine, reliability and safety | **7.0** | 9.0 | Careful ownership and conflict model, but Windows-only, global-scope-only, no history |
| Architecture and code quality | **6.5** | 8.5 | Clean boundaries and good tests; large files, a hand-rolled UI layer and CSS layering are accruing debt |
| UI/UX and visual polish | **6.5** | 9.0 | Explore, Connections and the palette are now good; Library, Reader and Editor are still functional rather than delightful |
| Community discovery and ecosystem | **6.5** | 9.0 | Real indexer and skills.sh search, but no trust signals, no update tracking and no script safety review |
| Real-world utility | **6.0** | 8.5 | Solves a real problem for one Windows user with 2–4 tools; misses teams, projects and most agents |
| Open-source portfolio value | **8.0** | 9.0 | Unusually broad and well-engineered for a solo project; needs screenshots, a release and outside users |
| **Overall** | **6.6** | **8.8** | A strong engine wrapped in a still-maturing product |

### 1.1 Core sync engine, reliability and safety: 7.0 now, 9.0 ceiling

**What earns the 7:**
- **Ownership tracking.** A per-destination ledger records which copies SkillHub owns. Atomic temp-file writes, a 3-layer loop guard, reviewable conflicts with snapshots and undo, and refusing to touch unknown files are better safety engineering than most commercial sync tools.
- **Regression tests.** 80 .NET tests cover the data-loss bugs found in earlier audits, plus a Chromium UI harness.
- **Evidence-based detection.** Provider detection now looks for real install evidence (Claude) rather than trusting any folder.

**What holds it back:**
- **Windows only.** WebView2 shell, registry startup, DPAPI and MSIX detection all tie it to Windows. Most AI developers use macOS or Linux at least part of the time.
- **Global user scope only.** There are no project-level skills (`<repo>/.claude/skills`, `.cursor/rules`, `AGENTS.md`), and that is where teams actually put shared rules.
- **No revision history.** An accidental edit or an accepted provider version cannot be rolled back beyond the one conflict snapshot.
- **Single JSON ledger.** It is rewritten in full on every change. That is fine at 50 skills, and unproven at 2,000 imported skills.
- **Lossy transpilation for single-file targets.** Cursor `.mdc` can't carry bundle files. That is now disclosed in the UI, but not solved.
- **No fault-injection testing.** There is no fuzz or property testing for watcher storms, disk-full, permission-denied or antivirus file locks, which are common on Windows.

### 1.2 Architecture and code quality: 6.5 now, 8.5 ceiling

**Good:**
- Clear layers: Core, Api, Mcp, Desktop, Watchers.
- A no-build frontend.
- A strict loopback trust boundary with a per-session token and CSP.
- Immutable SHA-keyed caches.
- `Latest` generation and abort handling in the UI.
- Honest error messages.

**Debt:**
- **Large files.** Several are big and mix concerns: `SkillImporterService.cs` at ~810 lines, `InstallManager.cs` at ~885, `McpJsonRpcHandler.cs` at ~815, and `explore.js` at ~605.
- **Manual DOM patching.** The vanilla-JS views rebuild DOM by hand and patch focus back afterwards. Every new feature pays this tax. A ~100-line keyed-render helper or tiny signals library (still build-free) would remove whole bug classes.
- **Override CSS.** `polish.css` (Design System v2) overrides `components.css` instead of replacing it, so two sources of truth now exist for buttons, rows and panels. I introduced that layering for speed. It should be folded together.
- **Loose API contracts.** DTOs are anonymous objects in endpoints, and the frontend has no typed contract. JSDoc types or a shared schema would catch API drift.
- **Odd project split.** `SkillHub.Core` compiles the Daemon's `Core/**` by file linking. It works, but it's surprising for contributors.

### 1.3 UI/UX and visual polish: 6.5 now, 9.0 ceiling

**Now genuinely good:**
- Explore v2: repository card, kind tabs, bundle file tabs and multi-select import.
- Connections: provider cards that separate skills-folder sync from MCP.
- The Ctrl+K palette.
- The toast stack.
- The live status dot.
- Mica title bar and a tokenized design system.

**Still holding it back:**
- **Editor.** It is a plain textarea with no live preview side by side, no inline lint, and no file tree for bundles. This is the screen power users spend the most time in.
- **Library.** It is better (glyphs, hover actions, sort, density), but still a flat list. There is no grouping, no keyboard row navigation (J/K, Enter, E), and no multi-select bulk actions.
- **No first-run onboarding.** A new user lands in an empty Library without being told what SkillHub did, which providers it found, or how to get started.
- **Accessibility.** It is reasoned about in code, but not verified with Narrator or NVDA, at high DPI with 200% zoom, or in forced-colors mode on the real WebView2.
- **Untested in the native window.** Visual verification so far has used Chromium with mock data. Nobody has done a systematic pass in the installed window across light and dark themes, 125%/150% scaling and small docked sizes.

### 1.4 Community discovery and ecosystem: 6.5 now, 9.0 ceiling

**Now:**
- Every repository commit is indexed once from a tarball and cached forever.
- All major Markdown formats are classified.
- Sibling files are preserved, so Anthropic's pdf skill keeps `FORMS.md` and `REFERENCE.md`.
- skills.sh search covers the full directory, ranked by installs.
- Pinned sources and non-blocking search.

**Missing:**
- **Trust signals.** Stars, last commit, publisher identity, install counts on non-skills.sh rows, and a verified or official badge.
- **Update tracking.** Imports store their source revision, but nothing tells you when upstream changes.
- **Safety review for executable content.** A skill can ship `scripts/*.py` or `.sh` that an agent will later run. SkillHub imports these with only a size and UTF-8 check. This is the biggest gap before inviting strangers to import community skills.
- **Browsing structure on skills.sh.** Its API is search-only, so SkillHub can't show categories, trending or "new this week".
- **First-download latency.** A first browse of a large repository (awesome-copilot is a 113 MB archive) takes 15–30 seconds.

### 1.5 Real-world utility and portfolio value: 6.0 and 8.0 now, 8.5 and 9.0 ceiling

**Utility:** SkillHub fully solves "I write a skill once and it reaches Claude, Codex, Gemini/Antigravity and Cursor on my Windows PC, and my AI can create skills through MCP". That is real and uncommon. It does not yet serve:
- teams;
- per-project rules;
- macOS or Linux;
- the long tail of agents that skills.sh supports (Copilot, Windsurf, Cline, Kiro, OpenCode, Amp, Goose and others).

**Portfolio:** high. It is a native desktop app, a sync engine with real safety properties, an MCP server over stdio, named pipe and HTTP, a transpiler layer and a GitHub indexer, all tested. What it lacks is evidence of outside use: a release, screenshots and a GIF, a few stars or issues, and a changelog.

---

## 2. How SkillHub compares

| Alternative | What it is | Where it beats SkillHub | Where SkillHub wins |
|---|---|---|---|
| **skills.sh / `npx skills` CLI** (Vercel Labs) | Installs community skills into many agents' folders from the terminal | Supports dozens of agents. Cross-platform. Project and global scope. Huge directory and network effects. Zero install footprint | Two-way sync: edits made inside any tool come back. Ownership and conflict safety. Transpiles frontmatter per tool. MCP so your AI can author skills. GUI review of every file before import. Your own skills, not just community ones |
| **Rule-distribution CLIs** such as rulesync and ruler | One source of rules generated into `.cursor/rules`, `CLAUDE.md`, Copilot instructions and more, usually per project | Project and repo scope committed with the code. Team-shareable through git. Cross-platform. CI-friendly | Live file watching and two-way merge. Skill bundles with supporting files. A desktop UI. MCP authoring. Discovery and import |
| **Dotfiles plus symlinks** (chezmoi, stow, a git repo) | Version-controlled config synced across machines | Full git history. Multi-machine sync. Works everywhere. Free and transparent | No per-tool format differences to hand-maintain. Conflict detection when a tool rewrites a file. Discovery. No symlink fragility on Windows |
| **`.cursorrules` / `AGENTS.md` committed in repos** | The emerging convention for project rules | The team sees and reviews rules in PRs, and they travel with the code | Personal cross-tool skills, bundles and community discovery. Not one file per tool |
| **Claude Code plugin marketplaces** | Distribute skills, commands, hooks and MCP servers for Claude | Native to Claude, with versioned plugins and hooks | Works across Claude, Codex, Gemini and Cursor. Your edits flow back. One library |
| **Mem0 / OpenMemory and other MCP memory servers** | Shared, retrievable memory across AI tools | Dynamic, automatically captured facts and semantic retrieval | Different job: SkillHub manages explicit, reviewable procedures (skills and rules), not inferred facts. They complement each other |
| **Raycast AI commands / prompt managers / Espanso** | Fast, hotkey-driven prompt snippets | Instant invocation anywhere and superb polish | Agent-native skills that tools load on their own, with bundles, sync and MCP |

**Honest positioning:** SkillHub is the only tool here that combines two-way sync, safe ownership, cross-tool transpiling and MCP authoring in a GUI. The CLI tools win on breadth (more agents, more platforms, project scope) and on network effects. The winning move is not to out-CLI them. It is to become their best desktop companion: read skills.sh (done), honour project scopes, and cover more agents.

---

## 3. Missing features — proposals only, not implemented

### 3.1 Must-have before a public open-source launch

| # | Feature | Why it is a launch blocker |
|---|---|---|
| M1 | **Import safety review**: flag executable files (`.py`, `.sh`, `.ps1`, `.js`), show them in a "Runs code" section, scan for secrets and suspicious commands (curl-pipe-shell, credential paths), and require explicit acknowledgement | Community skills can run code through agents. Shipping discovery without this is a supply-chain risk |
| M2 | **Revision history and restore**: plain-file history per skill under `~/.skillhub/history/`, a History tab with diff and one-click restore | Users must be able to undo. It also makes every risky operation reversible |
| M3 | **Upstream update inbox**: compare stored `import_revision` with the source repository and offer a 3-way diff with take / keep / merge | The data already exists. Without it, imports go stale silently |
| M4 | **Project-scoped skills (workspaces)**: add a repository folder and sync selected skills into `<repo>/.claude/skills`, `.cursor/rules`, `.github/instructions` and `AGENTS.md` | This is where teams keep rules, and the biggest gap against CLI tools |
| M5 | **More agents**: GitHub Copilot (`.github/instructions`, `.github/skills`), Windsurf, Cline, Kiro, OpenCode and Amp, via a declarative provider table | Four providers feels narrow next to skills.sh. Most additions are folder and format mappings |
| M6 | **First-run onboarding**: detected tools, an offer to import existing skills, connect MCP, and a "try it" prompt | New users need a guided first five minutes |
| M7 | **Release engineering**: signed installer, opt-in update check against GitHub Releases, winget manifest, changelog and a version bump to 1.3.0 | Unsigned binaries trigger SmartScreen and lose most visitors |
| M8 | **README and docs**: hero screenshot, a 30-second GIF ("ask Codex to create a skill, it appears in Claude and Cursor"), architecture diagram and FAQ | The project's quality is invisible without them |
| M9 | **Backup and multi-machine story**: export or import the whole library, or an optional git remote for `~/.skillhub/skills` | Users with two PCs (or a reinstall) otherwise lose everything |

### 3.2 Nice-to-have differentiators

| # | Feature | Delight factor |
|---|---|---|
| N1 | **Skill Doctor**: per-provider frontmatter lint, broken relative links, token budget, overlapping trigger descriptions, and a quality score in the Library | Makes skills better, not just synced |
| N2 | **MCP resources and prompts**: every skill exposed as an MCP prompt (`/skillhub:<slug>`) and resource, plus a `skillhub_suggest(task)` tool | Any MCP client gains your skills, even ones without skill folders |
| N3 | **Skill packs and deep links**: `*.skillpack.zip` and a registered `skillhub://install?repo=…` handler, so an "Add to SkillHub" badge on a README opens the review screen | One-click sharing without a server |
| N4 | **Editor 2.0**: split Write/Preview, a file tree for bundles, inline Doctor hints, and per-provider output tabs side by side | The screen power users live in |
| N5 | **Global hotkey quick panel**: from the tray, fuzzy-find a skill and copy it as a prompt or open it, Raycast-style | Makes skills useful in tools that don't load them natively |
| N6 | **Profiles**: Work, Personal or Client sets that switch which skills are enabled per provider | A clean answer to "too many skills" |
| N7 | **Trending and categories in Explore**: cached skills.sh popularity, GitHub stars and last-updated, plus "official" badges for Anthropic, OpenAI and Google repositories | Discovery that feels alive |
| N8 | **"Make this a skill" from chat**: an MCP tool or prompt that turns the current conversation into a draft skill, opened in SkillHub for review | Closes the authoring loop with the AI |
| N9 | **macOS and Linux daemon with a tray-less web UI**: the Core is already `net8.0` | Unlocks most of the potential audience |

**My suggested next batch:** M1, M2, M4, M6 and M8. Safety and reversibility come first, then the feature teams need most, then the two things that turn a visitor into a user.
