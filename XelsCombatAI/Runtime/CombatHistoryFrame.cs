using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed record CombatHistoryFrame(
    float T,
    bool InCombat,
    bool IsDead,
    uint PlayerClassJobId,
    uint TargetBaseId,
    ulong TargetObjectId,
    // Movement
    bool? Movement,
    bool AutomatedMovementSuppressed,
    string? MovementRangeStrategy,
    string? SafetyBuffer,
    float TargetUptimeRange,
    // Positionals
    Positional LastPositional,
    bool TrueNorthActive,
    uint TrueNorthCharges,
    // Gap closers
    string GapSafety,
    string EscapeSafety,
    // Healer coverage zone
    string HealerCoverageReason,
    bool HealerCoverageInjected,
    int HealerCoverageMembers,
    float HealerCoverageDist,
    // AoE pack
    string Reason,
    bool Henched,
    int Targets,
    int CurrentHits,
    int BestHits,
    bool Injected,
    string ActionName,
    string Shape,
    // Survivability zone
    string SurvZoneReason,
    bool SurvZoneInjected,
    string SurvZoneName,
    float SurvZoneDistance,
    // Passage of arms
    string PassageReason,
    bool PassageInjected,
    float PassageDistance,
    bool PassageInCone,
    // Aggro safety
    string AggroReason,
    bool AggroInjected,
    float AggroSeconds,
    // Boss frontal cone
    string BossFrontalReason);
