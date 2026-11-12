# SkillHub

**SkillHub** is a native desktop application, multi-provider skill transpiler, bi-directional file watcher, and local MCP server that keeps your AI agent skills in sync across:
- **Claude Desktop & Claude Code** (`~/.claude/skills/<name>/SKILL.md`)
- **Google Gemini CLI & Antigravity** (`~/.gemini/skills/<name>/SKILL.md` & `~/.gemini/antigravity/skills/<name>/SKILL.md`)
- **ChatGPT / OpenAI Codex** (`~/.codex/skills/<name>/SKILL.md` & `~/.agents/skills/<name>/SKILL.md`)
- **Cursor** (`~/.cursor/rules/<name>.mdc`)

---

## Installation & Native Desktop App

### One-Command Install (Windows)
```powershell
.\install.ps1
```
Running `.\install.ps1` (or `SkillHub-Setup.exe --install --launch-ui` built via `.\build-installer.ps1`) automatically:
1. Installs **SkillHub** to `%LOCALAPPDATA%\SkillHub\SkillHub.exe`.
2. Creates **Windows Desktop** and **Start Menu** shortcuts (`SkillHub.lnk`) with a custom Hub icon.
3. Registers **OS Login Auto-Start** (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SkillHub`) so the background MCP server and file watcher start silently in the **System Tray** whenever you sign in.
4. Discovers and imports your existing skills from `~/.codex/skills`, `~/.gemini/skills`, `~/.claude/skills`, and `~/.cursor/rules` into `~/.skillhub/skills/`.
5. Non-destructively registers the `skillhub` MCP server in your installed AI tools (`claude_desktop_config.json`, `~/.claude.json`, `~/.gemini/settings.json`, `~/.gemini/antigravity/mcp_config.json`, `~/.cursor/mcp.json`, and `~/.codex/config.toml`).
6. Opens the native **SkillHub** desktop window. Closing the window minimizes SkillHub to the System Tray so background synchronization continues uninterrupted.

---

## CLI Commands

```powershell
# Install shortcuts, auto-start, MCP configs & launch UI:
SkillHub.exe install --launch-ui

# Check live status of providers and MCP registrations:
SkillHub.exe status

# Uninstall shortcuts, OS auto-start, and MCP client entries:
SkillHub.exe uninstall
```

---

## Building & Running Tests

```powershell
dotnet build SkillSync.sln
dotnet test SkillSync.sln
```
