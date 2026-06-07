using System;
using System.Collections.Generic;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal static class PositionalDashPolicy
{
    public static bool IsActive(Positional positional)
        => positional is Positional.Rear or Positional.Flank or Positional.Front;

    public static bool IsSatisfied(Positional positional, Vector3 candidatePosition, Vector3 targetPosition, float targetRotation)
    {
        if (!IsActive(positional))
        {
            return true;
        }

        var toCandidate = candidatePosition - targetPosition;
        toCandidate.Y = 0f;
        if (toCandidate.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var frontDot = Vector3.Dot(Geometry.RotationToDirection(targetRotation), Vector3.Normalize(toCandidate));
        return positional switch
        {
            Positional.Flank => MathF.Abs(frontDot) < CombatConstants.PositionalDotThreshold,
            Positional.Rear => frontDot < -CombatConstants.PositionalDotThreshold,
            Positional.Front => frontDot > CombatConstants.PositionalDotThreshold,
            _ => true
        };
    }

    public static IEnumerable<Vector3> EnumeratePreferredLandings(
        Vector3 playerPosition,
        Vector3 targetPosition,
        float targetRotation,
        float ringRadius,
        Positional positional)
    {
        if (!IsActive(positional) || ringRadius <= 0f)
        {
            yield break;
        }

        var forward = Geometry.RotationToDirection(targetRotation);
        var right = new Vector3(forward.Z, 0f, -forward.X);
        var left = -right;

        if (positional == Positional.Rear)
        {
            yield return WithPlayerY(targetPosition - forward * ringRadius, playerPosition.Y);
            yield break;
        }

        if (positional == Positional.Front)
        {
            yield return WithPlayerY(targetPosition + forward * ringRadius, playerPosition.Y);
            yield break;
        }

        var rightLanding = WithPlayerY(targetPosition + right * ringRadius, playerPosition.Y);
        var leftLanding = WithPlayerY(targetPosition + left * ringRadius, playerPosition.Y);
        if (Geometry.Distance2D(playerPosition, rightLanding) <= Geometry.Distance2D(playerPosition, leftLanding))
        {
            yield return rightLanding;
            yield return leftLanding;
            yield break;
        }

        yield return leftLanding;
        yield return rightLanding;
    }

    private static Vector3 WithPlayerY(Vector3 position, float playerY)
    {
        position.Y = playerY;
        return position;
    }
}
