# Party Intent Feature Candidates

This file lists XelsCombatAI features that can plausibly use the party-intent protocol. It is based on the current plugin options and controllers, not only earlier brainstorming.

Keep these rules stable:

- BossMod Reborn remains authoritative for encounter safety.
- Peer data is advisory unless this document explicitly calls for a short server claim lease.
- Do not send raw character names, content IDs, actor IDs, world names, party IDs, territory IDs, instance IDs, exact cooldown timelines, or combat logs.
- Use direct peer data channels for frequent movement intent.
- Use the server only for rendezvous/signaling and for mutually exclusive room-wide claim leases.
- Keep game-state interpretation in this plugin repo. Use the sibling server repo `../XelsCombatAI.PartyIntent` for wire contracts, room matching, signaling, claim leases, and server tests.

## Implemented

### Rescue SOS

Related options and code:

- `PartyIntentEnabled`
- `PartyIntentAutoRescueEnabled`
- `PartyIntentClient`

Status: implemented as advisory by default, with healer opt-in auto-action.

How it works:

- A client sends `rescue.sos` when local BossMod safety says the player is unsafe, walking appears too slow, and no safe escape gap-closer result exists.
- The server broadcasts the SOS to peers in the same blinded room.
- Healer clients revalidate locally: healer job, Rescue cooldown, healer position safe, SOS actor visible in party, target alive, and target in Rescue range.
- A healer sends `rescue.claim`.
- The server grants a short first-claim-wins lease and broadcasts `rescue.claimed`.
- Other healer clients suppress duplicate responses while the lease is active.
- The plugin shows an overlay advisory by default.
- If `PartyIntentAutoRescueEnabled` is enabled by the healer, the healer client may automatically use Rescue after winning the claim and rechecking local safety, visible target mapping, range, casting state, animation lock, and Rescue availability.

Server role:

- Room-scoped SOS fanout.
- First-claim-wins Rescue lease.
- No combat scoring, healer selection, or Rescue recommendation.

### Cooperative Social Destack

Related options and code:

- `PartyIntentEnabled`
- `ManageSocialSpacing`
- `SocialSpacingPositioningController`
- `PartyIntentDirectPeerTransport`

Status: implemented over direct peer data channels.

How it works:

- Each client publishes a short-lived `movement-intent` packet when local social spacing is already biasing away from an exact visible player stack.
- Packet contains a blinded actor key, intent kind `destack`, coarse normalized bias, strength, and expiry.
- Receivers map actor keys to visible party members and use intent only as a small local scoring penalty against choosing the same tiny offset.
- BossMod safety, current-position safety, intentional stack clumps, manual movement suppression, and the local social-spacing trigger still win.

Server role:

- Rendezvous and `peer.offer` / `peer.answer` / `peer.ice` signaling only.
- No server relay for live social destack movement intent.
- No server-side scoring or position decisions.

## High-Fit Candidates

### Tank Stance Coordination

Related options and code:

- `TankDropStanceWhenCoTankHasStance`
- `TankBehaviorController`

Status: strong candidate if both tanks use the plugin.

How it would work:

- Tank clients publish `tank.intent` with blinded actor key, local stance state, and coarse preference such as `main`, `off`, or `flexible`.
- If both tanks are eligible to toggle stance, a short server lease such as `tank.claim` can prevent both clients toggling at once.
- Local client may only act if `TankDropStanceWhenCoTankHasStance` is enabled and local visible tank status agrees.
- Avoid toggling during tankbusters, scripted swap pressure, death recovery churn, recent stance changes, or when a BossMod module indicates tank stability pressure.

Server role:

- Optional first-claim-wins lease for mutually exclusive "I am taking stance" coordination.
- No server-side MT/OT decision.

Boundaries:

- Do not auto-Provoke, auto-Shirk, or execute tank swaps from peer data.
- Peer data can reduce double-toggle behavior but must not override local visible enmity and stance state.

### Healer Coverage Coordination

Related options and code:

- `ManageHealerCoverageZone`
- `HealerAoePositioningController`
- `PartyHealerRangePositioningController`

Status: useful for reducing duplicate healer-zone movement and awkward healer clustering.

How it would work:

- Healer clients publish short-lived `healer-coverage` intent when they are already safe and selecting a party coverage point.
- DPS clients can bias toward a visible healer who has announced stable safe coverage before raidwide/shared damage.
- Healer clients can avoid both moving to the same weak coverage point if the other healer is already covering that cluster.
- A healer intent can include blinded actor key, coarse coverage center, radius bucket, covered-member count bucket, strength, and expiry.

Server role:

- Direct peer signaling only for normal advisory packets.
- Optional short claim lease only if a future feature needs exactly one healer to cover a role-specific position.

Boundaries:

- BossMod safety and local healer visibility remain required.
- Do not use peer data to stand in unsafe ground or abandon required mechanic positions.

### Defensive Ground Effect Awareness

Related options and code:

- `ManageDefensiveGroundZonePositioning`
- `SurvivabilityZonePositioningController`

