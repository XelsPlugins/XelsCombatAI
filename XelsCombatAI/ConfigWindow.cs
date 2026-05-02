using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XelsCombatAI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Action resetRuntimeState;
    private readonly Action<bool> setEnabled;
    private readonly Func<string?> dependencyWarning;

    public ConfigWindow(Configuration config, Action save, Action resetRuntimeState, Action<bool> setEnabled, Func<string?> dependencyWarning)
        : base("Xel's Combat AI Configuration###XelsCombatAIConfig")
    {
        this.config = config;
        this.save = save;
        this.resetRuntimeState = resetRuntimeState;
        this.setEnabled = setEnabled;
        this.dependencyWarning = dependencyWarning;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(420, 280),
            MaximumSize = new(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var dependencyWarningText = this.dependencyWarning();

        if (dependencyWarningText != null)
        {
            ImGui.TextColored(0xff4040ff, $"Cannot enable: {dependencyWarningText}");
            ImGui.Separator();
        }

        this.DrawEnabledCheckbox(dependencyWarningText);
        changed |= this.Checkbox("Manage movement in combat", this.config.ManageMovement, v => this.config.ManageMovement = v);

        changed |= this.Checkbox("Manage range", this.config.ManageRange, v => this.config.ManageRange = v);
        if (this.config.ManageRange)
        {
            ImGui.Indent();
            changed |= this.Checkbox("Role-based range", this.config.RoleBasedRange, v => this.config.RoleBasedRange = v);
            if (this.config.RoleBasedRange)
            {
                ImGui.Indent();
                changed |= this.SliderFloat("Melee range", this.config.MeleeRange, 1f, 30f, v => this.config.MeleeRange = v);
                changed |= this.SliderFloat("Physical ranged range", this.config.PhysicalRangedRange, 1f, 30f, v => this.config.PhysicalRangedRange = v);
                changed |= this.SliderFloat("Healer range", this.config.HealerRange, 1f, 30f, v => this.config.HealerRange = v);
                changed |= this.SliderFloat("Magic ranged range", this.config.MagicRangedRange, 1f, 30f, v => this.config.MagicRangedRange = v);
                ImGui.Unindent();
            }

            changed |= this.Checkbox("AoE range in multi-target", this.config.AoERangeInMultiTarget, v => this.config.AoERangeInMultiTarget = v);
            if (this.config.AoERangeInMultiTarget)
            {
                ImGui.Indent();
                changed |= this.SliderFloat("AoE melee range", this.config.AoEMeleeRange, 1f, 30f, v => this.config.AoEMeleeRange = v);
                changed |= this.SliderFloat("AoE ranged range", this.config.AoERangedRange, 1f, 30f, v => this.config.AoERangedRange = v);
                changed |= this.SliderInt("AoE enemy threshold", this.config.AoEEnemyThreshold, 1, 10, v => this.config.AoEEnemyThreshold = v);
                changed |= this.SliderFloat("Enemy count radius", this.config.EnemyCountRadius, 1f, 30f, v => this.config.EnemyCountRadius = v);
                ImGui.Unindent();
            }

            ImGui.Unindent();
        }

        changed |= this.Checkbox("Manage forbidden-zone distance", this.config.ManageForbiddenZoneDistance, v => this.config.ManageForbiddenZoneDistance = v);
        if (this.config.ManageForbiddenZoneDistance)
        {
            ImGui.Indent();
            changed |= this.SliderFloat("Preferred distance to forbidden zones", this.config.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v);
            ImGui.Unindent();
        }

        changed |= this.Checkbox("Manage party-role follow", this.config.ManagePartyRoleFollow, v => this.config.ManagePartyRoleFollow = v);
        changed |= this.Checkbox("Manage positionals", this.config.ManagePositionals, v => this.config.ManagePositionals = v);
        changed |= this.Checkbox("Manage Ley Lines", this.config.ManageLeylines, v => this.config.ManageLeylines = v);
        if (this.config.ManageLeylines)
        {
            ImGui.Indent();
            changed |= this.Checkbox("Use Between the Lines", this.config.UseBetweenTheLines, v => this.config.UseBetweenTheLines = v);
            changed |= this.Checkbox("Use Retrace", this.config.UseRetrace, v => this.config.UseRetrace = v);
            changed |= this.Checkbox("Walk back to Ley Lines", this.config.ReturnToLeylines, v => this.config.ReturnToLeylines = v);
            ImGui.Unindent();
        }

        changed |= this.Checkbox("Echo command status to chat", this.config.EchoStatusToChat, v => this.config.EchoStatusToChat = v);

        ImGui.Separator();

        if (ImGui.Button("Reset ranges"))
        {
            this.config.ResetRanges();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset all"))
        {
            this.config.ResetAll();
            changed = true;
        }

        if (changed)
        {
            this.config.Clamp();
            this.resetRuntimeState();
            this.save();
        }
    }

    private void DrawEnabledCheckbox(string? dependencyWarningText)
    {
        var current = this.config.Enabled;
        if (!ImGui.Checkbox("Enabled", ref current))
        {
            return;
        }

        if (current && dependencyWarningText != null)
        {
            this.setEnabled(false);
            return;
        }

        this.setEnabled(current);
    }

    private bool Checkbox(string label, bool value, Action<bool> setter)
    {
        var current = value;
        if (!ImGui.Checkbox(label, ref current))
        {
            return false;
        }

        setter(current);
        return true;
    }

    private bool SliderFloat(string label, float value, float min, float max, Action<float> setter)
    {
        var current = value;
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.SliderFloat($"##{label}", ref current, min, max))
        {
            return false;
        }

        setter(current);
        return true;
    }

    private bool SliderInt(string label, int value, int min, int max, Action<int> setter)
    {
        var current = value;
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.SliderInt($"##{label}", ref current, min, max))
        {
            return false;
        }

        setter(current);
        return true;
    }
}
