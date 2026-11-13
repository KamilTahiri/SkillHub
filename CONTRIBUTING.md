# Contributing to SkillHub

Run the solution tests on Windows and the portable core tests on any .NET 8 development machine. Use a temporary home for daemon integration tests.

## Provider adapters

The provider contract is in [ISkillTranspiler](src/SkillSync.Daemon/Core/Transpilers/ISkillTranspiler.cs). See the [round-trip fixtures](tests/SkillSync.Tests/TranspilerRoundTripTests.cs) for examples with realistic skill metadata.

1. Parse the provider's primary file into a canonical model. Preserve canonical fields that the provider cannot express.
2. Render a deterministic projection and validate all provider paths through the shared containment rules.
3. Add a fixture for inbound parsing, outbound rendering, and a round trip using an existing canonical model. Include explicit clearing and unsupported metadata if the provider distinguishes them.
4. Run the watcher and sync safety tests before wiring a new destination into discovery.

Build release candidates with the pinned Inno Setup compiler used by the Windows CI workflow. Only promote packages after install, restart, upgrade, and uninstall checks pass.