Status: high-fit advisory extension for ownership/visibility gaps.

How it would work:

- The caster client publishes a `utility-zone` intent for friendly defensive ground effects such as Sacred Soil, Asylum, Earthly Star, or Collective Unconscious.
- Receivers still prefer actual local object/status detection when visible.
- Peer intent helps bridge brief visibility, ownership, or source-mapping uncertainty but does not create a trusted zone by itself.
- Intent should include blinded caster actor key, coarse zone center, radius bucket, utility type, strength, and short expiry.

Server role:

- Direct peer signaling only.
- No server relay for live zone intent unless direct transport is unavailable and the feature is explicitly downgraded to best-effort relay.

Boundaries:

- Do not move to a peer-declared zone unless local BossMod safety accepts the position.
- Do not prefer a stale or invisible zone over a locally visible placed object/status.

### Passage Of Arms Positioning

Related options and code:

- `ManagePassageOfArmsPositioning`
- `PassageOfArmsPositioningController`

Status: viable, but keep Paladin local visibility authoritative.

How it would work:

- Paladin client publishes a `utility-zone` intent with blinded actor key, cone origin, facing, radius, half-angle bucket, and short expiry when Passage of Arms is active.
- Receivers use it as an advisory to find safer positions behind the Paladin when local visibility is incomplete.
- Local object/status/facing checks override peer packets when available.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not break BossMod stack/spread requirements to chase a peer-declared cone.
- Do not trust stale facing. TTL should be very short.

### Friendly Gap-Closer Anchor Hints

Related options and code:

- `UseGapCloser`
- per-job gap closer allow-list
- `GapCloserController`
- `EscapeGapCloserController`

Status: high fit for friendly dashes, but needs tight local validation.

How it would work:

- A safe party member publishes `anchor.safe` intent with blinded actor key, coarse "safe anchor available" state, strength, and short expiry.
- Clients with friendly mobility such as Aetherial Manipulation, Icarus, Thunderclap, or Slither can prefer that visible actor if local dash safety, range, landing usefulness, and action availability validate.
- This can improve emergency escapes and re-engage movement without asking the server to decide safety.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not dash to a peer-declared anchor unless the actor is visible and local safety validates.
- Do not use this to bypass manual movement suppression, animation lock checks, or the gap-closer allow-list.

## Medium-Fit Candidates

### Shared Safe-Spot Preference

Related options and code:

- `ManageMovement`
- `CombatStyle`
- `ManageForbiddenZoneDistance`
- `PreferredForbiddenZoneDistance`
- `BossModReflectionSafety`

Status: possible, but riskier than social destack.

How it would work:

- Clients publish coarse "I am favoring this safe cluster" intent during low-pressure movement.
- Other clients use it to reduce unnatural spread across equivalent safe zones or avoid everyone selecting the same pixel.
- Use only when BossMod reports multiple safe options and no role/mechanic constraint conflicts.

Server role:

- Direct peer signaling only.

Boundaries:

- Never let peer consensus override BossMod safe zones.
- Avoid making movement look too coordinated or perfect; keep this as a weak tie-breaker.

### Party Facing During Downtime

Related options and code:

- `ManageSocialTurning`
- `FacingController`

Status: medium fit for low-pressure social polish.

How it would work:

- Clients publish a short `facing.intent` during downtime or non-mechanic windows with a coarse facing bucket and confidence.
- Receivers can use it as one weak vote when nearby party members are already lined up.
- This should only apply outside active safety movement and never during manual movement suppression.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not force perfect synchronized turning.
- Do not affect combat-facing requirements, directional dashes, or BossMod safety movement.

### AoE Pack Movement And Target Stability

Related options and code:

- `ManageAoePackPositioning`
- `PickBetterAoeTarget`
- `KeepTrashTargetSelected`
- `AoePackPositioningController`
- `TrashPullStateTracker`

Status: possible for trash quality, but lower priority.

How it would work:

- Clients publish soft pack-focus intent such as "favor centroid near this visible pack" or "I am already moving to this AoE side."
- Receivers can damp target switching or avoid overcorrecting to the same exact AoE point.
- Tank and DPS clients can independently validate local RotationSolver/BossMod/AoE scoring before using the hint.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not coordinate offensive target choice as a command.
- Keep local RotationSolver, BossMod, current target, and AoE scoring authoritative.

### Tank Front-Cone Party Avoidance

Related options and code:

- `TankKeepFrontConeAwayFromParty`
- `TankIgnoreFrontConeMovement`
- `TankBehaviorController`

Status: possible advisory extension for tank positioning.

How it would work:

- Non-tank clients publish coarse "I am holding here" position intent when safe and not moving for mechanics.
- Tank client can score tank-front cone positions with better knowledge of where plugin users intend to remain.
- Peer data should only improve the existing "hit the fewest visible party members" tie-breaker.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not let peer data cause the tank to ignore actual BossMod forbidden zones.
- `TankIgnoreFrontConeMovement` remains a local high-risk option and should not be triggered by party intent.

