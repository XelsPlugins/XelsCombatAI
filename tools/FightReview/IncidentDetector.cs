namespace FightReview;

internal static class IncidentDetector
{
    private const float StrongBossHpOutlierMultiplier = 18f;
    private const float ExtremeBossHpOutlierMultiplier = 40f;
    private const uint MinimumComparableNpcHp = 1000;

    public static IReadOnlyList<Incident> Detect(XcaiLog log)
    {
        var incidents = new List<Incident>();
        DetectDestinationChurn(log, incidents);
        DetectIndecisiveOscillation(log, incidents);
        DetectStuckMovement(log, incidents);
        DetectSafetyRasterIssues(log, incidents);
        DetectBmrExitWallRisks(log, incidents);
        DetectVnavmeshPathingIssues(log, incidents);
        DetectSlowPackFollow(log, incidents);
        DetectMovementJitter(log, incidents);
        DetectBmrConflicts(log, incidents);
        DetectRangeFailures(log, incidents);
        DetectTrashPackLateEngagement(log, incidents);
        DetectTrashPullCognitionIssues(log, incidents);
        DetectRouteMemoryIssues(log, incidents);
        DetectTrashAoeOpportunities(log, incidents);
        DetectPersistentEdgeHugging(log, incidents);
        DetectAwkwardDestinations(log, incidents);
        DetectScoringAnomalies(log, incidents);
        DetectManualCorrectionTakeovers(log, incidents);
        DetectManualHandoffIssues(log, incidents);
        return incidents
            .OrderBy(i => i.TimestampUtc)
            .ThenBy(i => i.Category, StringComparer.Ordinal)
            .ToArray();
    }

    private static void DetectDestinationChurn(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 4f;
        const float destinationChangeDistance = 1.25f;
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || frame.Planner.Destination == null)
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var changes = 0;
            var previous = frame.Planner.Destination;
            for (var j = i + 1; j <= end; j++)
            {
                var current = log.Frames[j].Planner.Destination;
                if (current != null && previous != null && Vec3.Distance2D(current, previous) >= destinationChangeDistance)
                {
                    changes++;
                    previous = current;
                }
            }

            if (changes >= 4)
            {
                incidents.Add(NewIncident(
                    "destination-churn",
                    frame,
                    "medium",
                    $"Planner destination changed {changes} times within {windowSeconds:0}s.",
                    "Increase hold time or destination stickiness so non-defensive movement does not disrupt ABC uptime.",
                    i,
                    end));
                nextAllowedT = log.Frames[end].T + windowSeconds;
            }
        }
    }

    private static void DetectIndecisiveOscillation(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 5f;
        const float sameZoneDistance = 1f;
        const float differentZoneDistance = 2f;
        var nextAllowedT = 0f;

        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || !HasActiveDestination(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var sequence = DestinationChanges(log.Frames, i, end, sameZoneDistance).ToArray();
            if (sequence.Length < 5)
            {
                continue;
            }

            var zoneA = sequence[0].Destination;
            Vec3? zoneB = null;
            var alternations = 0;
            var lastZone = 0;

            foreach (var (_, destination) in sequence)
            {
                var zone = 0;
                if (Vec3.Distance2D(destination, zoneA) <= sameZoneDistance)
                {
                    zone = 1;
                }
                else if (zoneB == null)
                {
                    if (Vec3.Distance2D(destination, zoneA) < differentZoneDistance)
                    {
                        continue;
                    }

                    zoneB = destination;
                    zone = 2;
                }
                else if (Vec3.Distance2D(destination, zoneB) <= sameZoneDistance)
                {
                    zone = 2;
                }
                else
                {
                    zone = 0;
                }

                if (zone is 1 or 2 && lastZone is 1 or 2 && zone != lastZone)
                {
                    alternations++;
                }

                if (zone is 1 or 2)
                {
                    lastZone = zone;
                }
            }

            if (zoneB != null && alternations >= 4)
            {
                incidents.Add(NewIncident(
                    "indecisive-oscillation",
                    frame,
                    "high",
                    $"Planner alternated between two destination zones {alternations} times within {windowSeconds:0}s.",
                    "Add tie-breaking, hysteresis, or source priority so safe-zone choice settles instead of bouncing and disrupting ABC.",
                    i,
                    end));
                nextAllowedT = log.Frames[end].T + windowSeconds;
            }
        }
    }

    private static void DetectStuckMovement(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var stuck = HasActiveDestination(frame) &&
                        DistanceToDestination(frame) > MathF.Max(frame.Planner.AcceptanceRadius ?? 0.5f, 0.5f) + 1f &&
                        IsNearZeroMomentum(frame);

            if (stuck && start < 0)
            {
                start = i;
            }
            else if (!stuck && start >= 0)
            {
                AddStuckMovementIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddStuckMovementIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddStuckMovementIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 1.5f)
        {
            return;
        }

        var averageStep = log.Frames
            .Skip(start)
            .Take(end - start + 1)
            .Select(frame => frame.Motion.PlayerStepDistance ?? 0f)
            .DefaultIfEmpty(0f)
            .Average();
        var frame = log.Frames[start];
        var defensiveZoneOvercommit = IsDefensiveZoneSource(frame);
        var safetyEvidence = BuildSafetyEvidence(frame);
        incidents.Add(NewIncident(
            defensiveZoneOvercommit ? "defensive-zone-overcommit" : "movement-stuck",
            frame,
            "high",
            defensiveZoneOvercommit
                ? $"Defensive-zone movement kept a target for {(log.Frames[end].T - frame.T):0.0}s while average frame movement was {averageStep:0.00}y.{safetyEvidence}"
                : $"Planner kept a movement target for {(log.Frames[end].T - frame.T):0.0}s while average frame movement was {averageStep:0.00}y.{safetyEvidence}",
            defensiveZoneOvercommit
                ? "Treat being inside a defensive ground zone as sufficient; avoid pulling toward the zone center when that costs movement or melee uptime."
                : "Detect zero-momentum pathing and replan or release movement so wall-walking does not cost safe ABC uptime.",
            start,
            end));
    }

    private static void DetectSafetyRasterIssues(XcaiLog log, List<Incident> incidents)
    {
        DetectSafetyBlockedDestinations(log, incidents);
        DetectSafetyBlockedRoutes(log, incidents);
        DetectSafetyBoundaryStuck(log, incidents);
    }

    private static void DetectBmrExitWallRisks(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowed ||
                IsManualSuppressed(frame) ||
                !HasBmrSafetyPressure(frame) ||
                !TryDescribeBmrExitWallRisk(frame, out var evidence))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "bmr-exit-wall-risk",
                frame,
                "high",
                $"BMR safety movement appears to aim the shortest mechanic exit into blocked or boundary geometry. {evidence}{BuildSafetyEvidence(frame)}",
                "Use BMR raster/bounds evidence to identify shortest-exit wall bumps and prefer a nearby safe inward recovery point once BossMod movement authority allows it.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowed = frame.T + 6f;
        }
    }

    private static bool TryDescribeBmrExitWallRisk(XcaiFrame frame, out string evidence)
    {
        evidence = string.Empty;
        var raster = frame.BossMod.SafetyRaster;
        if (!raster.Status.Equals("captured", StringComparison.Ordinal))
        {
            return false;
        }

        if (SafetyRasterCodec.IsHardBlocked(raster.Destination.State))
        {
            evidence = $"BMR destination was {raster.Destination.State}.";
            return true;
        }

        if (SafetyRasterCodec.IsHardBlocked(raster.FirstWaypoint.State))
        {
            evidence = $"BMR first waypoint was {raster.FirstWaypoint.State}.";
            return true;
        }

        if (raster.Destination.Position != null &&
            TryFindBlockedSafetyRoute(frame, raster.Destination.Position, out var state, out var blockedDistance))
        {
            evidence = $"Route from player to BMR destination crossed a raster {state} cell about {blockedDistance:0.0}y from the player.";
            return true;
        }

        if (raster.FirstWaypoint.Position != null &&
            TryFindBlockedSafetyRoute(frame, raster.FirstWaypoint.Position, out state, out blockedDistance))
        {
            evidence = $"Route from player to BMR first waypoint crossed a raster {state} cell about {blockedDistance:0.0}y from the player.";
            return true;
        }

        var endpoint = raster.FirstWaypoint.Position ?? raster.Destination.Position;
        if (endpoint != null &&
            LooksNearArenaEdge(frame, endpoint) &&
            (IsNearZeroMomentum(frame) || IsBmrMovementOverrideBlocked(frame)))
        {
            evidence = "BMR movement endpoint was near the pathfinding boundary while movement showed blocked or near-zero progress.";
            return true;
        }

        return false;
    }

    private static void DetectSafetyBlockedDestinations(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var destinationState = frame.BossMod.SafetyRaster.Destination.State;
            if (frame.T < nextAllowed ||
                !HasActiveDestination(frame) ||
                IsManualSuppressed(frame) ||
                !SafetyRasterCodec.IsHardBlocked(destinationState))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "safety-blocked-destination",
                frame,
                "high",
                $"Planner destination from {frame.Planner.ChosenSource} was classified by BMR pathfinding as {destinationState}.{BuildSafetyEvidence(frame)}",
                "Reject or re-score destinations that BMR's pathfinding map marks blocked or actively dangerous so movement does not choose wall, boundary, or no-go targets.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowed = frame.T + 6f;
        }
    }

    private static void DetectSafetyBlockedRoutes(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowed ||
                !HasActiveDestination(frame) ||
                IsManualSuppressed(frame) ||
                !TryFindBlockedSafetyRoute(frame, out var state, out var blockedDistance))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "safety-blocked-route",
                frame,
                "high",
                $"Planner route from player to {frame.Planner.ChosenSource} destination crossed a BMR raster {state} cell about {blockedDistance:0.0}y from the player.{BuildSafetyEvidence(frame)}",
                "Reject or reroute destinations whose immediate path crosses BMR blocked/no-go cells, even when the destination itself is safe and vnavmesh reports reachability.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowed = frame.T + 6f;
        }
    }

    private static void DetectSafetyBoundaryStuck(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var stuck = HasActiveDestination(frame) &&
                        !IsManualSuppressed(frame) &&
                        IsNearZeroMomentum(frame) &&
                        HasHardSafetyBlock(frame);
            if (stuck && start < 0)
            {
                start = i;
            }
            else if (!stuck && start >= 0)
            {
                AddSafetyBoundaryStuckIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddSafetyBoundaryStuckIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddSafetyBoundaryStuckIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 1.0f)
        {
            return;
        }

        var frame = log.Frames[start];
        incidents.Add(NewIncident(
            "safety-boundary-stuck",
            frame,
            "high",
            $"Movement had near-zero momentum while the safety raster showed a blocked/no-go point in the active route.{BuildSafetyEvidence(frame)}",
            "When movement has no progress against a BMR blocked/no-go boundary, replan to the nearest safe traversable point or release the destination before it becomes wall-walking.",
            start,
            end));
    }

    private static void DetectVnavmeshPathingIssues(XcaiLog log, List<Incident> incidents)
    {
        DetectVnavmeshDetours(log, incidents);
        DetectVnavmeshOffmeshDestinations(log, incidents);
        DetectVnavmeshQueryStalls(log, incidents);
        DetectVnavmeshReachableStuck(log, incidents);
    }

    private static void DetectVnavmeshDetours(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowed ||
                !HasActiveDestination(frame) ||
                IsManualSuppressed(frame) ||
                !PlannerPathIsReachable(frame) ||
                frame.Planner.PathDetourRatio is not { } ratio ||
                frame.Planner.ExtraPathDistance is not { } extra ||
                ratio < 1.75f ||
                extra < 8f)
            {
                continue;
            }

            incidents.Add(NewIncident(
                "vnavmesh-detour",
                frame,
                "medium",
                $"Chosen {frame.Planner.ChosenSource} destination required a vnavmesh detour: direct={frame.Planner.DirectDistance:0.0}y, path={frame.Planner.PathDistance:0.0}y, extra={extra:0.0}y, ratio={ratio:0.00}.",
                "Prefer a closer reachable destination or re-score high-detour candidates so safe movement does not look like wall routing or awkward overtravel.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowed = frame.T + 6f;
        }
    }

    private static void DetectVnavmeshOffmeshDestinations(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowed ||
                IsManualSuppressed(frame) ||
                frame.Planner.GeneratedCount <= 0 ||
                !HasVnavmeshOffmeshDestination(frame, out var distance))
            {
                continue;
            }

            var probe = frame.Planner.VnavmeshDestination!;
            incidents.Add(NewIncident(
                "vnavmesh-offmesh-destination",
                frame,
                "high",
                $"Planner probe for {frame.Planner.VnavmeshProbeSource} landed away from usable vnavmesh surface: status={probe.Status}, nearest reachable distance={(distance.HasValue ? $"{distance.Value:0.0}y" : "<none>")}.",
                "Snap or reject movement destinations that are far from reachable navmesh so chosen points do not send movement into walls, void edges, or blocked collision.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowed = frame.T + 6f;
        }
    }

    private static void DetectVnavmeshQueryStalls(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var pending = IsVnavmeshQueryPending(log.Frames[i]);
            if (pending && start < 0)
            {
                start = i;
            }
            else if (!pending && start >= 0)
            {
                AddVnavmeshQueryStallIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddVnavmeshQueryStallIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddVnavmeshQueryStallIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 1.5f)
        {
            return;
        }

        var span = log.Frames.Skip(start).Take(end - start + 1).ToArray();
        var pendingFrames = span.Count(IsVnavmeshQueryPending);
        if (pendingFrames < 4)
        {
            return;
        }

        var queuedAverage = span
            .Select(frame => frame.Planner.Vnavmesh.PathfindQueued)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Average();
        var frame = log.Frames[start];
        incidents.Add(NewIncident(
            "vnavmesh-query-stall",
            frame,
            "medium",
            $"vnavmesh path queries remained pending for {(log.Frames[end].T - frame.T):0.0}s across {pendingFrames}/{span.Length} frames; queued average={queuedAverage:0.0}.",
            "Keep a stable previous destination or bounded fallback while path queries are pending so query latency does not cause no-candidate movement stalls or jitter.",
            start,
            end));
    }

    private static void DetectVnavmeshReachableStuck(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var stuck = HasActiveDestination(frame) &&
                        PlannerPathIsReachable(frame) &&
                        frame.Planner.FirstWaypointDistance is > 0.75f &&
                        DistanceToDestination(frame) > MathF.Max(frame.Planner.AcceptanceRadius ?? 0.5f, 0.5f) + 1f &&
                        IsNearZeroMomentum(frame);
            if (stuck && start < 0)
            {
                start = i;
            }
            else if (!stuck && start >= 0)
            {
                AddVnavmeshReachableStuckIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddVnavmeshReachableStuckIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddVnavmeshReachableStuckIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 1.5f)
        {
            return;
        }

        var span = log.Frames.Skip(start).Take(end - start + 1).ToArray();
        var waypointDistance = span
            .Select(frame => frame.Planner.FirstWaypointDistance)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0f)
            .Average();
        var frame = log.Frames[start];
        incidents.Add(NewIncident(
            "vnavmesh-reachable-stuck",
            frame,
            "high",
            $"vnavmesh reported a reachable path, but movement had near-zero momentum for {(log.Frames[end].T - frame.T):0.0}s; average first-waypoint distance was {waypointDistance:0.0}y.",
            "Detect reachable-but-not-progressing movement and replan, snap to a reachable point, or release movement before it becomes wall-walking.",
            start,
            end));
    }

    private static void DetectSlowPackFollow(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 4f;
        var normalSpeed = EstimateNormalMovementSpeed(log);
        var slowThreshold = Math.Clamp(normalSpeed * 0.55f, 2.4f, 3.3f);
        var nextAllowedT = 0f;
        for (var i = 1; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || !IsPackFollowEligible(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var eligible = Enumerable.Range(i, end - i + 1)
                .Where(index => IsPackFollowEligible(log.Frames[index]))
                .ToArray();
            if (eligible.Length < 4)
            {
                continue;
            }

            var slow = eligible
                .Where(index => IsSlowMovementSpeed(log.Frames[index], slowThreshold))
                .ToArray();
            if (slow.Length < 4 || slow.Length < eligible.Length * 0.6f)
            {
                continue;
            }

            AddSlowPackFollow(log, incidents, slow[0], slow[^1], normalSpeed, slowThreshold);
            nextAllowedT = log.Frames[slow[^1]].T + windowSeconds;
        }
    }

    private static void AddSlowPackFollow(XcaiLog log, List<Incident> incidents, int start, int end, float normalSpeed, float slowThreshold)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 2f)
        {
            return;
        }

        var span = log.Frames
            .Skip(start)
            .Take(end - start + 1)
            .Where(IsPackFollowEligible)
            .ToArray();
        var speeds = span
            .Select(frame => frame.Motion.PlayerSpeed)
            .Where(speed => speed.HasValue)
            .Select(speed => speed!.Value)
            .ToArray();
        if (speeds.Length == 0 || speeds.Average() <= 0.25f || speeds.Average() >= slowThreshold)
        {
            return;
        }

        var partySpeeds = Enumerable.Range(start, end - start + 1)
            .Select(index => MedianPartySpeed(log.Frames, index))
            .Where(speed => speed.HasValue)
            .Select(speed => speed!.Value)
            .ToArray();
        var partyEvidence = partySpeeds.Length > 0
            ? $" Visible party median movement averaged {partySpeeds.Average():0.0}y/s."
            : " Visible party comparison was unavailable in this slice.";
        var safeRasterFrames = span.Count(frame =>
            SafetyRasterCodec.IsSafeForMovement(frame.BossMod.SafetyRaster.Player.State) &&
            SafetyRasterCodec.IsSafeForMovement(frame.BossMod.SafetyRaster.Destination.State));
        var safetyEvidence = safeRasterFrames > 0
            ? $" Safety raster showed player and destination in safe/goal/buffer cells for {safeRasterFrames}/{span.Length} eligible frames."
            : string.Empty;
        var frame = log.Frames[start];
        incidents.Add(NewIncident(
            "slow-pack-follow",
            frame,
            "medium",
            $"Player moved slowly for {(log.Frames[end].T - frame.T):0.0}s during active pack/range movement: average {speeds.Average():0.0}y/s, max {speeds.Max():0.0}y/s, observed normal run speed ~{normalSpeed:0.0}y/s.{partyEvidence}{safetyEvidence}",
            "Detect slow pack-follow separately from hard stuck movement; investigate whether target choice, stale destination hold, or movement input cadence is causing slow trailing during fluid trash pulls.",
            start,
            end));
    }

    private static void DetectMovementJitter(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 2.5f;
        const float headingFlipRadians = 1.25f;
        var nextAllowedT = 0f;

        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || !HasActiveDestination(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var flips = 0;
            float? previousHeading = null;
            for (var j = i; j <= end; j++)
            {
                var current = log.Frames[j];
                if (!HasActiveDestination(current))
                {
                    continue;
                }

                var heading = DirectionToDestination(current);
                if (heading == null)
                {
                    continue;
                }

                if (previousHeading.HasValue && MathF.Abs(NormalizeRadians(heading.Value - previousHeading.Value)) >= headingFlipRadians)
                {
                    flips++;
                }

                previousHeading = heading;
            }

            if (flips >= 4)
            {
                incidents.Add(NewIncident(
                    "movement-jitter",
                    frame,
                    "high",
                    $"Movement heading flipped {flips} times within {windowSeconds:0.0}s while destinations remained active.",
                    "Add angular hysteresis or short target-retention rules so movement retargeting does not look jittery or shake the camera.",
                    i,
                    end));
                nextAllowedT = log.Frames[end].T + windowSeconds;
            }
        }
    }

    private static void DetectBmrConflicts(XcaiLog log, List<Incident> incidents)
    {
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.Planner.Destination == null ||
                frame.Planner.ChosenSource == "<none>" ||
                frame.Planner.SuppressionReason.StartsWith("Bmr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var forced = frame.Planner.BmrForcedMovement != null;
            var forbiddenPressure = frame.Planner.BmrForbiddenZones > 0 && (frame.Planner.BmrMoveRequested || frame.Planner.BmrMoveImminent);
            if (!forced && !forbiddenPressure)
            {
                continue;
            }

            incidents.Add(NewIncident(
                "bmr-conflict",
                frame,
                "high",
                $"XCAI chose {frame.Planner.ChosenSource} while BMR pressure was active: forced={forced}, forbidden={frame.Planner.BmrForbiddenZones}, requested={frame.Planner.BmrMoveRequested}, imminent={frame.Planner.BmrMoveImminent}.",
                "Keep BMR movement authority dominant by suppressing lower-priority XCAI goals during forced movement or active forbidden-zone pressure.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
        }
    }

    private static void DetectRangeFailures(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var outOfRange = frame.TargetObjectId != 0 &&
                             frame.Motion.TargetSurfaceDistance.HasValue &&
                             frame.Motion.TargetSurfaceDistance.Value > EffectiveRangeFailureSurfaceRange(frame) + 1.5f &&
                             frame.Planner.ChosenSource == "<none>" &&
                             !IsManualSuppressed(frame);
            if (outOfRange && start < 0)
            {
                start = i;
            }
            else if (!outOfRange && start >= 0)
            {
                AddRangeFailureIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddRangeFailureIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddRangeFailureIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 2f)
        {
            return;
        }

        var frame = log.Frames[start];
        var span = log.Frames
            .Skip(start)
            .Take(end - start + 1)
            .ToArray();
        var bmrPressureFrames = span.Count(HasBmrSafetyPressure);
        var greedStyle = IsGreedCombatStyle(log);
        if (greedStyle && bmrPressureFrames == span.Length)
        {
            return;
        }

        var postPressureFrames = span.Length - bmrPressureFrames;
        var bmrContext = BuildRangeFailureBmrContext(greedStyle, bmrPressureFrames, postPressureFrames, span.Length);
        var suggestedGoal = BuildRangeFailureGoal(greedStyle, bmrPressureFrames);

        incidents.Add(NewIncident(
            "range-failure",
            frame,
            "medium",
            $"Player stayed outside useful range for {(log.Frames[end].T - frame.T):0.0}s without a chosen movement destination while manual suppression was inactive.{bmrContext}",
            suggestedGoal,
            start,
            end));
    }

    private static string BuildRangeFailureBmrContext(bool greedStyle, int bmrPressureFrames, int postPressureFrames, int totalFrames)
    {
        if (bmrPressureFrames == 0)
        {
            return " BMR safety pressure was not active.";
        }

        if (greedStyle)
        {
            return $" BMR safety pressure was present in {bmrPressureFrames}/{totalFrames} frames with Greed combat style active; that pressure can be intentional BMR uptime timing. Review the {postPressureFrames}/{totalFrames} post-pressure frames for recovery quality.";
        }

        return $" BMR safety pressure was present in {bmrPressureFrames}/{totalFrames} frames; review whether the mechanic required this downtime or whether recovery/position choice could be better.";
    }

    private static string BuildRangeFailureGoal(bool greedStyle, int bmrPressureFrames)
    {
        if (bmrPressureFrames == 0)
        {
            return "Restore ABC by generating a safe re-engage candidate when no BMR safety pressure blocks fighting.";
        }

        return greedStyle
            ? "Preserve BossMod Greed timing, then recover range smoothly once BMR pressure clears so intentional greed does not become avoidable ABC loss."
            : "Preserve BMR mechanic safety, but improve safe re-engage and recovery timing so BMR-driven movement does not create avoidable ABC loss.";
    }

    private static float EffectiveRangeFailureSurfaceRange(XcaiFrame frame)
    {
        if (frame.EngagementRange > 0f)
        {
            return frame.EngagementRange;
        }

        return UptimeJobProfile.For(frame.PlayerClassJobId).PreferredSurfaceRange;
    }

    private static void DetectTrashAoeOpportunities(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 3f;
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || !IsTrashAoeOpportunityFrame(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var span = Enumerable.Range(i, end - i + 1)
                .Select(index => log.Frames[index])
                .Where(IsTrashAoeOpportunityFrame)
                .ToArray();
            if (span.Length < 4 || span[^1].T - span[0].T < 1.5f)
            {
                continue;
            }

            var currentAverage = span.Average(f => f.CurrentHits);
            var bestAverage = span.Average(f => f.BestHits);
            var packAverage = span.Average(f => f.PackTargetCount);
            var action = span.FirstOrDefault(f => f.ActionName != "<none>")?.ActionName ?? "<unknown action>";
            incidents.Add(NewIncident(
                "trash-aoe-hit-opportunity",
                span[0],
                "medium",
                $"Trash AoE opportunity lasted {span[^1].T - span[0].T:0.0}s: current target/position averaged {currentAverage:0.0} hits, known best averaged {bestAverage:0.0} of {packAverage:0.0} visible pack targets for {action}.",
                "Review AoE target selection and movement positioning so trash AoE hits more enemies while preserving safe ABC.",
                i,
                end));
            nextAllowedT = log.Frames[end].T + windowSeconds;
        }
    }

    private static void DetectTrashPackLateEngagement(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 2f;
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT || !IsTrashPackLateEngagementFrame(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var span = Enumerable.Range(i, end - i + 1)
                .Select(index => log.Frames[index])
                .Where(IsTrashPackLateEngagementFrame)
                .ToArray();
            if (span.Length < 2 || span[^1].T - span[0].T < 0.25f)
            {
                continue;
            }

            var outOfRangeFrames = span.Count(IsOutsideUsefulPackRange);
            var unsupportedFrames = span.Count(IsUnsupportedSingleTargetFallback);
            var actionSummary = FormatActionSummary(span);
            var distanceAverage = span
                .Select(frame => frame.Motion.TargetSurfaceDistance)
                .Where(distance => distance.HasValue)
                .Select(distance => distance!.Value)
                .DefaultIfEmpty(0f)
                .Average();
            var rangeAverage = span.Average(frame => frame.EngagementRange);
            var packAverage = span.Average(frame => frame.PackTargetCount);
            incidents.Add(NewIncident(
                "trash-pack-late-engage",
                span[0],
                "high",
                $"Trash pack engagement arrived late for {span[^1].T - span[0].T:0.0}s: {actionSummary} while {packAverage:0.0} pack targets were visible, target surface distance averaged {distanceAverage:0.0}y versus {rangeAverage:0.0}y useful range, out-of-range frames={outOfRangeFrames}/{span.Length}, single-target fallback frames={unsupportedFrames}/{span.Length}.",
                "Move into the safe trash pack earlier so ABC uses AoE-capable uptime instead of single-target or ranged fallback actions when BMR safety is not blocking the path.",
                i,
                end));
            nextAllowedT = log.Frames[end].T + windowSeconds;
        }
    }

    private static void DetectTrashPullCognitionIssues(XcaiLog log, List<Incident> incidents)
    {
        DetectMissedTankLeadOpportunities(log, incidents);
        DetectTankLeadClamp(log, incidents);
        DetectStragglerFocusDuringGathering(log, incidents);
        DetectTankLeadCornerFailure(log, incidents);
    }

    private static void DetectMissedTankLeadOpportunities(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsTrashGathering(frame) ||
                IsManualSuppressed(frame) ||
                HasBmrSafetyPressure(frame) ||
                !frame.TrashPull.LeadCandidateActive ||
                frame.TrashPull.BehindDistance is not >= 7f ||
                HasTankLeadCandidate(frame))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "missed-tank-lead",
                frame,
                "high",
                $"Trash pull was gathering with the tank {frame.TrashPull.BehindDistance:0.0}y ahead, but no tank-lead movement candidate reached the planner. Reason={frame.TrashPull.Reason}; lead={frame.TrashPull.LeadRejectionReason}.",
                "Generate the tank-lead candidate whenever trash gathering is confident and the player is far behind, then let BMR/vnav reject unsafe destinations.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowedT = frame.T + 6f;
        }
    }

    private static void DetectTankLeadClamp(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsTrashGathering(frame) ||
                !frame.TrashPull.LeadClampApplied)
            {
                continue;
            }

            incidents.Add(NewIncident(
                "tank-lead-clamped",
                frame,
                "low",
                $"Tank-lead projection was clamped behind the tank at {frame.TrashPull.BehindDistance?.ToString("0.0") ?? "n/a"}y behind-distance.",
                "Keep the no-overtake clamp; if this repeats with late engagement, tune projection/trailing distance rather than allowing movement past the tank.",
                Math.Max(0, i - 2),
                Math.Min(log.Frames.Count - 1, i + 6)));
            nextAllowedT = frame.T + 8f;
        }
    }

    private static void DetectStragglerFocusDuringGathering(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsTrashGathering(frame) ||
                frame.TargetObjectId == 0 ||
                !frame.TrashPull.StragglerTargetIds.Contains(frame.TargetObjectId))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "straggler-focus-during-gathering",
                frame,
                "high",
                $"Current target was a logged straggler while the tank was still gathering the dominant pack. Dominant targets={frame.TrashPull.DominantTargetCount}, stragglers={frame.TrashPull.StragglerTargetCount}.",
                "During gathering, keep target selection on the dominant pack/tank path unless the straggler becomes part of the stable pack.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowedT = frame.T + 6f;
        }
    }

    private static void DetectTankLeadCornerFailure(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !frame.Planner.ChosenSource.Equals("Tank pull lead", StringComparison.Ordinal))
            {
                continue;
            }

            var detourFailure = frame.Planner.PathDetourRatio is >= 1.75f &&
                                frame.Planner.ExtraPathDistance is >= 8f;
            var stuckFailure = IsNearZeroMomentum(frame) &&
                               DistanceToDestination(frame) > MathF.Max(frame.Planner.AcceptanceRadius ?? 0.5f, 0.5f) + 1f;
            if (!detourFailure && !stuckFailure)
            {
                continue;
            }

            incidents.Add(NewIncident(
                "tank-lead-corner-failure",
                frame,
                "high",
                detourFailure
                    ? $"Tank-lead destination required a large corner detour: direct={frame.Planner.DirectDistance:0.0}y, path={frame.Planner.PathDistance:0.0}y, extra={frame.Planner.ExtraPathDistance:0.0}y, ratio={frame.Planner.PathDetourRatio:0.00}."
                    : "Tank-lead destination was active while movement had near-zero momentum before reaching it.",
                "Route tank-lead movement through reachable vnav waypoints or choose a nearer trailing point before sharp tank corners become wall-walking.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowedT = frame.T + 6f;
        }
    }

    private static void DetectRouteMemoryIssues(XcaiLog log, List<Incident> incidents)
    {
        DetectRouteMemoryChurn(log, incidents);
        DetectRouteMemoryBudgetExhaustion(log, incidents);
        DetectRouteMemoryUnsafeRejection(log, incidents);
        DetectRouteMemoryFallback(log, incidents);
    }

    private static void DetectRouteMemoryChurn(XcaiLog log, List<Incident> incidents)
    {
        const float windowSeconds = 4f;
        const float destinationChangeDistance = 1.5f;
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsRouteMemoryActive(frame) ||
                frame.Planner.RouteMemory.LocalDestination == null ||
                IsManualSuppressed(frame))
            {
                continue;
            }

            var end = FindFrameIndexAtOrBefore(log.Frames, frame.T + windowSeconds);
            var changes = RouteMemoryDestinationChanges(log.Frames, i, end, destinationChangeDistance).Count();
            if (changes < 4)
            {
                continue;
            }

            incidents.Add(NewIncident(
                "route-memory-churn",
                frame,
                "medium",
                $"Route memory local step changed {changes} times within {windowSeconds:0}s while following trash movement.",
                "Hold route-memory local waypoints longer or smooth tank-trail progress so trash following does not become visibly indecisive.",
                i,
                end));
            nextAllowedT = log.Frames[end].T + windowSeconds;
        }
    }

    private static void DetectRouteMemoryBudgetExhaustion(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsRouteMemoryActive(frame) ||
                frame.Planner.RouteMemory.QueryBudgetLimit <= 0 ||
                frame.Planner.RouteMemory.QueryBudgetUsed < frame.Planner.RouteMemory.QueryBudgetLimit ||
                !RouteBudgetPreventedMovement(frame))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "route-memory-budget-exhausted",
                frame,
                "medium",
                $"Route memory used its query budget {frame.Planner.RouteMemory.QueryBudgetUsed}/{frame.Planner.RouteMemory.QueryBudgetLimit} and movement had no accepted route candidate. Rejects={FormatRejects(frame.Planner.RejectedByReason)}.",
                "Keep the route-memory query budget bounded, but retain a safe cached local waypoint when budget exhaustion would otherwise produce no movement during a trash pull.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowedT = frame.T + 6f;
        }
    }

    private static void DetectRouteMemoryUnsafeRejection(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowedT = 0f;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.T < nextAllowedT ||
                !IsRouteMemoryActive(frame) ||
                !TryFindRouteMemoryRejection(frame, out var rejection))
            {
                continue;
            }

            incidents.Add(NewIncident(
                "route-memory-unsafe-waypoint",
                frame,
                "high",
                $"Route memory local step was rejected as {rejection}; source={frame.Planner.RouteMemory.Source}, invalidation={frame.Planner.RouteMemory.InvalidationReason}.",
                "When route memory points at unsafe, blocked, or off-mesh cells, keep following the tank trail but advance to the next safe local waypoint instead of dropping into target chasing or wall pressure.",
                Math.Max(0, i - 4),
                Math.Min(log.Frames.Count - 1, i + 8)));
            nextAllowedT = frame.T + 6f;
        }
    }

    private static void DetectRouteMemoryFallback(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var fallback = IsRouteMemoryActive(log.Frames[i]) &&
                           log.Frames[i].Planner.RouteMemory.Source.Equals("vnav-fallback", StringComparison.Ordinal);
            if (fallback && start < 0)
            {
                start = i;
            }
            else if (!fallback && start >= 0)
            {
                AddRouteMemoryFallbackIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddRouteMemoryFallbackIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddRouteMemoryFallbackIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 2f)
        {
            return;
        }

        var frame = log.Frames[start];
        incidents.Add(NewIncident(
            "route-memory-vnav-fallback",
            frame,
            "low",
            $"Route memory fell back from tank trail to bounded vnav goals for {(log.Frames[end].T - frame.T):0.0}s.",
            "Improve tank-trail sampling or pull-phase continuity so route memory usually follows a human-like tank route instead of reconstructing movement from moving goals.",
            start,
            end));
    }

    private static void DetectPersistentEdgeHugging(XcaiLog log, List<Incident> incidents)
    {
        var start = -1;
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var nearEdge = IsBossContext(frame) &&
                           frame.PlayerPosition != null &&
                           LooksNearArenaEdge(frame, frame.PlayerPosition);
            if (nearEdge && start < 0)
            {
                start = i;
            }
            else if (!nearEdge && start >= 0)
            {
                AddEdgeHuggingIfLongEnough(log, incidents, start, i - 1);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddEdgeHuggingIfLongEnough(log, incidents, start, log.Frames.Count - 1);
        }
    }

    private static void AddEdgeHuggingIfLongEnough(XcaiLog log, List<Incident> incidents, int start, int end)
    {
        if (end <= start || log.Frames[end].T - log.Frames[start].T < 8f)
        {
            return;
        }

        var frame = log.Frames[start];
        var span = log.Frames
            .Skip(start)
            .Take(end - start + 1)
            .ToArray();
        if (span.All(HasBmrSafetyPressure))
        {
            return;
        }

        var nearestEdge = log.Frames
            .Skip(start)
            .Take(end - start + 1)
            .Select(DistanceToArenaEdge)
            .Where(distance => distance.HasValue)
            .Select(distance => distance!.Value)
            .DefaultIfEmpty(0f)
            .Min();

        incidents.Add(NewIncident(
            "edge-hugging",
            frame,
            "high",
            $"Player stayed near the arena boundary for {(log.Frames[end].T - frame.T):0.0}s; closest boundary distance was {nearestEdge:0.0}y.",
            "Prefer a safe inward comfort position after mechanics resolve so ABC uptime does not come from unnatural corner hugging.",
            start,
            end));
    }

    private static void DetectAwkwardDestinations(XcaiLog log, List<Incident> incidents)
    {
        var nextAllowed = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            var destination = frame.Planner.Destination;
            if (destination == null || frame.TargetPosition == null)
            {
                continue;
            }

            var bossContext = IsBossContext(frame);
            if (bossContext && LooksNearArenaEdge(frame, destination) && CanEmit("arena-edge", frame.T, nextAllowed))
            {
                incidents.Add(NewIncident(
                    "arena-edge",
                    frame,
                    "low",
                    "Chosen destination is near the BMR pathfinding boundary.",
                    "Strengthen edge-avoidance comfort scoring when the edge is not needed for safe ABC uptime.",
                    Math.Max(0, i - 4),
                    Math.Min(log.Frames.Count - 1, i + 8)));
            }
        }
    }

    private static bool CanEmit(string category, float t, Dictionary<string, float> nextAllowed)
    {
        if (nextAllowed.TryGetValue(category, out var next) && t < next)
        {
            return false;
        }

        nextAllowed[category] = t + 6f;
        return true;
    }

    private static void DetectScoringAnomalies(XcaiLog log, List<Incident> incidents)
    {
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.Planner.GeneratedCount > 0 &&
                frame.Planner.AcceptedCount == 0 &&
                frame.Planner.SuppressionReason is "not evaluated" or "no_candidate")
            {
                incidents.Add(NewIncident(
                    "candidate-rejection",
                    frame,
                    "medium",
                    $"All {frame.Planner.GeneratedCount} generated movement candidates were rejected: {FormatRejects(frame.Planner.RejectedByReason)}.",
                    "Review rejection thresholds so safe, human-plausible ABC-preserving candidates are not discarded.",
                    Math.Max(0, i - 4),
                    Math.Min(log.Frames.Count - 1, i + 8)));
            }
        }
    }

    private static void DetectManualHandoffIssues(XcaiLog log, List<Incident> incidents)
    {
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (frame.Planner.Destination != null &&
                frame.Planner.ChosenSource != "<none>" &&
                frame.Planner.SuppressionReason == "ManualMovementSuppressed")
            {
                incidents.Add(NewIncident(
                    "manual-handoff",
                    frame,
                    "high",
                    "Planner retained a destination while manual movement suppression was active.",
                    "Ensure manual input clears or suppresses active movement intents until the resume delay expires.",
                    Math.Max(0, i - 4),
                    Math.Min(log.Frames.Count - 1, i + 8)));
            }
        }
    }

    private static void DetectManualCorrectionTakeovers(XcaiLog log, List<Incident> incidents)
    {
        for (var i = 0; i < log.Frames.Count; i++)
        {
            var frame = log.Frames[i];
            if (!frame.InCombat || frame.IsDead || !IsManualSuppressed(frame))
            {
                continue;
            }

            if (i > 0 && IsManualSuppressed(log.Frames[i - 1]))
            {
                continue;
            }

            var end = i;
            while (end + 1 < log.Frames.Count && IsManualSuppressed(log.Frames[end + 1]))
            {
                end++;
            }

            var lookbackStart = FindFrameIndexAtOrAfter(log.Frames, MathF.Max(0f, frame.T - 5f));
            var evidence = BuildManualCorrectionEvidence(log, lookbackStart, i, end);

            incidents.Add(NewIncident(
                "manual-correction",
                frame,
                "medium",
                evidence,
                "Treat manual takeovers as correction labels: inspect the preceding movement choice and reduce the behavior that forced player intervention while preserving clean handoff.",
                lookbackStart,
                Math.Min(log.Frames.Count - 1, end + 8)));
        }
    }

    private static string BuildManualCorrectionEvidence(XcaiLog log, int lookbackStart, int manualStart, int manualEnd)
    {
        var frames = log.Frames
            .Skip(lookbackStart)
            .Take(manualStart - lookbackStart + 1)
            .ToArray();
        var cues = new List<string>();
        var edgeFrames = frames.Count(frame => frame.PlayerPosition != null && LooksNearArenaEdge(frame, frame.PlayerPosition));
        var rangeFrames = frames.Count(frame => frame.Motion.TargetSurfaceDistance.HasValue &&
                                                frame.Motion.TargetSurfaceDistance.Value > frame.EngagementRange + 1.5f);
        var stuckFrames = frames.Count(frame => HasActiveDestination(frame) &&
                                                DistanceToDestination(frame) > MathF.Max(frame.Planner.AcceptanceRadius ?? 0.5f, 0.5f) + 1f &&
                                                IsNearZeroMomentum(frame));
        var offmeshFrames = frames.Count(frame => HasVnavmeshOffmeshDestination(frame, out _));
        var detourFrames = frames.Count(frame => frame.Planner.PathDetourRatio is >= 1.75f &&
                                                frame.Planner.ExtraPathDistance is >= 8f);
        var blockedDestinationFrames = frames.Count(frame => SafetyRasterCodec.IsHardBlocked(frame.BossMod.SafetyRaster.Destination.State));
        var blockedRouteFrames = frames.Count(HasHardSafetyBlock);
        var queryPendingFrames = frames.Count(IsVnavmeshQueryPending);
        var bmrPressureFrames = frames.Count(HasBmrSafetyPressure);
        var destinationChanges = DestinationChanges(frames, 0, frames.Length - 1, 1.25f).Count();
        var normalSpeed = EstimateNormalMovementSpeed(log);
        var slowThreshold = Math.Clamp(normalSpeed * 0.55f, 2.4f, 3.3f);
        var slowPackFrames = frames.Count(frame => IsTrashContext(frame) &&
                                                  IsPackFollowEligible(frame) &&
                                                  IsSlowMovementSpeed(frame, slowThreshold));
        var gatheringFrames = frames.Count(IsTrashGathering);
        var missedTankLeadFrames = frames.Count(frame => IsTrashGathering(frame) &&
                                                        frame.TrashPull.LeadCandidateActive &&
                                                        frame.TrashPull.BehindDistance is >= 7f &&
                                                        !HasTankLeadCandidate(frame));
        var stragglerFocusFrames = frames.Count(frame => IsTrashGathering(frame) &&
                                                        frame.TargetObjectId != 0 &&
                                                        frame.TrashPull.StragglerTargetIds.Contains(frame.TargetObjectId));
        var tankLeadFrames = frames.Count(frame => frame.Planner.ChosenSource.Equals("Tank pull lead", StringComparison.Ordinal));
        var routeMemoryFrames = frames.Count(IsRouteMemoryActive);
        var routeFallbackFrames = frames.Count(frame => IsRouteMemoryActive(frame) &&
                                                       frame.Planner.RouteMemory.Source.Equals("vnav-fallback", StringComparison.Ordinal));
        var routeRejectedFrames = frames.Count(frame => TryFindRouteMemoryRejection(frame, out _));

        if (edgeFrames >= 3)
        {
            cues.Add($"{edgeFrames} recent frames near the arena boundary");
        }

        if (rangeFrames >= 2)
        {
            cues.Add($"{rangeFrames} recent frames outside useful range");
        }

        if (destinationChanges >= 3)
        {
            cues.Add($"{destinationChanges} recent destination changes");
        }

        if (stuckFrames >= 2)
        {
            cues.Add($"{stuckFrames} recent low-momentum frames with an active destination");
        }

        if (offmeshFrames >= 1)
        {
            cues.Add($"{offmeshFrames} recent off-mesh destination probes");
        }

        if (detourFrames >= 1)
        {
            cues.Add($"{detourFrames} recent high-detour path probes");
        }

        if (blockedDestinationFrames >= 1)
        {
            cues.Add($"{blockedDestinationFrames} recent BMR-blocked planner destinations");
        }

        if (blockedRouteFrames >= 2)
        {
            cues.Add($"{blockedRouteFrames} recent frames with a blocked/no-go route point");
        }

        if (queryPendingFrames >= 2)
        {
            cues.Add($"{queryPendingFrames} recent pending vnavmesh path queries");
        }

        if (slowPackFrames >= 2)
        {
            cues.Add($"{slowPackFrames} recent slow trash-follow frames");
        }

        if (gatheringFrames >= 2)
        {
            cues.Add($"{gatheringFrames} recent trash-gathering frames");
        }

        if (missedTankLeadFrames >= 1)
        {
            cues.Add($"{missedTankLeadFrames} recent missed tank-lead opportunities");
        }

        if (stragglerFocusFrames >= 1)
        {
            cues.Add($"{stragglerFocusFrames} recent frames targeting a trash straggler during gathering");
        }

        if (tankLeadFrames >= 1)
        {
            cues.Add($"{tankLeadFrames} recent tank-lead planner frames");
        }

        if (routeMemoryFrames >= 1)
        {
            cues.Add($"{routeMemoryFrames} recent route-memory frames");
        }

        if (routeFallbackFrames >= 1)
        {
            cues.Add($"{routeFallbackFrames} recent route-memory fallback frames");
        }

        if (routeRejectedFrames >= 1)
        {
            cues.Add($"{routeRejectedFrames} recent rejected route-memory steps");
        }

        if (bmrPressureFrames >= 2)
        {
            cues.Add($"{bmrPressureFrames} recent frames with BMR safety pressure");
        }

        var previousPlan = frames.LastOrDefault(frame => HasActiveDestination(frame));
        if (previousPlan != null)
        {
            cues.Add($"last active planner source was {previousPlan.Planner.ChosenSource}");
        }

        if (cues.Count == 0)
        {
            cues.Add("no classified trigger yet; inspect the lookback slice");
        }

        var duration = log.Frames[manualEnd].T - log.Frames[manualStart].T;
        return $"Manual movement suppression started after {string.Join(", ", cues)}. Suppression lasted {duration:0.0}s.";
    }

    private static bool LooksNearArenaEdge(XcaiFrame frame, Vec3 destination)
    {
        var center = frame.BossMod.PathfindMapCenter;
        if (center == null)
        {
            return false;
        }

        if (frame.BossMod.PathfindMapRadius.HasValue)
        {
            return Vec3.Distance2D(destination, center) > frame.BossMod.PathfindMapRadius.Value - 2f;
        }

        if (frame.BossMod.PathfindMapHalfWidth.HasValue && frame.BossMod.PathfindMapHalfHeight.HasValue)
        {
            return MathF.Abs(destination.X - center.X) > frame.BossMod.PathfindMapHalfWidth.Value - 2f ||
                   MathF.Abs(destination.Z - center.Z) > frame.BossMod.PathfindMapHalfHeight.Value - 2f;
        }

        return false;
    }

    private static float? DistanceToArenaEdge(XcaiFrame frame)
    {
        var center = frame.BossMod.PathfindMapCenter;
        var position = frame.PlayerPosition;
        if (center == null || position == null)
        {
            return null;
        }

        if (frame.BossMod.PathfindMapRadius.HasValue)
        {
            return MathF.Max(0f, frame.BossMod.PathfindMapRadius.Value - Vec3.Distance2D(position, center));
        }

        if (frame.BossMod.PathfindMapHalfWidth.HasValue && frame.BossMod.PathfindMapHalfHeight.HasValue)
        {
            var dx = frame.BossMod.PathfindMapHalfWidth.Value - MathF.Abs(position.X - center.X);
            var dz = frame.BossMod.PathfindMapHalfHeight.Value - MathF.Abs(position.Z - center.Z);
            return MathF.Max(0f, MathF.Min(dx, dz));
        }

        return null;
    }

    private static IEnumerable<(int Index, Vec3 Destination)> DestinationChanges(IReadOnlyList<XcaiFrame> frames, int start, int end, float threshold)
    {
        Vec3? previous = null;
        for (var i = start; i <= end; i++)
        {
            var destination = frames[i].Planner.Destination;
            if (destination == null)
            {
                continue;
            }

            if (previous == null || Vec3.Distance2D(destination, previous) >= threshold)
            {
                yield return (i, destination);
                previous = destination;
            }
        }
    }

    private static IEnumerable<(int Index, Vec3 Destination)> RouteMemoryDestinationChanges(IReadOnlyList<XcaiFrame> frames, int start, int end, float threshold)
    {
        Vec3? previous = null;
        for (var i = start; i <= end; i++)
        {
            var destination = frames[i].Planner.RouteMemory.LocalDestination;
            if (destination == null || !IsRouteMemoryActive(frames[i]))
            {
                continue;
            }

            if (previous == null || Vec3.Distance2D(destination, previous) >= threshold)
            {
                yield return (i, destination);
                previous = destination;
            }
        }
    }

    private static bool HasActiveDestination(XcaiFrame frame)
    {
        return frame.Planner.Destination != null && frame.Planner.ChosenSource != "<none>";
    }

    private static bool IsRouteMemoryActive(XcaiFrame frame)
    {
        return frame.Planner.RouteMemory.Active &&
               IsTrashContext(frame) &&
               !IsManualSuppressed(frame);
    }

    private static bool RouteBudgetPreventedMovement(XcaiFrame frame)
    {
        return frame.Planner.ChosenSource == "<none>" ||
               frame.Planner.RejectedByReason.ContainsKey("RouteBudgetExceeded") ||
               frame.Planner.RejectedByReason.ContainsKey("RouteRecoveryBudgetExceeded") ||
               frame.Planner.RejectedByReason.ContainsKey("RouteBudgetSuppressed");
    }

    private static bool TryFindRouteMemoryRejection(XcaiFrame frame, out string rejection)
    {
        var candidate = frame.Planner.TopCandidates.FirstOrDefault(candidate =>
            candidate.Source.Equals("Trash route memory", StringComparison.Ordinal) &&
            !candidate.Accepted);
        rejection = candidate?.RejectionReason ?? string.Empty;
        return rejection is "BmrUnsafe" or "BmrPathBlocked" or "BmrPathActiveDanger" or "OutsidePathfindMap" or "OffMeshDestination" or "Unreachable";
    }

    private static bool PlannerPathIsReachable(XcaiFrame frame)
    {
        return frame.Planner.PathStatus.Equals("Reachable", StringComparison.Ordinal);
    }

    private static bool HasVnavmeshOffmeshDestination(XcaiFrame frame, out float? distance)
    {
        distance = null;
        if (!HasActiveDestination(frame))
        {
            return false;
        }

        var probe = frame.Planner.VnavmeshDestination;
        if (probe == null)
        {
            return false;
        }

        distance = probe.NearestReachablePointDistance ??
                   probe.NearestPointDistance ??
                   probe.FloorPointDistance;

        if (probe.Status.Equals("NoMeshPoint", StringComparison.Ordinal))
        {
            return true;
        }

        return distance is >= 1.5f;
    }

    private static bool HasHardSafetyBlock(XcaiFrame frame)
    {
        return SafetyRasterCodec.IsHardBlocked(frame.BossMod.SafetyRaster.Player.State) ||
               SafetyRasterCodec.IsHardBlocked(frame.BossMod.SafetyRaster.Destination.State) ||
               SafetyRasterCodec.IsHardBlocked(frame.BossMod.SafetyRaster.FirstWaypoint.State);
    }

    private static bool TryFindBlockedSafetyRoute(XcaiFrame frame, out string state, out float blockedDistance)
    {
        return TryFindBlockedSafetyRoute(frame, frame.Planner.Destination, out state, out blockedDistance);
    }

    private static bool TryFindBlockedSafetyRoute(XcaiFrame frame, Vec3? destination, out string state, out float blockedDistance)
    {
        state = string.Empty;
        blockedDistance = 0f;
        var raster = frame.BossMod.SafetyRaster;
        if (!raster.Status.Equals("captured", StringComparison.Ordinal) ||
            raster.Center == null ||
            frame.PlayerPosition == null ||
            destination == null ||
            raster.SourceResolution <= 0f ||
            raster.SourceWidth <= 0 ||
            raster.SourceHeight <= 0 ||
            raster.Width <= 0 ||
            raster.Height <= 0)
        {
            return false;
        }

        var cells = SafetyRasterCodec.Decode(raster.CellsRle, raster.Width * raster.Height);
        var distance = Vec3.Distance2D(frame.PlayerPosition, destination);
        var step = MathF.Max(0.25f, raster.SourceResolution * Math.Max(1, raster.CellScale) * 0.5f);
        var steps = Math.Clamp((int)MathF.Ceiling(distance / step), 1, 256);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var point = Lerp(frame.PlayerPosition, destination, t);
            if (!TryWorldToSafetyRasterCell(point, raster, out var cellX, out var cellY))
            {
                continue;
            }

            var cellState = cells[(cellY * raster.Width) + cellX];
            if (cellState is SafetyRasterCodec.Blocked or SafetyRasterCodec.ActiveDanger)
            {
                state = SafetyCellStateName(cellState);
                blockedDistance = distance * t;
                return true;
            }
        }

        return false;
    }

    private static bool IsBmrMovementOverrideBlocked(XcaiFrame frame)
    {
        return frame.BossMod.MovementOverride.Contains("Blocked=True", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSafetyEvidence(XcaiFrame frame)
    {
        var raster = frame.BossMod.SafetyRaster;
        if (!raster.Status.Equals("captured", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return $" Safety states: player={raster.Player.State}, destination={raster.Destination.State}, first-waypoint={raster.FirstWaypoint.State}, target={raster.Target.State}.";
    }

    private static bool TryWorldToSafetyRasterCell(Vec3 position, SafetyRasterSnapshot raster, out int cellX, out int cellY)
    {
        cellX = 0;
        cellY = 0;
        if (raster.Center == null || raster.SourceResolution <= 0f || raster.CellScale <= 0)
        {
            return false;
        }

        var dx = position.X - raster.Center.X;
        var dz = position.Z - raster.Center.Z;
        var sin = MathF.Sin(raster.RotationRadians);
        var cos = MathF.Cos(raster.RotationRadians);
        var sourceX = (raster.SourceWidth >> 1) + ((dx * cos) - (dz * sin)) / raster.SourceResolution;
        var sourceY = (raster.SourceHeight >> 1) + ((dx * sin) + (dz * cos)) / raster.SourceResolution;
        var sx = (int)MathF.Floor(sourceX);
        var sy = (int)MathF.Floor(sourceY);
        if (sx < 0 || sx >= raster.SourceWidth || sy < 0 || sy >= raster.SourceHeight)
        {
            return false;
        }

        cellX = Math.Clamp(sx / raster.CellScale, 0, raster.Width - 1);
        cellY = Math.Clamp(sy / raster.CellScale, 0, raster.Height - 1);
        return true;
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, float t)
    {
        return new Vec3(
            a.X + ((b.X - a.X) * t),
            a.Y + ((b.Y - a.Y) * t),
            a.Z + ((b.Z - a.Z) * t));
    }

    private static string SafetyCellStateName(int state)
    {
        return state switch
        {
            SafetyRasterCodec.Blocked => "blocked",
            SafetyRasterCodec.ActiveDanger => "active-danger",
            SafetyRasterCodec.FutureDanger => "future-danger",
            SafetyRasterCodec.AvoidBuffer => "avoid-buffer",
            SafetyRasterCodec.Goal => "goal",
            _ => "safe"
        };
    }

    private static bool IsVnavmeshQueryPending(XcaiFrame frame)
    {
        if (frame.Planner.PathStatus.Equals("Pending", StringComparison.Ordinal))
        {
            return true;
        }

        if (frame.Planner.TopCandidates.Any(candidate =>
                candidate.PathStatus.Equals("Pending", StringComparison.Ordinal) ||
                candidate.RejectionReason.Equals("Pending", StringComparison.Ordinal)))
        {
            return true;
        }

        return frame.Planner.GeneratedCount > 0 &&
               frame.Planner.AcceptedCount == 0 &&
               frame.Planner.Vnavmesh.PathfindInProgress == true &&
               frame.Planner.Vnavmesh.PathfindQueued.GetValueOrDefault() > 0;
    }

    private static bool IsPackFollowEligible(XcaiFrame frame)
    {
        if (!frame.InCombat ||
            frame.IsDead ||
            IsManualSuppressed(frame) ||
            !HasActiveDestination(frame) ||
            frame.Motion.PlayerSpeed == null ||
            DistanceToDestination(frame) <= MathF.Max(frame.Planner.AcceptanceRadius ?? 0.5f, 0.5f) + 1f)
        {
            return false;
        }

        if (!IsTrashContext(frame))
        {
            return false;
        }

        if (HasBossModContext(frame) && HasBmrSafetyPressure(frame))
        {
            return false;
        }

        return true;
    }

    private static bool IsBossContext(XcaiFrame frame)
    {
        return HasBossModContext(frame) ||
               IsBossOnlyDutyTarget(frame) ||
               HasBossLikeHealthOutlier(frame);
    }

    private static bool HasBossModContext(XcaiFrame frame)
    {
        return IsBossModEncounterModule(frame.BossModActiveModule) ||
               frame.BossModActiveZoneModule != "<none>";
    }

    private static bool IsBossModEncounterModule(string? moduleName)
    {
        return !string.IsNullOrWhiteSpace(moduleName) &&
               !moduleName.Equals("<none>", StringComparison.Ordinal) &&
               !IsBossModDungeonTrashModule(moduleName);
    }

    private static bool IsBossModDungeonTrashModule(string moduleName)
    {
        var separator = moduleName.LastIndexOf('.');
        var simpleName = separator >= 0 && separator < moduleName.Length - 1
            ? moduleName[(separator + 1)..]
            : moduleName;
        if (simpleName.Length < 3 || simpleName[0] != 'D')
        {
            return false;
        }

        var index = 1;
        while (index < simpleName.Length && char.IsDigit(simpleName[index]))
        {
            index++;
        }

        return index > 1 &&
               index < simpleName.Length &&
               simpleName[index - 1] == '0';
    }

    private static bool IsBossOnlyDutyTarget(XcaiFrame frame)
    {
        return frame.TargetBaseId != 0 &&
               frame.TargetObjectId != 0 &&
               DutyContentLookup.Find(frame.ContentFinderConditionId)?.IsBossOnlyDuty == true;
    }

    private static bool HasBossLikeHealthOutlier(XcaiFrame frame)
    {
        var target = FindTargetActor(frame);
        if (target == null || target.BaseId == 0 || IsPlayerOrPartyActor(target))
        {
            return false;
        }

        var targetHp = ObservedMaxHp(target);
        if (targetHp <= MinimumComparableNpcHp)
        {
            return false;
        }

        var baseline = VisibleComparableNpcHpBaseline(frame, target.GameObjectId);
        if (baseline is not > 0)
        {
            return false;
        }

        var ratio = targetHp / baseline.Value;
        if (ratio >= StrongBossHpOutlierMultiplier &&
            (target.Radius >= 2.5f || frame.TargetRadius >= 2.5f))
        {
            return true;
        }

        return ratio >= ExtremeBossHpOutlierMultiplier && frame.PackTargetCount <= 1;
    }

    private static ActorSnapshot? FindTargetActor(XcaiFrame frame)
    {
        return frame.Actors.FirstOrDefault(actor => actor.GameObjectId == frame.TargetObjectId) ??
               frame.Actors.FirstOrDefault(actor => actor.Relation.Equals("target", StringComparison.Ordinal)) ??
               frame.Actors.FirstOrDefault(actor => actor.BaseId != 0 && actor.BaseId == frame.TargetBaseId);
    }

    private static float? VisibleComparableNpcHpBaseline(XcaiFrame frame, ulong targetObjectId)
    {
        var hps = frame.Actors
            .Where(actor => actor.GameObjectId != targetObjectId)
            .Where(IsComparableNpcActor)
            .Select(ObservedMaxHp)
            .Where(hp => hp > MinimumComparableNpcHp)
            .Order()
            .ToArray();

        return hps.Length == 0 ? null : hps[hps.Length / 2];
    }

    private static bool IsComparableNpcActor(ActorSnapshot actor)
    {
        return actor.BaseId != 0 &&
               !actor.IsDead &&
               actor.Radius >= 0.5f &&
               !IsPlayerOrPartyActor(actor) &&
               ObservedMaxHp(actor) > MinimumComparableNpcHp;
    }

    private static bool IsPlayerOrPartyActor(ActorSnapshot actor)
    {
        return actor.Relation.Equals("player", StringComparison.Ordinal) ||
               actor.Relation.Equals("party", StringComparison.Ordinal) ||
               actor.ObjectKind.Equals("Player", StringComparison.Ordinal);
    }

    private static uint ObservedMaxHp(ActorSnapshot actor)
    {
        return actor.MaxHp > 0 ? actor.MaxHp : actor.CurrentHp;
    }

    private static bool IsTrashContext(XcaiFrame frame)
    {
        return !IsBossContext(frame) &&
               (frame.PackTargetCount >= 2 ||
                IsPackMovementSource(frame.Planner.ChosenSource) ||
                frame.TrashPull.Phase is "Gathering" or "Stabilizing" or "Burning");
    }

    private static bool IsPackMovementSource(string source)
    {
        return source is "Pack engagement" or "AoE pack" or "Tank pull lead";
    }

    private static bool IsTrashGathering(XcaiFrame frame)
    {
        return IsTrashContext(frame) &&
               frame.TrashPull.Phase.Equals("Gathering", StringComparison.Ordinal);
    }

    private static bool HasTankLeadCandidate(XcaiFrame frame)
    {
        return frame.Planner.ChosenSource.Equals("Tank pull lead", StringComparison.Ordinal) ||
               frame.Planner.TopCandidates.Any(candidate => candidate.Source.Equals("Tank pull lead", StringComparison.Ordinal)) ||
               frame.ActionName.Equals("Tank pull lead", StringComparison.Ordinal) ||
               frame.AoeReason.StartsWith("following tank lead", StringComparison.Ordinal);
    }

    private static bool IsSlowMovementSpeed(XcaiFrame frame, float slowThreshold)
    {
        if (frame.Motion.PlayerSpeed is not { } speed)
        {
            return false;
        }

        return speed < slowThreshold;
    }

    private static bool IsTrashAoeOpportunityFrame(XcaiFrame frame)
    {
        return IsTrashContext(frame) &&
               frame.InCombat &&
               !frame.IsDead &&
               !IsManualSuppressed(frame) &&
               !HasBmrSafetyPressure(frame) &&
               IsConcreteAoeAction(frame.ActionName) &&
               frame.PackTargetCount >= 2 &&
               frame.BestHits >= 2 &&
               frame.CurrentHits >= 0 &&
               frame.BestHits > frame.CurrentHits;
    }

    private static bool IsTrashPackLateEngagementFrame(XcaiFrame frame)
    {
        if (!IsTrashContext(frame) ||
            !frame.InCombat ||
            frame.IsDead ||
            IsManualSuppressed(frame) ||
            HasBmrSafetyPressure(frame) ||
            frame.PackTargetCount < 2)
        {
            return false;
        }

        if (!IsOutsideUsefulPackRange(frame))
        {
            return false;
        }

        return IsUnsupportedSingleTargetFallback(frame) ||
               IsConcreteCombatAction(frame.ActionName);
    }

    private static bool IsOutsideUsefulPackRange(XcaiFrame frame)
    {
        return frame.Motion.TargetSurfaceDistance.HasValue &&
               frame.Motion.TargetSurfaceDistance.Value > frame.EngagementRange + 1.5f;
    }

    private static bool IsUnsupportedSingleTargetFallback(XcaiFrame frame)
    {
        return frame.ActionName == "<none>" &&
               frame.AoeReason.StartsWith("RSR unsupported cast type", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConcreteAoeAction(string actionName)
    {
        return actionName != "<none>" &&
               actionName != "Pack engagement";
    }

    private static bool IsConcreteCombatAction(string actionName)
    {
        return actionName != "<none>" &&
               actionName != "Pack engagement";
    }

    private static string FormatActionSummary(IEnumerable<XcaiFrame> frames)
    {
        var labels = frames
            .Select(ActionLabel)
            .Where(label => label != "<none>")
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        return labels.Length == 0 ? "non-AoE fallback actions were observed" : $"actions={string.Join(", ", labels)}";
    }

    private static string ActionLabel(XcaiFrame frame)
    {
        if (frame.ActionName != "<none>")
        {
            return frame.ActionName;
        }

        const string UnsupportedPrefix = "RSR unsupported cast type";
        return frame.AoeReason.StartsWith(UnsupportedPrefix, StringComparison.OrdinalIgnoreCase)
            ? frame.AoeReason
            : "<none>";
    }

    private static float EstimateNormalMovementSpeed(XcaiLog log)
    {
        var speeds = log.Frames
            .Select(frame => frame.Motion.PlayerSpeed)
            .Where(speed => speed is > 3f and < 9f)
            .Select(speed => speed!.Value)
            .Order()
            .ToArray();
        if (speeds.Length == 0)
        {
            return 6f;
        }

        return speeds[Math.Clamp((int)(speeds.Length * 0.75f), 0, speeds.Length - 1)];
    }

    private static float? MedianPartySpeed(IReadOnlyList<XcaiFrame> frames, int index)
    {
        if (index <= 0 || index >= frames.Count)
        {
            return null;
        }

        var previous = frames[index - 1];
        var current = frames[index];
        var elapsed = current.T - previous.T;
        if (elapsed <= 0)
        {
            return null;
        }

        var previousParty = previous.Actors
            .Where(actor => actor.Relation.Equals("party", StringComparison.Ordinal))
            .ToDictionary(actor => actor.GameObjectId);
        var speeds = current.Actors
            .Where(actor => actor.Relation.Equals("party", StringComparison.Ordinal))
            .Select(actor => previousParty.TryGetValue(actor.GameObjectId, out var prev)
                ? Vec3.Distance2D(actor.Position, prev.Position) / elapsed
                : (float?)null)
            .Where(speed => speed is > 0.2f and < 12f)
            .Select(speed => speed!.Value)
            .Order()
            .ToArray();

        return speeds.Length == 0 ? null : speeds[speeds.Length / 2];
    }

    private static bool IsDefensiveZoneSource(XcaiFrame frame)
    {
        return frame.Planner.ChosenSource.Equals("Defensive zone", StringComparison.Ordinal);
    }

    private static bool IsManualSuppressed(XcaiFrame frame)
    {
        return frame.AutomatedMovementSuppressed ||
               frame.Planner.SuppressionReason.Equals("ManualMovementSuppressed", StringComparison.Ordinal) ||
               frame.Planner.SwitchReason.Equals("ManualMovementSuppressed", StringComparison.Ordinal);
    }

    private static bool HasBmrSafetyPressure(XcaiFrame frame)
    {
        return frame.Planner.BmrForcedMovement != null ||
               frame.Planner.BmrForbiddenZones > 0 ||
               frame.BossMod.ForbiddenZones.GetValueOrDefault() > 0 ||
               (HasBossModContext(frame) &&
                (frame.Planner.BmrMoveRequested ||
                 frame.Planner.BmrMoveImminent ||
                 frame.BossMod.GoalZones.GetValueOrDefault() > 0));
    }

    private static bool IsGreedCombatStyle(XcaiLog log)
    {
        return log.Header.CombatStyle.StartsWith("Greed", StringComparison.OrdinalIgnoreCase);
    }

    private static float DistanceToDestination(XcaiFrame frame)
    {
        return frame.PlayerPosition != null && frame.Planner.Destination != null
            ? Vec3.Distance2D(frame.PlayerPosition, frame.Planner.Destination)
            : 0f;
    }

    private static bool IsNearZeroMomentum(XcaiFrame frame)
    {
        return (frame.Motion.PlayerStepDistance.HasValue && frame.Motion.PlayerStepDistance.Value <= 0.08f) ||
               (frame.Motion.PlayerSpeed.HasValue && frame.Motion.PlayerSpeed.Value <= 0.2f);
    }

    private static float? DirectionToDestination(XcaiFrame frame)
    {
        if (frame.PlayerPosition == null || frame.Planner.Destination == null)
        {
            return null;
        }

        var dx = frame.Planner.Destination.X - frame.PlayerPosition.X;
        var dz = frame.Planner.Destination.Z - frame.PlayerPosition.Z;
        return (dx * dx) + (dz * dz) < 0.01f ? null : MathF.Atan2(dx, dz);
    }

    private static Incident NewIncident(string category, XcaiFrame frame, string severity, string evidence, string suggestedGoal, int startFrame, int endFrame)
    {
        return new Incident(
            $"{category}-{frame.T:0.00}".Replace('.', '-'),
            category,
            frame.TimestampUtc,
            frame.T,
            severity,
            evidence,
            suggestedGoal,
            startFrame,
            endFrame);
    }

    private static int FindFrameIndexAtOrBefore(IReadOnlyList<XcaiFrame> frames, float t)
    {
        var index = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].T > t)
            {
                break;
            }

            index = i;
        }

        return index;
    }

    private static int FindFrameIndexAtOrAfter(IReadOnlyList<XcaiFrame> frames, float t)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].T >= t)
            {
                return i;
            }
        }

        return Math.Max(0, frames.Count - 1);
    }

    private static string FormatRejects(IReadOnlyDictionary<string, int> rejected)
    {
        return rejected.Count == 0
            ? "<none>"
            : string.Join(", ", rejected.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}:{entry.Value}"));
    }

    private static float NormalizeRadians(float value)
    {
        while (value > MathF.PI)
        {
            value -= MathF.Tau;
        }

        while (value < -MathF.PI)
        {
            value += MathF.Tau;
        }

        return value;
    }
}
