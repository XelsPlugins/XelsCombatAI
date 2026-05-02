using System;
using System.Collections.Generic;
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
    private readonly HashSet<string> editingSliders = [];
    private readonly HashSet<string> openSections = [];

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

        // Manage range
        if (this.CollapsingCheckbox("Manage range", this.config.ManageRange, v => { this.config.ManageRange = v; changed = true; }))
        {
            ImGui.Indent();

            // Single target distance
            if (this.CollapsingCheckbox("Single target distance", this.config.RoleBasedRange, v => { this.config.RoleBasedRange = v; changed = true; }, this.config.ManageRange))
            {
                ImGui.Indent();
                changed |= this.SliderFloat("Melee max distance", this.config.MeleeRange, 1f, 30f, v => this.config.MeleeRange = v);
                changed |= this.SliderFloat("Physical ranged max distance", this.config.PhysicalRangedRange, 1f, 30f, v => this.config.PhysicalRangedRange = v);
                changed |= this.SliderFloat("Healer max distance", this.config.HealerRange, 1f, 30f, v => this.config.HealerRange = v);
                changed |= this.SliderFloat("Magic ranged max distance", this.config.MagicRangedRange, 1f, 30f, v => this.config.MagicRangedRange = v);
                ImGui.Unindent();
            }

            // AoE range in multi-target
            if (this.CollapsingCheckbox("AoE target distance", this.config.AoERangeInMultiTarget, v => { this.config.AoERangeInMultiTarget = v; changed = true; }, this.config.ManageRange))
            {
                ImGui.Indent();
                if (this.config.RoleBasedRange)
                    changed |= this.Checkbox("WHM/SCH/SGE use melee AoE range", this.config.AoEHealerMeleeRange, v => this.config.AoEHealerMeleeRange = v);
                changed |= this.SliderFloat("AoE melee max distance", this.config.AoEMeleeRange, 1f, 30f, v => this.config.AoEMeleeRange = v);
                if (this.config.RoleBasedRange)
                {
                    changed |= this.SliderFloat("AoE physical ranged max distance", this.config.AoEPhysicalRangedRange, 1f, 30f, v => this.config.AoEPhysicalRangedRange = v);
                    changed |= this.SliderFloat("AoE healer max distance", this.config.AoEHealerRange, 1f, 30f, v => this.config.AoEHealerRange = v);
                    changed |= this.SliderFloat("AoE magic ranged max distance", this.config.AoEMagicRangedRange, 1f, 30f, v => this.config.AoEMagicRangedRange = v);
                }
                else
                {
                    changed |= this.SliderFloat("AoE ranged max distance", this.config.AoERangedRange, 1f, 30f, v => this.config.AoERangedRange = v);
                }
                changed |= this.SliderInt("AoE enemy threshold", this.config.AoEEnemyThreshold, 1, 10, v => this.config.AoEEnemyThreshold = v);
                ImGui.Unindent();
            }

            ImGui.Unindent();
        }

        // Manage forbidden-zone distance
        if (this.CollapsingCheckbox("Manage forbidden-zone distance", this.config.ManageForbiddenZoneDistance, v => { this.config.ManageForbiddenZoneDistance = v; changed = true; }))
        {
            ImGui.Indent();
            changed |= this.SliderFloat("Preferred distance to forbidden zones", this.config.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v);
            ImGui.Unindent();
        }

        changed |= this.Checkbox("Manage party-role follow", this.config.ManagePartyRoleFollow, v => this.config.ManagePartyRoleFollow = v);
        changed |= this.Checkbox("Manage positionals", this.config.ManagePositionals, v => this.config.ManagePositionals = v);

        // Manage Ley Lines
        if (this.CollapsingCheckbox("Manage Ley Lines", this.config.ManageLeylines, v => { this.config.ManageLeylines = v; changed = true; }, tooltip: "Only manages existing Ley Lines placement — will not put down Ley Lines."))
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

    // Renders a toggle triangle button + checkbox on the same line. Triangle is disabled (and
    // section forced closed) when the parent feature is off. Returns true when expanded.
    private bool CollapsingCheckbox(string label, bool value, Action<bool> setter, bool enabled = true, string? tooltip = null)
    {
        if (!enabled)
            this.openSections.Remove(label);

        var isOpen = this.openSections.Contains(label);
        var arrow = isOpen ? "v" : ">";

        if (!enabled)
            ImGui.BeginDisabled();

        ImGui.PushID($"##{label}arrow");
        if (ImGui.SmallButton(arrow))
        {
            if (isOpen)
                this.openSections.Remove(label);
            else
                this.openSections.Add(label);
        }
        ImGui.PopID();

        if (!enabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        var current = value;
        if (ImGui.Checkbox(label, ref current))
            setter(current);
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        return isOpen && enabled && value;
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
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);

        if (this.editingSliders.Contains(label))
        {
            var input = value;
            ImGui.SetKeyboardFocusHere(0);
            if (ImGui.InputFloat(id, ref input, 0f, 0f, default, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                this.editingSliders.Remove(label);
                input = Math.Clamp(input, min, max);
                setter(input);
                return true;
            }

            if (ImGui.IsItemDeactivated())
                this.editingSliders.Remove(label);

            return false;
        }

        var current = value;
        if (!ImGui.SliderFloat(id, ref current, min, max))
        {
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                this.editingSliders.Add(label);
            return false;
        }

        setter(current);
        return true;
    }

    private bool SliderInt(string label, int value, int min, int max, Action<int> setter)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);

        if (this.editingSliders.Contains(label))
        {
            var input = value;
            ImGui.SetKeyboardFocusHere(0);
            if (ImGui.InputInt(id, ref input, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                this.editingSliders.Remove(label);
                input = Math.Clamp(input, min, max);
                setter(input);
                return true;
            }

            if (ImGui.IsItemDeactivated())
                this.editingSliders.Remove(label);

            return false;
        }

        var current = value;
        if (!ImGui.SliderInt(id, ref current, min, max))
        {
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                this.editingSliders.Add(label);
            return false;
        }

        setter(current);
        return true;
    }
}
