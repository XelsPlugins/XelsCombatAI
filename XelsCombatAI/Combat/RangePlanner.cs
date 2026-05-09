using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(DalamudServices services, BossModIpc bossMod, JobRangeProvider jobRangeProvider)
{
    private const float MeleeUptimeTolerance = 0.5f;

    public float CalculateTargetUptimeRange() => jobRangeProvider.EngagementRange;

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

    public bool ShouldDeferPartyGravityForMeleeUptime()
    {
        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target as IBattleChara;
        if (player == null ||
            target == null ||
            jobRangeProvider.EngagementRange > Configuration.InternalMeleeUptimeRange)
        {
            return false;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var targetPosition = new Vector2(target.Position.X, target.Position.Z);
        var uptimeRadius = target.HitboxRadius + Configuration.InternalMeleeUptimeRange + MeleeUptimeTolerance;
        return Vector2.DistanceSquared(playerPosition, targetPosition) > uptimeRadius * uptimeRadius;
    }
}
