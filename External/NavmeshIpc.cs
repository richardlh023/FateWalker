using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

public sealed class NavmeshIpc
{
    private readonly IPluginLog _log;

    private readonly ICallGateSubscriber<bool> _isNavReady;
    private readonly ICallGateSubscriber<bool> _pathIsRunning;
    private readonly ICallGateSubscriber<bool> _simpleMoveInProgress;
    private readonly ICallGateSubscriber<object> _pathStop;
    private readonly ICallGateSubscriber<Vector3, bool, bool> _simpleMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _simpleMoveCloseTo;
    private readonly ICallGateSubscriber<Vector3, bool, CancellationToken, Task<List<Vector3>>> _pathfindCancelable;

    public NavmeshIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _isNavReady          = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _pathIsRunning       = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _simpleMoveInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _pathStop            = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _simpleMoveTo        = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _simpleMoveCloseTo   = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathfindCancelable  = pi.GetIpcSubscriber<Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
    }

    public bool IsAvailable
    {
        get { try { _isNavReady.InvokeFunc(); return true; } catch (IpcError) { return false; } }
    }

    public bool IsReady
    {
        get { try { return _isNavReady.InvokeFunc(); } catch (IpcError) { return false; } }
    }

    public bool IsPathRunning
    {
        get { try { return _pathIsRunning.InvokeFunc(); } catch (IpcError) { return false; } }
    }

    public bool IsPathfindInProgress
    {
        get { try { return _simpleMoveInProgress.InvokeFunc(); } catch (IpcError) { return false; } }
    }

    public void Stop()
    {
        try { _pathStop.InvokeAction(); }
        catch (IpcError e) { _log.Warning(e, "vnavmesh Stop failed"); }
    }

    public bool PathfindAndMoveTo(Vector3 destination, bool fly)
    {
        if (!IsReady) return false;
        try { return _simpleMoveTo.InvokeFunc(destination, fly); }
        catch (IpcError e) { _log.Warning(e, "vnavmesh PathfindAndMoveTo failed"); return false; }
    }

    public bool PathfindAndMoveCloseTo(Vector3 destination, bool fly, float range)
    {
        if (!IsReady) return false;
        try { return _simpleMoveCloseTo.InvokeFunc(destination, fly, range); }
        catch (IpcError e) { _log.Warning(e, "vnavmesh PathfindAndMoveCloseTo failed"); return false; }
    }
}