### Ley Lines And Starry Muse Anchor Sharing

Related options and code:

- `ManageLeylines`
- `UseBetweenTheLines`
- `UseRetrace`
- `ReturnToLeylines`
- `ManagePictomancerStarryMuse`
- `UsePictomancerStarryMuseSmudge`
- `PictomancerStarryMusePositioningController`

Status: medium fit for party awareness, not for controlling the caster.

How it would work:

- Black Mage or Pictomancer client publishes a short-lived personal anchor intent for its own Ley Lines or Starry Muse zone.
- Other clients can treat the anchor as "avoid crowding this caster's comfort zone" or "do not choose the exact same social destack offset near the caster."
- The caster still uses local object/status detection for returning to their zone.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not tell the caster where to place Ley Lines or Starry Muse.
- Do not make other players chase or stack on a caster anchor.
- Do not use peer data to activate Between the Lines, Retrace, or Smudge.

### Red Mage Melee Combo Space Awareness

Related options and code:

- `UseRedMageMeleeComboMovement`
- `RedMageMeleeComboController`

Status: medium fit as a social collision reducer.

How it would work:

- Red Mage client publishes a very short `melee-window` intent when it is locally moving into enchanted melee combo range.
- Nearby clients can avoid choosing the exact same social spacing offset if they are already resolving a low-pressure stack.
- This is primarily for reducing awkward overlap, not for letting peers influence Red Mage combat decisions.

Server role:

- Direct peer signaling only.

Boundaries:

- Do not expose mana, action plan, or RotationSolver preview data.
- Do not use peer data to trigger Red Mage movement or Displacement.

### Arena Edge And Boss-Hitbox Comfort

Related options and code:

- `AvoidArenaEdge`
- `AvoidStandingInsideEnemies`
- `ArenaEdgePositioningController`
- `BossCenterAvoidanceController`

Status: low-to-medium fit as a shared comfort tie-breaker.

How it would work:

- Clients publish coarse "I am avoiding this weak comfort area" intent when local safe options are otherwise equivalent.
- Other clients can avoid overcorrecting into the same edge or boss-center comfort fallback.

Server role:

- Direct peer signaling only.

Boundaries:

- Keep this weaker than local arena-edge, boss-hitbox, and BossMod safety scoring.
- Do not use it for exact positioning.

## Low-Fit Or Do-Not-Implement Without Explicit Review

### Automatic Rescue Expansion

Related options and code:

- `PartyIntentAutoRescueEnabled`
- `PartyIntentClient`

Status: high-risk opt-in only.

Why:

- Rescue directly moves another player and can be harmful if wrong.
- The implemented version is limited to healer opt-in after a server claim lease and strict local validation.
- Do not broaden this into server-side decision making, role-wide defaults, non-healer actions, or automatic use without local BossMod safety.

### Tank Swap Automation

Related options and code:

- `TankDropStanceWhenCoTankHasStance`
- `TankUseRangedAggroRecovery`
- `TankBehaviorController`

Status: do not implement as party intent.

Why:

- Provoke/Shirk and scripted tank swaps are encounter-specific combat decisions.
- Peer intent may help stance coordination, but should not execute swaps.
- `TankUseRangedAggroRecovery` already presses tank actions locally and must not be expanded into peer-commanded aggro control.

### Offensive Rotation Or Target Commands

Related options and code:

- `PickBetterAoeTarget`
- `KeepTrashTargetSelected`
- `UseRedMageMeleeComboMovement`
- RotationSolver-derived action context

Status: out of scope.

Why:

- Party intent should not command offensive action choice, target priority, or rotation state.
- It may provide weak movement or collision hints, but local RotationSolver/BossMod logic must remain authoritative.

### Server-Side Combat Decisions

Status: out of scope.

Why:

- The server must not know jobs, positions, cooldowns, BossMod safety state, encounter mechanics, or action plans.
- Server responsibilities are room matching, direct peer signaling, relay fallback only when explicitly accepted for a feature, and short opaque claim leases.

### Fight Review Streaming

Related options and code:

- `FightReviewLoggingEnabled`
- `CombatLogWriter`

Status: do not implement through party intent.

Why:

- Run-review logs are high-volume diagnostic data, not live party intent.
- Streaming them would violate the "tiny short-lived intent packets only" boundary.

## Implementation Checklist For New Party Intent Features

Before adding a feature:

- Tie it to an existing plugin option/controller or explicitly add a new option with docs.
- Decide whether it is direct peer advisory data or a mutually exclusive room-wide claim.
- Add or update protocol docs and schemas in `../XelsCombatAI.PartyIntent`.
- Add relay tests for matching, rate limits, and claim lease behavior when applicable.
- Keep raw game identifiers out of the wire format.
- Add plugin-side local validation before using peer data.
- Add debug snapshot fields for observability.
- Keep frequent movement intent off the server data path.
- Keep the only broad user-facing party intent setting in the General tab unless explicitly requested otherwise.
- Validate the plugin and server repos when code changes are made.
