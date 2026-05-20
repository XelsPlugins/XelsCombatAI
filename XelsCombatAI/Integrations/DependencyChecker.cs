using System.Collections.Generic;

namespace XelsCombatAI.Integrations;

internal sealed class DependencyChecker(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    AvariceIpc avarice,
    RotationSolverIpc rotationSolver)
{
    public bool DependenciesAvailable(out string missing, bool forceRefresh = false)
    {
        _ = forceRefresh;

        if (!this.CanProbeDependencies(out missing))
        {
            return false;
        }

        var missingParts = new List<string>();

        if (!this.IsBossModAvailable())
        {
            missingParts.Add("BossMod Reborn is not loaded or its IPC is unavailable");
        }

        if (!this.IsAvariceAvailable())
        {
            missingParts.Add("Avarice is not loaded");
        }

        missing = string.Join("; ", missingParts);
        return missingParts.Count == 0;
    }

    public string? GetDependencyWarning()
    {
        return this.DependenciesAvailable(out var missing) ? null : missing;
    }

    public string? GetTrueNorthWarning(bool? rsrTrueNorthDisabled)
    {
        if (!this.CanProbeDependencies(out _))
        {
            return null;
        }

        if (!config.ManagePositionals || !config.ManageTrueNorth)
        {
            return null;
        }

        if (!this.IsRotationSolverAvailable())
        {
            return "Manage True North is enabled, but Rotation Solver Reborn is not loaded or its IPC is unavailable. XCAI will continue without disabling RSR Auto True North.";
        }

        if (rsrTrueNorthDisabled == false)
        {
            return "Manage True North is enabled, but XCAI could not disable Rotation Solver Reborn Auto True North.";
        }

        return null;
    }

    public bool IsBossModAvailable()
    {
        return this.CanProbeDependencies(out _) && bossMod.IsIpcReady();
    }

    public bool IsAvariceAvailable()
    {
        return this.CanProbeDependencies(out _) && avarice.IsAvailable();
    }

    public bool IsRotationSolverAvailable()
    {
        return this.CanProbeDependencies(out _) && rotationSolver.IsAvailable(services.PluginInterface);
    }

    public bool CanProbeDependencies(out string waitingReason)
    {
        if (!services.PluginInterface.IsAutoUpdateComplete)
        {
            waitingReason = "Dalamud plugin updates are still settling";
            return false;
        }

        if (!services.Framework.IsInFrameworkUpdateThread)
        {
            waitingReason = "game state will be checked from the framework thread";
            return false;
        }

        if (!services.ClientState.IsLoggedIn ||
            services.ClientState.TerritoryType == 0 ||
            services.ObjectTable.LocalPlayer == null)
        {
            waitingReason = "game state is still loading";
            return false;
        }

        waitingReason = string.Empty;
        return true;
    }
}
