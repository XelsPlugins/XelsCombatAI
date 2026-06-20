using System;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Game;

internal static class CasterMovementPolicy
{
    private const float SlidecastWindowSeconds = 0.5f;
    private const float MinimumCastTimeForSlidecastSeconds = 1f;
    private const float CasterGcdReadyHoldSeconds = 0.75f;
    private const float MaximumActionAheadHoldSeconds = 1f;

    public static bool ShouldSuppressAdvisoryMovement(IBattleChara? player)
    {
        if (player == null || !player.IsCasting)
        {
            return false;
        }

        return !IsCasterSlidecastWindow(player);
    }

    public static bool ShouldSuppressAdvisoryMovementForGcd(uint classJobId, float gcdRemaining, float gcdElapsed, float gcdTotal, float gcdActionAhead)
    {
        if (JobRoles.GetRangeRole(classJobId) is not (RangeRole.MagicRanged or RangeRole.Healer))
        {
            return false;
        }

        if (!float.IsFinite(gcdRemaining) ||
            !float.IsFinite(gcdElapsed) ||
            !float.IsFinite(gcdTotal) ||
            gcdRemaining < 0f ||
            gcdElapsed < 0f ||
            gcdTotal < MinimumCastTimeForSlidecastSeconds)
        {
            return false;
        }

        var actionAheadHold = float.IsFinite(gcdActionAhead) && gcdActionAhead > 0f
            ? Math.Clamp(gcdActionAhead + 0.2f, CasterGcdReadyHoldSeconds, MaximumActionAheadHoldSeconds)
            : CasterGcdReadyHoldSeconds;
        return gcdRemaining <= actionAheadHold;
    }

    public static bool IsCasterSlidecastWindow(IBattleChara player)
    {
        if (JobRoles.GetRangeRole(player) is not (RangeRole.MagicRanged or RangeRole.Healer))
        {
            return false;
        }

        var totalCastTime = player.TotalCastTime;
        var currentCastTime = player.CurrentCastTime;
        if (!float.IsFinite(totalCastTime) ||
            !float.IsFinite(currentCastTime) ||
            totalCastTime < MinimumCastTimeForSlidecastSeconds)
        {
            return false;
        }

        var remaining = totalCastTime - currentCastTime;
        return remaining >= 0f && remaining <= SlidecastWindowSeconds;
    }
}
