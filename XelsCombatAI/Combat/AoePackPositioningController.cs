using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameFunctions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record AoePackPositioningStatus(
    string HookState,
    string LastReason,
    string RsrStatus,
    uint ActionId,
    string ActionName,
    string Shape,
    int CurrentHits,
    int BestHits,
    bool Injected,
    bool RsrHenchedActive,
    int PriorityTargetCount);

internal sealed record AoePackOverlaySnapshot(
    uint ActionId,
    string ActionName,
    string Shape,
    Vector3 Candidate,
    Vector3 PrimaryTarget,
    float Radius,
    float HalfWidth,
    int CurrentHits,
    int BestHits,
    IReadOnlyList<AoePackOverlayTarget> Targets);

internal sealed record AoePackOverlayTarget(Vector3 Position, float Radius, bool Hit);

internal sealed class AoePackPositioningController(
    Configuration config,
    DalamudServices services,
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> automatedMovementSuppressed,
    RotationSolverIpc rotationSolverIpc,
    Func<bool> currentTargetHasBossModule)
{
    private FieldInfo? goalZonesField;
    private PropertyInfo? priorityTargetsProperty;
    private FieldInfo? enemyActorField;
    private MemberInfo? actorPositionField;
    private MemberInfo? actorHitboxRadiusField;
    private MemberInfo? actorInstanceIdField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private RsrAoeActionSnapshot? lastAction;
    private AoePackOverlaySnapshot? lastOverlay;
    private AoePackOverlaySnapshot? lastSuggestion;
    private int lastCurrentHits;
    private int lastBestHits;
    private bool lastInjected;
    private ulong lastBestPrimaryId;
    private bool rsrHenchedActive;
    private StateCommandType rsrSnapshotMode;
    private int lastPriorityTargetCount;
    private Vector2 lastInjectedCandidate;
    private int lastInjectedHits;
    private Delegate? lastInjectedGoalDelegate;
    private Vector2 pendingCandidate;
    private int pendingCandidateHits;
    private DateTime pendingCandidateSince = DateTime.MinValue;
    private Vector2 lastInjectedCentroid;
    private Delegate? lastCentroidGoalDelegate;
    private static readonly TimeSpan CandidateDebounce = TimeSpan.FromMilliseconds(300);
    private const float CandidateMovementThreshold = 0.5f;
    private const float CentroidMovementThreshold = 1f;

    public AoePackPositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        rotationSolverActions.Status,
        this.lastAction?.AdjustedActionId ?? 0,
        this.lastAction?.ActionName ?? "<none>",
        this.lastAction?.Shape.ToString() ?? "<none>",
        this.lastCurrentHits,
        this.lastBestHits,
        this.lastInjected,
        this.rsrHenchedActive,
        this.lastPriorityTargetCount);

    public AoePackOverlaySnapshot? Overlay => this.lastInjected ? this.lastOverlay : null;
    public AoePackOverlaySnapshot? SuggestedCandidate => this.lastInjected ? null : this.lastSuggestion;
    public bool RsrHenchedActive => this.rsrHenchedActive;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.priorityTargetsProperty = null;
        this.enemyActorField = null;
        this.actorPositionField = null;
        this.actorHitboxRadiusField = null;
        this.actorInstanceIdField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastAction = null;
        this.lastOverlay = null;
        this.lastSuggestion = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = 0;
        this.lastInjected = false;
        this.lastBestPrimaryId = 0;
        this.lastPriorityTargetCount = 0;
        this.lastInjectedCandidate = default;
        this.lastInjectedHits = 0;
        this.lastInjectedGoalDelegate = null;
        this.pendingCandidate = default;
        this.pendingCandidateHits = 0;
        this.pendingCandidateSince = DateTime.MinValue;
        this.lastInjectedCentroid = default;
        this.lastCentroidGoalDelegate = null;
        this.RestoreRsrIfNeeded();
        rotationSolverActions.Reset();
    }

    public void TryInjectGoal(object hints)
    {
        this.lastInjected = false;
        this.lastOverlay = null;
        if (!config.Enabled || !config.ManageAoePackPositioning)
        {
            this.lastReason = "disabled";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!config.ManageMovement)
        {
            this.lastReason = "movement management disabled";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            this.RestoreRsrIfNeeded();
            return;
        }

        var targets = this.ReadPriorityTargets(hints);
        this.lastPriorityTargetCount = targets.Count;
        var inAoeSituation = targets.Count >= config.AoePackPositioningMinimumExtraTargets + 1 &&
                             !currentTargetHasBossModule();

        var hasAoeAction = rotationSolverActions.TryGetUpcomingGcd(requirePreview: true, out var action, out var reason);
        var isTargetCenteredCircle = hasAoeAction && action.Shape == RsrAoeShape.Circle && action.Range > action.EffectRange + 3f;

        // --- Proactive AoE Combat Control ---
        // Takes over RSR targeting immediately when threshold is met, pulls toward pack centroid when out of range.
        if (config.AoePackPositioningAoeCombatControl && inAoeSituation)
        {
            this.ApplyRsrHenched();
            // Only update target if the current one is invalid — don't flip targets mid-stabilisation
            // as that resets the debounce and prevents committing to a candidate position.
            var currentIsValid = services.TargetManager.Target is IBattleNpc t &&
                                 t.StatusFlags.HasFlag(StatusFlags.InCombat) &&
                                 !t.IsDead && t.CurrentHp > 0 && t.IsHostile();
            if (!currentIsValid)
                this.UpdateTarget(this.lastBestPrimaryId);

            if ((!hasAoeAction || isTargetCenteredCircle) && targets.Count >= 2)
            {
                var localPlayer = services.ObjectTable.LocalPlayer;
                if (localPlayer != null)
                {
                    var centroid = targets.Aggregate(Vector2.Zero, (acc, t) => acc + t.Position) / targets.Count;
                    var playerPos2d = new Vector2(localPlayer.Position.X, localPlayer.Position.Z);
                    // When no AoE action is queued use the last known AoE range, or a fixed approach distance.
                    // MeleeActionRange (3y) is too tight and causes oscillation — use a wider threshold.
                    var referenceRange = hasAoeAction ? action.Range : (this.lastAction?.EffectRange ?? 8f);
                    var distToCentroid = Vector2.Distance(playerPos2d, centroid);
                    if (!isTargetCenteredCircle && distToCentroid > referenceRange + 1f)
                    {
                        var centroidGoalZones = this.goalZonesField!.GetValue(hints) as IList;
                        if (centroidGoalZones != null)
                        {
                            var acceptRadius = hasAoeAction ? Math.Max(1f, action.EffectRange) : 3f;
                            if (this.lastCentroidGoalDelegate == null ||
                                Vector2.Distance(centroid, this.lastInjectedCentroid) > CentroidMovementThreshold)
                            {
                                this.lastCentroidGoalDelegate = this.CreateCentroidGoalDelegate(centroid, acceptRadius);
                                this.lastInjectedCentroid = centroid;
                            }

                            centroidGoalZones.Add(this.lastCentroidGoalDelegate);
                            this.lastAction = hasAoeAction ? action : null;
                            this.lastReason = "closing to AoE range";
                            this.lastCurrentHits = 0;
                            this.lastBestHits = 0;
                            return;
                        }
                    }
                }
            }
        }
        else
        {
            this.RestoreRsrIfNeeded();
        }

        // --- Common early-exit for non-AoE actions ---
        if (!hasAoeAction || isTargetCenteredCircle)
        {
            this.lastAction = hasAoeAction ? action : null;
            this.lastReason = isTargetCenteredCircle ? "target-centered circle AoE skipped" : reason;
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            this.lastInjectedGoalDelegate = null;

            // While Henched with no AoE queued, keep the centroid pull active so BMR
            // doesn't fall back to range management and oscillate.
            if (this.rsrHenchedActive && targets.Count >= 2)
                this.InjectCentroidHold(hints, targets, this.lastAction);

            return;
        }

        this.lastAction = action;

        if (targets.Count < 2)
        {
            this.lastReason = "not enough BMR priority targets";
            this.lastCurrentHits = targets.Count;
            this.lastBestHits = targets.Count;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        var radius = Math.Max(1f, action.EffectRange);
        var castRange = Math.Min(Math.Max(action.Range, radius), 30f);
        var playerPos = new Vector2(player.Position.X, player.Position.Z);

        // For directional shapes try every in-combat target as primary to find the best orientation.
        // For circles the primary target only matters for target resolution, not direction.
        var primaryCandidates = action.Shape == RsrAoeShape.Circle
            ? [this.ResolvePrimaryTarget(action, targets) ?? targets[0]]
            : targets.ToArray();

        GoalPlan? bestPlan = null;
        GoalPlan.CandidateScore bestScore = default;
        int bestCurrentHits = 0;
        ulong bestPrimaryId = 0;

        foreach (var primary in primaryCandidates)
        {
            var plan = new GoalPlan(
                action.Shape,
                targets.ToArray(),
                primary,
                radius,
                castRange,
                action.HalfWidth,
                Math.Max(action.AoeCount, 1),
                config.AoePackPositioningMinimumExtraTargets);

            var currentHits = plan.ScoreHits(playerPos);
            var candidate = plan.FindBestCandidate(playerPos);

            if (bestPlan == null || candidate.Hits > bestScore.Hits ||
                (candidate.Hits == bestScore.Hits && currentHits > bestCurrentHits))
            {
                bestPlan = plan;
                bestScore = candidate;
                bestCurrentHits = currentHits;
                bestPrimaryId = primary.InstanceId;
            }
        }

        if (bestPlan == null)
        {
            this.lastReason = "primary target unavailable";
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            return;
        }

        this.lastCurrentHits = bestCurrentHits;
        this.lastBestHits = bestScore.Hits;
        this.lastBestPrimaryId = bestPrimaryId;

        if (this.lastCurrentHits >= 2 && this.lastBestHits - this.lastCurrentHits >= config.AoePackPositioningMinimumExtraTargets)
        {
            this.lastSuggestion = bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        }
        else
        {
            this.lastSuggestion = null;
        }

        if (this.lastBestHits - this.lastCurrentHits < config.AoePackPositioningMinimumExtraTargets)
        {
            this.lastReason = "no meaningful AoE improvement";
            this.pendingCandidateSince = DateTime.MinValue;
            if (config.AoePackPositioningAoeCombatControl && this.rsrHenchedActive)
            {
                this.UpdateTarget(bestPrimaryId);
                // Keep last injected delegate alive rather than dropping to centroid hold immediately —
                // transient score drops (action cycling, mob movement) should not abort an active goal.
                if (this.lastInjectedGoalDelegate != null)
                {
                    var staleZones = this.goalZonesField!.GetValue(hints) as IList;
                    staleZones?.Add(this.lastInjectedGoalDelegate);
                    this.lastInjected = true;
                    this.lastReason = "goal stable";
                }
                else
                    this.InjectCentroidHold(hints, targets, action);
            }
            else
                this.RestoreRsrIfNeeded();
            return;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.lastReason = "BMR goal zone list unavailable";
            if (config.AoePackPositioningAoeCombatControl && this.rsrHenchedActive)
            {
                this.UpdateTarget(bestPrimaryId);
                this.InjectCentroidHold(hints, targets, action);
            }
            else
                this.RestoreRsrIfNeeded();
            return;
        }

        var now = DateTime.UtcNow;
        // Always track the latest best position, but only reset the debounce timer when hit count changes.
        // Position drift within the same hit count (e.g. cone rotating slightly as mobs shuffle) is noise.
        this.pendingCandidate = bestScore.Position;
        if (bestScore.Hits != this.pendingCandidateHits)
        {
            this.pendingCandidateHits = bestScore.Hits;
            this.pendingCandidateSince = now;
        }

        var candidateStable = (now - this.pendingCandidateSince) >= CandidateDebounce;
        var candidateChanged = Vector2.Distance(this.pendingCandidate, this.lastInjectedCandidate) > CandidateMovementThreshold
                               || this.pendingCandidateHits != this.lastInjectedHits;

        if (candidateStable && candidateChanged)
        {
            this.lastInjectedGoalDelegate = bestPlan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
            this.lastInjectedCandidate = this.pendingCandidate;
            this.lastInjectedHits = this.pendingCandidateHits;
            this.lastOverlay = bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        }

        if (this.lastInjectedGoalDelegate == null)
        {
            this.lastReason = "candidate stabilising";
            return;
        }

        goalZones.Add(this.lastInjectedGoalDelegate);
        this.lastInjected = true;
        this.lastOverlay ??= bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        this.lastReason = candidateStable && candidateChanged ? "goal injected" : "goal stable";

        // Reactive RSR targeting: Henched only after a goal is injected (improvement found).
        if (!config.AoePackPositioningAoeCombatControl && config.AoePackPositioningControlRsrTarget)
        {
            this.ApplyRsrTargeting(bestPrimaryId);
        }
        else if (this.rsrHenchedActive)
        {
            // Proactive mode: update target to the new optimal primary.
            this.UpdateTarget(bestPrimaryId);
        }
    }

    private Delegate CreateCentroidGoalDelegate(Vector2 centroid, float acceptRadius = 3f)
    {
        var wposType = this.resolvedWPosType!;
        var xField = this.wposXField!;
        var zField = this.wposZField!;
        var parameter = System.Linq.Expressions.Expression.Parameter(wposType, "p");
        var xVal = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, xField), typeof(float));
        var zVal = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, zField), typeof(float));
        var dx = System.Linq.Expressions.Expression.Subtract(xVal, System.Linq.Expressions.Expression.Constant(centroid.X));
        var dz = System.Linq.Expressions.Expression.Subtract(zVal, System.Linq.Expressions.Expression.Constant(centroid.Y));
        var distSq = System.Linq.Expressions.Expression.Add(
            System.Linq.Expressions.Expression.Multiply(dx, dx),
            System.Linq.Expressions.Expression.Multiply(dz, dz));
        // Flat-top circle: score 1 within acceptRadius, 0 outside — no gradient so BMR stops moving once inside.
        var radiusSq = System.Linq.Expressions.Expression.Constant(acceptRadius * acceptRadius);
        var score = System.Linq.Expressions.Expression.Condition(
            System.Linq.Expressions.Expression.LessThanOrEqual(distSq, radiusSq),
            System.Linq.Expressions.Expression.Constant(1f),
            System.Linq.Expressions.Expression.Constant(0f));
        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        return System.Linq.Expressions.Expression.Lambda(delegateType, score, parameter).Compile();
    }

    private void ApplyRsrHenched()
    {
        if (this.rsrHenchedActive) return;
        var snapshot = rotationSolverIpc.TryGetCurrentState(services.Log);
        if (snapshot == null) return;
        // Never restore to Henched — if RSR was already Henched, restore to Auto.
        this.rsrSnapshotMode = snapshot.Value == StateCommandType.Henched ? StateCommandType.Auto : snapshot.Value;
        rotationSolverIpc.SetHenched();
        this.rsrHenchedActive = true;
    }

    private void ApplyRsrTargeting(ulong primaryId)
    {
        this.ApplyRsrHenched();
        this.UpdateTarget(primaryId);
    }

    private void UpdateTarget(ulong preferredId)
    {
        // Try preferred target first.
        if (preferredId != 0)
        {
            var preferred = services.ObjectTable.SearchById(preferredId) as IBattleNpc;
            if (preferred != null && !preferred.IsDead && preferred.CurrentHp > 0 && preferred.IsHostile())
            {
                if (services.TargetManager.Target?.GameObjectId != preferredId)
                    services.TargetManager.Target = preferred;
                return;
            }
        }

        // If the current target is already a valid hostile enemy, leave it alone.
        var currentTarget = services.TargetManager.Target as IBattleNpc;
        if (currentTarget != null &&
            currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) &&
            !currentTarget.IsDead && currentTarget.CurrentHp > 0 &&
            currentTarget.IsHostile())
        {
            return;
        }

        // Fall back to nearest hostile in-combat enemy only when current target is invalid.
        var player = services.ObjectTable.LocalPlayer;
        if (player == null) return;

        IBattleChara? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (!obj.IsHostile()) continue;
            if (!obj.StatusFlags.HasFlag(StatusFlags.InCombat)) continue;
            if (obj.IsDead || obj.CurrentHp == 0) continue;
            var dist = Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(obj.Position.X, obj.Position.Z));
            if (dist < bestDist) { best = obj; bestDist = dist; }
        }

        if (best != null)
            services.TargetManager.Target = best;
    }


    private void InjectCentroidHold(object hints, List<TargetSnapshot> targets, RsrAoeActionSnapshot? action)
    {
        if (targets.Count < 2) return;
        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null) return;
        var centroid = targets.Aggregate(Vector2.Zero, (acc, t) => acc + t.Position) / targets.Count;
        var acceptRadius = Math.Max(action?.EffectRange ?? 0f, 3f);
        if (this.lastCentroidGoalDelegate == null ||
            Vector2.Distance(centroid, this.lastInjectedCentroid) > CentroidMovementThreshold)
        {
            this.lastCentroidGoalDelegate = this.CreateCentroidGoalDelegate(centroid, acceptRadius);
            this.lastInjectedCentroid = centroid;
        }
        goalZones.Add(this.lastCentroidGoalDelegate);
        this.lastReason = "holding near pack";
    }

    private void RestoreRsrIfNeeded()
    {
        if (!this.rsrHenchedActive) return;
        rotationSolverIpc.RestoreMode(this.rsrSnapshotMode);
        this.rsrHenchedActive = false;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.priorityTargetsProperty != null &&
            this.enemyActorField != null &&
            this.actorPositionField != null &&
            this.actorHitboxRadiusField != null &&
            this.actorInstanceIdField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var priorityTargets = hintsType.GetProperty("PriorityTargets", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var enemyType = hintsType.GetNestedType("Enemy", BindingFlags.Public);
        var actorField = enemyType?.GetField("Actor", InstanceFlags);
        var actorType = actorField?.FieldType;
        var positionField = (MemberInfo?)actorType?.GetProperty("Position", InstanceFlags) ?? actorType?.GetField("Position", InstanceFlags);
        var hitboxField = (MemberInfo?)actorType?.GetField("HitboxRadius", InstanceFlags) ?? actorType?.GetProperty("HitboxRadius", InstanceFlags);
        var instanceField = (MemberInfo?)actorType?.GetField("InstanceID", InstanceFlags) ?? actorType?.GetProperty("InstanceID", InstanceFlags);
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || priorityTargets == null || wposType == null || actorField == null || positionField == null || hitboxField == null || instanceField == null || xField == null || zField == null)
        {
            this.lastReason = "BMR AoE goal reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.priorityTargetsProperty = priorityTargets;
        this.enemyActorField = actorField;
        this.actorPositionField = (MemberInfo)positionField;
        this.actorHitboxRadiusField = (MemberInfo)hitboxField;
        this.actorInstanceIdField = (MemberInfo)instanceField;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private static object? GetMemberValue(MemberInfo? member, object? obj) => member switch
    {
        FieldInfo f    => f.GetValue(obj),
        PropertyInfo p => p.GetValue(obj),
        _              => null
    };

    private List<TargetSnapshot> ReadPriorityTargets(object hints)
    {
        var result = new List<TargetSnapshot>(8);
        if (this.priorityTargetsProperty!.GetValue(hints) is not IEnumerable enemies)
        {
            return result;
        }

        foreach (var enemy in enemies)
        {
            if (enemy == null)
            {
                continue;
            }

            var actor = this.enemyActorField!.GetValue(enemy);
            if (actor == null)
            {
                continue;
            }

            var position = GetMemberValue(this.actorPositionField, actor);
            if (position == null)
            {
                continue;
            }

            var instanceId = Convert.ToUInt64(GetMemberValue(this.actorInstanceIdField, actor), System.Globalization.CultureInfo.InvariantCulture);
            var gameObj = services.ObjectTable.SearchById(instanceId) as IBattleChara;
            if (gameObj == null || !gameObj.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                continue;
            }

            result.Add(new TargetSnapshot(
                instanceId,
                new Vector2(
                    Convert.ToSingle(this.wposXField!.GetValue(position), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToSingle(this.wposZField!.GetValue(position), System.Globalization.CultureInfo.InvariantCulture)),
                Convert.ToSingle(GetMemberValue(this.actorHitboxRadiusField, actor), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private TargetSnapshot? ResolvePrimaryTarget(RsrAoeActionSnapshot action, IReadOnlyList<TargetSnapshot> targets)
    {
        foreach (var target in targets)
        {
            if (target.InstanceId == action.PrimaryTargetId)
            {
                return target;
            }
        }

        if (action.PrimaryTargetPosition != default)
        {
            return new TargetSnapshot(
                action.PrimaryTargetId,
                new Vector2(action.PrimaryTargetPosition.X, action.PrimaryTargetPosition.Z),
                action.PrimaryTargetRadius);
        }

        return targets.Count > 0 ? targets[0] : null;
    }


    private readonly record struct TargetSnapshot(ulong InstanceId, Vector2 Position, float Radius);

    private sealed class GoalPlan(
        RsrAoeShape shape,
        TargetSnapshot[] targets,
        TargetSnapshot primaryTarget,
        float radius,
        float range,
        float halfWidth,
        int aoeCount,
        int minimumExtraTargets)
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(GoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Vector2[] Directions =
        [
            new(1f, 0f),
            new(0.9238795f, 0.3826834f),
            new(0.7071068f, 0.7071068f),
            new(0.3826834f, 0.9238795f),
            new(0f, 1f),
            new(-0.3826834f, 0.9238795f),
            new(-0.7071068f, 0.7071068f),
            new(-0.9238795f, 0.3826834f),
            new(-1f, 0f),
            new(-0.9238795f, -0.3826834f),
            new(-0.7071068f, -0.7071068f),
            new(-0.3826834f, -0.9238795f),
            new(0f, -1f),
            new(0.3826834f, -0.9238795f),
            new(0.7071068f, -0.7071068f),
            new(0.9238795f, -0.3826834f)
        ];

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreFromWPosMethod,
                Expression.Field(parameter, xField),
                Expression.Field(parameter, zField));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        public int ScoreHits(Vector2 origin)
        {
            return shape switch
            {
                RsrAoeShape.Circle       => this.ScoreCircle(origin),
                RsrAoeShape.Cone         => this.ScoreCone(origin),
                RsrAoeShape.StraightLine => this.ScoreLine(origin),
                _                        => 0
            };
        }

        public CandidateScore FindBestCandidate(Vector2 playerPosition)
        {
            var best = this.ScoreHits(playerPosition);
            var bestPosition = playerPosition;
            foreach (var candidate in this.GenerateCandidates(playerPosition))
            {
                var hits = this.ScoreHits(candidate);
                if (hits > best)
                {
                    best = hits;
                    bestPosition = candidate;
                }
            }

            return new(bestPosition, best);
        }

        public AoePackOverlaySnapshot CreateOverlay(RsrAoeActionSnapshot action, Vector2 candidate, int currentHits, int bestHits, float y)
        {
            var targetMarkers = new List<AoePackOverlayTarget>(targets.Length);
            foreach (var target in targets)
            {
                targetMarkers.Add(new(
                    new Vector3(target.Position.X, y, target.Position.Y),
                    target.Radius,
                    this.TargetHit(target, candidate)));
            }

            return new(
                action.AdjustedActionId,
                action.ActionName,
                action.Shape.ToString(),
                new Vector3(candidate.X, y, candidate.Y),
                new Vector3(primaryTarget.Position.X, y, primaryTarget.Position.Y),
                radius,
                halfWidth,
                currentHits,
                bestHits,
                targetMarkers);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var hits = this.ScoreHits(new Vector2(x, z));
            var threshold = Math.Max(aoeCount, minimumExtraTargets + 1);
            return hits >= threshold ? hits * hits : 0f;
        }

        private IEnumerable<Vector2> GenerateCandidates(Vector2 playerPosition)
        {
            yield return playerPosition;
            yield return this.AverageTargets();
            foreach (var target in targets)
            {
                yield return target.Position;
            }

            var distances = new[] { Math.Min(3f, range), Math.Min(6f, range), Math.Min(radius, range), range };
            foreach (var anchor in new[] { primaryTarget.Position, this.AverageTargets() })
            {
                foreach (var distance in distances)
                {
                    if (distance <= 0.1f)
                    {
                        continue;
                    }

                    foreach (var direction in Directions)
                    {
                        yield return anchor - direction * distance;
                    }
                }
            }
        }

        private Vector2 AverageTargets()
        {
            var total = Vector2.Zero;
            foreach (var target in targets)
            {
                total += target.Position;
            }

            return targets.Length == 0 ? primaryTarget.Position : total / targets.Length;
        }

        private int ScoreCircle(Vector2 origin)
        {
            var count = 0;
            foreach (var target in targets)
            {
                if (this.TargetHit(target, origin))
                {
                    ++count;
                }
            }

            return count;
        }

        private int ScoreCone(Vector2 origin)
        {
            var toPrimary = primaryTarget.Position - origin;
            var primaryDistanceSq = toPrimary.LengthSquared();
            var effectiveRange = radius + primaryTarget.Radius;
            if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
            {
                return 0;
            }

            var direction = Vector2.Normalize(toPrimary);
            const float CosHalfAngle = 0.5f; // RSR treats cones as 60-degree half-angle.
            var count = 0;
            foreach (var target in targets)
            {
                var toTarget = target.Position - origin;
                if (this.TargetHitInCone(target, origin, direction, CosHalfAngle, toTarget))
                {
                    ++count;
                }
            }

            return count;
        }

        private int ScoreLine(Vector2 origin)
        {
            var toPrimary = primaryTarget.Position - origin;
            var primaryDistanceSq = toPrimary.LengthSquared();
            var effectiveRange = radius + primaryTarget.Radius;
            if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
            {
                return 0;
            }

            var direction = Vector2.Normalize(toPrimary);
            var count = 0;
            foreach (var target in targets)
            {
                if (this.TargetHitInLine(target, origin, direction))
                {
                    ++count;
                }
            }

            return count;
        }

        private bool TargetHit(TargetSnapshot target, Vector2 origin)
        {
            return shape switch
            {
                RsrAoeShape.Circle       => this.TargetHitInCircle(target, origin),
                RsrAoeShape.Cone         => this.TargetHitInCone(target, origin),
                RsrAoeShape.StraightLine => this.TargetHitInLine(target, origin),
                _                        => false
            };
        }

        private bool TargetHitInCircle(TargetSnapshot target, Vector2 origin)
        {
            var effective = radius + target.Radius;
            return Vector2.DistanceSquared(target.Position, origin) <= effective * effective;
        }

        private bool TargetHitInCone(TargetSnapshot target, Vector2 origin)
        {
            var toPrimary = primaryTarget.Position - origin;
            var primaryDistanceSq = toPrimary.LengthSquared();
            var effectiveRange = radius + primaryTarget.Radius;
            if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
            {
                return false;
            }

            return this.TargetHitInCone(target, origin, Vector2.Normalize(toPrimary), 0.5f, target.Position - origin);
        }

        private bool TargetHitInCone(TargetSnapshot target, Vector2 origin, Vector2 direction, float cosHalfAngle, Vector2 toTarget)
        {
            var effective = radius + target.Radius;
            if (toTarget.LengthSquared() > effective * effective)
            {
                return false;
            }

            var length = toTarget.Length();
            return length > 0.01f && Vector2.Dot(toTarget / length, direction) >= cosHalfAngle;
        }

        private bool TargetHitInLine(TargetSnapshot target, Vector2 origin)
        {
            var toPrimary = primaryTarget.Position - origin;
            var primaryDistanceSq = toPrimary.LengthSquared();
            var effectiveRange = radius + primaryTarget.Radius;
            return primaryDistanceSq > 0.01f &&
                   primaryDistanceSq <= effectiveRange * effectiveRange &&
                   this.TargetHitInLine(target, origin, Vector2.Normalize(toPrimary));
        }

        private bool TargetHitInLine(TargetSnapshot target, Vector2 origin, Vector2 direction)
        {
            var delta = target.Position - origin;
            var front = Vector2.Dot(delta, direction);
            var side = Math.Abs(delta.X * direction.Y - delta.Y * direction.X);
            return front >= 0f && front <= radius + target.Radius && side <= halfWidth + target.Radius;
        }

        public readonly record struct CandidateScore(Vector2 Position, int Hits);
    }
}
