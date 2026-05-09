using System;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(DalamudServices services, BossModIpc bossMod, JobRangeProvider jobRangeProvider)
{
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
}
