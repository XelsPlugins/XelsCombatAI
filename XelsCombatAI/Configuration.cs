using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XelsCombatAI;

public sealed class Configuration : IPluginConfiguration
{
    public const float DefaultMeleeRange = 2.6f;
    public const float DefaultRangedRange = 10f;
    public const float DefaultPhysicalRangedRange = 10f;
    public const float DefaultHealerRange = 10f;
    public const float DefaultMagicRangedRange = 10f;
    public const float DefaultAoEMeleeRange = 2.6f;
    public const float DefaultAoERangedRange = 12f;
    public const int DefaultAoEEnemyThreshold = 2;
    public const float DefaultEnemyCountRadius = 15f;
    public const float DefaultPreferredForbiddenZoneDistance = 1f;

    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = false;
    public bool ManageMovement { get; set; } = true;
    public bool ManageRange { get; set; } = true;
    public bool ManageForbiddenZoneDistance { get; set; } = true;
    public bool ManagePartyRoleFollow { get; set; } = true;
    public bool ManagePositionals { get; set; } = true;
    public bool ManageLeylines { get; set; } = true;
    public bool UseBetweenTheLines { get; set; } = true;
    public bool UseRetrace { get; set; } = true;
    public bool ReturnToLeylines { get; set; } = true;
    public bool RoleBasedRange { get; set; } = true;
    public bool AoERangeInMultiTarget { get; set; } = true;
    public bool EchoStatusToChat { get; set; } = true;
    public float MeleeRange { get; set; } = DefaultMeleeRange;
    public float RangedRange { get; set; } = DefaultRangedRange;
    public float PhysicalRangedRange { get; set; } = DefaultPhysicalRangedRange;
    public float HealerRange { get; set; } = DefaultHealerRange;
    public float MagicRangedRange { get; set; } = DefaultMagicRangedRange;
    public float AoEMeleeRange { get; set; } = DefaultAoEMeleeRange;
    public float AoERangedRange { get; set; } = DefaultAoERangedRange;
    public int AoEEnemyThreshold { get; set; } = DefaultAoEEnemyThreshold;
    public float EnemyCountRadius { get; set; } = DefaultEnemyCountRadius;
    public float PreferredForbiddenZoneDistance { get; set; } = DefaultPreferredForbiddenZoneDistance;

    internal void Migrate()
    {
        if (this.Version < 2)
        {
            this.PhysicalRangedRange = this.RangedRange;
            this.HealerRange = this.RangedRange;
            this.MagicRangedRange = this.RangedRange;
            this.Version = 2;
        }
    }

    internal void Clamp()
    {
        this.MeleeRange = Math.Clamp(this.MeleeRange, 1f, 30f);
        this.RangedRange = Math.Clamp(this.RangedRange, 1f, 30f);
        this.PhysicalRangedRange = Math.Clamp(this.PhysicalRangedRange, 1f, 30f);
        this.HealerRange = Math.Clamp(this.HealerRange, 1f, 30f);
        this.MagicRangedRange = Math.Clamp(this.MagicRangedRange, 1f, 30f);
        this.AoEMeleeRange = Math.Clamp(this.AoEMeleeRange, 1f, 30f);
        this.AoERangedRange = Math.Clamp(this.AoERangedRange, 1f, 30f);
        this.AoEEnemyThreshold = Math.Clamp(this.AoEEnemyThreshold, 1, 10);
        this.EnemyCountRadius = Math.Clamp(this.EnemyCountRadius, 1f, 30f);
        this.PreferredForbiddenZoneDistance = Math.Clamp(this.PreferredForbiddenZoneDistance, 0f, 3f);
    }

    internal void ResetRanges()
    {
        this.MeleeRange = DefaultMeleeRange;
        this.RangedRange = DefaultRangedRange;
        this.PhysicalRangedRange = DefaultPhysicalRangedRange;
        this.HealerRange = DefaultHealerRange;
        this.MagicRangedRange = DefaultMagicRangedRange;
        this.AoEMeleeRange = DefaultAoEMeleeRange;
        this.AoERangedRange = DefaultAoERangedRange;
        this.AoEEnemyThreshold = DefaultAoEEnemyThreshold;
        this.EnemyCountRadius = DefaultEnemyCountRadius;
        this.PreferredForbiddenZoneDistance = DefaultPreferredForbiddenZoneDistance;
    }

    internal void ResetAll()
    {
        this.Enabled = false;
        this.ManageMovement = true;
        this.ManageRange = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManagePartyRoleFollow = true;
        this.ManagePositionals = true;
        this.ManageLeylines = true;
        this.UseBetweenTheLines = true;
        this.UseRetrace = true;
        this.ReturnToLeylines = true;
        this.RoleBasedRange = true;
        this.AoERangeInMultiTarget = true;
        this.EchoStatusToChat = true;
        this.ResetRanges();
    }

    internal void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
