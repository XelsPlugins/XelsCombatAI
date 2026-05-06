using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal enum RsrAoeShape
{
    Circle = 2,
    Cone = 3,
    StraightLine = 4
}

internal sealed record RsrAoeActionSnapshot(
    uint ActionId,
    uint AdjustedActionId,
    string ActionName,
    RsrAoeShape Shape,
    float Range,
    float EffectRange,
    float HalfWidth,
    int AoeCount,
    ulong PrimaryTargetId,
    Vector3 PrimaryTargetPosition,
    float PrimaryTargetRadius,
    int AffectedTargetCount);

internal sealed class RotationSolverActionReflection(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
    private const string ActionUpdaterTypeName = "RotationSolver.Updaters.ActionUpdater";

    private Type? actionUpdaterType;
    private PropertyInfo? nextGcdActionProperty;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private string status = "unresolved";

    public string Status => this.status;

    public void Reset()
    {
        this.actionUpdaterType = null;
        this.nextGcdActionProperty = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.status = "unresolved";
    }

    public bool TryGetUpcomingGcd(bool requirePreview, out RsrAoeActionSnapshot snapshot, out string reason)
    {
        snapshot = default!;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var action = this.nextGcdActionProperty!.GetValue(null);
            if (action == null)
            {
                reason = "RSR next GCD unavailable";
                return false;
            }

            var actionType = action.GetType();
            var targetResult = GetPropertyValue(action, actionType, requirePreview ? "PreviewTarget" : "Target");
            if (targetResult == null && requirePreview)
            {
                reason = "RSR preview target unavailable";
                return false;
            }

            targetResult ??= GetPropertyValue(action, actionType, "Target");
            if (targetResult == null)
            {
                reason = "RSR target unavailable";
                return false;
            }

            var actionRow = GetPropertyValue(action, actionType, "Action");
            if (actionRow == null)
            {
                reason = "RSR action row unavailable";
                return false;
            }

            var castType = Convert.ToInt32(GetPropertyValue(actionRow, actionRow.GetType(), "CastType") ?? 0);
            if (castType is not 2 and not 3 and not 4)
            {
                reason = $"RSR unsupported cast type {castType}";
                return false;
            }

            var targetResultType = targetResult.GetType();
            var primaryTarget = GetPropertyValue(targetResult, targetResultType, "Target");
            if (primaryTarget == null)
            {
                reason = "RSR primary target unavailable";
                return false;
            }

            var primaryTargetType = primaryTarget.GetType();
            var primaryPosition = ReadVector3(GetPropertyValue(primaryTarget, primaryTargetType, "Position"));
            var primaryRadius = ReadFloat(GetPropertyValue(primaryTarget, primaryTargetType, "HitboxRadius"), 0f);
            var primaryId = ReadUlong(GetPropertyValue(primaryTarget, primaryTargetType, "GameObjectId"));
            if (primaryId == 0)
            {
                primaryId = ReadUlong(GetPropertyValue(primaryTarget, primaryTargetType, "EntityId"));
            }

            var affectedTargets = GetPropertyValue(targetResult, targetResultType, "AffectedTargets") as IEnumerable;
            var affectedCount = CountEnumerable(affectedTargets);
            var effectRange = ReadFloat(GetPropertyValue(actionRow, actionRow.GetType(), "EffectRange"), 0f);
            var xAxisModifier = ReadFloat(GetPropertyValue(actionRow, actionRow.GetType(), "XAxisModifier"), 0f);
            var targetInfo = GetPropertyValue(action, actionType, "TargetInfo");
            var range = targetInfo != null ? ReadFloat(GetPropertyValue(targetInfo, targetInfo.GetType(), "Range"), 25f) : 25f;
            var config = GetPropertyValue(action, actionType, "Config");
            var aoeCount = config != null ? Math.Max(1, Convert.ToInt32(GetPropertyValue(config, config.GetType(), "AoeCount") ?? 3)) : 3;
            var actionId = Convert.ToUInt32(GetPropertyValue(action, actionType, "ID") ?? 0u);
            var adjustedId = Convert.ToUInt32(GetPropertyValue(action, actionType, "AdjustedID") ?? actionId);
            var name = GetPropertyValue(actionRow, actionRow.GetType(), "Name")?.ToString() ?? adjustedId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            snapshot = new(
                actionId,
                adjustedId,
                name,
                (RsrAoeShape)castType,
                Math.Max(1f, range),
                Math.Max(1f, effectRange),
                xAxisModifier > 0f ? xAxisModifier / 2f : 2f,
                aoeCount,
                primaryId,
                primaryPosition,
                primaryRadius,
                affectedCount);
            reason = "available";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not query reflected RSR next GCD action: {ex.Message}");
            reason = "RSR action reflection query failed";
            return false;
        }
    }

    private bool EnsureResolved()
    {
        if (this.nextGcdActionProperty != null)
        {
            return true;
        }

        if (DateTime.UtcNow < this.nextResolveAttempt)
        {
            return false;
        }

        this.nextResolveAttempt = DateTime.UtcNow.AddSeconds(5);

        try
        {
            if (!IsRotationSolverLoaded(pluginInterface))
            {
                this.ResetWithStatus("RSR plugin not loaded");
                return false;
            }

            var type = FindActionUpdaterType();
            var property = type?.GetProperty("NextGCDAction", BindingFlags.Static | BindingFlags.NonPublic);
            if (type == null || property == null)
            {
                this.ResetWithStatus("RSR action updater not found");
                return false;
            }

            this.actionUpdaterType = type;
            this.nextGcdActionProperty = property;
            this.status = "available";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not resolve reflected RSR action integration: {ex.Message}");
            this.ResetWithStatus("RSR action reflection resolve failed");
            return false;
        }
    }

    private static Type? FindActionUpdaterType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(ActionUpdaterTypeName, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static bool IsRotationSolverLoaded(IDalamudPluginInterface pluginInterface)
    {
        if (pluginInterface.InstalledPlugins == null)
        {
            return false;
        }

        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (plugin.IsLoaded &&
                (string.Equals(plugin.InternalName, "RotationSolver", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.InternalName, "RotationSolverReborn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, "Rotation Solver Reborn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, "RotationSolver Reborn", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private void ResetWithStatus(string newStatus)
    {
        this.actionUpdaterType = null;
        this.nextGcdActionProperty = null;
        this.status = newStatus;
    }

    private static object? GetPropertyValue(object value, Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    }

    private static Vector3 ReadVector3(object? value)
    {
        return value is Vector3 vector ? vector : default;
    }

    private static float ReadFloat(object? value, float fallback)
    {
        return value == null ? fallback : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong ReadUlong(object? value)
    {
        return value == null ? 0UL : Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountEnumerable(IEnumerable? values)
    {
        if (values == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in values)
        {
            ++count;
        }

        return count;
    }
}
