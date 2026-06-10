# XCAI Fight Review

Local developer CLI for combining schema v3 XCAI run-review JSONL logs with BossMod Reborn replay logs.

The review goal is to find bad movement or targeting choices that caused danger, downtime, or unhuman-like behavior across a whole duty run when available. The analyzer treats ABC, Always Be Casting, as the baseline: when BossMod-safe and job-feasible, unnecessary time unable to cast or fight is a failure to investigate. Indecisive safe-zone bouncing, walking into walls with zero momentum, and jittery movement retargeting are failures even if every individual target point was technically safe.

## Usage

```bash
dotnet run --project tools/FightReview -- \
  --xcai /path/to/xcai.jsonl \
  --bmr /path/to/bmr.log
```

```bash
dotnet run --project tools/FightReview -- \
  --xcai /path/to/xcai.jsonl \
  --bmr-dir "$HOME/.xlcore/pluginConfigs/BossModReborn/replays" \
  --auto-match
```

If `--out` is omitted, output is written beside the XCAI log in a folder named `<xcai-log-name>-review`.

Outputs include `fight.normalized.jsonl`, `fight.report.md`, `fight.html`, `incidents/*.json`, and `agent.improvement.json`. The agent packet includes run scores for uptime, safety, efficiency, human-likeness, and resource discipline. Higher scores are better; uptime is the primary positive signal.

The uptime score uses logged target range as the RSR proxy: if the player is in useful range, RSR has more legal rotation choices. Melee and tanks treat ranged fallback as a missed melee uptime window: it shows the player avoided total inactivity, but it does not score as successful melee uptime. Trash pulls score pack hit quality so positions that hit more targets are rewarded. Healers score target uptime together with visible party coverage. BMR pressure remains safety context: Normal profile movement away from mechanics is not automatically bad, while Greed profiles are rewarded for staying useful until BMR actually requires movement.

## Requirements

On Linux, set `DALAMUD_HOME` before building or running so the BossMod reference can resolve Dalamud and Lumina assemblies.

BMR replay parsing also needs FFXIV game data. The CLI checks these paths in order:

- `FFXIV_GAME_PATH`
- `XLCORE_GAME_PATH`
- `XIV_GAME_PATH`
- `$HOME/.xlcore/patch/game`
- `$HOME/Games/steam/debian-installation/steamapps/common/FINAL FANTASY XIV Online`

The selected path can be the install root containing `game/sqpack`, the game directory containing `sqpack`, or the `sqpack` directory itself.
