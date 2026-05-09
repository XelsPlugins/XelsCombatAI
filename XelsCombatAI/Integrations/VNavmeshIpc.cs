using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XelsCombatAI.Integrations;

internal sealed class VNavmeshIpc
{
    private static readonly TimeSpan ReachabilityCacheDuration = TimeSpan.FromSeconds(2);
    private const float ReachabilityStartTolerance = 5f;
    private const float ReachabilityDestinationTolerance = 1.5f;
    private const int MaxReachabilityChecks = 64;

    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?> pathfind;
    private readonly object reachabilityLock = new();
    private readonly List<ReachabilityCheck> reachabilityChecks = [];

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface)
    {
        this.isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?>("vnavmesh.Nav.Pathfind");
    }

    public bool IsReady()
    {
        try
        {
            return this.isReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to)
    {
        try
        {
            return this.pathfind.InvokeFunc(from, to, false);
        }
        catch
        {
            return null;
        }
    }

    public bool TryValidateReachable(Vector3 from, Vector3 to, out string reason, bool failWhilePending = true)
    {
        if (!this.IsReady())
        {
            reason = "vnavmesh unavailable";
            return true;
        }

        var now = DateTime.UtcNow;
        lock (this.reachabilityLock)
        {
            this.PruneReachabilityChecks(now);
            var check = this.reachabilityChecks.FirstOrDefault(entry =>
                Distance2D(entry.From, from) <= ReachabilityStartTolerance &&
                Distance2D(entry.To, to) <= ReachabilityDestinationTolerance);
            if (check == null)
            {
                var task = this.Pathfind(from, to);
                if (task == null)
                {
                    reason = "vnavmesh pathfind unavailable";
                    return true;
                }

                check = new ReachabilityCheck(from, to, now, task);
                this.reachabilityChecks.Add(check);
                if (this.reachabilityChecks.Count > MaxReachabilityChecks)
                {
                    this.reachabilityChecks.RemoveAt(0);
                }
            }

            if (!check.Task.IsCompleted)
            {
                reason = "vnavmesh reachability pending";
                return !failWhilePending;
            }

            if (!check.Task.IsCompletedSuccessfully || check.Task.Result.Count == 0)
            {
                reason = "vnavmesh destination unreachable";
                return false;
            }

            reason = "vnavmesh destination reachable";
            return true;
        }
    }

    private void PruneReachabilityChecks(DateTime now)
    {
        this.reachabilityChecks.RemoveAll(entry => entry.Task.IsCompleted && now - entry.CreatedAt > ReachabilityCacheDuration);
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private sealed record ReachabilityCheck(Vector3 From, Vector3 To, DateTime CreatedAt, Task<List<Vector3>> Task);
}
