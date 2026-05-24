using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace FateWalker.External;

/// <summary>
/// Wrapper around the YesAlready plugin's IsPluginEnabled / SetPluginEnabled
/// IPC. We disable YesAlready while the bot is running so its ambient
/// auto-click of confirm dialogs doesn't race with our own dialog handling
/// (mis-clicked trades, double Returns on death, etc.) — pattern from
/// Questionable/AutoDuty.
/// </summary>
public sealed class YesAlreadyIpc
{
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<bool> _isEnabled;
    private readonly ICallGateSubscriber<bool, object> _setEnabled;

    public YesAlreadyIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _isEnabled  = pi.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled");
        _setEnabled = pi.GetIpcSubscriber<bool, object>("YesAlready.SetPluginEnabled");
    }

    public bool IsInstalled
    {
        get { try { return _isEnabled.HasFunction; } catch (IpcError) { return false; } }
    }

    public bool? IsEnabled
    {
        get { try { return _isEnabled.InvokeFunc(); } catch (IpcError) { return null; } }
    }

    public void SetEnabled(bool enabled)
    {
        try { _setEnabled.InvokeAction(enabled); }
        catch (IpcError e) { _log.Warning(e, $"YesAlready.SetPluginEnabled({enabled}) failed"); }
    }
}
