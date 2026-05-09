using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal readonly record struct TargetSnapshot(ulong InstanceId, Vector2 Position, float Radius);

internal sealed class GoalPlan(
    RsrAoeShape shape,
    TargetSnapshot[] targets,
    TargetSnapshot primaryTarget,
    float radius,
    float range,
    float halfWidth,
    int minHits = 2)
{
    private static readonly MethodInfo ScoreFromWPosMethod = typeof(GoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Vector2[] Directions =
    [
        new(1f, 0f),
        new(0.9238795f, 0.3826834f),
        new(0.7071068f, 0.7071068f),
        new(0.3826834f, 0.9238795f),
        new(0f, 1f),
        new(-0.3826834f, 0.9238795f),
        new(-0.7071068f, 0.7071068f),
        new(-0.9238795f, 0.3826834f),
        new(-1f, 0f),
        new(-0.9238795f, -0.3826834f),
        new(-0.7071068f, -0.7071068f),
        new(-0.3826834f, -0.9238795f),
        new(0f, -1f),
        new(0.3826834f, -0.9238795f),
        new(0.7071068f, -0.7071068f),
        new(0.9238795f, -0.3826834f)
    ];

    public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
    {
        return this.CreateGoalDelegate(wposType, xField, zField, minHits - 1);
    }

    public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField, int scoreBaselineHits)
    {
        var parameter = Expression.Parameter(wposType, "p");
        var call = Expression.Call(
            Expression.Constant(this),
            ScoreFromWPosMethod,
            Expression.Field(parameter, xField),
            Expression.Field(parameter, zField),
            Expression.Constant(scoreBaselineHits));
        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    public int ScoreHits(Vector2 origin)
    {
        return shape switch
        {
            RsrAoeShape.Circle       => this.ScoreCircle(origin),
            RsrAoeShape.Cone         => this.ScoreCone(origin),
            RsrAoeShape.StraightLine => this.ScoreLine(origin),
            _                        => 0
        };
    }

    public CandidateScore FindBestCandidate(Vector2 playerPosition)
    {
        var best = this.ScoreCandidate(playerPosition);
        var bestPosition = playerPosition;
        foreach (var candidate in this.GenerateCandidates(playerPosition))
        {
            var score = this.ScoreCandidate(candidate);
            if (score.Hits > best.Hits ||
                (score.Hits == best.Hits &&
                 Vector2.DistanceSquared(candidate, playerPosition) < Vector2.DistanceSquared(bestPosition, playerPosition)))
            {
                best = score;
                bestPosition = candidate;
            }
        }

        return best with { Position = bestPosition };
    }

    public AoePackOverlaySnapshot CreateOverlay(RsrAoeActionSnapshot action, Vector2 candidate, int currentHits, int bestHits, float y)
    {
        var targetMarkers = new List<AoePackOverlayTarget>(targets.Length);
        foreach (var target in targets)
        {
            targetMarkers.Add(new(
                new Vector3(target.Position.X, y, target.Position.Y),
                target.Radius,
                this.TargetHit(target, candidate),
                false));
        }

        return new(
            action.AdjustedActionId,
            action.ActionName,
            action.Shape.ToString(),
            new Vector3(candidate.X, y, candidate.Y),
            new Vector3(primaryTarget.Position.X, y, primaryTarget.Position.Y),
            radius,
            halfWidth,
            currentHits,
            bestHits,
            targetMarkers);
    }

    private float ScoreFromWPos(float x, float z, int scoreBaselineHits)
    {
        var pos = new Vector2(x, z);
        var hits = this.ScoreHits(pos);
        if (hits < minHits || hits <= scoreBaselineHits) return 0f;
        var maxImprovement = Math.Max(1, targets.Length - scoreBaselineHits);
        return Math.Clamp((hits - scoreBaselineHits) / (float)maxImprovement, 0f, GoalZoneScorePolicy.AoeRepositionPreference);
    }

    private CandidateScore ScoreCandidate(Vector2 position)
    {
        return new(position, this.ScoreHits(position));
    }

    private IEnumerable<Vector2> GenerateCandidates(Vector2 playerPosition)
    {
        yield return playerPosition;
        yield return this.AverageTargets();
        foreach (var target in targets)
        {
            yield return target.Position;
        }

        var distances = new[] { Math.Min(3f, range), Math.Min(6f, range), Math.Min(radius, range), range };
        foreach (var anchor in new[] { primaryTarget.Position, this.AverageTargets() })
        {
            foreach (var distance in distances)
            {
                if (distance <= 0.1f) continue;
                foreach (var direction in Directions)
                {
                    yield return anchor - direction * distance;
                }
            }
        }
    }

    private Vector2 AverageTargets()
    {
        var total = Vector2.Zero;
        foreach (var target in targets)
            total += target.Position;
        return targets.Length == 0 ? primaryTarget.Position : total / targets.Length;
    }

    private int ScoreCircle(Vector2 origin)
    {
        var count = 0;
        foreach (var target in targets)
            if (this.TargetHit(target, origin)) ++count;
        return count;
    }

    private int ScoreCone(Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange) return 0;

        var direction = Vector2.Normalize(toPrimary);
        // halfWidth is the half-angle in radians (π/3 = 60°, matching RSR's hardcoded _alpha).
        var cosHalfAngle = MathF.Cos(halfWidth);
        var count = 0;
        foreach (var target in targets)
        {
            var toTarget = target.Position - origin;
            if (this.TargetHitInCone(target, origin, direction, cosHalfAngle, toTarget)) ++count;
        }
        return count;
    }

    private int ScoreLine(Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange) return 0;

        var direction = Vector2.Normalize(toPrimary);
        var count = 0;
        foreach (var target in targets)
            if (this.TargetHitInLine(target, origin, direction)) ++count;
        return count;
    }

    private bool TargetHit(TargetSnapshot target, Vector2 origin)
    {
        return shape switch
        {
            RsrAoeShape.Circle       => this.TargetHitInCircle(target, origin),
            RsrAoeShape.Cone         => this.TargetHitInCone(target, origin),
            RsrAoeShape.StraightLine => this.TargetHitInLine(target, origin),
            _                        => false
        };
    }

    private bool TargetHitInCircle(TargetSnapshot target, Vector2 origin)
    {
        var effective = radius + target.Radius;
        return Vector2.DistanceSquared(target.Position, origin) <= effective * effective;
    }

    private bool TargetHitInCone(TargetSnapshot target, Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange) return false;
        var cosHalfAngle = MathF.Cos(halfWidth);
        return this.TargetHitInCone(target, origin, Vector2.Normalize(toPrimary), cosHalfAngle, target.Position - origin);
    }

    private bool TargetHitInCone(TargetSnapshot target, Vector2 origin, Vector2 direction, float cosHalfAngle, Vector2 toTarget)
    {
        var effective = radius + target.Radius;
        if (toTarget.LengthSquared() > effective * effective) return false;
        var length = toTarget.Length();
        return length > 0.01f && Vector2.Dot(toTarget / length, direction) >= cosHalfAngle;
    }

    private bool TargetHitInLine(TargetSnapshot target, Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        return primaryDistanceSq > 0.01f &&
               primaryDistanceSq <= effectiveRange * effectiveRange &&
               this.TargetHitInLine(target, origin, Vector2.Normalize(toPrimary));
    }

    private bool TargetHitInLine(TargetSnapshot target, Vector2 origin, Vector2 direction)
    {
        var delta = target.Position - origin;
        var front = Vector2.Dot(delta, direction);
        var side = Math.Abs(delta.X * direction.Y - delta.Y * direction.X);
        return front >= 0f && front <= radius + target.Radius && side <= halfWidth + target.Radius;
    }

    public readonly record struct CandidateScore(Vector2 Position, int Hits);
}
