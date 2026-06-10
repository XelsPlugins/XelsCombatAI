# Party Intent Protocol Notes

Party intent is split across two repositories:

- Plugin client: `XelsCombatAI/Integrations/PartyIntentClient.cs`
- Rendezvous and relay server: sibling checkout `../XelsCombatAI.PartyIntent`

The server repository owns wire contracts, schemas, relay matching, WebSocket routing, claim leases, deployment notes, and protocol tests. The plugin repository owns local combat interpretation, BossMod safety checks, visible party actor mapping, cooldown/range/job validation, UI prompts, and all behavior decisions.

## Current Client Behavior

The main user setting is `Configuration.PartyIntentEnabled`, shown in the General tab. `Configuration.PartyIntentAutoRescueEnabled` is a separate healer opt-in for automatic Rescue SOS handling and is disabled by default. The server URL is static: `https://xcai.xel-serv.com`. Server failures are best-effort unavailable states and must not affect local combat behavior.

The plugin sends only blinded HMAC-derived values:

- `roomKey`
- `contextHash`
- optional `partyToken`
- `rosterHash`
- `actorKey`

Do not send raw character names, content IDs, actor IDs, world names, party IDs, territory IDs, or instance IDs.

## Rescue SOS

Rescue SOS is advisory coordination for a player who appears unable to reach BossMod-safe ground in time.

Current flow:

1. Local client sends `rescue.sos` only after local BossMod safety checks indicate current position is unsafe, walking looks too slow, and no safe escape gap-closer result is available.
2. Server broadcasts the SOS only inside the matched room.
3. Healer clients revalidate locally before prompting: healer job, Rescue availability, healer safety, visible target mapping, and Rescue range.
4. A healer client sends `rescue.claim`.
5. Server accepts the first valid claim lease for that `sosId` and broadcasts `rescue.claimed`.
6. Other healer clients suppress their advisory while the lease is active.
7. `rescue.release`, `rescue.resolved`, lease expiry, peer expiry, or room cleanup clears the advisory.

By default, the plugin only shows the Rescue advisory. If `PartyIntentAutoRescueEnabled` is enabled on a healer, that healer may automatically use Rescue only after the server confirms its local claim won and the client rechecks local safety, target visibility, range, casting state, animation lock, and Rescue availability. BossMod remains authoritative for safety, and peer/server state is never a substitute for local validation. The server must never decide whether Rescue should be used.

## Future Protocol Work

Frequent low-stakes intent such as social destacking uses direct client-to-client data channels after server rendezvous. The server only routes `peer.offer`, `peer.answer`, and `peer.ice` setup messages for direct peer connections. Use server room-wide leases only for mutually exclusive actions where duplicate responses are harmful, such as Rescue SOS or a future tank stance claim.

When changing the protocol, update both repos:

- `../XelsCombatAI.PartyIntent/protocol/`
- `../XelsCombatAI.PartyIntent/src/Xcai.PartyIntent.Protocol/`
- `../XelsCombatAI.PartyIntent/src/Xcai.PartyIntent.Server/`
- `../XelsCombatAI.PartyIntent/src/Xcai.PartyIntent.Relay/`
- `../XelsCombatAI.PartyIntent/tests/`
- `XelsCombatAI/Integrations/PartyIntentClient.cs`
