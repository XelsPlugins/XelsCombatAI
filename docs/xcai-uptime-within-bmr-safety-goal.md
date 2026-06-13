# XCAI Uptime Within BMR Safety Goal

## Objective

Improve XCAI's movement and positioning choices so it preserves or recovers job uptime inside BossMod Reborn's safety envelope.

BossMod Reborn owns encounter safety: its job is to get the player to a safe place on time. XCAI should choose more useful, human-like options among BossMod-safe choices: staying in attack range, improving AoE coverage, preserving positionals, avoiding awkward trash-pack density, supporting party safety, and pre-positioning for healing coverage.

## Hard Boundaries

- Do not bypass BossMod Reborn encounter safety, forbidden zones, safe-position checks, or dash safety checks.
- Do not force unsafe movement for uptime.
- Do not automate unrelated gameplay decisions.
- Do not make reactions more perfect, instant, or mechanical than a competent human player.
- Do not broaden combat control beyond movement, facing, target/range support, or existing scoped role behavior.
- Preserve manual movement handoff and out-of-combat control release.
- Treat uncertain encounter data as a reason to skip a preference, not as permission to guess.

## Role Scenarios

### Tanks

Goal: bias safe choices toward party safety.

Examples:

- Keep boss cleaves and cone tankbusters away from visible party members when a safe tanking position exists.
- Recover trash aggro with target selection, ranged attacks, or Provoke without unnecessary walking.
- Hold sensible tanking positions instead of drifting to boss centers, frontals, or arena edges.
- If reliable encounter or telegraph data exists, pre-align large slices or half-room cleaves so the party has an easy safe side, or turn the boss to avoid most visible party members.

Bound: only use boss-facing or pre-turn logic when the direction, timing, and party positions are reliable enough to avoid making the mechanic worse.

### Melee

Goal: reduce wasted ranged GCDs and preserve melee uptime.

Examples:

- Move back into melee range when BossMod-safe space exists.
- Use safe mobility to recover melee uptime after forced movement.
- Preserve rear/flank access when the next action needs a positional.
- Avoid trash-pack positions that reduce AoE hits or create unnatural crowding.
- Prefer pack positions that allow good AoE coverage without standing at maximum enemy density.

Bound: do not chase uptime through unsafe landings, excessive target switching, oscillation, or movement that fights active BossMod mechanics.

### Casters And Physical Ranged

Goal: avoid being stranded out of attack range after mechanics.

Examples:

- If mechanics push the player out of range, look for any BossMod-safe spot, even a small one, that restores attack range.
- Keep advisory movement suppressed during hard casts except where the existing slidecast allowance applies.
- Prefer small, believable corrections over repeated micro-movement.

Bound: cast locks, manual correction, and active BossMod movement pressure take priority over uptime preferences.

### Healers

Goal: balance healing coverage and DPS uptime.

Examples:

- Pre-position for upcoming raidwide, shared, or heavy personal damage before health is already low.
- Prefer safe spots that keep party members inside AoE healing range.
- Keep DPS uptime when healing coverage is already adequate.
- Account for healing arriving in surges: future coverage can matter more than current health in some windows.

Bound: healing coverage and survival beat DPS uptime. Tanks should keep tanking positions unless healer movement is explicitly safe and valuable.

## Candidate Implementation Areas

Start by auditing existing behavior before adding new systems:

- `XelsCombatAI/Combat/RangePlanner.cs` for attack-range recovery planning.
- `XelsCombatAI/Combat/AoePackPositioningController.cs` for trash-pack density and AoE coverage.
- `XelsCombatAI/Combat/PositionalsController.cs` for melee positional preservation.
- `XelsCombatAI/Combat/GapCloserController.cs` and `XelsCombatAI/Combat/EscapeGapCloserController.cs` for safe mobility use.
- `XelsCombatAI/Combat/HealerAoePositioningController.cs`, `XelsCombatAI/Combat/PartyHealerRangePositioningController.cs`, and survivability/party-utility controllers for pre-emptive healer coverage.
- `XelsCombatAI/Combat/TankBehaviorController.cs` and facing-related controllers for party-safe tank orientation.
- `XelsCombatAI/UI/DecisionOverlayController.cs` and relevant overlay snapshots for any new player-visible decision.
- `tools/FightReview` only if new evidence or scoring fields are needed to validate the behavior.

If a named file has moved, find the current responsibility-equivalent file instead of creating a duplicate system.

## Expected Agent Workflow

1. Identify one role scenario to improve first; do not attempt all scenarios in one broad change.
2. Read the existing controller and overlay paths for that scenario.
3. Confirm where BossMod-safe candidate positions, movement pressure, target range, action timing, and party positions already enter the decision.
4. Add the smallest policy change that improves the scenario without changing unrelated role behavior.
5. Add or update focused tests where pure policy logic exists.
6. Update the decision overlay if the new decision affects movement, target choice, safety, or party utility in a way the player can act on.
7. Update README/config text only for user-visible behavior or settings.
8. Run the most relevant available validation command and report any command that cannot run.

## Completion Criteria

A completed implementation slice should demonstrate:

- BossMod Reborn remains the safety authority.
- The behavior improves uptime, coverage, party safety, or comfort only among BossMod-safe choices.
- The behavior is job-aware or role-aware where range, casts, AoE shape, healing coverage, tank facing, or mobility differs.
- The behavior avoids target churn, movement jitter, snap turning, and overly perfect reactions.
- Manual player input still pauses or weakens advisory movement as before.
- FightReview logs, debug snapshots, focused tests, or in-game observation can explain why the decision was made.

## Purpose Fit Template

Use this in the final response for any implementation based on this goal:

- Human-like behavior improved:
- BossMod Reborn authority preserved:
- Unnatural behavior risk and bound:
- Validation performed:
- In-game manual test still needed:
