using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record HealerAoePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    int PartyMembers,
    float DistanceToCenter,
    int CoveredMembers);

internal sealed record HealerCoverageOverlaySnapshot(
    Vector3 Center,
    float Radius,
    bool Injected,
    float DistanceToCenter,
    int CoveredMembers,
    int TotalMembers,
    IReadOnlyList<Vector3> Members);

internal sealed class HealerAoePositioningController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Healer AoE heals and support actions commonly use a 20y radius around the healer.
    private const float CoverageRadius = 20f;
    private const float CoverageRadiusSquared = CoverageRadius * CoverageRadius;
    private const float PreferredScore = GoalZoneScorePolicy.NormalPreference;

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private int lastPartyMembers;
    private float lastDistanceToCenter;
    private int lastCoveredMembers;
    private HealerCoverageOverlaySnapshot? lastOverlay;
    private HealerCoverageGoalPlan? lastPlan;
    private Delegate? lastGoalDelegate;

    public HealerAoePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPartyMembers,
        this.lastDistanceToCenter,
        this.lastCoveredMembers);

    public HealerCoverageOverlaySnapshot? Overlay => this.lastOverlay;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastInjected = false;
        this.lastPartyMembers = 0;
        this.lastDistanceToCenter = 0f;
        this.lastCoveredMembers = 0;
        this.lastOverlay = null;
        this.lastPlan = null;
        this.lastGoalDelegate = null;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManageHealerCoverageZone)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!config.ManageMovement)
        {
            this.lastReason = "movement management disabled";
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            return;
        }

        if (JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer) != RangeRole.Healer)
        {
            this.lastReason = "not a healer";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
            return;

        if (this.goalZonesField!.GetValue(hints) is not System.Collections.IList)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return;
        }

        var partySnapshot = PartyAllyProvider.GetVisiblePartyAllies(services, player);
        var members = partySnapshot.Members;
        this.lastPartyMembers = members.Count;

        if (members.Count < 1)
        {
            this.lastReason = "no visible party members";
            return;
        }

        var plan = this.BuildPlan(player, members);

        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastCoveredMembers = plan.BestCoveredCount;
        var shouldMove = plan.CurrentCoveredCount < plan.BestCoveredCount;

        if (this.lastPlan == null || !this.lastPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
            this.lastPlan = plan;
        }

        if (shouldMove)
        {
            contributions.Add(new(this.lastGoalDelegate!, BossModGoalPriority.Convenience, "Healer coverage zone"));
            this.lastReason = $"covering {plan.CurrentCoveredCount}/{plan.TotalMembers}, can cover {plan.BestCoveredCount}/{plan.TotalMembers}";
        }
        else
        {
            this.lastReason = $"covering {plan.CurrentCoveredCount}/{plan.TotalMembers}";
        }

        this.lastInjected = shouldMove;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: shouldMove);
    }

    private HealerCoverageGoalPlan BuildPlan(IBattleChara player, IReadOnlyList<IBattleChara> members)
    {
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var allPositions = new List<Vector2>(members.Count);
        foreach (var m in members)
            allPositions.Add(new Vector2(m.Position.X, m.Position.Z));

        var candidates = this.GenerateCoverageCandidates(playerPos, allPositions);
        var currentCovered = CountCovered(playerPos, allPositions);

        Vector2 bestCenter = playerPos;
        int bestCount = currentCovered;
        float bestDistSq = 0f;
        var bestCovered = GetCoveredMembers(playerPos, allPositions);

        foreach (var candidate in candidates)
        {
            var covered = GetCoveredMembers(candidate, allPositions);
            var distToCandidateSq = Vector2.DistanceSquared(playerPos, candidate);
            if (covered.Count > bestCount ||
                (covered.Count == bestCount && distToCandidateSq < bestDistSq))
            {
                bestCount = covered.Count;
                bestCenter = candidate;
                bestDistSq = distToCandidateSq;
                bestCovered = covered;
            }
        }

        return new HealerCoverageGoalPlan(bestCenter, bestCovered, allPositions, MathF.Sqrt(bestDistSq), currentCovered);
    }

    private IEnumerable<Vector2> GenerateCoverageCandidates(Vector2 playerPos, IReadOnlyList<Vector2> members)
    {
        yield return playerPos;

        var average = Vector2.Zero;
        foreach (var member in members)
        {
            average += member;
            yield return member;
        }

        average /= members.Count;
        yield return average;

        for (var i = 0; i < members.Count; i++)
        {
            for (var j = i + 1; j < members.Count; j++)
            {
                var delta = members[j] - members[i];
                var distanceSq = delta.LengthSquared();
                if (distanceSq <= 0.001f || distanceSq > 4f * CoverageRadiusSquared)
                    continue;

                var distance = MathF.Sqrt(distanceSq);
                var midpoint = (members[i] + members[j]) * 0.5f;
                var halfDistance = distance * 0.5f;
                var height = MathF.Sqrt(MathF.Max(0f, CoverageRadiusSquared - (halfDistance * halfDistance)));
                var perpendicular = new Vector2(-delta.Y / distance, delta.X / distance);

                yield return midpoint + (perpendicular * height);
                if (height > 0.001f)
                    yield return midpoint - (perpendicular * height);
            }
        }
    }

    private static int CountCovered(Vector2 candidate, IReadOnlyList<Vector2> members)
    {
        var covered = 0;
        foreach (var member in members)
        {
            if (Vector2.DistanceSquared(candidate, member) <= CoverageRadiusSquared)
                covered++;
        }

        return covered;
    }

    private static IReadOnlyList<Vector2> GetCoveredMembers(Vector2 candidate, IReadOnlyList<Vector2> members)
    {
        var covered = new List<Vector2>(members.Count);
        foreach (var member in members)
        {
            if (Vector2.DistanceSquared(candidate, member) <= CoverageRadiusSquared)
                covered.Add(member);
        }

        return covered;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR healer coverage reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private sealed class HealerCoverageGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(HealerCoverageGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Optimal healer position that covers the most party members.
        private readonly Vector2 optimalCenter;
        // Members covered from optimalCenter (for overlay display).
        private readonly IReadOnlyList<Vector2> coveredMembers;
        // All visible party member positions (for scoring any candidate healer position).
        private readonly IReadOnlyList<Vector2> allMembers;
        private readonly float distanceToCenter;
        private readonly int currentCoveredCount;

        public HealerCoverageGoalPlan(Vector2 optimalCenter, IReadOnlyList<Vector2> coveredMembers, IReadOnlyList<Vector2> allMembers, float distanceToCenter, int currentCoveredCount)
        {
            this.optimalCenter = optimalCenter;
            this.coveredMembers = coveredMembers;
            this.allMembers = allMembers;
            this.distanceToCenter = distanceToCenter;
            this.currentCoveredCount = currentCoveredCount;
        }

        public float DistanceToCenter => this.distanceToCenter;
        public int BestCoveredCount => this.coveredMembers.Count;
        public int CurrentCoveredCount => this.currentCoveredCount;
        public int TotalMembers => this.allMembers.Count;

        public bool SameSource(HealerCoverageGoalPlan other)
        {
            if (Vector2.DistanceSquared(this.optimalCenter, other.optimalCenter) > 1f ||
                this.coveredMembers.Count != other.coveredMembers.Count ||
                this.allMembers.Count != other.allMembers.Count)
            {
                return false;
            }

            for (var i = 0; i < this.allMembers.Count; i++)
            {
                if (Vector2.DistanceSquared(this.allMembers[i], other.allMembers[i]) > 1f)
                    return false;
            }

            return true;
        }

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreFromWPosMethod,
                Expression.Convert(Expression.Field(parameter, xField), typeof(float)),
                Expression.Convert(Expression.Field(parameter, zField), typeof(float)));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        public HealerCoverageOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            var memberPositions = new Vector3[coveredMembers.Count];
            for (var i = 0; i < coveredMembers.Count; i++)
                memberPositions[i] = new Vector3(coveredMembers[i].X, y, coveredMembers[i].Y);
            return new(new Vector3(optimalCenter.X, y, optimalCenter.Y), CoverageRadius, injected, distanceToCenter, this.coveredMembers.Count, this.allMembers.Count, memberPositions);
        }

        private float ScoreFromWPos(float x, float z)
        {
            // Score by how many members the healer's 20y circle would cover at this position.
            var candidatePos = new Vector2(x, z);
            var covered = 0;
            foreach (var member in allMembers)
            {
                if (Vector2.DistanceSquared(candidatePos, member) <= CoverageRadiusSquared)
                    covered++;
            }

            if (covered == 0)
                return 0f;

            var fraction = (float)covered / allMembers.Count;
            return PreferredScore * fraction;
        }
    }
}
