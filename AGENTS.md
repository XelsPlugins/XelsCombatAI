# AGENTS.md

## Scope

These instructions apply to the entire repository. More specific `AGENTS.md` files override these rules for their subtree.

Xel's Combat AI is a C# Dalamud plugin for Final Fantasy XIV. It manages a dedicated BossMod Reborn preset during combat, using BossMod IPC for movement/range/positioning strategies, Avarice shared data for positionals, and optional RotationSolver Reborn IPC for True North behavior.

## Repository Layout

- `XelsCombatAI/Plugin.cs` - composition root for Dalamud lifecycle, command registration, DTR, config save wiring, and UI setup.
- `XelsCombatAI/Config/` - persisted configuration, migrations, clamping, defaults, and reset behavior.
- `XelsCombatAI/UI/` - ImGui windows and UI-only helpers.
- `XelsCombatAI/Runtime/` - framework update orchestration, BossMod preset lifecycle, runtime cache, and status reporting.
- `XelsCombatAI/Combat/` - combat policy controllers for range, positionals, gap closers, and escape movement.
- `XelsCombatAI/Game/` - low-level game helpers, job role mapping, action IDs, constants, and geometry utilities.
- `XelsCombatAI/Integrations/` - BossMod, Avarice, RotationSolver, reflection, IPC, and dependency wrappers.
- `XelsCombatAI/Services/` - injected Dalamud service container/wrappers.
- `XelsCombatAI/Models/` - small shared enums and simple types.
- `XelsCombatAI/GlobalUsings.cs` - global imports for internal XCAI namespaces.
- `scripts/package-release.sh` - release build and zip packaging script.
- `.github/workflows/ci.yml` - build check on every PR (required status check for branch protection).
- `.github/workflows/bump-and-prerelease.yml` - bumps version on merge, pushes `v*-pre` tag.
- `.github/workflows/prerelease.yml` - builds and uploads a GitHub pre-release on `v*-pre` tags.
- `.github/workflows/promote-stable.yml` - manual `workflow_dispatch` to promote testing → stable.
- `.github/workflows/release.yml` - builds and uploads a stable GitHub release on `v*` tags (no `-pre`).
- `pluginmaster.json` - custom plugin repository metadata.
- `external/` - read-only external reference workspace. See `external/AGENTS.md`; its instructions override this file inside that directory.

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
- There is currently no test project. Prefer `dotnet build` as the minimum validation after code changes.
- Before finishing code changes, run the most relevant available validation command and report any command that could not be run.

## C# Organization

- Use standard SDK-style C# organization for this plugin: one assembly, responsibility-based folders, and file-scoped namespaces matching folder paths under `XelsCombatAI` (`XelsCombatAI.Combat`, `XelsCombatAI.Runtime`, etc.).
- Keep `Plugin.cs` as the composition root only. Do not add combat policy, IPC details, game math, or UI drawing logic to `Plugin.cs`.
- Use `internal` for implementation types by default. Add `public` only for plugin/config surface or when required by Dalamud serialization.
- Follow the existing style: nullable enabled, explicit access modifiers, `this.` for instance members, and small internal helper methods.
- Use injected `DalamudServices` or explicit service dependencies in implementation classes. Do not reference static `Plugin.*` services outside `Plugin.cs`.
- Keep runtime update work lightweight. `CombatRuntime.OnFrameworkUpdate` runs frequently and currently throttles strategy updates to 250 ms.
- Use `System.Globalization.CultureInfo.InvariantCulture` for serialized numeric IPC strings.

## Documentation Policy

- `README.md` is user-facing only: installation, requirements, commands, configuration, behavior, and user safety notes.
- Do not put architecture, contributor, build, release, or agent instructions in `README.md`.
- Put repo structure, coding standards, validation commands, and agent workflow rules in `AGENTS.md`.
- When adding or changing user-visible settings, update `UI/ConfigWindow.cs`, `Config/Configuration.cs`, defaults/migrations as needed, and the user-facing `README.md`.

## Config UI Style

