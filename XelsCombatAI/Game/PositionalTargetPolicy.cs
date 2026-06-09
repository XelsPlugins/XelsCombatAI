using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XelsCombatAI.Game;

internal static class PositionalTargetPolicy
{
    public static bool CanApplyPositionals(IBattleChara? target, IDataManager dataManager)
    {
        if (target is not IBattleNpc npc ||
            npc.BattleNpcKind != BattleNpcSubKind.Combatant ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            return false;
        }

        return !HasDirectionalDisregard(target) &&
               !IsOmnidirectional(npc, dataManager);
    }

    private static bool HasDirectionalDisregard(IBattleChara target)
        => target.StatusList.Any(status =>
            status.StatusId == ActionUse.DirectionalDisregardStatusId &&
            status.RemainingTime > 0f);

    private static bool IsOmnidirectional(IBattleNpc target, IDataManager dataManager)
        => dataManager.GetExcelSheet<BNpcBase>().TryGetRow(target.BaseId, out var npcBase) &&
           npcBase.IsOmnidirectional;
}
