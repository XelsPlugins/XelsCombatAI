using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class PositionalsController(
    Configuration config,
    DalamudServices services,
    RotationSolverIpc rotationSolver,
    RotationSolverActionReflection rotationSolverActions,
    Action<Positional> setPositional,
    Action updateDtr,
    Func<AoePackPositioningStatus> aoePackStatus)
{
    private bool? trueNorthStrategy;

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
    }

    public void Apply()
    {
        if (!config.ManagePositionals)
        {
            return;
        }

        if (ShouldSuppressPositionalsForAoePack(aoePackStatus()))
        {
            this.trueNorthStrategy = null;
            this.LastTrueNorthDecisionSource = "local";
            this.LastTrueNorthDecisionReason = "positionals suppressed for AoE pack";
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
                setPositional(Positional.Any);
            }
            else
            {
                if (this.trueNorthStrategy == null)
                    this.trueNorthStrategy = this.HasActiveTrueNorth() || this.GetTrueNorthCharges() > 0;
                if (this.IsCurrentPositionalCorrect(positional))
                {
                    this.LastTrueNorthDecisionSource = "local";
                    this.LastTrueNorthDecisionReason = $"already at {positional}";
                    setPositional(positional);
                }
                else if (this.trueNorthStrategy == true)
                {
                    var shouldWalk = this.ShouldWalkForUpcomingPositional(positional, out var walkSource, out var walkReason);
                    this.LastTrueNorthDecisionSource = walkSource;
                    this.LastTrueNorthDecisionReason = walkReason;
                    if (shouldWalk)
                    {
                        setPositional(positional);
                        return;
                    }

                    var usedTrueNorth = this.TryUseTrueNorth(positional);
                    this.LastTrueNorthDecisionReason = $"{walkReason}; {(usedTrueNorth ? "used True North" : "True North unavailable")}";
                    var pending = !this.HasActiveTrueNorth() && !this.IsOutsideMeleeRange();
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
                    setPositional(positional);
                }
            }
        }
        else
        {
            this.LastTrueNorthDecisionSource = "local";
            this.LastTrueNorthDecisionReason = "Manage True North disabled";
            setPositional(this.HasTrueNorthCoverage() ? Positional.Any : this.ResolvePositionalIntent());
        }
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
            out var moveDistance))
        {
            source = "local";
            reason = $"could not estimate walk distance for {positional}";
            return false;
        }

        return PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(positional, action, moveDistance, out reason);
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
}
