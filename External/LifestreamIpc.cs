using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

/// <summary>
/// Lifestream IPC wrapper. Two distinct teleport modes:
///   • <see cref="Teleport"/>    — cast Aetheryte spell, cross-zone (we use this)
///   • <see cref="AethernetHop"/> — in-zone aethernet shard, no cast cost
/// Bare <see cref="Teleport"/> does NOT set IsBusy — caller must poll
/// <see cref="GetRealTerritoryType"/> until the destination matches.
/// </summary>
public sealed class LifestreamIpc
{
    private readonly IPluginLog _log;

    private readonly ICallGateSubscriber<uint, byte, bool> _teleport;
    private readonly ICallGateSubscriber<uint, bool>       _aethernetById;
    private readonly ICallGateSubscriber<uint>             _getRealTerritory;
    private readonly ICallGateSubscriber<bool>             _isBusy;
    private readonly ICallGateSubscriber<object>           _abort;

    public LifestreamIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _teleport         = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        _aethernetById    = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
        _getRealTerritory = pi.GetIpcSubscriber<uint>("Lifestream.GetRealTerritoryType");
        _isBusy           = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _abort            = pi.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool IsAvailable
    {
        get { try { _ = _isBusy.HasFunction; return _isBusy.HasFunction; } catch (IpcError) { return false; } }
    }

    public bool IsBusy
    {
        get { try { return _isBusy.InvokeFunc(); } catch (IpcError) { return false; } }
    }

    /// <summary>Real territory type — instance-resilient. 0 if mid-load.</summary>
    public uint CurrentTerritory
    {
        get { try { return _getRealTerritory.InvokeFunc(); } catch (IpcError) { return 0; } }
    }

    /// <summary>
    /// Cross-zone aetheryte teleport (casts the Aetheryte spell). Returns false
    /// if not attuned, animation-locked, or cast unavailable. Even on `true`,
    /// poll <see cref="CurrentTerritory"/> for arrival confirmation.
    /// </summary>
    public bool Teleport(uint aetheryteId, byte subIndex = 0)
    {
        try { return _teleport.InvokeFunc(aetheryteId, subIndex); }
        catch (IpcError e) { _log.Warning(e, $"Lifestream Teleport({aetheryteId}) failed"); return false; }
    }

    /// <summary>In-zone aethernet shard hop. Requires being in range of an aetheryte/shard.</summary>
    public bool AethernetHop(uint aetheryteId)
    {
        try { return _aethernetById.InvokeFunc(aetheryteId); }
        catch (IpcError e) { _log.Warning(e, "Lifestream AethernetHop failed"); return false; }
    }

    /// <summary>Cancel Lifestream's task queue + FollowPath.</summary>
    public void Abort()
    {
        try { _abort.InvokeAction(); }
        catch (IpcError e) { _log.Warning(e, "Lifestream Abort failed"); }
    }
}
