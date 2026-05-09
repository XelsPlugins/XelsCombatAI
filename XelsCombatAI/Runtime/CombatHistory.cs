using System;
using System.Text;
using XelsCombatAI.Combat;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Runtime;

internal sealed class CombatHistory
{
    private const int MaxFrames = 1200;
    private static readonly TimeSpan RecordInterval = TimeSpan.FromMilliseconds(250);

    private readonly CombatHistoryFrame?[] frames = new CombatHistoryFrame?[MaxFrames];
    private int head;
    private int count;
    private DateTime combatStart = DateTime.MinValue;
    private DateTime lastRecordedAt = DateTime.MinValue;
    private StateCommandType lastSeenRsrSnapshotMode;

    public void Reset()
    {
        this.head = 0;
        this.count = 0;
        this.combatStart = DateTime.MinValue;
        this.lastRecordedAt = DateTime.MinValue;
        this.lastSeenRsrSnapshotMode = default;
    }

    public void Record(RuntimeStatus status, AoePackPositioningStatus aoe)
    {
        var now = DateTime.UtcNow;
        if (now - this.lastRecordedAt < RecordInterval)
            return;

        if (this.combatStart == DateTime.MinValue)
            this.combatStart = now;

        this.lastRecordedAt = now;

        if (aoe.RsrHenchedActive)
            this.lastSeenRsrSnapshotMode = aoe.RsrSnapshotMode;

        var frame = new CombatHistoryFrame(
            T: (float)(now - this.combatStart).TotalSeconds,
            InCombat: status.InCombat,
            IsDead: status.IsDead,
            PlayerClassJobId: status.PlayerClassJobId,
            TargetBaseId: status.TargetBaseId,
            TargetObjectId: status.TargetObjectId,
            Movement: status.LastMovement,
            AutomatedMovementSuppressed: status.AutomatedMovementSuppressed,
            MovementRangeStrategy: status.LastMovementRangeStrategy,
            SafetyBuffer: status.LastForbiddenZoneCushion,
            TargetUptimeRange: status.LastTargetUptimeRange,
            LastPositional: status.LastPositional,
            TrueNorthActive: status.TrueNorthActive,
            TrueNorthCharges: status.TrueNorthCharges,
            GapSafety: status.LastGapCloserSafety,
            EscapeSafety: status.LastEscapeGapCloserSafety,
            PartyGravityReason: status.PartyGravityPositioning.LastReason,
            PartyGravityInjected: status.PartyGravityPositioning.Injected,
            PartyGravityMembers: status.PartyGravityPositioning.PartyMembers,
            PartyGravityClusterMembers: status.PartyGravityPositioning.ClusterMembers,
            PartyGravityDutySupportMembers: status.PartyGravityPositioning.DutySupportMembers,
            HealerAoeReason: status.HealerAoePositioning.LastReason,
            HealerAoeInjected: status.HealerAoePositioning.Injected,
            HealerAoeMembers: status.HealerAoePositioning.PartyMembers,
            HealerAoeCurrentHits: status.HealerAoePositioning.CurrentHits,
            HealerAoeBestHits: status.HealerAoePositioning.BestHits,
            Reason: aoe.LastReason,
            Henched: aoe.RsrHenchedActive,
            Targets: aoe.PriorityTargetCount,
            CurrentHits: aoe.CurrentHits,
            BestHits: aoe.BestHits,
            Injected: aoe.Injected,
            ActionName: aoe.ActionName,
            Shape: aoe.Shape);

        var index = (this.head + this.count) % MaxFrames;
        this.frames[index] = frame;
        if (this.count < MaxFrames)
            this.count++;
        else
            this.head = (this.head + 1) % MaxFrames;
    }

