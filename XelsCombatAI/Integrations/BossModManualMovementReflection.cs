using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModManualMovementReflection(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
    private const string BossModPluginTypeName = "BossMod.Plugin";
    private const string BossModMovementOverrideTypeName = "BossMod.MovementOverride";

    private FieldInfo? instanceField;
    private MethodInfo? isMoveRequestedMethod;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private string status = "unresolved";

    public string Status => this.status;

    public void Reset()
    {
        this.instanceField = null;
        this.isMoveRequestedMethod = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.status = "unresolved";
    }

    public bool IsManualMovementRequested()
    {
        if (!this.EnsureResolved())
        {
            return false;
        }

        try
        {
            var instance = this.instanceField!.GetValue(null);
            if (instance == null)
            {
                this.status = "BMR movement override unavailable";
                return false;
            }

            var value = this.isMoveRequestedMethod!.Invoke(instance, null);
            if (value is bool requested)
            {
                this.status = "available";
                return requested;
            }

            this.status = "BMR manual movement query returned invalid value";
            return false;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not query reflected BossMod manual movement input: {ex.Message}");
            this.ResetWithStatus("BMR manual movement query failed");
            return false;
        }
    }

    private bool EnsureResolved()
    {
        if (this.instanceField != null && this.isMoveRequestedMethod != null)
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
            var plugin = this.FindBossModPlugin();
            if (plugin == null)
            {
                this.ResetWithStatus("BMR plugin instance not found");
                return false;
            }

            var movementOverrideType = plugin.GetType().Assembly.GetType(BossModMovementOverrideTypeName);
            if (movementOverrideType == null)
            {
                this.ResetWithStatus("BMR movement override type not found");
                return false;
            }

            var instance = movementOverrideType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            var isMoveRequested = movementOverrideType.GetMethod("IsMoveRequested", BindingFlags.Instance | BindingFlags.Public);
            if (instance == null || isMoveRequested == null || isMoveRequested.ReturnType != typeof(bool))
            {
                this.ResetWithStatus("BMR manual movement members not found");
                return false;
            }

            this.instanceField = instance;
            this.isMoveRequestedMethod = isMoveRequested;
            this.status = "available";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not resolve reflected BossMod manual movement integration: {ex.Message}");
            this.ResetWithStatus("BMR manual movement resolve failed");
            return false;
        }
    }

    private void ResetWithStatus(string newStatus)
    {
        var oldStatus = this.status;
        this.instanceField = null;
        this.isMoveRequestedMethod = null;
        this.status = newStatus;
        if (!string.Equals(oldStatus, newStatus, StringComparison.Ordinal))
        {
            log.Verbose($"BossMod reflected manual movement unavailable: {newStatus}");
        }
    }

    private object? FindBossModPlugin()
    {
        if (pluginInterface.InstalledPlugins != null)
        {
            foreach (var plugin in pluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded)
                {
                    continue;
                }

                if (!string.Equals(plugin.InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.InternalName, "BossMod", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.Name, "BossMod Reborn", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.Name, "BossMod", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var found = FindObject(plugin, BossModPluginTypeName, maxDepth: 8);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return FindObject(pluginInterface, BossModPluginTypeName, maxDepth: 8);
    }

    private static object? FindObject(object root, string typeFullName, int maxDepth)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!visited.Add(value))
            {
                continue;
            }

            var type = value.GetType();
            if (type.FullName == typeFullName)
            {
                return value;
            }

            if (depth >= maxDepth || ShouldSkip(type))
            {
                continue;
            }

            foreach (var child in EnumerateChildren(value, type))
            {
                if (child != null)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateChildren(object value, Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(Flags))
        {
            if (field.IsStatic || ShouldSkip(field.FieldType))
            {
                continue;
            }

            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        foreach (var property in type.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead || ShouldSkip(property.PropertyType))
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        if (value is not IEnumerable enumerable || value is string)
        {
            yield break;
        }

        foreach (var child in enumerable)
        {
            yield return child;
        }
    }

    private static bool ShouldSkip(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type.FullName?.StartsWith("System.", StringComparison.Ordinal) == true;
    }
}
