using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed class CombatComposition : IDisposable
{
    private CombatComposition(CombatRuntime runtime, DecisionOverlayController decisionOverlay, JobRangeProvider jobRangeProvider, RotationSolverActionReflection rotationSolverActions, PictomancerStarryMusePositioningController pictomancerStarryMusePositioningController)
    {
        this.Runtime = runtime;
        this.DecisionOverlay = decisionOverlay;
        this.JobRangeProvider = jobRangeProvider;
        this.RotationSolverActions = rotationSolverActions;
        this.PictomancerStarryMusePositioningController = pictomancerStarryMusePositioningController;
    }

    public CombatRuntime Runtime { get; }
    public DecisionOverlayController DecisionOverlay { get; }
    public JobRangeProvider JobRangeProvider { get; }
    private RotationSolverActionReflection RotationSolverActions { get; }
    private PictomancerStarryMusePositioningController PictomancerStarryMusePositioningController { get; }

    public static CombatComposition Create(
        Configuration config,
        DalamudServices services,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        string configDirectory,
        Action saveConfig,
        Action updateDtr,
        Action<string> print)
    {
        var bossModGate = new BossModRuntimeGate();
        var bossMod = new BossModIpc(pluginInterface, log, bossModGate);
        var mechanicPressure = new BossModMechanicPressureMonitor();
        var bossModSafety = new BossModReflectionSafety(pluginInterface, log, bossModGate, bossMod);
        var vnavmesh = new VNavmeshIpc(pluginInterface);
        var manualMovement = new ManualMovementInputDetector();
        var autoFaceTargetOptionController = new AutoFaceTargetOptionController(config, services);
        var manualCorrectionFeedback = new ManualCorrectionFeedback();
        var rotationSolver = new RotationSolverIpc(pluginInterface, log);
        var rotationSolverActions = new RotationSolverActionReflection(pluginInterface, log);
        var dependencyChecker = new DependencyChecker(config, services, bossMod, rotationSolver);
        var jobRangeProvider = new JobRangeProvider(services);
        jobRangeProvider.Initialize();
        var targetUptimePlanner = new TargetUptimePlanner(services, bossMod, jobRangeProvider, rotationSolverActions);
        BossModPresetController? presetController = null;
        CombatRuntime? runtime = null;
        var mobilityDecisionEvaluator = new MobilityDecisionEvaluator(bossModSafety, vnavmesh, jobRangeProvider);
        var arenaEdgePositioningController = new ArenaEdgePositioningController(config, services);
        var dashStyleController = new DashStyleController(config, jobRangeProvider, arenaEdgePositioningController);
        var facingController = new FacingController(config, services, bossMod, () => mechanicPressure.Current, manualMovement, new LocalPlayerFacingActuator());
        var redMageMeleeComboController = new RedMageMeleeComboController(config, services, rotationSolverActions, bossModSafety, mobilityDecisionEvaluator, facingController, () => targetUptimePlanner.CurrentTargetHasBossModule());
        var aoePackPositioningController = new AoePackPositioningController(config, services, rotationSolverActions, () => runtime?.AutomatedMovementSuppressed == true, rotationSolver, () => targetUptimePlanner.CurrentTargetHasBossModule(), jobRangeProvider, () => mechanicPressure.Current);
        targetUptimePlanner.TargetUptimeRangeOverride = () =>
            redMageMeleeComboController.GetTargetUptimeRangeOverride() ??
            aoePackPositioningController.GetTargetUptimeRangeOverride();
        var positionalsController = new PositionalsController(config, services, rotationSolver, rotationSolverActions, positional => presetController!.SetPositional(positional), updateDtr, () => aoePackPositioningController.Status);
        var passageOfArmsPositioningController = new PassageOfArmsPositioningController(config, services, () => runtime?.AutomatedMovementSuppressed == true);
        var healerAoePositioningController = new HealerAoePositioningController(config, services, bossMod, rotationSolverActions, () => runtime?.AutomatedMovementSuppressed == true, () => targetUptimePlanner.CurrentTargetHasBossModule(), mobilityDecisionEvaluator, facingController, () => mechanicPressure.Current);
        var survivabilityZonePositioningController = new SurvivabilityZonePositioningController(config, services, () => runtime?.AutomatedMovementSuppressed == true);
        var pictomancerStarryMusePositioningController = new PictomancerStarryMusePositioningController(config, services, rotationSolverActions, mobilityDecisionEvaluator, facingController, () => runtime?.AutomatedMovementSuppressed == true, () => mechanicPressure.Current);
        var bossCenterAvoidanceController = new BossCenterAvoidanceController(config, services, () => runtime?.AutomatedMovementSuppressed == true, () => targetUptimePlanner.CurrentTargetHasBossModule(), () => mechanicPressure.Current);
        var socialSpacingPositioningController = new SocialSpacingPositioningController(config, services, bossModSafety, () => runtime?.AutomatedMovementSuppressed == true);
        var tankBehaviorController = new TankBehaviorController(config, services, () => targetUptimePlanner.CurrentTargetHasBossModule(), () => mechanicPressure.Current);
        IBossModGoalZoneContributor[] legacyMovementContributors = [aoePackPositioningController, passageOfArmsPositioningController, healerAoePositioningController, survivabilityZonePositioningController, pictomancerStarryMusePositioningController, bossCenterAvoidanceController, arenaEdgePositioningController, socialSpacingPositioningController, tankBehaviorController];
        var aoeGoalHook = new BossModGoalZoneHook(config, pluginInterface, services, log, bossModGate, legacyMovementContributors, manualCorrectionFeedback);
        var gapCloserController = new GapCloserController(
            config,
            services,
            bossMod,
            bossModSafety,
            jobRangeProvider,
            mobilityDecisionEvaluator,
            dashStyleController,
            facingController,
            rotationSolverActions,
            () => presetController?.LastPositional ?? Positional.Any,
            () => aoePackPositioningController.RsrHenchedActive,
            () => aoePackPositioningController.Status.TrashPull,
            () => mechanicPressure.Current);
        var escapeGapCloserController = new EscapeGapCloserController(
            config,
            services,
            bossModSafety,
            mobilityDecisionEvaluator,
            gapCloserController,
            dashStyleController,
            facingController,
            () => presetController?.LastPositional ?? Positional.Any,
            () => aoeGoalHook.MovementDiagnostics,
            () => mechanicPressure.Current);
        var combatLogWriter = new CombatLogWriter(Path.Combine(configDirectory, "combat-logs"), log);
        presetController = new BossModPresetController(
            config,
            services,
            bossMod,
            bossModSafety,
            targetUptimePlanner,
            positionalsController,
            gapCloserController,
            escapeGapCloserController,
            redMageMeleeComboController,
            pictomancerStarryMusePositioningController,
            () => mechanicPressure.Current);

        runtime = new CombatRuntime(
            config,
            services,
            bossMod,
            mechanicPressure,
            bossModGate,
            dependencyChecker,
            presetController,
            positionalsController,
            rotationSolver,
            rotationSolverActions,
            bossModSafety,
            aoeGoalHook,
            aoePackPositioningController,
            passageOfArmsPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            pictomancerStarryMusePositioningController,
            arenaEdgePositioningController,
            tankBehaviorController,
            redMageMeleeComboController,
            combatLogWriter,
            manualMovement,
            autoFaceTargetOptionController,
            manualCorrectionFeedback,
            mobilityDecisionEvaluator,
            gapCloserController,
            escapeGapCloserController,
            dashStyleController,
            facingController,
            jobRangeProvider,
            saveConfig,
            updateDtr,
            print);
        var decisionOverlay = new DecisionOverlayController(
            config,
            services,
            aoePackPositioningController,
            () => targetUptimePlanner.CurrentTargetHasBossModule(),
            passageOfArmsPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            pictomancerStarryMusePositioningController,
            bossModSafety,
            mobilityDecisionEvaluator,
            gapCloserController,
            escapeGapCloserController,
            redMageMeleeComboController,
            rotationSolverActions);

        return new CombatComposition(runtime, decisionOverlay, jobRangeProvider, rotationSolverActions, pictomancerStarryMusePositioningController);
    }

    public void Dispose()
    {
        this.JobRangeProvider.Dispose();
        this.RotationSolverActions.Dispose();
        this.PictomancerStarryMusePositioningController.Dispose();
    }
}