    public string Build(Configuration config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Combat History ===");

        if (this.count == 0)
        {
            sb.AppendLine("No frames recorded.");
            return sb.ToString();
        }

        var first = this.frames[this.head]!;
        var last = this.frames[(this.head + this.count - 1) % MaxFrames]!;
        sb.AppendLine($"Start={this.combatStart:O}  Duration={last.T:0.0}s  Frames={this.count}");
        sb.AppendLine();
        sb.AppendLine("[Header]");
        sb.AppendLine($"Job={first.PlayerClassJobId}  TargetUptime={config.ManageTargetUptime}  PartyGravity={config.ManagePartyGravityPositioning}  PickAoeTarget={config.PickBetterAoeTarget}  KeepTrashTarget={config.KeepTrashTargetSelected}  ManagePositionals={config.ManagePositionals}  ManageTrueNorth={config.ManageTrueNorth}  CombatStyle={config.CombatStyle}  RsrSnapshot={this.lastSeenRsrSnapshotMode}");
        sb.AppendLine();
        sb.AppendLine("[Frames]");

        CombatHistoryFrame? prev = null;
        for (var i = 0; i < this.count; i++)
        {
            var frame = this.frames[(this.head + i) % MaxFrames]!;
            sb.Append($"[T+{frame.T,6:0.00}]");

            // Always show state changes relevant to debugging
            AppendIfChanged(sb, "InCombat", frame.InCombat, prev?.InCombat);
            AppendIfChanged(sb, "Dead", frame.IsDead, prev?.IsDead);
            AppendIfChanged(sb, "Target", frame.TargetBaseId, prev?.TargetBaseId);
            AppendIfChanged(sb, "TargetObj", frame.TargetObjectId, prev?.TargetObjectId);
            AppendIfChanged(sb, "Move", frame.Movement, prev?.Movement);
            AppendIfChanged(sb, "Suppressed", frame.AutomatedMovementSuppressed, prev?.AutomatedMovementSuppressed);
            AppendIfChanged(sb, "Strategy", frame.MovementRangeStrategy, prev?.MovementRangeStrategy);
            AppendIfChanged(sb, "SafetyBuffer", frame.SafetyBuffer, prev?.SafetyBuffer);
            AppendIfChanged(sb, "TargetUptime", $"{frame.TargetUptimeRange:0.0}", prev == null ? null : $"{prev.TargetUptimeRange:0.0}");
            AppendIfChanged(sb, "Positional", frame.LastPositional, prev?.LastPositional);
            AppendIfChanged(sb, "TrueNorth", frame.TrueNorthActive, prev?.TrueNorthActive);
            AppendIfChanged(sb, "TNCharges", frame.TrueNorthCharges, prev?.TrueNorthCharges);
            AppendIfChanged(sb, "Gap", frame.GapSafety, prev?.GapSafety);
            AppendIfChanged(sb, "Escape", frame.EscapeSafety, prev?.EscapeSafety);
            AppendIfChanged(sb, "PartyGravity", frame.PartyGravityReason, prev?.PartyGravityReason);
            AppendIfChanged(sb, "PGInjected", frame.PartyGravityInjected, prev?.PartyGravityInjected);
            AppendIfChanged(sb, "PGMembers", frame.PartyGravityMembers, prev?.PartyGravityMembers);
            AppendIfChanged(sb, "PGCluster", frame.PartyGravityClusterMembers, prev?.PartyGravityClusterMembers);
            AppendIfChanged(sb, "PGDutySupport", frame.PartyGravityDutySupportMembers, prev?.PartyGravityDutySupportMembers);
            AppendIfChanged(sb, "HealerAoE", frame.HealerAoeReason, prev?.HealerAoeReason);
            AppendIfChanged(sb, "HAInjected", frame.HealerAoeInjected, prev?.HealerAoeInjected);
            AppendIfChanged(sb, "HAMembers", frame.HealerAoeMembers, prev?.HealerAoeMembers);
            if (frame.HealerAoeCurrentHits != 0 || frame.HealerAoeBestHits != 0 || prev?.HealerAoeCurrentHits != 0 || prev?.HealerAoeBestHits != 0)
                AppendIfChanged(sb, "HAHits", $"{frame.HealerAoeCurrentHits}/{frame.HealerAoeBestHits}", $"{prev?.HealerAoeCurrentHits}/{prev?.HealerAoeBestHits}");

            // AoE pack fields — only print when relevant
            AppendIfChanged(sb, "AoEPack", frame.Reason, prev?.Reason);
            AppendIfChanged(sb, "Henched", frame.Henched, prev?.Henched);
            AppendIfChanged(sb, "Targets", frame.Targets, prev?.Targets);
            if (frame.CurrentHits != 0 || frame.BestHits != 0 || prev?.CurrentHits != 0 || prev?.BestHits != 0)
                AppendIfChanged(sb, "Hits", $"{frame.CurrentHits}/{frame.BestHits}", $"{prev?.CurrentHits}/{prev?.BestHits}");
            AppendIfChanged(sb, "Injected", frame.Injected, prev?.Injected);
            if (frame.ActionName != "<none>")
                AppendIfChanged(sb, "Action", $"{frame.ActionName}({frame.Shape})", prev == null ? null : $"{prev.ActionName}({prev.Shape})");

            sb.AppendLine();
            prev = frame;
        }

        return sb.ToString();
    }

    private static void AppendIfChanged<T>(StringBuilder sb, string label, T current, T? previous) where T : struct
    {
        if (previous == null || !current.Equals(previous.Value))
            sb.Append($"  {label}={current}");
    }

    private static void AppendIfChanged(StringBuilder sb, string label, bool? current, bool? previous)
    {
        if (current != previous)
            sb.Append($"  {label}={current}");
    }

    private static void AppendIfChanged(StringBuilder sb, string label, string? current, string? previous)
    {
        if (current != previous)
            sb.Append($"  {label}={current}");
    }
}
