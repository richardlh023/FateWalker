using System;
using System.Diagnostics.CodeAnalysis;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

/// <summary>
/// Wrapper around TextAdvance's external-control IPC.
///
/// TextAdvance has two independent enable states:
///   1. Plugin loaded in Dalamud (the /xlplugins toggle)
///   2. Plugin "active" internally (P.Enabled) — controls whether it processes events
///
/// A user can have (1) on while (2) is off, in which case TextAdvance does nothing.
/// EnableExternalControl forces TextAdvance to act regardless of (2), with our
/// chosen per-feature settings. We acquire it on Start(), release on Stop()/Dispose().
/// </summary>
public sealed class TextAdvanceIpc : IDisposable
{
    private readonly IPluginLog _log;
    private readonly string _requester;

    private readonly ICallGateSubscriber<string, ExternalTerritoryConfig, bool> _enableExternalControl;
    private readonly ICallGateSubscriber<string, bool> _disableExternalControl;
    private readonly ICallGateSubscriber<bool> _isInExternalControl;
    private readonly ICallGateSubscriber<bool> _isEnabled;

    private bool _acquired;

    public TextAdvanceIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _requester = pi.InternalName;

        _enableExternalControl = pi.GetIpcSubscriber<string, ExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl");
        _disableExternalControl = pi.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");
        _isInExternalControl = pi.GetIpcSubscriber<bool>("TextAdvance.IsInExternalControl");
        _isEnabled = pi.GetIpcSubscriber<bool>("TextAdvance.IsEnabled");
    }

    /// <summary>True if TextAdvance is installed (IPC subscribers resolved).</summary>
    public bool IsAvailable
    {
        get { try { return _isInExternalControl.HasFunction; } catch (IpcError) { return false; } }
    }

    /// <summary>
    /// True if TextAdvance is currently active (user has enabled it OR we hold
    /// external control). Returns false if not installed.
    /// </summary>
    public bool IsActive
    {
        get { try { return _isEnabled.InvokeFunc(); } catch { return false; } }
    }

    /// <summary>True if we currently hold external control.</summary>
    public bool HoldsExternalControl => _acquired;

    /// <summary>
    /// Force TextAdvance to enable Talk skip / cutscene confirm regardless of
    /// user config. Safe to call multiple times — TextAdvance accepts re-acquire
    /// from the same requester and applies the new config.
    /// </summary>
    public bool Acquire()
    {
        if (!IsAvailable) return false;
        try
        {
            var ok = _enableExternalControl.InvokeFunc(_requester, BuildConfig());
            if (ok) { _acquired = true; _log.Information($"TextAdvance external control acquired by {_requester}"); }
            else _log.Warning("TextAdvance.EnableExternalControl returned false — another plugin holds it.");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "TextAdvance.EnableExternalControl threw");
            return false;
        }
    }

    /// <summary>Release external control. No-op if we don't hold it.</summary>
    public void Release()
    {
        if (!_acquired) return;
        if (!IsAvailable) { _acquired = false; return; }
        try
        {
            var ok = _disableExternalControl.InvokeFunc(_requester);
            if (ok) _log.Information("TextAdvance external control released.");
            else _log.Warning("TextAdvance.DisableExternalControl returned false (foreign requester?).");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "TextAdvance.DisableExternalControl threw");
        }
        _acquired = false;
    }

    public void Dispose() => Release();

    private static ExternalTerritoryConfig BuildConfig() => new()
    {
        EnableTalkSkip = true,            // FATE-start NPC dialog (the main need)
        EnableCutsceneEsc = true,         // some FATEs intro into a brief cutscene
        EnableCutsceneSkipConfirm = true,
        EnableRequestHandin = true,       // hand-in FATEs (collect quest items)
        EnableRewardPick = true,
        // null = inherit user setting; we don't drive these.
        EnableQuestAccept = null,
        EnableQuestComplete = null,
        EnableRequestFill = null,
        EnableAutoInteract = null,
    };

    /// <summary>
    /// Mirror of TextAdvance's ExternalTerritoryConfig. Field names and types
    /// must match the producer side (Dalamud IPC serializes by reflection).
    /// </summary>
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public sealed class ExternalTerritoryConfig
    {
#pragma warning disable CS0414
        public bool? EnableQuestAccept;
        public bool? EnableQuestComplete;
        public bool? EnableRewardPick;
        public bool? EnableRequestHandin;
        public bool? EnableCutsceneEsc;
        public bool? EnableCutsceneSkipConfirm;
        public bool? EnableTalkSkip;
        public bool? EnableRequestFill;
        public bool? EnableAutoInteract;
#pragma warning restore CS0414
    }
}
