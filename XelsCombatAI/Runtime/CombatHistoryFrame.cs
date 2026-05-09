using System.Numerics;
using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed record CombatHistoryFrame(
    float T,
    bool InCombat,
    bool IsDead,
    uint PlayerClassJobId,
    Vector3? PlayerPosition,
    float PlayerRotation,
    uint TargetBaseId,
    ulong TargetObjectId,
    Vector3? TargetPosition,
    float TargetRotation,
    float TargetRadius,
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
    Vector3? AoeCandidate,
    Vector3? AoePrimaryTarget,
    bool AoeCandidateInjected,
    // Survivability zone
    string SurvZoneReason,
    bool SurvZoneInjected,
    string SurvZoneName,
    float SurvZoneDistance,
    Vector3? SurvZoneCenter,
    Vector3? SurvZoneCaster,
    // Passage of arms
    string PassageReason,
    bool PassageInjected,
    float PassageDistance,
    bool PassageInCone,
    Vector3? PassagePaladin,
    Vector3? PassagePreferred,
    // Healer coverage
    Vector3? HealerCoverageCenter,
    // Aggro safety
    string AggroReason,
    bool AggroInjected,
    float AggroSeconds,
    string ArenaEdgeReason,
    // BossMod goal hook
    string GoalPriority,
    string GoalSources,
    string BossModActiveModule,
    string BossModActiveZoneModule,
    string BossModNavigationDestination,
    string BossModNavigationNextWaypoint,
    string BossModNavigationStats,
    string BossModVnavmeshGuard,
    string BossModControllerTarget,
    string BossModMovementOverride,
    string BossModHintSummary,
    BossModMovementDiagnostics BossModMovement,
    string ManualMovementInput);
