# Contributing to SkillHub

Bug reports, provider corrections and pull requests are welcome. Corrections for the experimental adapters (Cursor, Copilot, Windsurf, Cline, Roo Code) are especially useful when you can confirm a path against an installed copy of the tool.

## Workflow

1. Fork the repository and branch from `main`.
2. Build and test on Windows (.NET 8 SDK, Node.js 22+, Chrome or Edge):

   ```powershell
   dotnet test SkillSync.sln
   node --test "tests/SkillSync.Frontend/*.test.mjs"
   ```

   The portable engine tests (`tests/SkillHub.Core.Tests`) also run on any .NET 8 machine. Tests that touch the file system use a temporary home folder, never your real `~/.skillhub` or AI tool folders; keep it that way in new tests.
3. To try the app itself, `.\install.ps1` builds the installer, installs it for your user and launches it (Inno Setup 6.7.4 is required; set `SKILLHUB_ISCC` if it is not on `PATH`).
4. Open a pull request against `main`. CI (`.github/workflows/verify.yml`) builds with warnings as errors, runs the same tests and builds the installer. Leave version bumps and releases to the maintainer.

Never include credentials, personal paths or the contents of your own skills in commits, fixtures or logs.

## Provider adapters

The provider contract is in [ISkillTranspiler](src/SkillSync.Daemon/Core/Transpilers/ISkillTranspiler.cs). See the [round-trip fixtures](tests/SkillSync.Tests/TranspilerRoundTripTests.cs) for examples with realistic skill metadata.

1. Parse the provider's primary file into a canonical model. Preserve canonical fields that the provider cannot express.
2. Render a deterministic projection and validate all provider paths through the shared containment rules.
3. Add a fixture for inbound parsing, outbound rendering, and a round trip using an existing canonical model. Include explicit clearing and unsupported metadata if the provider distinguishes them.
4. Run the watcher and sync safety tests before wiring a new destination into discovery.

Build release candidates with the pinned Inno Setup compiler used by the Windows CI workflow. Only promote packages after install, restart, upgrade, and uninstall checks pass.
