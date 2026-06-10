using Dalamud.Game.ClientState.Conditions;

namespace XelsCombatAI.UI;

internal static class PluginUiVisibility
{
    public static bool ShouldHide(DalamudServices services)
    {
        return services.GameGui.GameUiHidden ||
               services.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
               services.Condition[ConditionFlag.WatchingCutscene78] ||
               services.Condition[ConditionFlag.WatchingCutscene] ||
               services.ClientState.IsGPosing;
    }
}
