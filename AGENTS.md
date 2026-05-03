# AGENTS.md

## Project Overview

Xel's Combat AI is a C# Dalamud plugin for Final Fantasy XIV. It manages a dedicated BossMod Reborn preset during combat, using BossMod IPC for movement/range/positioning strategies, Avarice shared data for positionals, and optional RotationSolver Reborn IPC for True North behavior.

The plugin assembly and source live under `XelsCombatAI/`. The repository root also contains release metadata, packaging scripts, CI, and read-only integration references.

## Repository Layout

- `XelsCombatAI/` - plugin source, manifest, icon, project file, and lock file.
- `XelsCombatAI/Plugin.cs` - main plugin lifecycle, command handling, combat update loop, runtime strategy application.
- `XelsCombatAI/Configuration.cs` - persisted user configuration, migration, clamping, reset defaults.
- `XelsCombatAI/ConfigWindow.cs` - ImGui configuration UI.
- `XelsCombatAI/BossModIpc.cs` - BossMod IPC contract wrapper and preset payload.
- `XelsCombatAI/RotationSolverIpc.cs` - RotationSolver Reborn IPC wrapper.
- `scripts/package-release.sh` - release build and zip packaging script.
- `.github/workflows/release.yml` - GitHub release workflow.
- `pluginmaster.json` - custom plugin repository metadata.
- `vendor/` - read-only external reference code. See `vendor/AGENTS.md`; its instructions override this file inside that directory.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsCombatAI/XelsCombatAI.csproj
dotnet build XelsCombatAI/XelsCombatAI.csproj -c Release -p:EnableWindowsTargeting=true
scripts/package-release.sh
```

Notes:

- The project targets `net10.0-windows8.0` through `Dalamud.NET.Sdk/15.0.0`.
- On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies before building.
- Local non-CI builds reference ECommons at `../../AutoDuty/ECommons/ECommons/ECommons.csproj`.
- CI builds reference ECommons at `../ECommons/ECommons.csproj` after checkout by the release workflow.
- There is currently no test project in this repository. Prefer `dotnet build` as the minimum validation after code changes.

## Coding Guidelines

- Follow the existing C# style: file-scoped namespace, nullable enabled, explicit access modifiers, `this.` for instance members, and small internal helper methods.
- Keep plugin behavior conservative. The code runs during combat and writes BossMod transient strategies, so avoid broad refactors or timing changes unless the task requires them.
- Preserve IPC names, track names, option strings, and preset payload module names unless you have verified the upstream contract in `vendor/` or the relevant upstream project.
- Keep runtime update work lightweight. `Plugin.OnFrameworkUpdate` runs frequently and currently throttles strategy updates to 250 ms.
- Clamp and migrate any new persisted configuration fields in `Configuration.cs`.
- Update `ConfigWindow.cs`, `README.md`, and defaults together when adding or changing user-visible settings.
- Use `System.Globalization.CultureInfo.InvariantCulture` for serialized numeric IPC strings.
- Log recoverable integration failures with `Plugin.Log.Verbose` where the plugin should keep running.

## Release And Metadata

When changing the plugin version, keep these files in sync:

- `XelsCombatAI/XelsCombatAI.csproj` `Version`
- `pluginmaster.json` `AssemblyVersion`

When changing plugin description, tags, icon URL, name, or Dalamud API metadata, check both:

- `XelsCombatAI/XelsCombatAI.json`
- `pluginmaster.json`

`scripts/package-release.sh` writes `artifacts/XelsCombatAI.zip`. Treat `artifacts/`, `bin/`, and `obj/` as generated output.

## Dependency And Integration Notes

- Required runtime dependencies are BossMod Reborn and Avarice.
- RotationSolver Reborn is optional and only required for `Manage True North`.
- BossMod preset name is `Xel's Combat AI`; changing it affects preset lookup, activation, cleanup, and user state.
- The plugin should fully hand control back out of combat by disabling movement, resetting range/role/positional strategy state, and clearing the active preset when appropriate.
- Death/resurrection handling matters because BossMod can clear active presets on death.

## Safety Rules For Agents

- Do not edit files under `vendor/` except `vendor/README.md` or `vendor/AGENTS.md`, and only when explicitly asked.
- Do not commit or package vendored external plugin code.
- Do not remove or loosen dependency checks for BossMod, Avarice, or RotationSolver unless the requested behavior explicitly requires it.
- Do not introduce network calls or background tasks in the combat update path.
- Avoid changing default movement, range, gap closer, or True North behavior without updating docs and considering user safety.
- Before finishing code changes, run the most relevant available validation command and report any command you could not run.
