using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal static class GapCloserDecisionPolicy
{
    private const float ConservativeTrashGapCloserRangeReserve = 2.5f;
    private const float ConservativeTrashMinimumConfidence = 0.55f;
    private const int ConservativeTrashMinimumTargets = 3;
    private const float TrashGapCloserAwayFromAnchorTolerance = 2f;
    private const float FriendlyAnchorMinimumMoveDistance = 6f;
    private const float FriendlyAnchorMinimumUptimeGain = 4f;
    private const float FriendlyAnchorMinimumTargetGain = 3f;
    private const float FriendlyAnchorMinimumTargetDirectionDot = 0.55f;
    private const float HostileRelayMinimumTargetGain = 4f;
    private const float HostileRelayMinimumDirectionDot = 0.45f;

    public static float EstimateWalkSeconds(float moveDistance)
        => PositionalTrueNorthPolicy.EstimateMovementSeconds(MathF.Max(0f, moveDistance));

    public static bool CanWalkToRangeBeforeGcd(float distanceToHitbox, float engagementRange, RsrGcdActionTimingSnapshot action, out string reason)
    {
        reason = string.Empty;
        if (distanceToHitbox <= engagementRange)
        {
            reason = $"already within {engagementRange:0.0}y engagement range";
            return true;
        }

        if (!AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = "RSR GCD timing unavailable";
            return false;
        }

        var moveDistance = MathF.Max(0f, distanceToHitbox - engagementRange);
        var requiredSeconds = EstimateWalkSeconds(moveDistance);
        var budgetSeconds = PositionalTrueNorthPolicy.CalculateMovementBudgetSeconds(action.GcdRemaining, action.GcdActionAhead);
        if (requiredSeconds <= budgetSeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"walking can reach melee for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
            return true;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking misses melee for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
        return false;
    }

    public static bool CanWalkToPositionalBeforeGcd(Positional requiredPositional, RsrGcdActionTimingSnapshot action, float moveDistance, out string reason)
    {
        return PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(requiredPositional, action, moveDistance, out reason);
    }

    public static bool CanWalkToBossModSafetyBeforeUrgency(float safeMovementDistance, BossModMovementDiagnostics movement, out string reason)
    {
        if (!TryGetBossModMovementUrgency(movement, out var urgencySeconds))
        {
            reason = "BMR movement timing unavailable";
            return false;
        }

        var requiredSeconds = EstimateWalkSeconds(safeMovementDistance);
        if (requiredSeconds <= urgencySeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"walking can reach BMR safety ({safeMovementDistance:0.0}y needs {requiredSeconds:0.0}s, {urgencySeconds:0.0}s available)");
            return true;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking misses BMR safety timing ({safeMovementDistance:0.0}y needs {requiredSeconds:0.0}s, {urgencySeconds:0.0}s available)");
        return false;
    }

    public static bool TryGetBossModMovementUrgency(BossModMovementDiagnostics movement, out float urgencySeconds)
    {
        urgencySeconds = float.MaxValue;
        var found = false;
        AddUrgency(movement.NavigationDetails.ForceMovementIn, ref urgencySeconds, ref found);
        AddUrgency(movement.NavigationDetails.LeewaySeconds, ref urgencySeconds, ref found);
        return found;
    }

    public static bool ShouldUseFriendlyAnchorDash(
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        reason = string.Empty;
        if (moveDistance < FriendlyAnchorMinimumMoveDistance)
        {
            reason = $"ally anchor too close: {moveDistance:0.0}y";
            return false;
        }

        if (safetyGain > 0.1f ||
            pathGain > 0.1f)
        {
            return true;
        }

        if (uptimeGain < FriendlyAnchorMinimumUptimeGain)
        {
            reason = $"ally anchor low uptime gain: {uptimeGain:0.0}y";
            return false;
        }

        return true;
    }

    public static bool ShouldUseFriendlyAnchorDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        if (!ShouldUseFriendlyAnchorDash(moveDistance, safetyGain, uptimeGain, pathGain, out reason))
        {
            return false;
        }

        if (!TryEvaluateFriendlyAnchorTargetProgress(
                playerPosition,
                playerRadius,
                anchorPosition,
                targetPosition,
                targetRadius,
                out var targetGain,
                out var directionDot,
                out reason))
        {
            return false;
        }

        if (targetGain < FriendlyAnchorMinimumTargetGain)
        {
            reason = $"ally anchor low target progress: {targetGain:0.0}y";
            return false;
        }

        if (directionDot < FriendlyAnchorMinimumTargetDirectionDot)
        {
            reason = $"ally anchor sideways to target: {directionDot:0.00}";
            return false;
        }

        reason = $"ally anchor gains {targetGain:0.0}y toward target; direction {directionDot:0.00}";
        return true;
    }

    public static bool ShouldAllowTrashPullGapCloserTarget(Vector3 playerPosition, Vector3 targetPosition, Vector3 anchorPosition, out string reason)
    {
        var playerAnchorDistance = Geometry.Distance2D(playerPosition, anchorPosition);
        var targetAnchorDistance = Geometry.Distance2D(targetPosition, anchorPosition);
        if (targetAnchorDistance > playerAnchorDistance + TrashGapCloserAwayFromAnchorTolerance)
        {
            reason = $"trash pull dash would move away from tank pack: target={targetAnchorDistance:0.#}y anchor, player={playerAnchorDistance:0.#}y";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool ShouldUseHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out string reason)
    {
        return TryEvaluateHostileRelayDash(
            playerPosition,
            playerRadius,
            landingPosition,
            intendedTargetPosition,
            intendedTargetRadius,
            out _,
            out _,
            out reason);
    }

    public static bool TryEvaluateHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out float gain,
        out float directionDot,
        out string reason)
    {
        var currentDistance = Geometry.DistanceToHitbox(playerPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        var landingDistance = Geometry.DistanceToHitbox(landingPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        gain = currentDistance - landingDistance;
        if (gain < HostileRelayMinimumTargetGain)
        {
            directionDot = 0f;
            reason = $"relay low target gain: {gain:0.0}y";
            return false;
        }

        if (!TryDirectionDot(playerPosition, landingPosition, intendedTargetPosition, out directionDot))
        {
            reason = "relay direction unknown";
            return false;
        }

        if (directionDot < HostileRelayMinimumDirectionDot)
        {
            reason = $"relay wrong direction: {directionDot:0.00}";
            return false;
        }

        reason = $"relay gains {gain:0.0}y; direction {directionDot:0.00}";
        return true;
    }

    private static bool TryEvaluateFriendlyAnchorTargetProgress(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        out float gain,
        out float directionDot,
        out string reason)
    {
        var currentDistance = Geometry.DistanceToHitbox(playerPosition, playerRadius, targetPosition, targetRadius);
        var anchorDistance = Geometry.DistanceToHitbox(anchorPosition, playerRadius, targetPosition, targetRadius);
        gain = currentDistance - anchorDistance;
        if (!TryDirectionDot(playerPosition, anchorPosition, targetPosition, out directionDot))
        {
            reason = "ally anchor target direction unknown";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool CanUseHostileRelayGapCloser(uint classJobId)
    {
        return classJobId is
            1 or 19 or
            3 or 21 or
            32 or
            37 or
            2 or 20 or
            4 or 22 or
            34 or
            41 or
            35 or
            40;
    }

    public static float ResolveHostileRelayGapCloserRange(uint classJobId)
    {
        return classJobId == 40
            ? 25f
            : CombatConstants.GapCloserMaxRange;
    }

    public static bool IsConservativeTrashPullContext(TrashPullDiagnostics trash)
    {
        return (trash.Phase is TrashPullPhase.Gathering or TrashPullPhase.Stabilizing or TrashPullPhase.Burning) &&
               trash.Confidence >= ConservativeTrashMinimumConfidence &&
               trash.DominantTargetCount >= ConservativeTrashMinimumTargets;
    }

    public static Vector3 ResolveTrashPullGapCloserAnchor(TrashPullDiagnostics trash, IGameObject target)
    {
        if (trash.Phase == TrashPullPhase.Gathering && trash.ProjectedTankPosition.HasValue)
        {
            return trash.ProjectedTankPosition.Value;
        }

        return trash.PackCentroid ?? target.Position;
    }

    public static float ResolveConservativeTrashGapCloserRange(uint classJobId)
    {
        return classJobId switch
        {
            25 or 40 => 25f,
            24 or 38 or 39 or 42 => CombatConstants.FixedForwardGapCloserRange,
            _ => CombatConstants.GapCloserMaxRange
        };
    }

    public static float ResolveConservativeTrashGapCloserUseThreshold(uint classJobId)
    {
        return MathF.Max(0f, ResolveConservativeTrashGapCloserRange(classJobId) - ConservativeTrashGapCloserRangeReserve);
    }

    private static bool TryDirectionDot(Vector3 from, Vector3 to, Vector3 toward, out float dot)
    {
        var movement = to - from;
        var target = toward - from;
        movement.Y = 0f;
        target.Y = 0f;
        if (movement.LengthSquared() <= 0.0001f || target.LengthSquared() <= 0.0001f)
        {
            dot = 0f;
            return false;
        }

        dot = Vector3.Dot(Vector3.Normalize(movement), Vector3.Normalize(target));
        return true;
    }

    private static void AddUrgency(float? value, ref float urgencySeconds, ref bool found)
    {
        if (!value.HasValue ||
            !float.IsFinite(value.Value) ||
            value.Value < 0f)
        {
            return;
        }

        urgencySeconds = MathF.Min(urgencySeconds, value.Value);
        found = true;
    }
}
