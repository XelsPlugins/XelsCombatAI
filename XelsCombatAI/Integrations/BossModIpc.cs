using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XelsCombatAI.Integrations;

internal sealed class BossModIpc
{
    public const string DefaultPresetName = "Xel's Combat AI";

    private const string PresetPayload = """
    {
      "Name": "Xel's Combat AI",
      "Modules": {
        "BossMod.Autorotation.MiscAI.StayCloseToTarget": [],
        "BossMod.Autorotation.MiscAI.StayWithinLeylines": [
          {
            "Track": "Use Between The Lines",
            "Option": "Yes"
          },
          {
            "Track": "Use Retrace",
            "Option": "Yes"
          }
        ],
        "BossMod.Autorotation.MiscAI.GoToPositional": [],
        "BossMod.Autorotation.MiscAI.NormalMovement": [
          {
            "Track": "Destination",
            "Option": "Pathfind"
          },
          {
            "Track": "ForbiddenZoneCushion",
            "Option": "Medium"
          }
        ]
      }
    }
    """;

    private static readonly string[] RequiredPresetModules =
    [
        "BossMod.Autorotation.MiscAI.StayCloseToTarget",
        "BossMod.Autorotation.MiscAI.StayWithinLeylines",
        "BossMod.Autorotation.MiscAI.GoToPositional",
        "BossMod.Autorotation.MiscAI.NormalMovement"
    ];

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string?> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ICallGateSubscriber<uint, bool> hasModuleByDataId;
    private readonly ICallGateSubscriber<string, bool, bool> disableModule;
    private readonly ICallGateSubscriber<string, string, string, string, bool> addTransientStrategy;
    private readonly ICallGateSubscriber<string, string, string, bool> clearTransientStrategy;
    private readonly ICallGateSubscriber<string, bool> clearTransientPresetStrategies;

    public BossModIpc(IDalamudPluginInterface pluginInterface)
    {
        this.getPreset = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
        this.createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        this.getActivePreset = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
        this.setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        this.clearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        this.hasModuleByDataId = pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId");
        this.disableModule = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Configuration.DisableModule");
        this.addTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
        this.clearTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy");
        this.clearTransientPresetStrategies = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.ClearTransientPresetStrategies");
    }

    public bool EnsurePreset()
    {
        var preset = this.getPreset.InvokeFunc(DefaultPresetName);
        if (preset != null && RequiredPresetModules.All(preset.Contains))
        {
            return true;
        }

        return this.createPreset.InvokeFunc(PresetPayload, true);
    }

    public bool IsAvailable()
    {
        try
        {
            _ = this.getPreset.InvokeFunc(DefaultPresetName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetActive(string presetName) => this.setActivePreset.InvokeFunc(presetName);

    public string? GetActive() => this.getActivePreset.InvokeFunc();

    public bool ClearActive() => this.clearActivePreset.InvokeFunc();

    public bool SetPositional(string presetName, Positional positional)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.GoToPositional",
            "Positional",
            positional.ToString());
    }

    public bool SetRange(string presetName, float range)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToTarget",
            "range",
            MathF.Round(range, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool SetMovement(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Destination",
            enabled ? "Pathfind" : "None");
    }

    public bool SetForbiddenZoneCushion(string presetName, string cushion)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "ForbiddenZoneCushion",
            cushion);
    }

    public bool SetMovementRangeStrategy(string presetName, string strategy)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Range",
            strategy);
    }

    public bool SetLeylinesBetweenTheLines(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Between The Lines",
            enabled ? "Yes" : "No");
    }

    public bool SetLeylinesRetrace(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Retrace",
            enabled ? "Yes" : "No");
    }

    public bool SetLeylinesGoal(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Goal",
            enabled ? "Enabled" : "Disabled");
    }

    public bool ClearTransientStrategy(string presetName, string moduleTypeName, string trackName)
    {
        return this.clearTransientStrategy.InvokeFunc(presetName, moduleTypeName, trackName);
    }

    public bool ClearTransientPresetStrategies(string presetName)
    {
        return this.clearTransientPresetStrategies.InvokeFunc(presetName);
    }

    public bool HasModuleByDataId(uint dataId) => this.hasModuleByDataId.InvokeFunc(dataId);

    public bool DisableModule(string moduleName, bool disabled) => this.disableModule.InvokeFunc(moduleName, disabled);
}