- Keep the config window organized around the current tab layout: `Main`, `Positioning`, `Distance`, `Gap Closers`, and `Chat & Reset`.
- Put only broad, frequently used controls in `Main`. Keep positional behavior in `Positioning`, range and forbidden-zone behavior in `Distance`, gap closer behavior in `Gap Closers`, and feedback/reset controls in `Chat & Reset`.
- Use `DrawSectionHeader` for plain groups and `DrawToggleSectionHeader` for master toggles that own a group of dependent controls.
- Use existing UI helper methods (`Checkbox`, `Combo`, `SliderFloat`, `SliderInt`, `DrawToggleSectionHeader`) instead of drawing one-off controls, so reset behavior, disabled-state tooltips, and info icons stay consistent.
- For options whose label alone may be unclear, add a `FontAwesomeIcon.InfoCircle` info icon through the existing tooltip helper path. The icon tooltip should explain the user-visible behavior in clear, direct language.
- Do not put long explanations on the main label hover when an info icon is present. Label/control hover should remain available for disabled-state explanations and reset behavior.
- Keep tooltip text concise and scannable. Use short sentences and explicit line breaks for multi-part explanations. All config tooltips should go through the wrapped tooltip helper rather than raw `ImGui.SetTooltip`.
- Do not mention `XCAI`, "this plugin", or similar self-references in config tooltips unless the distinction is necessary to avoid ambiguity. The user already knows which plugin they are configuring.
- Use disabled-state tooltips to explain why a control is unavailable, usually by naming the controlling option and tab.
- Use skull icons only for combat-risk warnings. When a feature can put the player in danger during combat, keep the normal info icon focused on what the option does and put the risk warning on the skull icon tooltip.
- Do not make config text patronizing or alarmist. Be plain about risk, limitations, and dependencies without blaming the user.

## Integration Safety

- Keep plugin behavior conservative. The code runs during combat and writes BossMod transient strategies, so avoid broad refactors or timing changes unless the task requires them.
- Preserve IPC names, track names, option strings, preset payload module names, and BossMod preset name unless you have verified the upstream contract in `external/` or the relevant upstream project.
- Required runtime dependencies are BossMod Reborn and Avarice. Do not remove or loosen those dependency checks unless the requested behavior explicitly requires it.
- RotationSolver Reborn is optional and only required for `Manage True North`. Do not loosen this behavior unless explicitly requested.
- Do not introduce network calls or background tasks in the combat update path.
- Log recoverable integration failures with `IPluginLog.Verbose` where the plugin should keep running.
- The plugin should fully hand control back out of combat by disabling movement, resetting range/role/positional strategy state, and clearing the active preset when appropriate.
- Death/resurrection handling matters because BossMod can clear active presets on death.

## Release And Metadata

### Automated release pipeline

All changes flow through pull requests to `master`. Never commit directly to `master` after branch protection is enabled.

**PR labels** control which version component is bumped on merge:

| Label | Effect |
|-------|--------|
| `release:patch` | Bumps 4th component — default if no label |
| `release:minor` | Bumps 3rd component, zeros 4th |
| `release:major` | Bumps 2nd component, zeros 3rd+4th |
| `no-release` | Skips version bump (docs, chores) |

On merge, `bump-and-prerelease.yml` automatically updates the version files, commits them (`[skip ci]`), and pushes a `v{version}-pre` tag. `prerelease.yml` picks up the tag and publishes a GitHub pre-release — visible only to Dalamud users with **Receive plugin testing versions** enabled.

To cut a stable release, run the **Promote to Stable** workflow manually from the GitHub Actions UI. It reads the current `TestingAssemblyVersion`, updates `AssemblyVersion` and the stable download URLs in `pluginmaster.json`, then pushes a `v{version}` tag (no `-pre`) which triggers `release.yml`.

### Version file sync

The automation keeps these in sync, but if you touch them manually they must match:

- `XelsCombatAI/XelsCombatAI.csproj` → `<Version>` (build version)
- `pluginmaster.json` → `AssemblyVersion` (stable), `TestingAssemblyVersion` (testing)

Download URLs in `pluginmaster.json` must point to specific tag releases (not `/latest/`) so stable and testing can coexist independently.

When changing plugin description, tags, icon URL, name, or Dalamud API metadata, check both:

- `XelsCombatAI/XelsCombatAI.json`
- `pluginmaster.json`

`scripts/package-release.sh` writes `artifacts/XelsCombatAI.zip`. Treat `artifacts/`, `bin/`, and `obj/` as generated output.

## External References And Generated Files

- Do not edit files under ignored external checkouts in `external/BossmodReborn/`, `external/Avarice/`, or `external/RotationSolverReborn/`.
- Do not commit or package external plugin source.
- Keep external reference URLs and tracked branches in `external/sources.json`.
- External references should track the latest remote branch heads, not pinned commits.
- Use `external/fetch-sources.sh` to clone or refresh ignored reference checkouts.
- Do not hand-edit generated build output under `artifacts/`, `bin/`, or `obj/`.
