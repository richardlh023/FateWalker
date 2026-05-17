using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

/// <summary>
/// RotationSolverReborn IPC. Mirrors the StateCommandType enum locally so we
/// don't take a hard reference on RSR's dll. Byte values must match RSR's
/// definition at RotationSolver.Basic\Data\RSCommandType.cs.
/// </summary>
public sealed class RsrIpc
{
    public enum StateCommandType : byte
    {
        Off = 0,
        Auto = 1,
        TargetOnly = 2,
        Manual = 3,
        AutoDuty = 4,
    }

    /// <summary>
    /// Mirrors <c>RotationSolver.Basic.Data.TargetingType</c>. Integer-backed
    /// (RSR's enum has no explicit base type → default int). Used by
    /// <c>AutodutyChangeOperatingMode</c>.
    /// </summary>
    public enum TargetingType
    {
        Big = 0,
        Small = 1,
        HighHP = 2,
        LowHP = 3,
        HighHPPercent = 4,
        LowHPPercent = 5,
        HighMaxHP = 6,
        LowMaxHP = 7,
        Nearest = 8,
        Farthest = 9,
        PvPHealers = 10,
        PvPTanks = 11,
        PvPDPS = 12,
    }

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<StateCommandType, object> _changeMode;
    private readonly ICallGateSubscriber<StateCommandType, TargetingType, object> _autodutyChangeMode;
    private readonly ICallGateSubscriber<string, object> _test;
    private readonly ICallGateSubscriber<object> _enableTargetFreely;
    private readonly ICallGateSubscriber<object> _disableTargetFreely;

    public RsrIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _changeMode          = pi.GetIpcSubscriber<StateCommandType, object>("RotationSolverReborn.ChangeOperatingMode");
        _autodutyChangeMode  = pi.GetIpcSubscriber<StateCommandType, TargetingType, object>("RotationSolverReborn.AutodutyChangeOperatingMode");
        _test                = pi.GetIpcSubscriber<string, object>("RotationSolverReborn.Test");
        _enableTargetFreely  = pi.GetIpcSubscriber<object>("RotationSolverReborn.EnableTargetFreelyOverride");
        _disableTargetFreely = pi.GetIpcSubscriber<object>("RotationSolverReborn.DisableTargetFreelyOverride");
    }

    public bool IsAvailable
    {
        get
        {
            try { _test.InvokeAction("FateWalker availability probe"); return true; }
            catch (IpcError) { return false; }
        }
    }

    /// <summary>
    /// Manual mode = RSR casts on the player's current target (which BossMod's
    /// AutoTarget sets). Plus TargetFreely override removes RSR's own filters.
    /// </summary>
    public void Activate()
    {
        try
        {
            _enableTargetFreely.InvokeAction();
            _changeMode.InvokeAction(StateCommandType.Manual);
        }
        catch (IpcError e) { _log.Warning(e, "RSR Activate failed"); }
    }

    /// <summary>
    /// Auto mode with explicit target preference (e.g. Farthest → bot pulls
    /// the farthest hostile and AoEs closer mobs en route, classic
    /// wall-to-wall pattern). Uses RSR's <c>AutodutyChangeOperatingMode</c>
    /// IPC which sets state + targeting type atomically.
    /// </summary>
    public void ActivateAuto(TargetingType targetingType)
    {
        try
        {
            _enableTargetFreely.InvokeAction();
            _autodutyChangeMode.InvokeAction(StateCommandType.Auto, targetingType);
        }
        catch (IpcError e) { _log.Warning(e, $"RSR ActivateAuto({targetingType}) failed"); }
    }

    public void Deactivate()
    {
        try
        {
            _changeMode.InvokeAction(StateCommandType.Off);
            _disableTargetFreely.InvokeAction();
        }
        catch (IpcError e) { _log.Warning(e, "RSR Deactivate failed"); }
    }
}
