using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record HealerAoePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    int PartyMembers,
    int CurrentHits,
    int BestHits);

internal sealed class HealerAoePositioningController(
    Configuration config,
    DalamudServices services,
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private int lastPartyMembers;
    private int lastCurrentHits;
    private int lastBestHits;
    private Delegate? lastGoalDelegate;
    private GoalPlan? lastPlan;
    private AoePackOverlaySnapshot? lastOverlay;

    public HealerAoePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPartyMembers,
        this.lastCurrentHits,
        this.lastBestHits);

    public AoePackOverlaySnapshot? Overlay => this.lastInjected ? this.lastOverlay : null;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastInjected = false;
        this.lastPartyMembers = 0;
        this.lastCurrentHits = 0;
        this.lastBestHits = 0;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        rotationSolverActions.Reset();
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManageHealerAoePositioning)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!config.ManageMovement)
        {
            this.lastReason = "movement management disabled";
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            return;
        }

        if (JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer) != RangeRole.Healer)
        {
            this.lastReason = "not a healer";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
            return;

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return;
        }

        var partySnapshot = PartyAllyProvider.GetVisiblePartyAllies(services, player);
        var members = partySnapshot.Members;
        this.lastPartyMembers = members.Count;

        if (members.Count < 1)
        {
            this.lastReason = "no visible party members";
            return;
        }

        // Try to use the queued AoE heal action for precise positioning.
        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _) &&
            action.IsFriendly &&
            action.Shape == RsrAoeShape.Circle)
        {
            var playerPos = new Vector2(player.Position.X, player.Position.Z);
            var targets = BuildPartySnapshots(player, members);

            // For self-centered heals (Range ~1y after clamping), primary target is the player.
            var primary = new TargetSnapshot(player.GameObjectId, playerPos, player.HitboxRadius);
            var plan = new GoalPlan(
                RsrAoeShape.Circle,
                targets,
                primary,
                action.EffectRange,
                action.EffectRange,
                0f,
                minHits: 1);

            var currentHits = plan.ScoreHits(playerPos);
            var best = plan.FindBestCandidate(playerPos);

            this.lastCurrentHits = currentHits;
            this.lastBestHits = best.Hits;

            if (best.Hits > currentHits)
            {
                if (this.lastPlan == null || !SamePlan(this.lastPlan, plan, playerPos))
                {
                    this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, currentHits);
                    this.lastPlan = plan;
                    this.lastOverlay = plan.CreateOverlay(action, best.Position, currentHits, best.Hits, player.Position.Y);
                }

                contributions.Add(new(this.lastGoalDelegate!, BossModGoalPriority.ImmediateAction, "Healer AoE"));
                this.lastInjected = true;
                this.lastReason = $"heal coverage: {currentHits} -> {best.Hits}";
                return;
            }

            this.lastReason = "already covering all reachable members";
        }
        else
        {
            this.lastReason = "no AoE heal queued";
        }
    }

    private static TargetSnapshot[] BuildPartySnapshots(IBattleChara player, IReadOnlyList<IBattleChara> members)
    {
        var snapshots = new TargetSnapshot[members.Count + 1];
        snapshots[0] = new(player.GameObjectId, new Vector2(player.Position.X, player.Position.Z), player.HitboxRadius);
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            snapshots[i + 1] = new(m.GameObjectId, new Vector2(m.Position.X, m.Position.Z), m.HitboxRadius);
        }
        return snapshots;
    }

    private static bool SamePlan(GoalPlan existing, GoalPlan candidate, Vector2 playerPos)
    {
        // Reuse the existing delegate if the candidate position hasn't shifted significantly.
        var existingBest = existing.FindBestCandidate(playerPos);
        var newBest = candidate.FindBestCandidate(playerPos);
        return existingBest.Hits == newBest.Hits &&
               Vector2.Distance(existingBest.Position, newBest.Position) < 1f;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR healer AoE reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }
}
