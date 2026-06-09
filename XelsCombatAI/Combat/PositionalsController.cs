using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class PositionalsController(
    Configuration config,
    DalamudServices services,
    RotationSolverIpc rotationSolver,
    RotationSolverActionReflection rotationSolverActions,
    BossModReflectionSafety bossModSafety,
    Action<Positional> setPositional,
    Action updateDtr,
    Func<AoePackPositioningStatus> aoePackStatus,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private const float MinimumCenterLandingDistance = 1.25f;
    private const float DeepInteriorHitboxFraction = 0.2f;
    private const float ComfortableSurfaceDistance = 1.2f;
    private const float PreferredPositionalBoundaryDistance = 1f;
    private const float PositionalEdgeScoreFloor = 0.35f;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ScoreLandingMethod = typeof(PositionalsController).GetMethod(nameof(ScoreLanding), BindingFlags.Static | BindingFlags.NonPublic)!;
    private bool? trueNorthStrategy;
    private PositionalMovementIntent? activeMovementIntent;
    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? lastGoalDelegate;
    private PositionalLandingPlan? lastPlan;
    private string hookState = "unresolved";

    public bool? RsrTrueNorthDisabled { get; private set; }
    public string LastIntentSource { get; private set; } = "none";
    public string LastIntentReason { get; private set; } = "not evaluated";
    public string LastTrueNorthDecisionSource { get; private set; } = "none";
    public string LastTrueNorthDecisionReason { get; private set; } = "not evaluated";

    public void Reset()
    {
        this.RsrTrueNorthDisabled = null;
        this.trueNorthStrategy = null;
        this.LastIntentSource = "none";
        this.LastIntentReason = "reset";
        this.LastTrueNorthDecisionSource = "none";
        this.LastTrueNorthDecisionReason = "reset";
        this.activeMovementIntent = null;
        this.goalZonesField = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.hookState = "unresolved";
    }

    public void Apply()
    {
        if (!config.ManagePositionals)
        {
            this.ClearMovementIntent();
            return;
        }

        if (ShouldSuppressPositionalsForAoePack(aoePackStatus()))
        {
            this.trueNorthStrategy = null;
            this.LastTrueNorthDecisionSource = "local";
            this.LastTrueNorthDecisionReason = "positionals suppressed for AoE pack";
            this.ClearMovementIntent();
            setPositional(Positional.Any);
            return;
        }

        if (config.ManageTrueNorth)
        {
            this.EnsureRsrTrueNorthDisabled();
            var positional = this.ResolvePositionalIntent();
            if (positional == Positional.Any)
            {
                this.trueNorthStrategy = null;
                this.LastTrueNorthDecisionSource = "none";
                this.LastTrueNorthDecisionReason = "no positional intent";
                this.ClearMovementIntent();
                setPositional(Positional.Any);
            }
            else
            {
                if (this.trueNorthStrategy == null)
                    this.trueNorthStrategy = this.HasActiveTrueNorth() || this.GetTrueNorthCharges() > 0;
                if (this.IsCurrentPositionalCorrect(positional))
                {
                    this.LastTrueNorthDecisionSource = "local";
                    this.LastTrueNorthDecisionReason = $"already at {positional}; holding current position";
                    this.ClearMovementIntent();
                    setPositional(Positional.Any);
                }
                else if (this.trueNorthStrategy == true)
                {
                    var shouldWalk = this.ShouldWalkForUpcomingPositional(positional, out var walkSource, out var walkReason);
                    this.LastTrueNorthDecisionSource = walkSource;
                    this.LastTrueNorthDecisionReason = walkReason;
                    if (shouldWalk)
                    {
                        this.SetMovementIntent(positional);
                        setPositional(positional);
                        return;
                    }

                    var usedTrueNorth = this.TryUseTrueNorth(positional);
                    this.LastTrueNorthDecisionReason = $"{walkReason}; {(usedTrueNorth ? "used True North" : "True North unavailable")}";
                    var pending = !this.HasActiveTrueNorth() && !this.IsOutsideMeleeRange();
                    this.ClearMovementIntent();
                    setPositional(Positional.Any);
                    if (pending && this.IsOutsideMeleeRange())
                    {
                        return;
                    }
                }
                else
                {
                    this.LastTrueNorthDecisionSource = "local";
                    this.LastTrueNorthDecisionReason = "True North unavailable; walking to positional";
                    this.SetMovementIntent(positional);
                    setPositional(positional);
                }
            }
        }
        else
        {
            this.LastTrueNorthDecisionSource = "local";
            this.LastTrueNorthDecisionReason = "Manage True North disabled";
            var positional = this.HasTrueNorthCoverage() ? Positional.Any : this.ResolvePositionalIntent();
            if (positional == Positional.Any || this.IsCurrentPositionalCorrect(positional))
            {
                this.ClearMovementIntent();
                setPositional(Positional.Any);
                return;
            }

            this.SetMovementIntent(positional);
            setPositional(positional);
        }
    }

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        _ = this.hookState;
        if (!this.TryCreateLandingPlan(hints, out var plan))
        {
            return;
        }

        if (this.lastGoalDelegate == null ||
            this.lastPlan == null ||
            !this.lastPlan.Value.SameSource(plan))
        {
            this.lastGoalDelegate = this.CreateGoalDelegate(plan);
            this.lastPlan = plan;
        }

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Uptime, "Positional landing"));
    }

    public void EnsureRsrTrueNorthDisabled()
    {
        if (this.RsrTrueNorthDisabled != null)
        {
            return;
        }

        try
        {
            if (!rotationSolver.DisableAutoTrueNorth())
            {
                this.RsrTrueNorthDisabled = false;
                updateDtr();
                return;
            }

            this.RsrTrueNorthDisabled = true;
            services.Log.Verbose("Disabled Rotation Solver Reborn Auto True North.");
        }
        catch (Exception ex)
        {
            this.RsrTrueNorthDisabled = false;
            services.Log.Verbose(ex, "Could not disable Rotation Solver Reborn Auto True North.");
            if (config.EchoStatusToChat)
            {
                services.ChatGui.Print("[Xel's Combat AI] Warning: Manage True North is enabled, but Rotation Solver Reborn Auto True North could not be disabled.");
            }
            updateDtr();
        }
    }

    public uint GetTrueNorthCharges()
    {
        try
        {
            return this.GetTrueNorthChargesUnsafe();
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not read True North charges.");
            return 0;
        }
    }

    public bool HasActiveTrueNorth()
    {
        return services.ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == ActionUse.TrueNorthStatusId && status.RemainingTime > 0) == true;
    }

    private unsafe bool TryUseTrueNorth(Positional positional)
    {
        if (positional == Positional.Any)
        {
            return false;
        }

        if (JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer) != RangeRole.Melee)
        {
            return false;
        }

        if (this.IsCurrentPositionalCorrect(positional))
        {
            return false;
        }

        if (this.HasActiveTrueNorth())
        {
            return false;
        }

        if (this.IsNinjaMudraWindow())
        {
            return false;
        }

        if (this.GetTrueNorthCharges() == 0)
        {
            return false;
        }

        if (this.IsOutsideMeleeRange())
        {
            return false;
        }

        if (rotationSolver.IsNoCasting(services.Log))
        {
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            return false;
        }

        if (services.ObjectTable.LocalPlayer?.IsCasting == true)
        {
            return false;
        }

        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, ActionUse.TrueNorthActionId) != 0)
        {
            return false;
        }

        ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.TrueNorthActionId);
        return true;
    }

    private bool ShouldWalkForUpcomingPositional(Positional positional, out string source, out string reason)
    {
        source = "local";
        reason = string.Empty;
        if (!config.ManageMovement)
        {
            reason = "movement management disabled";
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            reason = "missing player or target";
            return false;
        }

        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var action, out reason))
        {
            source = "none";
            return false;
        }

        source = action.Source;
        if (action.PrimaryTargetId != 0 && action.PrimaryTargetId != target.GameObjectId)
        {
            reason = $"RSR next GCD {action.ActionName} targets 0x{action.PrimaryTargetId:X}, not current target";
            return false;
        }

        if (!PositionalTrueNorthPolicy.TryEstimateWalkDistance(
            player.Position,
            player.HitboxRadius,
            target.Position,
            target.HitboxRadius,
            target.Rotation,
            positional,
            this.IsBossModSafeOrUnknown,
            out var moveDistance,
            out var distanceReason))
        {
            source = "local";
            reason = distanceReason;
            return false;
        }

        return PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(positional, action, moveDistance, out reason);
    }

    private bool IsBossModSafeOrUnknown(Vector3 position)
    {
        return !bossModSafety.TryIsPositionSafe(position, out var safe, out _) || safe;
    }

    private void SetMovementIntent(Positional positional)
    {
        if (!config.ManageMovement ||
            automatedMovementSuppressed() ||
            services.ObjectTable.LocalPlayer == null ||
            services.TargetManager.Target is not IBattleChara target)
        {
            this.ClearMovementIntent();
            return;
        }

        this.activeMovementIntent = new PositionalMovementIntent(positional, target.GameObjectId);
    }

    private void ClearMovementIntent()
    {
        this.activeMovementIntent = null;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
    }

    private Positional ResolvePositionalIntent()
    {
        if (this.TryReadRsrPositionalIntent(out var rsrPositional, out var rsrReason))
        {
            this.LastIntentSource = "RSR reflected";
            this.LastIntentReason = rsrReason;
            return rsrPositional;
        }

        this.LastIntentSource = "none";
        this.LastIntentReason = string.IsNullOrWhiteSpace(rsrReason)
            ? "no positional intent"
            : rsrReason;
        return Positional.Any;
    }

    private bool TryReadRsrPositionalIntent(out Positional positional, out string reason)
    {
        positional = Positional.Any;
        reason = string.Empty;
        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var action, out reason))
        {
            return false;
        }

        var target = services.TargetManager.Target;
        if (action.PrimaryTargetId != 0 && action.PrimaryTargetId != target?.GameObjectId)
        {
            reason = $"RSR next GCD {action.ActionName} targets 0x{action.PrimaryTargetId:X}, not current target";
            return false;
        }

        if (!PositionalTrueNorthPolicy.TryGetActionPositional(action, out positional))
        {
            reason = $"RSR next GCD {action.ActionName} is not a known positional";
            return false;
        }

        reason = $"RSR next GCD {action.ActionName} requires {positional}";
        return true;
    }

    private bool IsOutsideMeleeRange()
    {
        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        return Vector3.Distance(player.Position, target.Position) - player.HitboxRadius - target.HitboxRadius > CombatConstants.MeleeActionRange;
    }

    private bool IsCurrentPositionalCorrect(Positional positional)
    {
        if (positional == Positional.Any)
        {
            return true;
        }

        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        var toPlayer = player.Position - target.Position;
        toPlayer.Y = 0;
        if (toPlayer.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var frontDot = Vector3.Dot(Geometry.RotationToDirection(target.Rotation), Vector3.Normalize(toPlayer));
        return positional switch
        {
            Positional.Flank => Math.Abs(frontDot) < CombatConstants.PositionalDotThreshold,
            Positional.Rear => frontDot < -CombatConstants.PositionalDotThreshold,
            Positional.Front => frontDot > CombatConstants.PositionalDotThreshold,
            _ => true
        };
    }

    private bool HasTrueNorthCoverage()
    {
        return this.HasActiveTrueNorth() || this.GetTrueNorthCharges() > 0;
    }

    private bool IsNinjaMudraWindow()
    {
        var player = services.ObjectTable.LocalPlayer;
        return player?.ClassJob.RowId is 29 or 30 &&
               player.StatusList.Any(status =>
                   status.RemainingTime > 0f &&
                   status.StatusId is ActionUse.NinjaMudraStatusId or ActionUse.NinjaTenChiJinStatusId or ActionUse.NinjaThreeMudraStatusId);
    }

    private unsafe uint GetTrueNorthChargesUnsafe()
    {
        return ActionManager.Instance()->GetCurrentCharges(ActionUse.TrueNorthActionId);
    }

    internal static bool ShouldSuppressPositionalsForAoePack(AoePackPositioningStatus status)
    {
        return status.PriorityTargetCount >= 2 ||
               status.TrashPull.DominantTargetCount >= 2 ||
               status.TrashPull.Phase is TrashPullPhase.Gathering or TrashPullPhase.Stabilizing or TrashPullPhase.Burning;
    }

    private bool TryCreateLandingPlan(object hints, out PositionalLandingPlan plan)
    {
        plan = default;
        if (!config.Enabled ||
            !config.ManageMovement ||
            !config.ManagePositionals ||
            automatedMovementSuppressed() ||
            services.Condition[ConditionFlag.Unconscious] ||
            !CombatEngagementDetector.IsEffectivelyInCombat(services) ||
            this.activeMovementIntent is not { } intent ||
            !PositionalDashPolicy.IsActive(intent.Positional))
        {
            return false;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return false;
        }

        if (this.HasBossModMechanicGoal(hints) ||
            VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f ||
            ShouldSuppressPositionalsForAoePack(aoePackStatus()))
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null ||
            player.IsDead ||
            player.CurrentHp == 0 ||
            services.TargetManager.Target is not IBattleChara target ||
            target.GameObjectId != intent.TargetId ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            this.IsCurrentPositionalCorrect(intent.Positional))
        {
            return false;
        }

        plan = new PositionalLandingPlan(
            intent.Positional,
            target.GameObjectId,
            target.Position,
            target.Rotation,
            target.HitboxRadius,
            player.HitboxRadius);
        return true;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.resolvedWPosType != null &&
            this.goalZonesField != null &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || forcedMovement == null || forbiddenZones == null || wposType == null || xField == null || zField == null)
        {
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.wposXField = xField;
        this.wposZField = zField;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        return true;
    }

    private bool HasBossModMechanicGoal(object hints)
    {
        return this.goalZonesField?.GetValue(hints) is ICollection { Count: > 0 };
    }

    private Delegate CreateGoalDelegate(PositionalLandingPlan plan)
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var call = Expression.Call(
            ScoreLandingMethod,
            Expression.Convert(Expression.Field(parameter, this.wposXField!), typeof(float)),
            Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float)),
            Expression.Constant(plan.TargetPosition.X),
            Expression.Constant(plan.TargetPosition.Z),
            Expression.Constant(plan.TargetRotation),
            Expression.Constant(plan.TargetHitboxRadius),
            Expression.Constant(plan.PlayerHitboxRadius),
            Expression.Constant(plan.Positional));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    internal static float ScoreLanding(
        float x,
        float z,
        float targetX,
        float targetZ,
        float targetRotation,
        float targetHitboxRadius,
        float playerHitboxRadius,
        Positional positional)
    {
        if (!PositionalDashPolicy.IsActive(positional))
        {
            return 0f;
        }

        var dx = x - targetX;
        var dz = z - targetZ;
        var distance = MathF.Sqrt((dx * dx) + (dz * dz));
        if (distance <= 0.0001f)
        {
            return 0f;
        }

        var innerLimit = MathF.Max(MinimumCenterLandingDistance, targetHitboxRadius * DeepInteriorHitboxFraction);
        if (distance < innerLimit)
        {
            return 0f;
        }

        var surfaceDistance = distance - targetHitboxRadius - playerHitboxRadius;
        if (surfaceDistance > CombatConstants.MeleeActionRange)
        {
            return 0f;
        }

        var (sin, cos) = MathF.SinCos(targetRotation);
        var front = (dx * sin) + (dz * cos);
        var side = MathF.Abs((dx * cos) - (dz * sin));
        var boundaryClearance = positional switch
        {
            Positional.Flank => side - MathF.Abs(front),
            Positional.Rear => -front - side,
            Positional.Front => front - side,
            _ => 0f
        } * CombatConstants.PositionalDotThreshold;
        if (boundaryClearance <= 0f)
        {
            return 0f;
        }

        var radialScore = surfaceDistance >= ComfortableSurfaceDistance
            ? 1f
            : Math.Clamp((distance - innerLimit) / MathF.Max(0.1f, targetHitboxRadius + playerHitboxRadius + ComfortableSurfaceDistance - innerLimit), 0f, 1f);
        var angularScore = PositionalEdgeScoreFloor + ((1f - PositionalEdgeScoreFloor) * Math.Clamp(boundaryClearance / PreferredPositionalBoundaryDistance, 0f, 1f));
        return GoalZoneScorePolicy.StrongPreference * radialScore * angularScore;
    }

    private static float VectorLengthSquared(object? value)
    {
        if (value == null)
        {
            return 0f;
        }

        if (value is Vector3 vector)
        {
            return vector.LengthSquared();
        }

        var type = value.GetType();
        var x = ReadFloat(type.GetField("X", InstanceFlags)?.GetValue(value));
        var y = ReadFloat(type.GetField("Y", InstanceFlags)?.GetValue(value));
        var z = ReadFloat(type.GetField("Z", InstanceFlags)?.GetValue(value));
        return (x * x) + (y * y) + (z * z);
    }

    private static float ReadFloat(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }

    private readonly record struct PositionalMovementIntent(Positional Positional, ulong TargetId);

    private readonly record struct PositionalLandingPlan(
        Positional Positional,
        ulong TargetId,
        Vector3 TargetPosition,
        float TargetRotation,
        float TargetHitboxRadius,
        float PlayerHitboxRadius)
    {
        public bool SameSource(PositionalLandingPlan other)
        {
            return this.Positional == other.Positional &&
                   this.TargetId == other.TargetId &&
                   Vector3.DistanceSquared(this.TargetPosition, other.TargetPosition) <= 0.04f &&
                   MathF.Abs(Geometry.NormalizeRadians(this.TargetRotation - other.TargetRotation)) <= 0.02f &&
                   MathF.Abs(this.TargetHitboxRadius - other.TargetHitboxRadius) <= 0.05f &&
                   MathF.Abs(this.PlayerHitboxRadius - other.PlayerHitboxRadius) <= 0.05f;
        }
    }
}
