# AGENTS.md

## Scope

These instructions apply to `third_party/`.

This directory contains pinned git submodules that are required for local builds. They are third-party source dependencies, not code owned by Xel's Combat AI.

## Rules

- Do not edit, format, refactor, delete, or rename files inside submodules.
- Do not copy upstream source into Xel's Combat AI.
- To update a dependency, update the submodule to the desired upstream commit and validate the plugin build.
- Use submodule source as API evidence for compile-time behavior.
- Keep refreshable reference-only upstream checkouts under `external/`, not here.

## Current Build Dependencies

- `ECommons/` is referenced by `XelsCombatAI/XelsCombatAI.csproj`.
- `BossmodReborn/` is referenced by `tools/FightReview/FightReview.csproj`.
