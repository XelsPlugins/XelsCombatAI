using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class GapCloserController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModReflectionSafety bossModSafety,
    JobRangeProvider jobRangeProvider,
    MobilityDecisionEvaluator mobilityEvaluator,
    DashStyleController dashStyleController,
    FacingController facingController,
    RotationSolverActionReflection rotationSolverActions,
    Func<Positional> positionalIntent,
    Func<bool> rsrHenchedActive,
    Func<TrashPullDiagnostics> trashPullDiagnostics,
    Func<BossModMechanicPressure> mechanicPressure)
{
    private const float DirectionalDashBetterLandingThreshold = 0.75f;
    private readonly record struct DirectionalDashFacingCandidate(float Rotation, Vector3 Destination);
    private readonly record struct LocationDashCandidate(Vector3 Destination, string Reason);

    private DateTime nextGapCloserAttempt = DateTime.MinValue;
    private string lastGapCloserSafety = "not checked";
    private Vector3? lastSafeLandingPosition;

    public string LastGapCloserSafety => this.lastGapCloserSafety;
    public Vector3? LastSafeLandingPosition => this.lastSafeLandingPosition;

    public void Reset()
    {
        this.nextGapCloserAttempt = DateTime.MinValue;
        this.lastGapCloserSafety = "not checked";
        this.lastSafeLandingPosition = null;
    }

    public unsafe bool TryUseReengageGapCloser()
    {
        if (DateTime.UtcNow < this.nextGapCloserAttempt)
        {
            return false;
        }

        this.nextGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastGapCloserSafety = "missing player";
            return false;
        }

        var target = services.TargetManager.Target;
        if (this.TryResolveRsrAlignedReengageTarget(out var rsrTarget, out var rsrTargetReason))
        {
            target = rsrTarget;
        }
        else if (rsrTargetReason.Length > 0)
        {
            this.lastGapCloserSafety = rsrTargetReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", rsrTargetReason);
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastGapCloserSafety = "animation lock";
            return false;
        }

        if (IsNinjaMudraWindow(player))
        {
            this.lastGapCloserSafety = "NIN mudra active";
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", this.lastGapCloserSafety);
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        if (ShouldBlockRangedReengageGapCloser(classJobId) &&
            !this.ShouldAllowPhantomTargetAoeReengageGapCloser(classJobId, target))
        {
            this.lastGapCloserSafety = "ranged gap closer reserved for safety or job-specific movement";
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", this.lastGapCloserSafety);
            return false;
        }

        if (this.TryUsePairedReturn(player, target as IBattleNpc))
        {
            return true;
        }

        if (target == null)
        {
            this.lastGapCloserSafety = "missing target";
            return false;
        }

        if (target is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            this.lastGapCloserSafety = "target is not attackable";
            return false;
        }

        var intendedTarget = battleNpc;
        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var safeMovementDestination = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _)
            ? safeDestination
            : (Vector3?)null;
        var pressure = mechanicPressure();
        var currentPositionUnsafe = bossModSafety.TryIsPositionSafe(player.Position, out var currentPositionSafe, out _) && !currentPositionSafe;

        var reengageRange = MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange);
        var phantomAoeTargetReengageAllowed = this.ShouldAllowPhantomTargetAoeReengageGapCloser(classJobId, target);
        var effectiveReengageRange = phantomAoeTargetReengageAllowed
            ? MathF.Max(
                CombatConstants.MeleeActionRange,
                AoePackPositioningController.ResolveEffectivePackAoeRange(classJobId, jobRangeProvider.PackAoeRange, inAoeSituation: true))
            : reengageRange;
        var targetHasBossModule = target is IBattleNpc moduleTarget && bossMod.HasModuleByDataId(moduleTarget.BaseId);
        var knockbackRecoveryDashAllowed = ShouldAllowKnockbackRecoveryReengage(
            dashStyleController.KnockbackRecoveryActive || pressure.KnockbackRecoveryActive,
            JobRoles.GetRangeRole(player) == RangeRole.Melee,
            targetHasBossModule,
            HasAntiKnockbackStatus(player),
            distanceToHitbox,
            reengageRange);
        var hasReengageTiming = this.TryGetOffensiveGcdTiming(intendedTarget, out var reengageTiming, out var timingReason);
        var walkReason = hasReengageTiming ? string.Empty : timingReason;
        var directWalkCanMakeGcd = hasReengageTiming &&
                                   GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(distanceToHitbox, reengageRange, reengageTiming, out walkReason);
        var missingTimingMeleeRecovery = ShouldTreatMissingRsrTimingAsMissedMelee(
            JobRoles.GetRangeRole(player) == RangeRole.Melee,
            hasReengageTiming,
            distanceToHitbox,
            reengageRange);
        if (missingTimingMeleeRecovery)
        {
            walkReason = "RSR has no usable melee GCD while outside melee range";
        }

        var pathBlockReason = string.Empty;
        var meleeReengagePathBlocked = JobRoles.GetRangeRole(player) == RangeRole.Melee &&
                                       distanceToHitbox > reengageRange &&
                                       this.TryIsMeleeReengagePathBlocked(player, intendedTarget, reengageRange, out pathBlockReason);
        if (meleeReengagePathBlocked)
        {
            walkReason = pathBlockReason;
        }

        var reengageDashNeeded = (hasReengageTiming && !directWalkCanMakeGcd) || missingTimingMeleeRecovery || meleeReengagePathBlocked;
        var desiredPositional = this.ResolveDesiredDashPositional(player, intendedTarget, reengageTiming, out var positionalDashReason);
        var positionalDashNeeded = PositionalDashPolicy.IsActive(desiredPositional);
        var originalTargetObjectId = target.GameObjectId;
        IBattleNpc? relayReturnTarget = null;
        if (reengageDashNeeded &&
            ShouldTryHostileRelay(classJobId, distanceToHitbox, reengageRange) &&
            this.TryFindHostileRelayGapCloserTarget(player, intendedTarget, distanceToHitbox, classJobId, reengageRange, reengageTiming, out var relayTarget))
        {
            relayReturnTarget = intendedTarget;
            target = relayTarget;
            distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        }

        var primaryActionId = this.GetPrimaryReengageActionId(classJobId);
        var styleOpportunity = dashStyleController.EvaluateReengageOpportunity(player, target, distanceToHitbox, primaryActionId, reengageDashNeeded || knockbackRecoveryDashAllowed || positionalDashNeeded);
        if (styleOpportunity.Active &&
            originalTargetObjectId != target.GameObjectId &&
            target is IBattleNpc surfTarget &&
            surfTarget.BattleNpcKind == BattleNpcSubKind.Combatant)
        {
            styleOpportunity = styleOpportunity with { Reason = "pack surf" };
        }

        if (!GapCloserDecisionPolicy.ShouldUsePostDowntimeReengage(
                targetAttackable: true,
                walkingWouldMissUsefulGcd: reengageDashNeeded || missingTimingMeleeRecovery,
                pressure,
                out var downtimeReason))
        {
            this.lastGapCloserSafety = downtimeReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", downtimeReason);
            return false;
        }

        if (GapCloserDecisionPolicy.ShouldHoldReengageForMechanicPressure(
                pressure,
                reengageDashNeeded || missingTimingMeleeRecovery,
                knockbackRecoveryDashAllowed,
                styleOpportunity.Active && styleOpportunity.RequiresStrongGain,
                out var pressureReason))
        {
            this.lastGapCloserSafety = pressureReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", pressureReason);
            return false;
        }

        if (GapCloserDecisionPolicy.ShouldHoldCasterImmobilityWindow(
                classJobId,
                player.IsCasting,
                this.HasActiveCasterStationaryBuff(player),
                currentPositionUnsafe,
                out var casterHoldReason))
        {
            this.lastGapCloserSafety = casterHoldReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", casterHoldReason);
            return false;
        }

        var friendlyReengageTarget = relayReturnTarget ?? target as IBattleChara;
        var hasFriendlyReengageOption = classJobId is 2 or 20 or 41;
        var allowStyleReengageInsideEngagementRange = this.ShouldAllowStyleReengageInsideEngagementRange(target, styleOpportunity);
        if (relayReturnTarget == null &&
            ((distanceToHitbox <= effectiveReengageRange && !allowStyleReengageInsideEngagementRange) ||
             (distanceToHitbox > CombatConstants.GapCloserMaxRange && !hasFriendlyReengageOption)))
        {
            this.lastGapCloserSafety = distanceToHitbox <= effectiveReengageRange
                ? $"target within {effectiveReengageRange:0.#}y engagement range"
                : "target not in gap closer range";
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (relayReturnTarget == null &&
            !reengageDashNeeded &&
            !knockbackRecoveryDashAllowed &&
            !positionalDashNeeded &&
            !allowStyleReengageInsideEngagementRange)
        {
            this.lastGapCloserSafety = hasReengageTiming ? walkReason : timingReason;
            if (positionalDashReason.Length > 0 && !hasReengageTiming)
            {
                this.lastGapCloserSafety = positionalDashReason;
            }

            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", this.lastGapCloserSafety);
            return false;
        }

        if (this.ShouldConserveTrashPullGapCloser(
                player,
                target,
                distanceToHitbox,
                classJobId,
                primaryActionId,
                currentPositionUnsafe,
                reengageDashNeeded,
                out var trashConserveReason))
        {
            this.lastGapCloserSafety = trashConserveReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", trashConserveReason);
            return false;
        }

        if (!this.ShouldAllowTrashPullGapCloserTarget(player, target, out var trashTargetReason))
        {
            this.lastGapCloserSafety = trashTargetReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", trashTargetReason);
            return false;
        }

        if (this.TryUsePhantomReengageGapCloser(classJobId, distanceToHitbox, reengageRange, target, safeMovementDestination, styleOpportunity, relayReturnTarget))
        {
            return true;
        }

        return classJobId switch
        {
            1 or 19 when config.GapCloserPLD => this.TryUseTargetGapCloser(ActionUse.PaladinInterveneActionId, "Intervene", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            3 or 21 when config.GapCloserWAR => this.TryUseTargetGapCloser(ActionUse.WarriorOnslaughtActionId, "Onslaught", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            32 when config.GapCloserDRK => this.TryUseTargetGapCloser(ActionUse.DarkKnightShadowstrideActionId, "Shadowstride", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            37 when config.GapCloserGNB => this.TryUseTargetGapCloser(ActionUse.GunbreakerTrajectoryActionId, "Trajectory", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            2 or 20 when config.GapCloserMNK => (distanceToHitbox <= CombatConstants.GapCloserMaxRange && this.TryUseTargetGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget)) ||
                                               this.TryUseFriendlyReengageGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", CombatConstants.GapCloserMaxRange, friendlyReengageTarget, safeMovementDestination, styleOpportunity, reengageTiming, reengageRange, missingTimingMeleeRecovery, meleeReengagePathBlocked),
            4 or 22 when config.GapCloserDRG => this.TryUseTargetGapCloser(ActionUse.DragoonWingedGlideActionId, "Winged Glide", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            29 or 30 when config.GapCloserNIN => this.TryUseNinjaShukuchi(target, safeMovementDestination, styleOpportunity, meleeReengagePathBlocked, reengageRange),
            34 when config.GapCloserSAM => this.TryUseTargetGapCloser(ActionUse.SamuraiGyotenActionId, "Gyoten", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            39 when config.GapCloserRPR => this.TryUseReaperRegress(ref this.lastGapCloserSafety, distanceToHitbox, safeMovementDestination, styleOpportunity, target as IBattleChara) || this.TryUseForwardGapCloser(ActionUse.ReaperHellsIngressActionId, "Hell's Ingress", distanceToHitbox, MathF.Max(reengageRange, CombatConstants.MeleeActionRange + 1f), target, safeMovementDestination, styleOpportunity),
            41 when config.GapCloserVPR => (distanceToHitbox <= CombatConstants.GapCloserMaxRange && this.TryUseTargetGapCloser(ActionUse.ViperSlitherActionId, "Slither", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget)) ||
                                          this.TryUseFriendlyReengageGapCloser(ActionUse.ViperSlitherActionId, "Slither", CombatConstants.GapCloserMaxRange, friendlyReengageTarget, safeMovementDestination, styleOpportunity, reengageTiming, reengageRange, missingTimingMeleeRecovery, meleeReengagePathBlocked),
            _ => false
        };
    }

    private bool ShouldAllowStyleReengageInsideEngagementRange(IGameObject target, DashStyleReengageOpportunity styleOpportunity)
    {
        return styleOpportunity.Active &&
               target is IBattleNpc battleNpc &&
               bossMod.HasModuleByDataId(battleNpc.BaseId);
    }

    internal static bool ShouldAllowPhantomReengageGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled)
    {
        return ShouldAllowPhantomTargetReengageGapCloser(classJobId, phantomGapClosersEnabled, currentJobGapCloserEnabled) ||
               ShouldAllowPhantomForwardReengageGapCloser(classJobId, phantomGapClosersEnabled, currentJobGapCloserEnabled);
    }

    internal static bool ShouldAllowPhantomTargetReengageGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled)
    {
        return phantomGapClosersEnabled &&
               currentJobGapCloserEnabled &&
               (JobRoles.IsTankJob(classJobId) || JobRoles.GetRangeRole(classJobId) == RangeRole.Melee);
    }

    internal static bool ShouldAllowPhantomTargetAoeReengageGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled, bool inAoePackContext, float packAoeRange)
    {
        return phantomGapClosersEnabled &&
               currentJobGapCloserEnabled &&
               inAoePackContext &&
               AoePackPositioningController.ResolveEffectivePackAoeRange(classJobId, packAoeRange, inAoeSituation: true) <= 8f;
    }

    internal static bool ShouldAllowPhantomForwardReengageGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled)
    {
        return phantomGapClosersEnabled &&
               currentJobGapCloserEnabled &&
               !JobRoles.IsTankJob(classJobId) &&
               JobRoles.GetRangeRole(classJobId) == RangeRole.Melee;
    }

    private bool TryUsePhantomReengageGapCloser(
        uint classJobId,
        float distanceToHitbox,
        float reengageRange,
        IGameObject target,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        IBattleNpc? restoreTargetAfterUse)
    {
        var targetReengageAllowed = ShouldAllowPhantomTargetReengageGapCloser(
            classJobId,
            config.UsePhantomGapClosers,
            config.IsPhantomGapCloserJobEnabled(classJobId)) ||
            this.ShouldAllowPhantomTargetAoeReengageGapCloser(classJobId, target);
        var forwardReengageAllowed = ShouldAllowPhantomForwardReengageGapCloser(
            classJobId,
            config.UsePhantomGapClosers,
            config.IsPhantomGapCloserJobEnabled(classJobId));
        if (!targetReengageAllowed && !forwardReengageAllowed)
        {
            return false;
        }

        if (targetReengageAllowed &&
            distanceToHitbox <= CombatConstants.PhantomKickMaxRange &&
            this.TryUseTargetGapCloser(
                ActionUse.PhantomKickActionId,
                "Phantom Kick",
                distanceToHitbox,
                target,
                safeMovementDestination,
                styleOpportunity,
                restoreTargetAfterUse))
        {
            return true;
        }

        if (targetReengageAllowed && distanceToHitbox > CombatConstants.PhantomKickMaxRange)
        {
            this.lastGapCloserSafety = "target not in Phantom Kick range";
        }

        if (!forwardReengageAllowed)
        {
            return false;
        }

        return this.TryUseForwardGapCloser(
            ActionUse.OccultFeatherfootActionId,
            "Occult Featherfoot",
            distanceToHitbox,
            MathF.Max(reengageRange, CombatConstants.MeleeActionRange + 1f),
            target,
            safeMovementDestination,
            styleOpportunity);
    }

    private bool ShouldAllowPhantomTargetAoeReengageGapCloser(uint classJobId, IGameObject? target)
    {
        if (target is not IBattleNpc battleNpc ||
            battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant ||
            battleNpc.GameObjectId == 0 ||
            battleNpc.IsDead ||
            battleNpc.CurrentHp <= 0 ||
            bossMod.HasModuleByDataId(battleNpc.BaseId))
        {
            return false;
        }

        var trash = trashPullDiagnostics();
        var inAoePackContext = trash.Phase is TrashPullPhase.Gathering or TrashPullPhase.Stabilizing or TrashPullPhase.Burning &&
                               trash.DominantTargetCount >= 2 &&
                               (trash.DominantTargetIds.Count == 0 || trash.DominantTargetIds.Contains(battleNpc.GameObjectId));
        return ShouldAllowPhantomTargetAoeReengageGapCloser(
            classJobId,
            config.UsePhantomGapClosers,
            config.IsPhantomGapCloserJobEnabled(classJobId),
            inAoePackContext,
            jobRangeProvider.PackAoeRange);
    }

    private bool TryResolveRsrAlignedReengageTarget(out IBattleNpc? target, out string reason)
    {
        target = null;
        reason = string.Empty;
        if (!rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _))
        {
            return false;
        }

        if (!ShouldUseRsrActionTarget(
                rsrHenchedActive(),
                actionAvailable: true,
                action.IsFriendly,
                action.PrimaryTargetId,
                CurrentTargetMatchesRsrTarget(services.TargetManager.Target as IBattleChara, action.PrimaryTargetId)))
        {
            return false;
        }

        target = this.FindRsrActionTarget(action);
        if (IsUsableGapCloserTarget(target))
        {
            reason = "RSR Auto action target";
            return true;
        }

        reason = "RSR Auto target mismatch; reflected target unavailable";
        return false;
    }

    internal static bool ShouldUseRsrActionTarget(
        bool rsrHenchedActive,
        bool actionAvailable,
        bool actionIsFriendly,
        ulong actionPrimaryTargetId,
        bool currentTargetMatchesAction)
    {
        return !rsrHenchedActive &&
               actionAvailable &&
               !actionIsFriendly &&
               actionPrimaryTargetId != 0 &&
               !currentTargetMatchesAction;
    }

    private static bool CurrentTargetMatchesRsrTarget(IBattleChara? currentTarget, ulong actionPrimaryTargetId)
    {
        return currentTarget != null &&
               actionPrimaryTargetId != 0 &&
               (currentTarget.GameObjectId == actionPrimaryTargetId ||
                currentTarget.EntityId == actionPrimaryTargetId);
    }

    private IBattleNpc? FindRsrActionTarget(RsrAoeActionSnapshot action)
    {
        if (action.PrimaryTargetId != 0)
        {
            if (services.ObjectTable.SearchById(action.PrimaryTargetId) is IBattleNpc byGameObjectId)
            {
                return byGameObjectId;
            }

            foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
            {
                if (npc.EntityId == action.PrimaryTargetId)
                {
                    return npc;
                }
            }
        }

        if (action.PrimaryTargetPosition == default)
        {
            return null;
        }

        IBattleNpc? best = null;
        var bestDistanceSq = 4f;
        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (!IsUsableGapCloserTarget(npc))
            {
                continue;
            }

            var distanceSq = Geometry.Distance2D(npc.Position, action.PrimaryTargetPosition);
            distanceSq *= distanceSq;
            if (distanceSq < bestDistanceSq)
            {
                best = npc;
                bestDistanceSq = distanceSq;
            }
        }

        return best;
    }

    private bool TryGetOffensiveGcdTiming(IBattleChara? expectedTarget, out RsrGcdActionTimingSnapshot action, out string reason)
    {
        action = null!;
        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var timing, out reason))
        {
            return false;
        }

        if (!rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var preview, out var previewReason))
        {
            reason = previewReason;
            return false;
        }

        if (preview.IsFriendly)
        {
            reason = "RSR next GCD is friendly";
            return false;
        }

        if (expectedTarget != null &&
            timing.PrimaryTargetId != 0 &&
            !CurrentTargetMatchesRsrTarget(expectedTarget, timing.PrimaryTargetId))
        {
            reason = "RSR next GCD targets another enemy";
            return false;
        }

        action = timing;
        reason = "RSR next offensive GCD timing available";
        return true;
    }

    private static bool IsUsableGapCloserTarget(IBattleNpc? target)
    {
        return target != null &&
               target.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.GameObjectId != 0 &&
               target.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !target.IsDead &&
               target.CurrentHp > 0;
    }

    private bool TryCommitGapCloserTarget(IGameObject target, out string reason)
    {
        reason = string.Empty;
        if (target is not IBattleNpc battleNpc ||
            battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant ||
            battleNpc.GameObjectId == 0 ||
            battleNpc.IsDead ||
            battleNpc.CurrentHp <= 0)
        {
            reason = "gap closer target is no longer valid";
            return false;
        }

        if (services.TargetManager.Target?.GameObjectId != battleNpc.GameObjectId)
        {
            services.TargetManager.Target = battleNpc;
        }

        if (services.TargetManager.Target?.GameObjectId == battleNpc.GameObjectId)
        {
            return true;
        }

        reason = "could not select gap closer target";
        return false;
    }

    private unsafe bool TryUsePairedReturn(IBattleChara player, IBattleNpc? currentTarget)
    {
        if (!dashStyleController.TryGetPairedReturn(out var request, out _))
        {
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            this.lastGapCloserSafety = currentReason;
            return false;
        }

        if (!currentSafe)
        {
            this.lastGapCloserSafety = "paired return blocked: current position unsafe";
            return false;
        }

        if (!bossModSafety.TryCanAttemptDashNow(out var dashReason))
        {
            this.lastGapCloserSafety = dashReason;
            return false;
        }

        if (!ActionUse.CanUseAction(request.ActionId))
        {
            this.lastGapCloserSafety = $"{request.ActionName} unavailable";
            dashStyleController.ClearPairedReturn("paired return unavailable");
            return false;
        }

        var safeMovementDestination = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _)
            ? safeDestination
            : (Vector3?)null;

        if (request.Kind == DashReturnKind.Regress)
        {
            var requiredDistance = currentTarget != null
                ? Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius)
                : 0f;
            if (currentTarget != null &&
                this.TryGetOffensiveGcdTiming(currentTarget, out var regressTiming, out _) &&
                !GapCloserDecisionPolicy.ShouldUsePairedReturnDash(
                    requiredDistance,
                    MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange),
                    regressTiming,
                    currentPositionUnsafe: false,
                    out var regressReturnReason))
            {
                this.lastGapCloserSafety = regressReturnReason;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, request.ActionName, regressReturnReason);
                return false;
            }

            var styleOpportunity = new DashStyleReengageOpportunity(true, "paired return", false, false);
            if (this.TryUseReaperRegress(ref this.lastGapCloserSafety, requiredDistance, safeMovementDestination, styleOpportunity))
            {
                dashStyleController.ClearPairedReturn("paired return");
                this.lastGapCloserSafety = "used Regress (paired return)";
                return true;
            }

            if (this.lastGapCloserSafety.Contains("no portal", StringComparison.OrdinalIgnoreCase))
            {
                dashStyleController.ClearPairedReturn("paired return portal missing");
            }

            return false;
        }

        if (currentTarget == null ||
            currentTarget.BattleNpcKind != BattleNpcSubKind.Combatant ||
            currentTarget.GameObjectId == 0 ||
            currentTarget.IsDead ||
            currentTarget.CurrentHp <= 0)
        {
            this.lastGapCloserSafety = "paired return needs current target";
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius);
        if (distanceToHitbox <= CombatConstants.MeleeActionRange || distanceToHitbox > CombatConstants.GapCloserMaxRange)
        {
            this.lastGapCloserSafety = "paired return target not in dash range";
            return false;
        }

        var pairedReturnTiming = this.TryGetOffensiveGcdTiming(currentTarget, out var returnTiming, out _)
            ? returnTiming
            : null;
        if (!GapCloserDecisionPolicy.ShouldUsePairedReturnDash(
                distanceToHitbox,
                MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange),
                pairedReturnTiming,
                currentPositionUnsafe: false,
                out var pairedReturnReason))
        {
            this.lastGapCloserSafety = pairedReturnReason;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, request.ActionName, pairedReturnReason);
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, currentTarget.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate paired return landing";
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            currentTarget,
            safeMovementDestination,
            MobilityIntent.Uptime,
            request.ActionName,
            request.ActionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, request.ActionId, currentTarget.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used)
        {
            dashStyleController.ClearPairedReturn("paired return");
            this.lastGapCloserSafety = $"used {request.ActionName} ({decision.IntentLabel}, paired return)";
            return true;
        }

        dashStyleController.ClearPairedReturn("paired return failed");
        this.lastGapCloserSafety = $"failed to use {request.ActionName} ({decision.IntentLabel}, paired return)";
        return false;
    }

    public unsafe bool TryUseReaperRegress(ref string lastSafety, float distanceToHitboxRequired = 0f, Vector3? safeMovementDestination = null, DashStyleReengageOpportunity? styleOpportunity = null, IBattleChara? targetOverride = null)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.ReaperRegressActionId))
        {
            lastSafety = "Regress unavailable";
            return false;
        }

        var portal = this.TryFindReaperPortal(player);
        if (portal == null)
        {
            lastSafety = "Regress: no portal found";
            return false;
        }

        var portalPosition = portal.Position;

        if (distanceToHitboxRequired > 0f)
        {
            var target = targetOverride ?? services.TargetManager.Target;
            if (target == null)
            {
                lastSafety = "Regress: no target";
                return false;
            }

            var regressDistanceToHitbox = Geometry.DistanceToHitbox(portalPosition, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (regressDistanceToHitbox >= distanceToHitboxRequired || regressDistanceToHitbox > CombatConstants.MeleeActionRange + 1f)
            {
                lastSafety = "Regress: portal would not re-engage";
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Regress", "portal would not re-engage");
                return false;
            }
        }

        if (safeMovementDestination.HasValue &&
            !mobilityEvaluator.TryValidateDashDestination(
                player,
                portalPosition,
                targetOverride ?? services.TargetManager.Target as IBattleChara,
                safeMovementDestination.Value,
                distanceToHitboxRequired > 0f ? MobilityIntent.Uptime : MobilityIntent.Safety,
                "Regress",
                ActionUse.ReaperRegressActionId,
                0f,
                requireSafetyProgress: distanceToHitboxRequired <= 0f,
                requireUptimeProgress: distanceToHitboxRequired > 0f,
                requireVnavReachable: true,
                out var regressSafetyDecision))
        {
            lastSafety = $"Regress: {regressSafetyDecision.RiskReason}";
            return false;
        }

        if (!safeMovementDestination.HasValue &&
            !mobilityEvaluator.TryValidateDashDestination(
                player,
                portalPosition,
                targetOverride ?? services.TargetManager.Target as IBattleChara,
                null,
                MobilityIntent.Uptime,
                "Regress",
                ActionUse.ReaperRegressActionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: distanceToHitboxRequired > 0f,
                requireVnavReachable: false,
                out var regressUptimeDecision))
        {
            lastSafety = $"Regress: {regressUptimeDecision.RiskReason}";
            return false;
        }

        var location = portalPosition;
        var decision = mobilityEvaluator.LastDecision;
        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref lastSafety))
        {
            return false;
        }

        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.ReaperRegressActionId, player.GameObjectId, &location);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleOpportunity is { Active: true } activeStyle)
        {
            dashStyleController.RecordStyleUse(activeStyle.Reason);
        }

        lastSafety = used && styleOpportunity is { Active: true } usedStyle
            ? $"used Regress ({decision.IntentLabel}, {usedStyle.Reason})"
            : used ? $"used Regress ({decision.IntentLabel})" : $"failed to use Regress ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseFriendlyReengageGapCloser(
        uint actionId,
        string actionName,
        float maxRange,
        IBattleChara? currentTarget,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        RsrGcdActionTimingSnapshot? reengageTiming,
        float engagementRange,
        bool allowMissingTimingMeleeRecovery,
        bool meleeReengagePathBlocked)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (currentTarget == null)
        {
            this.lastGapCloserSafety = "friendly reengage needs target";
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (dashStyleController.ReengageStyleActive)
        {
            var styleCandidates = new List<DashStyleCandidate<IBattleChara>>();
            foreach (var ally in this.EnumerateFriendlyReengageTargets(player, currentTarget, maxRange))
            {
                if (!mobilityEvaluator.TryValidateDashDestination(
                    player,
                    ally.Position,
                    currentTarget,
                    safeMovementDestination,
                    MobilityIntent.Uptime,
                    actionName,
                    actionId,
                    0f,
                    requireSafetyProgress: false,
                    requireUptimeProgress: true,
                    requireVnavReachable: false,
                    out var decision))
                {
                    this.lastGapCloserSafety = decision.RiskReason;
                    continue;
                }

                var relayTimingReason = reengageTiming == null ? "RSR GCD timing unavailable for ally relay" : string.Empty;
                if ((!allowMissingTimingMeleeRecovery && reengageTiming == null) ||
                    (reengageTiming != null &&
                    !GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(
                        Geometry.DistanceToHitbox(ally.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius),
                        engagementRange,
                        reengageTiming,
                        out relayTimingReason)))
                {
                    this.lastGapCloserSafety = reengageTiming == null ? "RSR GCD timing unavailable for ally relay" : relayTimingReason;
                    mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, this.lastGapCloserSafety);
                    continue;
                }

                if (!this.ShouldUseFriendlyReengageAnchorDash(
                        player.Position,
                        player.HitboxRadius,
                        ally.Position,
                        currentTarget.Position,
                        currentTarget.HitboxRadius,
                        engagementRange,
                        meleeReengagePathBlocked,
                        HasAntiKnockbackStatus(ally),
                        decision.MoveDistance,
                        decision.SafetyGain,
                        decision.UptimeGain,
                        decision.PathGain,
                        out var anchorReason))
                {
                    this.lastGapCloserSafety = anchorReason;
                    mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, anchorReason);
                    continue;
                }

                var anchorLabel = FormatFriendlyReengageAnchorLabel(anchorReason);
                var reason = styleOpportunity.Reason is "knockback recovery" or "capped charge" or "pack surf"
                    ? styleOpportunity.Reason
                    : anchorLabel;
                styleCandidates.Add(dashStyleController.ScoreCandidate(
                    ally,
                    player,
                    ally.Position,
                    currentTarget,
                    safeMovementDestination,
                    decision,
                    reason));
            }

            if (dashStyleController.TrySelectBest(styleCandidates, out var selected))
            {
                if (!this.AcceptStyleOpportunity(styleOpportunity, selected.Decision, ref this.lastGapCloserSafety))
                {
                    this.lastSafeLandingPosition = null;
                    return false;
                }

                if (!this.TryCommitGapCloserTarget(currentTarget, out var targetCommitReason))
                {
                    this.lastGapCloserSafety = targetCommitReason;
                    this.lastSafeLandingPosition = null;
                    mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
                    return false;
                }

                this.lastSafeLandingPosition = selected.Destination;
                var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, selected.Source.GameObjectId);
                mobilityEvaluator.RecordActionResult(selected.Decision, used, used ? "action used" : "action failed");
                if (used)
                {
                    dashStyleController.RecordStyleUse(selected.Reason);
                }

                this.lastGapCloserSafety = used
                    ? $"used {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})"
                    : $"failed to use {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})";
                return used;
            }

            if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "current position safe")
            {
                this.lastGapCloserSafety = "no safe ally anchor";
            }

            return false;
        }

        foreach (var ally in this.EnumerateFriendlyReengageTargets(player, currentTarget, maxRange))
        {
            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                ally.Position,
                currentTarget,
                safeMovementDestination,
                MobilityIntent.Uptime,
                actionName,
                actionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                this.lastGapCloserSafety = decision.RiskReason;
                continue;
            }

            var relayTimingReason = reengageTiming == null ? "RSR GCD timing unavailable for ally relay" : string.Empty;
            if ((!allowMissingTimingMeleeRecovery && reengageTiming == null) ||
                (reengageTiming != null &&
                !GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(
                    Geometry.DistanceToHitbox(ally.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius),
                    engagementRange,
                    reengageTiming,
                    out relayTimingReason)))
            {
                this.lastGapCloserSafety = reengageTiming == null ? "RSR GCD timing unavailable for ally relay" : relayTimingReason;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, this.lastGapCloserSafety);
                continue;
            }

            if (!this.ShouldUseFriendlyReengageAnchorDash(
                player.Position,
                player.HitboxRadius,
                ally.Position,
                currentTarget.Position,
                currentTarget.HitboxRadius,
                engagementRange,
                meleeReengagePathBlocked,
                HasAntiKnockbackStatus(ally),
                decision.MoveDistance,
                decision.SafetyGain,
                decision.UptimeGain,
                decision.PathGain,
                out var anchorReason))
            {
                this.lastGapCloserSafety = anchorReason;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, anchorReason);
                continue;
            }

            var anchorLabel = FormatFriendlyReengageAnchorLabel(anchorReason);
            if (!this.TryCommitGapCloserTarget(currentTarget, out var targetCommitReason))
            {
                this.lastGapCloserSafety = targetCommitReason;
                this.lastSafeLandingPosition = null;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
                return false;
            }

            this.lastSafeLandingPosition = ally.Position;
            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, ally.GameObjectId);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastGapCloserSafety = used
                ? $"used {actionName} on ally ({decision.IntentLabel}, {anchorLabel})"
                : $"failed to use {actionName} on ally ({decision.IntentLabel}, {anchorLabel})";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "current position safe")
        {
            this.lastGapCloserSafety = "no safe ally anchor";
        }

        return false;
    }

    private static string FormatFriendlyReengageAnchorLabel(string anchorReason)
    {
        return anchorReason.StartsWith("melee stack recovery", StringComparison.Ordinal)
            ? "melee stack recovery"
            : "ally anchor";
    }

    internal static bool ShouldUseFriendlyAnchorDash(
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
        => GapCloserDecisionPolicy.ShouldUseFriendlyAnchorDash(moveDistance, safetyGain, uptimeGain, pathGain, out reason);

    internal static bool ShouldUseFriendlyAnchorDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
        => GapCloserDecisionPolicy.ShouldUseFriendlyAnchorDash(
            playerPosition,
            playerRadius,
            anchorPosition,
            targetPosition,
            targetRadius,
            moveDistance,
            safetyGain,
            uptimeGain,
            pathGain,
            out reason);

    private bool ShouldUseFriendlyReengageAnchorDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        float engagementRange,
        bool meleeReengagePathBlocked,
        bool anchorAntiKnockbackActive,
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        var localPlayer = services.ObjectTable.LocalPlayer;
        var partyPositions = localPlayer == null
            ? []
            : PartyAllyProvider.EnumerateVisiblePartyAllies(services, localPlayer)
                .Select(ally => ally.Position)
                .ToArray();
        var currentPositionUnsafe = bossModSafety.TryIsPositionSafe(playerPosition, out var currentSafe, out _) && !currentSafe;
        if (GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
                JobRoles.GetRangeRole(localPlayer) == RangeRole.Melee,
                meleeReengagePathBlocked,
                anchorPosition,
                targetPosition,
                playerRadius,
                targetRadius,
                engagementRange,
                partyPositions,
                mechanicPressure(),
                moveDistance,
                out reason))
        {
            return true;
        }

        return GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            playerPosition,
            playerRadius,
            anchorPosition,
            targetPosition,
            targetRadius,
            bossModSafety.TryGetSafeMovementIntent(playerPosition, out var safeDestination, out _) ? safeDestination : null,
            partyPositions,
            mechanicPressure(),
            currentPositionUnsafe,
            moveDistance,
            safetyGain,
            uptimeGain,
            pathGain,
            engagementRange,
            anchorAntiKnockbackActive,
            out reason);
    }

    private unsafe bool TryUseTargetGapCloser(uint actionId, string actionName, float distanceToHitbox, IGameObject target, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity, IBattleNpc? restoreTargetAfterUse = null)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, target.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate dash destination";
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!mobilityEvaluator.TryValidateFixedDashDestination(
            player,
            destination,
            target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            fixedDashRange: CombatConstants.FixedForwardGapCloserRange,
            fixedDashBackwards: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.ShouldAllowKnockbackRecoveryDashDirection(player, target as IBattleChara, destination, safeMovementDestination, styleOpportunity, decision))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.TryCommitGapCloserTarget(target, out var targetCommitReason))
        {
            this.lastGapCloserSafety = targetCommitReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (restoreTargetAfterUse != null)
        {
            this.TryRestoreTargetAfterRelay(restoreTargetAfterUse);
        }

        if (used && styleOpportunity.Active)
        {
            dashStyleController.RecordStyleUse(styleOpportunity.Reason);
        }

        this.lastGapCloserSafety = used && styleOpportunity.Active
            ? $"used {actionName} ({decision.IntentLabel}, {styleOpportunity.Reason})"
            : used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private void TryRestoreTargetAfterRelay(IBattleNpc target)
    {
        if (!IsUsableGapCloserTarget(target))
        {
            return;
        }

        services.TargetManager.Target = target;
    }

    private unsafe bool TryUseForwardGapCloser(uint actionId, string actionName, float distanceToHitbox, float requiredLandingRange, IGameObject target, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        var forward = Geometry.RotationToDirection(player.Rotation);
        var destination = player.Position + forward * CombatConstants.FixedForwardGapCloserRange;
        var destinationDistanceToHitbox = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= distanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
        {
            if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, null, null))
            {
                return false;
            }

            this.lastGapCloserSafety = "fixed dash would not re-engage";
            return false;
        }

        if (!mobilityEvaluator.TryValidateFixedDashDestination(
            player,
            destination,
            target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            fixedDashRange: CombatConstants.FixedForwardGapCloserRange,
            fixedDashBackwards: false,
            out var decision))
        {
            if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, null, null))
            {
                return false;
            }

            this.lastGapCloserSafety = decision.RiskReason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.ShouldAllowKnockbackRecoveryDashDirection(player, target as IBattleChara, destination, safeMovementDestination, styleOpportunity, decision))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, destination, decision))
        {
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleOpportunity.Active)
        {
            dashStyleController.RecordStyleUse(styleOpportunity.Reason);
        }

        this.lastGapCloserSafety = used && styleOpportunity.Active
            ? $"used {actionName} ({decision.IntentLabel}, {styleOpportunity.Reason})"
            : used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseNinjaShukuchi(
        IGameObject target,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        bool meleeReengagePathBlocked,
        float engagementRange)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.NinjaShukuchiActionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!this.TryFindSafeShukuchiDestination(
                player,
                target as IBattleChara,
                target.Position,
                target.HitboxRadius,
                safeMovementDestination,
                styleOpportunity,
                meleeReengagePathBlocked,
                engagementRange,
                out var destination,
                out var decision,
                out var styleReason))
        {
            return false;
        }

        if (!this.ShouldAllowKnockbackRecoveryDashDirection(player, target as IBattleChara, destination, safeMovementDestination, styleOpportunity, decision))
        {
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            return false;
        }

        if (!this.TryCommitGapCloserTarget(target, out var targetCommitReason))
        {
            this.lastGapCloserSafety = targetCommitReason;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Shukuchi", targetCommitReason);
            return false;
        }

        var location = destination;
        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.NinjaShukuchiActionId, player.GameObjectId, &location);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleReason.Length > 0)
        {
            dashStyleController.RecordStyleUse(styleReason);
        }

        this.lastGapCloserSafety = used && styleReason.Length > 0
            ? $"used Shukuchi ({decision.IntentLabel}, {styleReason})"
            : used ? $"used Shukuchi ({decision.IntentLabel})" : $"failed to use Shukuchi ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryRequestForwardDashFacing(
        IBattleChara player,
        IBattleChara? target,
        uint actionId,
        string actionName,
        float dashDistance,
        float requiredLandingRange,
        float currentDistanceToHitbox,
        Vector3? safeMovementDestination,
        Vector3? currentDestination,
        MobilityDecisionDiagnostics? currentDecision)
    {
        if (target == null)
        {
            return false;
        }

        var desiredPositional = this.ResolveDesiredDashPositional(player, target);
        if (!dashStyleController.ReengageStyleActive && !PositionalDashPolicy.IsActive(desiredPositional))
        {
            return currentDecision == null &&
                   this.TryRequestDirectForwardDashFacing(
                       player,
                       target,
                       actionId,
                       actionName,
                       dashDistance,
                       requiredLandingRange,
                       currentDistanceToHitbox,
                       safeMovementDestination);
        }

        if (currentDestination.HasValue &&
            PositionalDashPolicy.IsActive(desiredPositional) &&
            PositionalDashPolicy.IsSatisfied(desiredPositional, currentDestination.Value, target.Position, target.Rotation))
        {
            return false;
        }

        var currentMaxMeleeError = currentDestination.HasValue
            ? this.CalculateMaxMeleeLandingError(player, currentDestination.Value, target)
            : float.PositiveInfinity;
        var candidates = new List<DashStyleCandidate<float>>();
        foreach (var candidate in this.EnumerateForwardDashFacingCandidates(player, target, dashDistance, desiredPositional))
        {
            if (PositionalDashPolicy.IsActive(desiredPositional) &&
                !PositionalDashPolicy.IsSatisfied(desiredPositional, candidate.Destination, target.Position, target.Rotation))
            {
                continue;
            }

            if (this.ShouldRejectNonTankFrontOrCenterLanding(player, target, candidate.Destination, alternativeExists: true, out var landingReason))
            {
                this.lastGapCloserSafety = landingReason;
                continue;
            }

            var destinationDistanceToHitbox = Geometry.DistanceToHitbox(candidate.Destination, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (destinationDistanceToHitbox >= currentDistanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
            {
                continue;
            }

            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                candidate.Destination,
                target,
                safeMovementDestination,
                MobilityIntent.Uptime,
                actionName,
                actionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                this.lastGapCloserSafety = decision.RiskReason;
                continue;
            }

            if (currentDecision != null)
            {
                var candidateError = this.CalculateMaxMeleeLandingError(player, candidate.Destination, target);
                if (!PositionalDashPolicy.IsActive(desiredPositional) &&
                    candidateError + DirectionalDashBetterLandingThreshold >= currentMaxMeleeError)
                {
                    continue;
                }
            }

            candidates.Add(dashStyleController.ScoreCandidate(
                candidate.Rotation,
                player,
                candidate.Destination,
                target,
                safeMovementDestination,
                decision,
                PositionalDashPolicy.IsActive(desiredPositional) ? "positional dash" : "directional dash"));
        }

        if (!dashStyleController.TrySelectBest(candidates, out var selected))
        {
            return false;
        }

        if (Geometry.AbsAngleDelta(player.Rotation, selected.Source) <= FacingController.DirectionalDashToleranceRadians)
        {
            return false;
        }

        facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(selected.Source, selected.Destination, $"turn for {actionName}", FacingBossModPolicy.AssistValidatedDash));
        this.lastSafeLandingPosition = selected.Destination;
        this.lastGapCloserSafety = $"turning for {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})";
        return true;
    }

    private bool TryRequestDirectForwardDashFacing(
        IBattleChara player,
        IBattleChara target,
        uint actionId,
        string actionName,
        float dashDistance,
        float requiredLandingRange,
        float currentDistanceToHitbox,
        Vector3? safeMovementDestination)
    {
        var toTarget = target.Position - player.Position;
        toTarget.Y = 0f;
        if (toTarget.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var direction = Vector3.Normalize(toTarget);
        var destination = player.Position + direction * dashDistance;
        var destinationDistanceToHitbox = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= currentDistanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
        {
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            target,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            return false;
        }

        if (this.ShouldRejectNonTankFrontOrCenterLanding(player, target, destination, alternativeExists: true, out var landingReason))
        {
            this.lastGapCloserSafety = landingReason;
            return false;
        }

        var desiredRotation = Geometry.DirectionToRotation(direction);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) <= FacingController.DirectionalDashToleranceRadians)
        {
            return false;
        }

        facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, $"turn for {actionName}", FacingBossModPolicy.AssistValidatedDash));
        this.lastSafeLandingPosition = destination;
        this.lastGapCloserSafety = $"turning for {actionName} ({decision.IntentLabel}, direct dash)";
        return true;
    }

    private bool TryFindSafeShukuchiDestination(
        IBattleChara player,
        IBattleChara? target,
        Vector3 targetPosition,
        float targetHitboxRadius,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        bool meleeReengagePathBlocked,
        float engagementRange,
        out Vector3 destination,
        out MobilityDecisionDiagnostics selectedDecision,
        out string styleReason)
    {
        var desiredPositional = this.ResolveDesiredDashPositional(player, target);
        var targetRotation = target?.Rotation ?? 0f;
        if (dashStyleController.ReengageStyleActive)
        {
            var candidates = new List<DashStyleCandidate<Vector3>>();
            foreach (var candidateInfo in this.EnumerateShukuchiCandidates(player, target, targetPosition, targetRotation, targetHitboxRadius, desiredPositional, meleeReengagePathBlocked, engagementRange))
            {
                var candidate = candidateInfo.Destination;
                var candidateReason = candidateInfo.Reason;
                if (Vector3.Distance(player.Position, candidate) > CombatConstants.GapCloserMaxRange)
                {
                    continue;
                }

                if (target != null &&
                    this.ShouldRejectNonTankFrontOrCenterLanding(player, target, candidate, alternativeExists: true, out var landingReason))
                {
                    this.lastGapCloserSafety = landingReason;
                    continue;
                }

                if (mobilityEvaluator.TryValidateDashDestination(
                    player,
                    candidate,
                    target,
                    safeMovementDestination,
                    MobilityIntent.Uptime,
                    "Shukuchi",
                    ActionUse.NinjaShukuchiActionId,
                    0f,
                    requireSafetyProgress: false,
                    requireUptimeProgress: true,
                    requireVnavReachable: false,
                    out var decision))
                {
                    var reason = candidateReason.Length > 0
                        ? candidateReason
                        : styleOpportunity.Reason is "knockback recovery" or "capped charge" or "pack surf"
                        ? styleOpportunity.Reason
                        : PositionalDashPolicy.IsActive(desiredPositional) &&
                          PositionalDashPolicy.IsSatisfied(desiredPositional, candidate, targetPosition, targetRotation)
                            ? "positional Shukuchi"
                        : "precision Shukuchi";
                    if (candidateReason.Length > 0)
                    {
                        destination = candidate;
                        selectedDecision = decision;
                        styleReason = reason;
                        this.lastSafeLandingPosition = destination;
                        this.lastGapCloserSafety = $"Shukuchi ready ({decision.IntentLabel}, {styleReason})";
                        return true;
                    }

                    candidates.Add(dashStyleController.ScoreCandidate(
                        candidate,
                        player,
                        candidate,
                        target,
                        safeMovementDestination,
                        decision,
                        reason));
                    continue;
                }

                this.lastGapCloserSafety = decision.RiskReason;
            }

            if (dashStyleController.TrySelectBest(candidates, out var selected))
            {
                destination = selected.Destination;
                selectedDecision = selected.Decision;
                styleReason = selected.Reason;
                this.lastSafeLandingPosition = destination;
                this.lastGapCloserSafety = $"Shukuchi ready ({selected.Decision.IntentLabel}, {selected.Reason})";
                return true;
            }

            destination = default;
            selectedDecision = MobilityDecisionDiagnostics.Empty;
            styleReason = string.Empty;
            if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
            {
                this.lastGapCloserSafety = "no safe Shukuchi destination";
            }

            return false;
        }

        var positionalCandidates = PositionalDashPolicy.IsActive(desiredPositional)
            ? new List<DashStyleCandidate<Vector3>>()
            : null;
        MobilityDecisionDiagnostics? firstFallbackDecision = null;
        var firstFallbackDestination = default(Vector3);
        var firstFallbackReason = string.Empty;

        foreach (var candidateInfo in this.EnumerateShukuchiCandidates(player, target, targetPosition, targetRotation, targetHitboxRadius, desiredPositional, meleeReengagePathBlocked, engagementRange))
        {
            var candidate = candidateInfo.Destination;
            var candidateReason = candidateInfo.Reason;
            if (Vector3.Distance(player.Position, candidate) > CombatConstants.GapCloserMaxRange)
            {
                continue;
            }

            if (target != null &&
                this.ShouldRejectNonTankFrontOrCenterLanding(player, target, candidate, alternativeExists: true, out var landingReason))
            {
                this.lastGapCloserSafety = landingReason;
                continue;
            }

            if (mobilityEvaluator.TryValidateDashDestination(
                player,
                candidate,
                target,
                safeMovementDestination,
                MobilityIntent.Uptime,
                "Shukuchi",
                ActionUse.NinjaShukuchiActionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                if (candidateReason.Length > 0)
                {
                    destination = candidate;
                    selectedDecision = decision;
                    styleReason = candidateReason;
                    this.lastSafeLandingPosition = destination;
                    this.lastGapCloserSafety = $"Shukuchi ready ({decision.IntentLabel}, {styleReason})";
                    return true;
                }

                if (PositionalDashPolicy.IsActive(desiredPositional) &&
                    PositionalDashPolicy.IsSatisfied(desiredPositional, candidate, targetPosition, targetRotation))
                {
                    positionalCandidates!.Add(dashStyleController.ScoreCandidate(
                        candidate,
                        player,
                        candidate,
                        target,
                        safeMovementDestination,
                        decision,
                        candidateReason.Length > 0 ? candidateReason : "positional Shukuchi"));
                    continue;
                }

                if (positionalCandidates != null)
                {
                    firstFallbackDecision ??= decision;
                    firstFallbackDestination = candidate;
                    firstFallbackReason = candidateReason;
                    continue;
                }

                destination = candidate;
                selectedDecision = decision;
                styleReason = candidateReason;
                this.lastGapCloserSafety = styleReason.Length > 0
                    ? $"Shukuchi ready ({decision.IntentLabel}, {styleReason})"
                    : $"Shukuchi ready ({decision.IntentLabel})";
                return true;
            }

            this.lastGapCloserSafety = decision.RiskReason;
        }

        if (positionalCandidates != null && dashStyleController.TrySelectBest(positionalCandidates, out var selectedPositional))
        {
            destination = selectedPositional.Destination;
            selectedDecision = selectedPositional.Decision;
            styleReason = selectedPositional.Reason;
            this.lastGapCloserSafety = $"Shukuchi ready ({selectedPositional.Decision.IntentLabel}, {selectedPositional.Reason})";
            return true;
        }

        if (firstFallbackDecision != null)
        {
            destination = firstFallbackDestination;
            selectedDecision = firstFallbackDecision;
            styleReason = firstFallbackReason;
            this.lastGapCloserSafety = styleReason.Length > 0
                ? $"Shukuchi ready ({firstFallbackDecision.IntentLabel}, {styleReason})"
                : $"Shukuchi ready ({firstFallbackDecision.IntentLabel})";
            return true;
        }

        destination = default;
        selectedDecision = MobilityDecisionDiagnostics.Empty;
        styleReason = string.Empty;
        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
        {
            this.lastGapCloserSafety = "no safe Shukuchi destination";
        }

        return false;
    }

    private bool ShouldAllowKnockbackRecoveryDashDirection(
        IBattleChara player,
        IBattleChara? target,
        Vector3 destination,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        MobilityDecisionDiagnostics decision)
    {
        if (styleOpportunity.Reason != "knockback recovery" || target == null)
        {
            return true;
        }

        if (GapCloserDecisionPolicy.ShouldAllowKnockbackRecoveryDashDirection(
                player.Position,
                destination,
                target.Position,
                safeMovementDestination,
                decision.SafetyGain > 0.1f,
                out var reason))
        {
            return true;
        }

        this.lastGapCloserSafety = reason;
        mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, decision.ActionName, reason);
        return false;
    }

    private bool ShouldRejectNonTankFrontOrCenterLanding(
        IBattleChara player,
        IBattleChara target,
        Vector3 destination,
        bool alternativeExists,
        out string reason)
    {
        return GapCloserDecisionPolicy.ShouldRejectNonTankFrontOrCenterLanding(
            JobRoles.IsTankJob(player.ClassJob.RowId),
            destination,
            target.Position,
            target.Rotation,
            player.HitboxRadius,
            target.HitboxRadius,
            alternativeExists,
            out reason);
    }

    private bool AcceptStyleOpportunity(DashStyleReengageOpportunity? styleOpportunity, MobilityDecisionDiagnostics decision, ref string lastSafety)
    {
        var pressure = mechanicPressure();
        if (styleOpportunity is { Active: true } &&
            pressure.BadForGreedyDash &&
            !pressure.KnockbackRecoveryActive)
        {
            lastSafety = pressure.FormatOptionalMovementHoldReason();
            return false;
        }

        if (styleOpportunity is not { Active: true } activeStyle || !activeStyle.RequiresStrongGain)
        {
            return true;
        }

        if (dashStyleController.MeetsStrongStyleGain(decision))
        {
            return true;
        }

        lastSafety = $"{activeStyle.Reason}: style gain under 3y";
        return false;
    }

    private bool ShouldHoldOptionalReengageDash(BossModMechanicPressure pressure)
    {
        return pressure.BadForOptionalMovement &&
               !pressure.KnockbackRecoveryActive &&
               !dashStyleController.KnockbackRecoveryActive;
    }

    private bool ShouldConserveTrashPullGapCloser(
        IBattleChara player,
        IGameObject target,
        float distanceToHitbox,
        uint classJobId,
        uint? primaryActionId,
        bool currentPositionUnsafe,
        bool walkingWouldMissUsefulGcd,
        out string reason)
    {
        reason = string.Empty;

        var trash = trashPullDiagnostics();
        if (!IsConservativeTrashPullContext(trash))
        {
            return false;
        }

        if (target is IBattleNpc battleNpc && bossMod.HasModuleByDataId(battleNpc.BaseId))
        {
            return false;
        }

        var useThreshold = GapCloserDecisionPolicy.ResolveConservativeTrashGapCloserUseThreshold(classJobId);
        var anchor = GapCloserDecisionPolicy.ResolveTrashPullGapCloserAnchor(trash, target);
        var anchorDistance = Geometry.Distance2D(player.Position, anchor);
        var triggerDistance = MathF.Max(distanceToHitbox, anchorDistance);
        var currentCharges = primaryActionId.HasValue ? ActionUse.GetCurrentCharges(primaryActionId.Value) : 0u;
        return GapCloserDecisionPolicy.ShouldConserveTrashPullGapCloser(
            triggerDistance,
            useThreshold,
            currentCharges,
            currentPositionUnsafe,
            walkingWouldMissUsefulGcd,
            out reason);
    }

    private bool ShouldAllowTrashPullGapCloserTarget(IBattleChara player, IGameObject target, out string reason)
    {
        reason = string.Empty;

        var trash = trashPullDiagnostics();
        if (!IsConservativeTrashPullContext(trash))
        {
            return true;
        }

        if (trash.Phase != TrashPullPhase.Gathering)
        {
            return true;
        }

        if (target is IBattleNpc battleNpc &&
            trash.DominantTargetIds.Count > 0 &&
            !trash.DominantTargetIds.Contains(battleNpc.GameObjectId))
        {
            reason = "trash pull target is not in tank pack";
            return false;
        }

        var anchor = GapCloserDecisionPolicy.ResolveTrashPullGapCloserAnchor(trash, target);
        if (!ShouldAllowTrashPullGapCloserTarget(
                player.Position,
                target.Position,
                anchor,
                out reason))
        {
            return false;
        }

        return true;
    }

    internal static bool ShouldAllowTrashPullGapCloserTarget(Vector3 playerPosition, Vector3 targetPosition, Vector3 anchorPosition, out string reason)
        => GapCloserDecisionPolicy.ShouldAllowTrashPullGapCloserTarget(playerPosition, targetPosition, anchorPosition, out reason);

    internal static bool ShouldTryHostileRelay(uint classJobId, float intendedDistanceToHitbox, float engagementRange)
        => GapCloserDecisionPolicy.CanUseHostileRelayGapCloser(classJobId) &&
           intendedDistanceToHitbox > MathF.Max(CombatConstants.MeleeActionRange, engagementRange);

    private bool TryIsMeleeReengagePathBlocked(IBattleChara player, IBattleChara target, float engagementRange, out string reason)
    {
        reason = string.Empty;
        var foundBlockedPath = false;
        var blockedReason = string.Empty;
        var foundSafeMeleePoint = false;

        foreach (var candidate in EnumerateMeleeReengagePathCandidates(player, target, engagementRange))
        {
            if (!bossModSafety.TryIsPositionSafe(candidate, out var safe, out var safetyReason))
            {
                reason = safetyReason;
                continue;
            }

            if (!safe)
            {
                continue;
            }

            foundSafeMeleePoint = true;
            if (!bossModSafety.TryCheckNavigationLine(player.Position, candidate, out var lineCheck))
            {
                continue;
            }

            if (lineCheck.Clear)
            {
                reason = "BMR walking path to melee is clear";
                return false;
            }

            foundBlockedPath = true;
            blockedReason = lineCheck.Reason;
        }

        if (foundBlockedPath)
        {
            reason = $"BMR walking path to melee blocked: {blockedReason}";
            return true;
        }

        reason = foundSafeMeleePoint
            ? "BMR walking path to melee unknown"
            : "no BossMod-safe melee reengage point found";
        return false;
    }

    private static IEnumerable<Vector3> EnumerateMeleeReengagePathCandidates(IBattleChara player, IBattleChara target, float engagementRange)
    {
        var ringRadius = target.HitboxRadius + player.HitboxRadius + MathF.Max(CombatConstants.MeleeActionRange, engagementRange);
        var toPlayer = player.Position - target.Position;
        toPlayer.Y = 0f;
        if (toPlayer.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toPlayer);
            yield return new Vector3(
                target.Position.X + (direction.X * ringRadius),
                player.Position.Y,
                target.Position.Z + (direction.Z * ringRadius));
        }

        for (var i = 0; i < 24; i++)
        {
            var angle = i * (MathF.Tau / 24f);
            yield return new Vector3(
                target.Position.X + (MathF.Cos(angle) * ringRadius),
                player.Position.Y,
                target.Position.Z + (MathF.Sin(angle) * ringRadius));
        }
    }

    private bool IsGcdReengageUrgent(float distanceToHitbox, float engagementRange)
    {
        if (!this.TryGetOffensiveGcdTiming(services.TargetManager.Target as IBattleChara, out var action, out _))
        {
            return false;
        }

        return ShouldTreatGcdReengageAsUrgent(distanceToHitbox, engagementRange, action);
    }

    internal static bool ShouldTreatGcdReengageAsUrgent(float distanceToHitbox, float engagementRange, float gcdRemaining)
    {
        var action = new RsrGcdActionTimingSnapshot(0, 0, "next GCD", "test", 0, gcdRemaining, 0f, 2.5f, 0.35f);
        return ShouldTreatGcdReengageAsUrgent(distanceToHitbox, engagementRange, action);
    }

    internal static bool ShouldTreatGcdReengageAsUrgent(float distanceToHitbox, float engagementRange, RsrGcdActionTimingSnapshot action)
    {
        return distanceToHitbox > engagementRange &&
               !GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(distanceToHitbox, engagementRange, action, out _);
    }

    internal static bool ShouldTreatMissingRsrTimingAsMissedMelee(
        bool playerIsMeleeRangeRole,
        bool hasReengageTiming,
        float distanceToHitbox,
        float engagementRange)
    {
        return playerIsMeleeRangeRole &&
               !hasReengageTiming &&
               distanceToHitbox > engagementRange;
    }

    internal static bool ShouldAllowKnockbackRecoveryReengage(
        bool knockbackRecoveryActive,
        bool playerIsMeleeRangeRole,
        bool targetHasBossModule,
        bool antiKnockbackActive,
        float distanceToHitbox,
        float engagementRange)
    {
        return knockbackRecoveryActive &&
               playerIsMeleeRangeRole &&
               targetHasBossModule &&
               !antiKnockbackActive &&
               distanceToHitbox > engagementRange;
    }

    private bool TryFindHostileRelayGapCloserTarget(
        IBattleChara player,
        IBattleNpc intendedTarget,
        float intendedDistanceToHitbox,
        uint classJobId,
        float engagementRange,
        RsrGcdActionTimingSnapshot? reengageTiming,
        out IBattleNpc relayTarget)
    {
        relayTarget = null!;
        var maxRange = GapCloserDecisionPolicy.ResolveHostileRelayGapCloserRange(classJobId);
        var bestScore = float.NegativeInfinity;

        foreach (var candidate in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (candidate.GameObjectId == intendedTarget.GameObjectId ||
                !IsUsableGapCloserTarget(candidate))
            {
                continue;
            }

            var candidateDistance = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, candidate.Position, candidate.HitboxRadius);
            if (candidateDistance <= CombatConstants.MeleeActionRange || candidateDistance > maxRange)
            {
                continue;
            }

            if (!Geometry.TryCalculateTargetDashDestination(player.Position, candidate.Position, candidateDistance, out var landing))
            {
                continue;
            }

            if (!GapCloserDecisionPolicy.TryEvaluateHostileRelayDash(
                    player.Position,
                    player.HitboxRadius,
                    landing,
                    intendedTarget.Position,
                    intendedTarget.HitboxRadius,
                    out var gain,
                    out var dot,
                    out _))
            {
                continue;
            }

            var relayLandingDistance = Geometry.DistanceToHitbox(landing, player.HitboxRadius, intendedTarget.Position, intendedTarget.HitboxRadius);
            if (reengageTiming != null &&
                !GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(relayLandingDistance, engagementRange, reengageTiming, out _))
            {
                continue;
            }

            var score = gain + (dot * 2f) - (candidateDistance * 0.025f);
            if (score > bestScore)
            {
                relayTarget = candidate;
                bestScore = score;
            }
        }

        return bestScore > float.NegativeInfinity &&
               Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, relayTarget.Position, relayTarget.HitboxRadius) < intendedDistanceToHitbox;
    }

    internal static bool ShouldUseHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out string reason)
        => GapCloserDecisionPolicy.ShouldUseHostileRelayDash(
            playerPosition,
            playerRadius,
            landingPosition,
            intendedTargetPosition,
            intendedTargetRadius,
            out reason);

    internal static bool ShouldBlockRangedReengageGapCloser(uint classJobId)
    {
        return classJobId is 5 or 23 or 24 or 25 or 35 or 38 or 40 or 42;
    }

    private static bool IsConservativeTrashPullContext(TrashPullDiagnostics trash)
        => GapCloserDecisionPolicy.IsConservativeTrashPullContext(trash);

    private static bool IsNinjaMudraWindow(IBattleChara player)
    {
        return player.ClassJob.RowId is 29 or 30 &&
               HasAnyStatus(
                   player,
                   ActionUse.NinjaMudraStatusId,
                   ActionUse.NinjaTenChiJinStatusId,
                   ActionUse.NinjaThreeMudraStatusId);
    }

    private static bool HasAntiKnockbackStatus(IBattleChara player)
    {
        return HasAnyStatus(
            player,
            ActionUse.ArmsLengthStatusId,
            ActionUse.SurecastStatusId);
    }

    private bool HasActiveCasterStationaryBuff(IBattleChara player)
    {
        return HasAnyStatus(
            player,
            ActionUse.LeyLinesStatusId,
            ActionUse.CircleOfPowerStatusId,
            ActionUse.PictomancerStarryMuseStatusId,
            ActionUse.PictomancerHyperphantasiaStatusId,
            ActionUse.PictomancerInspirationStatusId);
    }

    private static bool HasAnyStatus(IBattleChara player, params uint[] statusIds)
    {
        return player.StatusList.Any(status => status.RemainingTime > 0f && statusIds.Contains(status.StatusId));
    }

    private IEnumerable<DirectionalDashFacingCandidate> EnumerateForwardDashFacingCandidates(IBattleChara player, IBattleChara target, float dashDistance, Positional desiredPositional)
    {
        var targetSurface = this.CalculateTargetMaxMeleeSurface();
        var ringRadius = target.HitboxRadius + player.HitboxRadius + targetSurface;
        foreach (var idealLanding in PositionalDashPolicy.EnumeratePreferredLandings(player.Position, target.Position, target.Rotation, ringRadius, desiredPositional))
        {
            if (TryCreateDirectionalDashFacingCandidate(player.Position, idealLanding, dashDistance, out var candidate))
            {
                yield return candidate;
            }
        }

        var toTarget = target.Position - player.Position;
        toTarget.Y = 0f;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            var idealLanding = target.Position - direction * ringRadius;
            if (TryCreateDirectionalDashFacingCandidate(player.Position, idealLanding, dashDistance, out var candidate))
            {
                yield return candidate;
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var angle = i * (MathF.Tau / 24f);
            var idealLanding = new Vector3(
                target.Position.X + MathF.Sin(angle) * ringRadius,
                player.Position.Y,
                target.Position.Z + MathF.Cos(angle) * ringRadius);
            if (TryCreateDirectionalDashFacingCandidate(player.Position, idealLanding, dashDistance, out var candidate))
            {
                yield return candidate;
            }
        }
    }

    private float CalculateMaxMeleeLandingError(IBattleChara player, Vector3 destination, IBattleChara target)
    {
        var surfaceDistance = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        return MathF.Abs(surfaceDistance - this.CalculateTargetMaxMeleeSurface());
    }

    private float CalculateTargetMaxMeleeSurface()
    {
        return MathF.Min(CombatConstants.GapCloserDestinationMeleeRange, MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange));
    }

    private static bool TryCreateDirectionalDashFacingCandidate(Vector3 playerPosition, Vector3 idealLanding, float dashDistance, out DirectionalDashFacingCandidate candidate)
    {
        var direction = idealLanding - playerPosition;
        direction.Y = 0f;
        if (direction.LengthSquared() <= 0.0001f)
        {
            candidate = default;
            return false;
        }

        direction = Vector3.Normalize(direction);
        var destination = playerPosition + direction * dashDistance;
        candidate = new(Geometry.DirectionToRotation(direction), destination);
        return true;
    }

    private uint? GetPrimaryReengageActionId(uint classJobId)
    {
        return classJobId switch
        {
            1 or 19 when config.GapCloserPLD => ActionUse.PaladinInterveneActionId,
            3 or 21 when config.GapCloserWAR => ActionUse.WarriorOnslaughtActionId,
            32 when config.GapCloserDRK => ActionUse.DarkKnightShadowstrideActionId,
            37 when config.GapCloserGNB => ActionUse.GunbreakerTrajectoryActionId,
            2 or 20 when config.GapCloserMNK => ActionUse.MonkThunderclapActionId,
            4 or 22 when config.GapCloserDRG => ActionUse.DragoonWingedGlideActionId,
            29 or 30 when config.GapCloserNIN => ActionUse.NinjaShukuchiActionId,
            34 when config.GapCloserSAM => ActionUse.SamuraiGyotenActionId,
            39 when config.GapCloserRPR => ActionUse.ReaperHellsIngressActionId,
            41 when config.GapCloserVPR => ActionUse.ViperSlitherActionId,
            _ => null
        };
    }

    private IEnumerable<LocationDashCandidate> EnumerateShukuchiCandidates(
        IBattleChara player,
        IBattleChara? target,
        Vector3 targetPosition,
        float targetRotation,
        float targetHitboxRadius,
        Positional desiredPositional,
        bool meleeReengagePathBlocked,
        float engagementRange)
    {
        if (target != null)
        {
            foreach (var candidate in this.EnumerateMeleeStackRecoveryLocationCandidates(player, target, meleeReengagePathBlocked, engagementRange))
            {
                yield return candidate;
            }
        }

        var playerPosition = player.Position;
        var radius = targetHitboxRadius + CombatConstants.GapCloserDestinationMeleeRange;
        foreach (var preferred in PositionalDashPolicy.EnumeratePreferredLandings(playerPosition, targetPosition, targetRotation, radius, desiredPositional))
        {
            yield return new(preferred, string.Empty);
        }

        var toTarget = targetPosition - playerPosition;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            yield return new(
                new Vector3(targetPosition.X - (direction.X * radius), playerPosition.Y, targetPosition.Z - (direction.Z * radius)),
                string.Empty);
        }

        for (var i = 0; i < 16; i++)
        {
            var angle = i * (MathF.Tau / 16f);
            yield return new(
                new Vector3(
                    targetPosition.X + MathF.Cos(angle) * radius,
                    playerPosition.Y,
                    targetPosition.Z + MathF.Sin(angle) * radius),
                string.Empty);
        }
    }

    private IEnumerable<LocationDashCandidate> EnumerateMeleeStackRecoveryLocationCandidates(
        IBattleChara player,
        IBattleChara target,
        bool meleeReengagePathBlocked,
        float engagementRange)
    {
        if (!meleeReengagePathBlocked)
        {
            yield break;
        }

        var partyPositions = PartyAllyProvider.EnumerateVisiblePartyAllies(services, player)
            .Select(ally => ally.Position)
            .ToArray();
        foreach (var ally in PartyAllyProvider.EnumerateVisiblePartyAllies(services, player)
                     .Where(ally => ally.GameObjectId != player.GameObjectId &&
                                    ally.GameObjectId != 0 &&
                                    !ally.IsDead &&
                                    ally.CurrentHp > 0)
                     .OrderBy(ally => Geometry.DistanceToHitbox(ally.Position, player.HitboxRadius, target.Position, target.HitboxRadius))
                     .ThenByDescending(ally => Geometry.Distance2D(player.Position, ally.Position)))
        {
            if (GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
                    playerIsMeleeRangeRole: JobRoles.GetRangeRole(player) == RangeRole.Melee,
                    reengageWalkBlocked: true,
                    anchorPosition: ally.Position,
                    targetPosition: target.Position,
                    playerRadius: player.HitboxRadius,
                    targetRadius: target.HitboxRadius,
                    engagementRange: engagementRange,
                    partyPositions: partyPositions,
                    pressure: mechanicPressure(),
                    moveDistance: Geometry.Distance2D(player.Position, ally.Position),
                    out var reason))
            {
                yield return new(ally.Position, "melee stack recovery");
            }
            else
            {
                this.lastGapCloserSafety = reason;
            }
        }
    }

    private unsafe Positional ResolveDesiredDashPositional(IBattleChara player, IBattleChara? target)
        => this.ResolveDesiredDashPositional(player, target, null, out _);

    private unsafe Positional ResolveDesiredDashPositional(IBattleChara player, IBattleChara? target, RsrGcdActionTimingSnapshot? action, out string reason)
    {
        reason = string.Empty;
        var positional = positionalIntent();
        if (target == null ||
            !PositionalDashPolicy.IsActive(positional) ||
            PositionalDashPolicy.IsSatisfied(positional, player.Position, target.Position, target.Rotation))
        {
            return Positional.Any;
        }

        if (config.ManageTrueNorth && HasTrueNorthCoverage(player))
        {
            reason = "True North covers positional; dash held";
            return Positional.Any;
        }

        action ??= this.TryGetOffensiveGcdTiming(target, out var nextAction, out var timingReason) ? nextAction : null;
        if (action == null)
        {
            reason = "RSR positional GCD timing unavailable";
            return Positional.Any;
        }

        if (!PositionalTrueNorthPolicy.TryEstimateWalkDistance(
                player.Position,
                player.HitboxRadius,
                target.Position,
                target.HitboxRadius,
                target.Rotation,
                positional,
                candidate => bossModSafety.TryIsPositionSafe(candidate, out var safe, out _) && safe,
                out var moveDistance,
                out var walkEstimateReason))
        {
            reason = walkEstimateReason;
            return positional;
        }

        if (GapCloserDecisionPolicy.CanWalkToPositionalBeforeGcd(positional, action, moveDistance, out var walkReason))
        {
            reason = walkReason;
            return Positional.Any;
        }

        reason = walkReason;
        return positional;
    }

    private static unsafe bool HasTrueNorthCoverage(IBattleChara player)
    {
        return HasAnyStatus(player, ActionUse.TrueNorthStatusId) ||
               ActionUse.GetCurrentCharges(ActionUse.TrueNorthActionId) > 0;
    }

    private IEnumerable<IBattleChara> EnumerateFriendlyReengageTargets(IBattleChara player, IBattleChara currentTarget, float maxRange)
    {
        return PartyAllyProvider.EnumerateVisiblePartyAllies(services, player)
            .Where(ally =>
                ally.GameObjectId != player.GameObjectId &&
                ally.GameObjectId != 0 &&
                !ally.IsDead &&
                ally.CurrentHp > 0 &&
                Vector3.Distance(player.Position, ally.Position) <= maxRange)
            .OrderBy(ally => Geometry.DistanceToHitbox(ally.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius))
            .ThenByDescending(ally => Vector3.Distance(player.Position, ally.Position));
    }

    private IGameObject? TryFindReaperPortal(IGameObject player)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == ActionUse.ReaperHellsgatePortalDataId && obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }
}
