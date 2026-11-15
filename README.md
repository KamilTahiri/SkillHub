# SkillHub

**SkillHub** is a native desktop application, multi-provider skill transpiler, bi-directional file watcher, and local MCP server that keeps your AI agent skills in sync across:
- **Claude Desktop & Claude Code** (`~/.claude/skills/<name>/SKILL.md`)
- **Google Gemini CLI & Antigravity** (`~/.gemini/skills/<name>/SKILL.md` & `~/.gemini/antigravity/skills/<name>/SKILL.md`)
- **ChatGPT / OpenAI Codex** (`~/.codex/skills/<name>/SKILL.md` & `~/.agents/skills/<name>/SKILL.md`)
- **Cursor** (`~/.cursor/rules/<name>.mdc`)
- **GitHub Copilot** (`~/.copilot/skills/<name>/SKILL.md`) and **Windsurf** (`~/.codeium/windsurf/skills/<name>/SKILL.md`)
- **Cline** (`~/Documents/Cline/Rules/<name>.md`) and **Roo Code** (`~/.roo/rules/<name>.md`), opt-in per skill because these tools load every rule into every chat

A skill is `SKILL.md` plus every text file in its folder (for example `rules/*.md`, `references/`, `scripts/`).
Every save is kept in revision history (`~/.skillhub/history/`, 30 per skill) with diff and restore. Skill Doctor
checks provider rules, broken bundle links, overlapping triggers, leaked secrets and risky scripts. Explore indexes
GitHub repositories and the skills.sh directory, reviews scripts before import, and reports upstream updates for
imported skills. The MCP server also exposes every skill as a prompt (`/skillhub:<name>`) and a resource
(`skillhub://skills/<name>`).

---

## Windows installation

SkillHub is a Windows 11 x64 desktop application. It needs the Microsoft Edge WebView2 Evergreen Runtime. The setup includes .NET 8. For an unsigned preview build, install only the artifact you built or whose checksum you verified.

Run `SkillHub-Setup.exe`. It installs per user into `%LOCALAPPDATA%\SkillHub` with a Start Menu shortcut and an entry in Installed Apps. Choose **Open SkillHub** on the final screen. Use Settings to connect clients and manage login startup. The first daemon run reconciles detected provider skills with the canonical store.

To build setup from source, use the .NET 8 SDK and Inno Setup 6.7.4. Set `SKILLHUB_ISCC` to the compiler path if needed.

```powershell
.\build-installer.ps1
.\install.ps1
```

The build emits `dist/SkillHub-Setup.exe` and its SHA-256 file. To sign setup and its uninstaller, supply an Inno SignTool command with `-SignCommand` or `SKILLHUB_SETUP_SIGN_COMMAND`. The packaged app records SHA-256 hashes of the installed files in `install-manifest.json`.

Remove client connections and disable login startup in Settings before uninstalling from Installed Apps. The uninstaller removes packaged files and the Start Menu shortcut. Skills, conflict backups, and WebView profile data are retained.

## CLI Commands

```powershell
# Legacy registration command for an existing installation:
SkillHub.exe install --launch-ui

# Check live status of providers and MCP registrations:
SkillHub.exe status

# Remove registrations created by the legacy CLI installer:
SkillHub.exe uninstall
```

---

## Building & Running Tests

```powershell
dotnet build SkillSync.sln
dotnet test SkillSync.sln
# Portable engine and provider adapter tests
dotnet test tests/SkillHub.Core.Tests
```

## Release status and privacy

The package is a Windows preview. Codex, Claude Code and Desktop, Gemini CLI and Antigravity, and Cursor are supported configuration targets. Compatibility with a specific installed client depends on its configuration format. Inspect effective paths and registration status in Settings.

Skills and sync state stay in the user profile. Explore contacts GitHub for repository metadata and pinned files when you browse or review an import. Imported instructions and supporting files can be reviewed before deploying them to providers.

See [LICENSE](LICENSE), [third-party notices](THIRD_PARTY_NOTICES.md), [Contributing](CONTRIBUTING.md), and [Security](SECURITY.md).

## Local access and recovery

- Installed AI clients use `SkillHub.exe mcp-stdio`. The proxy talks to the current user's named pipe. If the daemon is unavailable, start SkillHub and retry.
- HTTP MCP is off by default. For a local HTTP client, set `SKILLHUB_HTTP_MCP_TOKEN` to a private value of at least 32 characters before launching SkillHub, then pass it as an `Authorization: Bearer` header. HTTP remains loopback-only. The desktop uses a separate per-run credential. Quick Tunnel is disabled.
- Local MCP compatibility is 2024-11-05 and 2025-06-18 over stdio, 2024-11-05 over legacy HTTP+SSE, and 2025-06-18 over Streamable HTTP. For Streamable HTTP, use the returned session ID and negotiated version on subsequent requests. The client must send the initialized notification before invoking tools.
- Explore reviews one skill at an immutable revision. It shows the license status and supporting files before saving. Choose provider targets in the review to deploy the new copy. The MCP equivalents are `prepare_skill_import` and `apply_skill_import`.
- Custom tags and desktop preferences are stored in `~/.skillhub/tags.json` and `~/.skillhub/desktop.json`. Library, Reader, and MCP use the same tag registry.
- Turning off a provider withdraws the files SkillHub owns at that destination. A missing provider copy appears in sync status and can be repaired explicitly. Conflicting edits remain on disk, and SkillHub saves a review copy under `~/.skillhub/conflicts/`.
- Deleting a managed skill moves its canonical bundle to `~/.skillhub/trash/`. Use the MCP `restore_skill` tool to restore it. Client config repair leaves a `.bak` copy alongside each file it changes.
- REST skill lists return metadata. Fetch `GET /api/v1/skills/{name}` for full details and the revision used by `PUT`. Use the dedicated catalog activation or clone command to make a managed copy.


### Browser regression test

Run `node --test tests/SkillSync.Frontend/workbench.test.mjs` with Node.js 22 or later and Chrome or Edge. Set `SKILLHUB_BROWSER` if the browser is installed outside its default Windows location. The test serves fixture data and uses a temporary browser profile.
