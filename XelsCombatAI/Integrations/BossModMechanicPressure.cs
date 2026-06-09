using System;
using System.Globalization;

namespace XelsCombatAI.Integrations;

internal enum BossModMechanicPressureKind
{
    None,
    Pyretic,
    NoMovement,
    Freezing,
    Misdirection,
    Knockback,
    Tankbuster,
    Raidwide,
    SharedDamage,
    Damage,
    Downtime,
    Vulnerable
}

internal enum BossModPredictedDamageType
{
    None = 0,
    Tankbuster = 1,
    Raidwide = 2,
    Shared = 3
}

internal enum BossModSpecialMode
{
    Normal = 0,
    Pyretic = 1,
    NoMovement = 2,
    Freezing = 3,
    Misdirection = 4
}

internal sealed record BossModMechanicPressure(
    float BMRRaidwideIn,
    float BMRTankbusterIn,
    float BMRKnockbackIn,
    float BMRDamageIn,
    float BMRDowntimeIn,
    float BMRDowntimeEndIn,
    float BMRVulnerableIn,
    float BMRVulnerableEndIn,
    float BMRRaidwideDamageIn,
    float BMRTankbusterDamageIn,
    int BMRNextDamageType,
    float BMRSpecialModeIn,
    int BMRSpecialModeType,
    bool BMRHasActiveModule,
    string? BMRActiveModuleName,
    string BMRTimelineDebug,
    DateTime KnockbackRecoveryUntilUtc)
{
    public const float RaidwidePressureSeconds = 3f;
    public const float DamagePressureSeconds = 3f;
    public const float TankbusterPressureSeconds = 4f;
    public const float TankbusterHardPressureSeconds = 1.5f;
    public const float KnockbackReserveSeconds = 5f;
    public const float KnockbackHardPressureSeconds = 1.5f;
    public const float DowntimePressureSeconds = 3f;
    public const float VulnerablePressureSeconds = 3f;
    public const float MovementLockPressureSeconds = 1f;
    public const float FreezingPressureSeconds = 0.5f;

    public static BossModMechanicPressure None { get; } = new(
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        0,
        float.MaxValue,
        0,
        false,
        null,
        "<unavailable>",
        DateTime.MinValue);

    public BossModPredictedDamageType NextDamageType => Enum.IsDefined(typeof(BossModPredictedDamageType), this.BMRNextDamageType)
        ? (BossModPredictedDamageType)this.BMRNextDamageType
        : BossModPredictedDamageType.None;
    public BossModSpecialMode SpecialMode => Enum.IsDefined(typeof(BossModSpecialMode), this.BMRSpecialModeType)
        ? (BossModSpecialMode)this.BMRSpecialModeType
        : BossModSpecialMode.Normal;
    public bool RaidwideSoon => IsSoon(this.BMRRaidwideIn, RaidwidePressureSeconds) || IsSoon(this.BMRRaidwideDamageIn, RaidwidePressureSeconds);
    public bool TankbusterSoon => IsSoon(this.BMRTankbusterIn, TankbusterPressureSeconds) || IsSoon(this.BMRTankbusterDamageIn, TankbusterPressureSeconds);
    public bool KnockbackSoon => IsSoon(this.BMRKnockbackIn, KnockbackReserveSeconds);
    public bool DamageSoon => IsSoon(this.BMRDamageIn, DamagePressureSeconds);
    public bool DowntimeSoon => IsSoon(this.BMRDowntimeIn, DowntimePressureSeconds);
    public bool VulnerableSoon => IsSoon(this.BMRVulnerableIn, VulnerablePressureSeconds);
    public bool SharedDamageSoon => this.NextDamageType == BossModPredictedDamageType.Shared && this.DamageSoon;
    public bool MovementLockSoon => (this.SpecialMode is BossModSpecialMode.Pyretic or BossModSpecialMode.NoMovement) &&
                                    IsActiveOrSoon(this.BMRSpecialModeIn, MovementLockPressureSeconds);
    public bool FreezingSoon => this.SpecialMode == BossModSpecialMode.Freezing &&
                                IsActiveOrSoon(this.BMRSpecialModeIn, FreezingPressureSeconds);
    public bool MisdirectionActive => this.SpecialMode == BossModSpecialMode.Misdirection &&
                                      IsActiveOrSoon(this.BMRSpecialModeIn, 0f);
    public bool HardTankbusterSoon => IsSoon(this.BMRTankbusterIn, TankbusterHardPressureSeconds) || IsSoon(this.BMRTankbusterDamageIn, TankbusterHardPressureSeconds);
    public bool HardKnockbackSoon => IsSoon(this.BMRKnockbackIn, KnockbackHardPressureSeconds);
    public bool KnockbackRecoveryActive => DateTime.UtcNow <= this.KnockbackRecoveryUntilUtc;
    public bool RaidwideOrDamageSoon => this.RaidwideSoon || this.DamageSoon;
    public bool BadForOptionalMovement => this.MovementLockSoon || this.KnockbackSoon || this.RaidwideOrDamageSoon || this.DowntimeSoon;
    public bool BadForGreedyDash => this.MovementLockSoon || this.KnockbackSoon || this.RaidwideOrDamageSoon || this.DowntimeSoon;
    public bool TankStabilityPressure => this.TankbusterSoon;

    public BossModMechanicPressureKind PrimaryPressure
    {
        get
        {
            if (this.MovementLockSoon)
            {
                return this.SpecialMode == BossModSpecialMode.Pyretic
                    ? BossModMechanicPressureKind.Pyretic
                    : BossModMechanicPressureKind.NoMovement;
            }

            if (this.FreezingSoon)
            {
                return BossModMechanicPressureKind.Freezing;
            }

            if (this.MisdirectionActive)
            {
                return BossModMechanicPressureKind.Misdirection;
            }

            if (this.KnockbackSoon)
            {
                return BossModMechanicPressureKind.Knockback;
            }

            if (this.TankbusterSoon)
            {
                return BossModMechanicPressureKind.Tankbuster;
            }

            if (this.RaidwideSoon)
            {
                return BossModMechanicPressureKind.Raidwide;
            }

            if (this.SharedDamageSoon)
            {
                return BossModMechanicPressureKind.SharedDamage;
            }

            if (this.DamageSoon)
            {
                return BossModMechanicPressureKind.Damage;
            }

            if (this.DowntimeSoon)
            {
                return BossModMechanicPressureKind.Downtime;
            }

            return this.VulnerableSoon
                ? BossModMechanicPressureKind.Vulnerable
                : BossModMechanicPressureKind.None;
        }
    }

    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"{this.PrimaryPressure}: module={this.BMRActiveModuleName ?? "<none>"}, raidwide={FormatTimer(this.BMRRaidwideIn)}/{FormatTimer(this.BMRRaidwideDamageIn)}, tankbuster={FormatTimer(this.BMRTankbusterIn)}/{FormatTimer(this.BMRTankbusterDamageIn)}, knockback={FormatTimer(this.BMRKnockbackIn)}, damage={FormatTimer(this.BMRDamageIn)}:{this.NextDamageType}, special={this.SpecialMode}@{FormatTimer(this.BMRSpecialModeIn)}, downtime={FormatTimer(this.BMRDowntimeIn)}, vulnerable={FormatTimer(this.BMRVulnerableIn)}, timeline={FormatTimelineSummary(this.BMRTimelineDebug)}, kbRecovery={this.KnockbackRecoveryActive}");

    public BossModMechanicPressure WithKnockbackRecoveryUntil(DateTime recoveryUntilUtc)
    {
        return this with { KnockbackRecoveryUntilUtc = recoveryUntilUtc };
    }

    public string FormatOptionalMovementHoldReason()
    {
        return this.PrimaryPressure switch
        {
            BossModMechanicPressureKind.Pyretic => string.Create(CultureInfo.InvariantCulture, $"held: pyretic in {this.BMRSpecialModeIn:0.0}s"),
            BossModMechanicPressureKind.NoMovement => string.Create(CultureInfo.InvariantCulture, $"held: no-movement in {this.BMRSpecialModeIn:0.0}s"),
            BossModMechanicPressureKind.Freezing => string.Create(CultureInfo.InvariantCulture, $"held: freezing movement check in {this.BMRSpecialModeIn:0.0}s"),
            BossModMechanicPressureKind.Misdirection => "held: misdirection active",
            BossModMechanicPressureKind.Knockback => string.Create(CultureInfo.InvariantCulture, $"held: knockback in {this.BMRKnockbackIn:0.0}s"),
            BossModMechanicPressureKind.Raidwide => string.Create(CultureInfo.InvariantCulture, $"held: raidwide in {FormatNearest(this.BMRRaidwideIn, this.BMRRaidwideDamageIn)}s"),
            BossModMechanicPressureKind.SharedDamage => string.Create(CultureInfo.InvariantCulture, $"held: shared damage in {this.BMRDamageIn:0.0}s"),
            BossModMechanicPressureKind.Damage => string.Create(CultureInfo.InvariantCulture, $"held: damage in {this.BMRDamageIn:0.0}s"),
            BossModMechanicPressureKind.Downtime => string.Create(CultureInfo.InvariantCulture, $"held: downtime in {this.BMRDowntimeIn:0.0}s"),
            BossModMechanicPressureKind.Tankbuster => string.Create(CultureInfo.InvariantCulture, $"held: tankbuster in {FormatNearest(this.BMRTankbusterIn, this.BMRTankbusterDamageIn)}s"),
            BossModMechanicPressureKind.Vulnerable => string.Create(CultureInfo.InvariantCulture, $"held: vulnerability window in {this.BMRVulnerableIn:0.0}s"),
            _ => "no mechanic pressure"
        };
    }

    private static bool IsSoon(float value, float threshold)
    {
        return float.IsFinite(value) && value > 0f && value <= threshold;
    }

    private static bool IsActiveOrSoon(float value, float threshold)
    {
        return float.IsFinite(value) && value <= threshold;
    }

    private static string FormatTimer(float value)
    {
        return float.IsFinite(value) && value < float.MaxValue / 2f
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private static string FormatNearest(float first, float second)
    {
        var nearest = MathF.Min(
            IsFiniteTimer(first) ? first : float.MaxValue,
            IsFiniteTimer(second) ? second : float.MaxValue);
        return IsFiniteTimer(nearest)
            ? nearest.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private static bool IsFiniteTimer(float value)
    {
        return float.IsFinite(value) && value < float.MaxValue / 2f;
    }

    private static string FormatTimelineSummary(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        var firstSeparator = value.IndexOf('|', StringComparison.Ordinal);
        return firstSeparator > 0
            ? value[..firstSeparator].Trim()
            : value.Trim();
    }
}

internal sealed class BossModMechanicPressureMonitor
{
    private static readonly TimeSpan KnockbackRecoveryWindow = TimeSpan.FromSeconds(2);

    private bool sawKnockbackPressure;
    private DateTime knockbackRecoveryUntilUtc = DateTime.MinValue;

    public BossModMechanicPressure Current { get; private set; } = BossModMechanicPressure.None;

    public void Update(BossModIpc bossMod)
    {
        var now = DateTime.UtcNow;
        var pressure = bossMod.GetMechanicPressure();
        if (pressure.KnockbackSoon)
        {
            this.sawKnockbackPressure = true;
        }
        else if (this.sawKnockbackPressure)
        {
            this.knockbackRecoveryUntilUtc = now.Add(KnockbackRecoveryWindow);
            this.sawKnockbackPressure = false;
        }

        if (now > this.knockbackRecoveryUntilUtc && !pressure.KnockbackSoon)
        {
            this.knockbackRecoveryUntilUtc = DateTime.MinValue;
        }

        this.Current = pressure.WithKnockbackRecoveryUntil(this.knockbackRecoveryUntilUtc);
    }

    public void Reset()
    {
        this.sawKnockbackPressure = false;
        this.knockbackRecoveryUntilUtc = DateTime.MinValue;
        this.Current = BossModMechanicPressure.None;
    }
}
