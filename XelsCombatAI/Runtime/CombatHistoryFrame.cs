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
    // Party gravity
    string PartyGravityReason,
    bool PartyGravityInjected,
    int PartyGravityMembers,
    int PartyGravityClusterMembers,
    int PartyGravityDutySupportMembers,
    // Healer AoE
    string HealerAoeReason,
    bool HealerAoeInjected,
    int HealerAoeMembers,
    int HealerAoeCurrentHits,
    int HealerAoeBestHits,
    // AoE pack
    string Reason,
    bool Henched,
    int Targets,
    int CurrentHits,
    int BestHits,
    bool Injected,
    string ActionName,
    string Shape);
