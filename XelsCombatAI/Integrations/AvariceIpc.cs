using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class AvariceIpc
{
    private readonly ICallGateSubscriber<IntPtr, int> cardinalDirection;
    private readonly IPluginLog log;
    private DateTime nextFailureLog = DateTime.MinValue;

    public AvariceIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.cardinalDirection = pluginInterface.GetIpcSubscriber<IntPtr, int>("Avarice.CardinalDirection");
    }

    public bool IsAvailable()
    {
        try
        {
            return this.cardinalDirection.HasFunction;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Could not check Avarice IPC readiness.");
            return false;
        }
    }

    private void LogRecoverableFailure(Exception ex, string message)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextFailureLog)
        {
            return;
        }

        this.log.Verbose(ex, message);
        this.nextFailureLog = now.AddSeconds(10);
    }
}
