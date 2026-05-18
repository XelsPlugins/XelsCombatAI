using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed class BossCenterAvoidanceController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed,
    Func<bool> currentTargetHasBossModule)
    : IBossModGoalZoneContributor
{
    private const float BossHitboxAvoidanceMargin = 0.35f;
    private const float CandidateExtraDistance = 0.9f;
    internal const float BossLikeHitboxRadius = 4f;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? lastGoalDelegate;
    private Vector2 lastTargetPosition;
    private float lastAvoidanceRadius;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";

    public string LastReason => this.lastReason;

    internal static float AvoidanceRadius(float hitboxRadius)
    {
        return Math.Max(1.25f, hitboxRadius + BossHitboxAvoidanceMargin);
    }

    internal static bool IsBossLikeHitbox(float hitboxRadius) => hitboxRadius >= BossLikeHitboxRadius;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        _ = this.hookState;

        if (!config.Enabled || !config.ManageMovement || !config.AvoidStandingInsideEnemies)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            return;
        }

        if (!currentTargetHasBossModule())
        {
            this.lastReason = "no boss module target";
            return;
        }

        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            this.lastReason = "boss target unavailable";
            return;
        }

        if (!IsBossLikeHitbox(target.HitboxRadius))
        {
            this.lastReason = "target hitbox not boss-like";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var avoidanceRadius = AvoidanceRadius(target.HitboxRadius);
        var player2 = new Vector2(player.Position.X, player.Position.Z);
        var target2 = new Vector2(target.Position.X, target.Position.Z);
        if (this.lastGoalDelegate == null ||
            Vector2.DistanceSquared(this.lastTargetPosition, target2) > 1f ||
            MathF.Abs(this.lastAvoidanceRadius - avoidanceRadius) > 0.1f)
        {
            this.lastGoalDelegate = this.CreateGoalDelegate(target2, avoidanceRadius);
            this.lastTargetPosition = target2;
            this.lastAvoidanceRadius = avoidanceRadius;
        }

        contributions.Add(new(this.lastGoalDelegate!, BossModGoalPriority.Convenience, "Boss center avoidance"));
        this.lastReason = Vector2.DistanceSquared(player2, target2) < avoidanceRadius * avoidanceRadius
            ? "avoiding boss center"
            : "biasing away from boss center";
    }

    public void Reset()
    {
        this.hookState = "unresolved";
        this.lastReason = "reset";
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastGoalDelegate = null;
        this.lastTargetPosition = default;
        this.lastAvoidanceRadius = 0f;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.resolvedWPosType != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR boss center goal reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private Delegate CreateGoalDelegate(Vector2 targetPosition, float avoidanceRadius)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(this.resolvedWPosType!, "p");
        var call = System.Linq.Expressions.Expression.Call(
            typeof(BossCenterAvoidanceController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Static | BindingFlags.NonPublic)!,
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, this.wposXField!), typeof(float)),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, this.wposZField!), typeof(float)),
            System.Linq.Expressions.Expression.Constant(targetPosition.X),
            System.Linq.Expressions.Expression.Constant(targetPosition.Y),
            System.Linq.Expressions.Expression.Constant(avoidanceRadius));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return System.Linq.Expressions.Expression.Lambda(delegateType, call, parameter).Compile();
    }

    private static float ScoreFromWPos(float x, float z, float targetX, float targetZ, float avoidanceRadius)
    {
        var dx = x - targetX;
        var dz = z - targetZ;
        var distance = MathF.Sqrt((dx * dx) + (dz * dz));
        if (distance <= avoidanceRadius)
        {
            return 0f;
        }

        var falloff = Math.Clamp((distance - avoidanceRadius) / CandidateExtraDistance, 0f, 1f);
        return GoalZoneScorePolicy.WeakPreference * falloff;
    }
}
