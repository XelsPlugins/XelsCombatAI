# Xel's Combat AI

Small Dalamud plugin for personal BossMod Reborn combat AI helpers. The first helper mirrors the useful part of AutoDuty's passive BMR setup:

- reads Avarice shared data key `Avarice.PositionalStatus`
- maps `1` to `Rear`, `2` to `Flank`, otherwise `Any`
- pushes `BossMod.Autorotation.MiscAI.GoToPositional` transient strategy into a BMR preset
- creates and activates the `Xel's Combat AI` preset
- sets `NormalMovement.Destination = Pathfind`
- adjusts `StayCloseToTarget.range` using separate melee, physical ranged, healer, magic ranged, and role-aware AoE target-count rules
- adjusts `NormalMovement.ForbiddenZoneCushion` from a configurable preferred forbidden-zone distance, defaulting to `1.0`
- adjusts `StayCloseToPartyRole.Role` to `None` for targets with a BMR boss module and `Tank` otherwise
- exposes `/xcai` commands for toggling, status, and config

BossMod preset name: `Xel's Combat AI`.

Commands:

- `/xcai` toggles the plugin
- `/xcai on`
- `/xcai off`
- `/xcai toggle`
- `/xcai status`
- `/xcai config`

The plugin only pushes runtime BMR strategy changes while the player is in combat. Out of combat, it does not follow party members or targets and sets BMR movement to `None` when combat ends.

The config window lets users adjust the range values, forbidden-zone distance, and behavior toggles. Use `Reset ranges` to restore only distance/range values, or `Reset all` to restore the full plugin configuration.

## Custom Repository

After pushing this repo to GitHub as `xeltor/XelsCombatAI` and publishing a release with `XelsCombatAI.zip`, add this URL to Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/xeltor/XelsCombatAI/main/pluginmaster.json
```

If the GitHub owner or repository name changes, update the URLs in `pluginmaster.json` before publishing.

To build the zip locally:

```bash
scripts/package-release.sh
```
