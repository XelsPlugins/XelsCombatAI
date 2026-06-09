using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal static class PositionalTrueNorthPolicy
{
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float ArrivalBufferSeconds = 0.2f;
    private const float FallbackActionAheadSeconds = 0.35f;
    private const float MinimumWalkRingOffset = 0.1f;
    private const float DistinctCandidateRadius = 0.25f;
    private const int PositionalArcSamples = 72;

    public static bool ShouldWalkInsteadOfTrueNorth(Positional requiredPositional, RsrGcdActionTimingSnapshot action, float moveDistance, out string reason)
    {
        reason = string.Empty;
        if (!PositionalDashPolicy.IsActive(requiredPositional))
        {
            return false;
        }

        if (!TryGetActionPositional(action, out var actionPositional))
        {
            reason = $"RSR next GCD {action.ActionName} is not a known positional";
            return false;
        }

        if (actionPositional != requiredPositional)
        {
            reason = $"positional intent {requiredPositional} does not match RSR next GCD {action.ActionName} {actionPositional}";
            return false;
        }

        if (!AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = "RSR GCD timing unavailable";
            return false;
        }

        var requiredSeconds = EstimateMovementSeconds(moveDistance);
        var budgetSeconds = CalculateMovementBudgetSeconds(action.GcdRemaining, action.GcdActionAhead);
        if (requiredSeconds > budgetSeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"positional move too late for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking to {requiredPositional} for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
        return true;
    }

    public static bool TryEstimateWalkDistance(
        Vector3 playerPosition,
        float playerHitboxRadius,
        Vector3 targetPosition,
        float targetHitboxRadius,
        float targetRotation,
        Positional requiredPositional,
        out float distance)
    {
        return TryEstimateWalkDistance(
            playerPosition,
            playerHitboxRadius,
            targetPosition,
            targetHitboxRadius,
            targetRotation,
            requiredPositional,
            candidateAllowed: null,
            out distance,
            out _);
    }

    public static bool TryEstimateWalkDistance(
        Vector3 playerPosition,
        float playerHitboxRadius,
        Vector3 targetPosition,
        float targetHitboxRadius,
        float targetRotation,
        Positional requiredPositional,
        Func<Vector3, bool>? candidateAllowed,
        out float distance,
        out string reason)
    {
        distance = 0f;
        reason = string.Empty;
        if (!PositionalDashPolicy.IsActive(requiredPositional))
        {
            reason = $"positional {requiredPositional} does not require movement";
            return false;
        }

        var nearest = float.PositiveInfinity;
        var blockedCandidates = 0;
        foreach (var candidate in EnumerateWalkCandidates(
            playerPosition,
            playerHitboxRadius,
            targetPosition,
            targetHitboxRadius,
            targetRotation,
            requiredPositional))
        {
            if (candidateAllowed?.Invoke(candidate) == false)
            {
                blockedCandidates++;
                continue;
            }

            nearest = MathF.Min(nearest, Geometry.Distance2D(playerPosition, candidate));
        }

        if (!float.IsFinite(nearest))
        {
            reason = blockedCandidates > 0
                ? $"no allowed {requiredPositional} walk candidate"
                : $"could not estimate walk distance for {requiredPositional}";
            return false;
        }

        distance = nearest;
        return true;
    }

    private static IEnumerable<Vector3> EnumerateWalkCandidates(
        Vector3 playerPosition,
        float playerHitboxRadius,
        Vector3 targetPosition,
        float targetHitboxRadius,
        float targetRotation,
        Positional positional)
    {
        var minRingRadius = MathF.Max(0f, targetHitboxRadius + playerHitboxRadius + MinimumWalkRingOffset);
        var maxRingRadius = minRingRadius + CombatConstants.MeleeActionRange;
        var currentRadius = Geometry.Distance2D(playerPosition, targetPosition);
        var naturalRadius = Math.Clamp(currentRadius, minRingRadius, maxRingRadius);

        foreach (var candidate in PositionalDashPolicy.EnumeratePreferredLandings(
            playerPosition,
            targetPosition,
            targetRotation,
            maxRingRadius,
            positional))
        {
            yield return candidate;
        }

        foreach (var radius in EnumerateCandidateRadii(naturalRadius, maxRingRadius))
        {
            foreach (var candidate in EnumerateArcCandidates(playerPosition, targetPosition, targetRotation, radius, positional))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<float> EnumerateCandidateRadii(float naturalRadius, float maxRingRadius)
    {
        yield return naturalRadius;
        if (MathF.Abs(naturalRadius - maxRingRadius) > DistinctCandidateRadius)
        {
            yield return maxRingRadius;
        }
    }

    private static IEnumerable<Vector3> EnumerateArcCandidates(Vector3 playerPosition, Vector3 targetPosition, float targetRotation, float radius, Positional positional)
    {
        var forward = Geometry.RotationToDirection(targetRotation);
        var right = new Vector3(forward.Z, 0f, -forward.X);
        for (var i = 0; i < PositionalArcSamples; ++i)
        {
            var angle = MathF.Tau * i / PositionalArcSamples;
            var direction = forward * MathF.Cos(angle) + right * MathF.Sin(angle);
            var candidate = targetPosition + direction * radius;
            candidate.Y = playerPosition.Y;
            if (PositionalDashPolicy.IsSatisfied(positional, candidate, targetPosition, targetRotation))
            {
                yield return candidate;
            }
        }
    }

    internal static bool TryGetActionPositional(RsrGcdActionTimingSnapshot action, out Positional positional)
    {
        return TryGetActionPositional(action.AdjustedActionId, out positional) ||
               TryGetActionPositional(action.ActionId, out positional);
    }

    internal static float CalculateMovementBudgetSeconds(float gcdRemaining, float gcdActionAhead)
    {
        var actionAhead = float.IsFinite(gcdActionAhead) && gcdActionAhead >= 0f
            ? gcdActionAhead
            : FallbackActionAheadSeconds;
        return MathF.Max(0f, gcdRemaining - actionAhead);
    }

    internal static float EstimateMovementSeconds(float moveDistance)
        => moveDistance / EstimatedCombatMoveSpeed + ArrivalBufferSeconds;

    internal static bool TryGetActionPositional(uint actionId, out Positional positional)
    {
        positional = actionId switch
        {
            56 or 3554 or 3563 or 7482 or 24382 or 36947 or 36970 or 34610 or 34611 or 34621 => Positional.Flank,
            66 or 88 or 2255 or 2258 or 3556 or 7481 or 24383 or 25772 or 36971 or 34612 or 34613 or 34622 => Positional.Rear,
            _ => Positional.Any
        };
        return positional != Positional.Any;
    }
}
