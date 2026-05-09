using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;

namespace XelsCombatAI.Combat;

internal sealed class ArenaEdgePositioningController(Configuration config, DalamudServices services)
    : IBossModGoalZoneContributor
{
    private const float EdgeBand = 2.5f;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ScoreCircleMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreCircle), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ScoreRectMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreRect), BindingFlags.Static | BindingFlags.NonPublic)!;

    private FieldInfo? centerField;
    private FieldInfo? boundsField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private FieldInfo? radiusField;
    private Type? resolvedHintsType;
    private Delegate? lastGoalDelegate;
    private object? lastBounds;
    private float lastCenterX;
    private float lastCenterZ;
    private string hookState = "unresolved";

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        _ = this.hookState;
        if (!config.Enabled || !config.ManageMovement || !config.AvoidArenaEdge)
        {
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var center = this.centerField!.GetValue(hints);
        var bounds = this.boundsField!.GetValue(hints);
        if (center == null || bounds == null)
        {
            return;
        }

        var centerX = ReadFloat(this.wposXField!.GetValue(center));
        var centerZ = ReadFloat(this.wposZField!.GetValue(center));
        if (this.lastGoalDelegate == null ||
            !ReferenceEquals(this.lastBounds, bounds) ||
            MathF.Abs(this.lastCenterX - centerX) > 0.1f ||
            MathF.Abs(this.lastCenterZ - centerZ) > 0.1f)
        {
            if (!this.TryCreateGoalDelegate(bounds, centerX, centerZ, out this.lastGoalDelegate))
            {
                return;
            }

            this.lastBounds = bounds;
            this.lastCenterX = centerX;
            this.lastCenterZ = centerZ;
        }

        if (this.lastGoalDelegate != null)
        {
            contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Convenience, "Arena edge avoidance"));
        }
    }

    public void Reset()
    {
        this.centerField = null;
        this.boundsField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.radiusField = null;
        this.resolvedHintsType = null;
        this.lastGoalDelegate = null;
        this.lastBounds = null;
        this.lastCenterX = 0f;
        this.lastCenterZ = 0f;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.centerField != null &&
            this.boundsField != null &&
            this.wposXField != null &&
            this.wposZField != null &&
            this.radiusField != null)
        {
            return true;
        }

        var center = hintsType.GetField("PathfindMapCenter", InstanceFlags);
        var bounds = hintsType.GetField("PathfindMapBounds", InstanceFlags);
        var wposType = center?.FieldType;
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        var radius = bounds?.FieldType.GetField("Radius", InstanceFlags);
        if (center == null || bounds == null || xField == null || zField == null || radius == null)
        {
            return false;
        }

        this.centerField = center;
        this.boundsField = bounds;
        this.wposXField = xField;
        this.wposZField = zField;
        this.radiusField = radius;
        this.resolvedHintsType = hintsType;
        this.lastGoalDelegate = null;
        this.lastBounds = null;
        return true;
    }

    private bool TryCreateGoalDelegate(object bounds, float centerX, float centerZ, out Delegate? goal)
    {
        goal = null;
        var radius = ReadFloat(this.radiusField!.GetValue(bounds));
        if (radius <= EdgeBand + 1f)
        {
            return false;
        }

        var boundsType = bounds.GetType();
        var wposType = this.wposXField!.DeclaringType!;
        var parameter = Expression.Parameter(wposType, "p");
        var x = Expression.Convert(Expression.Field(parameter, this.wposXField), typeof(float));
        var z = Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float));

        var halfWidthField = boundsType.GetField("HalfWidth", InstanceFlags);
        var halfHeightField = boundsType.GetField("HalfHeight", InstanceFlags);
        var orientationField = boundsType.GetField("Orientation", InstanceFlags);
        var orientation = orientationField?.GetValue(bounds);
        var orientationXField = orientation?.GetType().GetField("X", InstanceFlags);
        var orientationZField = orientation?.GetType().GetField("Z", InstanceFlags);

        Expression call;
        if (halfWidthField != null && halfHeightField != null && orientation != null && orientationXField != null && orientationZField != null)
        {
            var halfWidth = ReadFloat(halfWidthField.GetValue(bounds));
            var halfHeight = ReadFloat(halfHeightField.GetValue(bounds));
            var orientationX = ReadFloat(orientationXField.GetValue(orientation));
            var orientationZ = ReadFloat(orientationZField.GetValue(orientation));
            call = Expression.Call(
                ScoreRectMethod,
                x,
                z,
                Expression.Constant(centerX),
                Expression.Constant(centerZ),
                Expression.Constant(halfWidth),
                Expression.Constant(halfHeight),
                Expression.Constant(orientationX),
                Expression.Constant(orientationZ));
        }
        else
        {
            call = Expression.Call(
                ScoreCircleMethod,
                x,
                z,
                Expression.Constant(centerX),
                Expression.Constant(centerZ),
                Expression.Constant(radius));
        }

        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        goal = Expression.Lambda(delegateType, call, parameter).Compile();
        return true;
    }

    private static float ScoreCircle(float x, float z, float centerX, float centerZ, float radius)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        var edgeDistance = radius - MathF.Sqrt(dx * dx + dz * dz);
        return ScoreEdgeDistance(edgeDistance);
    }

    private static float ScoreRect(float x, float z, float centerX, float centerZ, float halfWidth, float halfHeight, float orientationX, float orientationZ)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        var parallel = dx * orientationX + dz * orientationZ;
        var ortho = dx * orientationZ - dz * orientationX;
        var edgeDistance = MathF.Min(halfHeight - MathF.Abs(parallel), halfWidth - MathF.Abs(ortho));
        return ScoreEdgeDistance(edgeDistance);
    }

    private static float ScoreEdgeDistance(float edgeDistance)
    {
        if (edgeDistance <= 0f)
        {
            return 0f;
        }

        if (edgeDistance >= EdgeBand)
        {
            return GoalZoneScorePolicy.WeakPreference;
        }

        return GoalZoneScorePolicy.WeakPreference * edgeDistance / EdgeBand;
    }

    private static float ReadFloat(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }
}
