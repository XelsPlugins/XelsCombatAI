using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal sealed class EnemyMovementTracker
{
    private const float MovementDistanceThreshold = 0.35f;
    private static readonly TimeSpan SampleExpiration = TimeSpan.FromSeconds(4);
    private readonly Dictionary<ulong, MovementSample> samples = [];

    public void Reset()
    {
        this.samples.Clear();
    }

    public bool ObserveMoving(ulong gameObjectId, Vector3 position, DateTime now, out string reason)
    {
        reason = string.Empty;
        if (gameObjectId == 0)
        {
            return false;
        }

        this.RemoveExpiredSamples(now);
        if (!this.samples.TryGetValue(gameObjectId, out var sample) ||
            now - sample.ObservedAtUtc > SampleExpiration ||
            now < sample.ObservedAtUtc)
        {
            this.samples[gameObjectId] = new MovementSample(position, now);
            return false;
        }

        var movementDistance = Geometry.Distance2D(position, sample.Position);
        this.samples[gameObjectId] = new MovementSample(position, now);
        if (movementDistance >= MovementDistanceThreshold)
        {
            reason = $"enemy target still moving: {movementDistance:0.0}y since last check";
            return true;
        }

        return false;
    }

    private void RemoveExpiredSamples(DateTime now)
    {
        foreach (var pair in this.samples.ToArray())
        {
            if (now - pair.Value.ObservedAtUtc > SampleExpiration)
            {
                this.samples.Remove(pair.Key);
            }
        }
    }

    private readonly record struct MovementSample(Vector3 Position, DateTime ObservedAtUtc);
}
