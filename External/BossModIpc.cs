using System;
using System.Globalization;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

/// <summary>
/// Drives BossmodReborn presets + AI toggle. Ships an embedded FateBot preset
/// that enables MiscAI.FateUtils (hand-in, collect, sync, chocobo) plus an
/// AutoTarget priority module. The user's job autorotation module is appended
/// at runtime in <see cref="EnableForJob"/>.
/// </summary>
public sealed class BossModIpc
{
    private const string PluginName = "BossMod";
    public const string PresetName = "FateWalker - FATE";

    private readonly IPluginLog _log;
    private readonly ICommandManager _commands;

    private readonly ICallGateSubscriber<string, string?> _getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> _createPreset;
    private readonly ICallGateSubscriber<string, bool> _setPreset;
    private readonly ICallGateSubscriber<bool> _clearPreset;
    private readonly ICallGateSubscriber<string, string, string, string, bool> _addTransientStrategy;

    public BossModIpc(IDalamudPluginInterface pi, ICommandManager commands, IPluginLog log)
    {
        _log = log;
        _commands = commands;
        _getPreset             = pi.GetIpcSubscriber<string, string?>($"{PluginName}.Presets.Get");
        _createPreset          = pi.GetIpcSubscriber<string, bool, bool>($"{PluginName}.Presets.Create");
        _setPreset             = pi.GetIpcSubscriber<string, bool>($"{PluginName}.Presets.SetActive");
        _clearPreset           = pi.GetIpcSubscriber<bool>($"{PluginName}.Presets.ClearActive");
        // (presetName, moduleTypeName, trackName, value) — see AutoDuty/IPCSubscriber.cs.
        // Used to override `StayCloseToTarget.Range` per job role so melee jobs
        // chase mobs instead of throwing ranged abilities at out-of-range targets.
        _addTransientStrategy  = pi.GetIpcSubscriber<string, string, string, string, bool>($"{PluginName}.Presets.AddTransientStrategy");
    }

    public bool IsAvailable
    {
        get { try { _ = _getPreset.HasFunction; return _getPreset.HasFunction; } catch (IpcError) { return false; } }
    }

    public void Activate(string presetJson)
    {
        try
        {
            // Always (re-)create the preset so changes (e.g. different job module
            // for the current class job) take effect. The second arg `true`
            // overwrites the existing preset of the same name.
            _createPreset.InvokeFunc(presetJson, true);
            _commands.ProcessCommand("/vbm cfg Autorotation ClearPresetOnCombatEnd false");
            _commands.ProcessCommand("/vbmai on");
            _setPreset.InvokeFunc(PresetName);
        }
        catch (IpcError e) { _log.Warning(e, "BossMod Activate failed"); }
    }

    public void Deactivate()
    {
        try
        {
            _commands.ProcessCommand("/vbmai off");
            _clearPreset.InvokeFunc();
        }
        catch (IpcError e) { _log.Warning(e, "BossMod Deactivate failed"); }
    }

    /// <summary>
    /// Set the desired distance to target for the AI. Tank/Melee jobs need ~2.6y
    /// so they walk into melee range; ranged/casters/healers want ~25y so they
    /// stand and shoot. Values are rounded to one decimal and pushed via
    /// <c>Presets.AddTransientStrategy</c> against our preset's StayCloseToTarget
    /// track — same pattern AutoDuty uses.
    /// </summary>
    public void SetTargetRange(float rangeYalms)
    {
        try
        {
            var value = MathF.Round(rangeYalms, 1).ToString("0.0", CultureInfo.InvariantCulture);
            var ok = _addTransientStrategy.InvokeFunc(PresetName,
                "BossMod.Autorotation.MiscAI.StayCloseToTarget", "Range", value);
            if (!ok) _log.Warning($"BossMod SetTargetRange({value}) returned false");
            else _log.Information($"BossMod target range set to {value}y");
        }
        catch (IpcError e) { _log.Warning(e, "BossMod SetTargetRange failed"); }
    }

    /// <summary>
    /// Override AutoTarget's Retarget option at runtime. Use "Never" when an
    /// external rotation plugin (e.g. RSR in Auto+Farthest mode) is in charge
    /// of target selection — otherwise BossMod's closest-target picker fights
    /// the external plugin and the target oscillates each tick.
    /// Valid values: "NoTarget", "Hostiles", "Always", "Never".
    /// </summary>
    public void SetAutoTargetRetarget(string retargetOption)
    {
        try
        {
            var ok = _addTransientStrategy.InvokeFunc(PresetName,
                "BossMod.Autorotation.MiscAI.AutoTarget", "Retarget", retargetOption);
            if (!ok) _log.Warning($"BossMod SetAutoTargetRetarget({retargetOption}) returned false");
        }
        catch (IpcError e) { _log.Warning(e, "BossMod SetAutoTargetRetarget failed"); }
    }

    /// <summary>
    /// Generic transient-strategy setter — exposes the underlying IPC for
    /// modules/tracks not covered by a dedicated helper. Caller supplies the
    /// full BossMod module type name and track name (case-sensitive).
    /// </summary>
    public bool AddTransientStrategy(string presetName, string moduleTypeName, string trackName, string value)
    {
        try
        {
            return _addTransientStrategy.InvokeFunc(presetName, moduleTypeName, trackName, value);
        }
        catch (IpcError e)
        {
            _log.Warning(e, $"BossMod AddTransientStrategy({moduleTypeName}/{trackName}={value}) failed");
            return false;
        }
    }
}
