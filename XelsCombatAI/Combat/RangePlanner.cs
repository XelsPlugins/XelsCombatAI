using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(
    DalamudServices services,
    BossModIpc bossMod,
    JobRangeProvider jobRangeProvider,
    RotationSolverActionReflection rotationSolverActions)
{
    public Func<float?> TargetUptimeRangeOverride { get; set; } = () => null;
    public string LastTargetUptimeRangeSource { get; private set; } = "none";
    public string LastTargetUptimeRangeReason { get; private set; } = "not checked";

    public float CalculateTargetUptimeRange()
    {
        var overrideRange = this.TargetUptimeRangeOverride();
        if (overrideRange.HasValue)
        {
            this.LastTargetUptimeRangeSource = "local override";
            this.LastTargetUptimeRangeReason = $"controller override range {overrideRange.Value:0.0}y";
            return overrideRange.Value;
        }

        if (!this.HasUsableHostileTarget())
        {
            this.LastTargetUptimeRangeSource = "local";
            this.LastTargetUptimeRangeReason = "no usable hostile target";
            return Configuration.InternalDisabledUptimeRange;
        }

        var rangeRole = JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer);
        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _) &&
            !action.IsFriendly)
        {
            var range = ResolveTargetUptimeRange(rangeRole, jobRangeProvider.EngagementRange, action.Range);
            this.LastTargetUptimeRangeSource = action.Source;
            this.LastTargetUptimeRangeReason = $"next GCD {action.ActionName} action range {action.Range:0.0}y -> uptime range {range:0.0}y";
            return range;
        }

        this.LastTargetUptimeRangeSource = "local";
        this.LastTargetUptimeRangeReason = $"job engagement range {jobRangeProvider.EngagementRange:0.0}y";
        return jobRangeProvider.EngagementRange;
    }

    internal static float ResolveTargetUptimeRange(RangeRole rangeRole, float jobEngagementRange, float actionRange)
    {
        var usableActionRange = actionRange > 0f
            ? Math.Clamp(actionRange, CombatConstants.MeleeActionRange, Configuration.InternalDisabledUptimeRange)
            : jobEngagementRange;

        return rangeRole == RangeRole.Melee
            ? MathF.Min(jobEngagementRange, usableActionRange)
            : usableActionRange;
    }

    public bool CurrentTargetHasBossModule()
    {
        var dataId = services.TargetManager.Target?.BaseId ?? 0;
        if (dataId == 0)
        {
            return false;
        }

        try
        {
            return bossMod.HasModuleByDataId(dataId);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not query BossMod module state yet.");
            return false;
        }
    }

    private bool HasUsableHostileTarget()
    {
        return services.TargetManager.Target is IBattleNpc target &&
               target.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.GameObjectId != 0 &&
               target.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !target.IsDead &&
               target.CurrentHp > 0;
    }
}
