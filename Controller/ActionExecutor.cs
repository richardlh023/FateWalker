using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace FateWalker.Controller;

/// <summary>
/// Thin wrapper around FFXIVClientStructs ActionManager for the handful of
/// game actions we need to invoke (mount, dismount, sprint).
/// </summary>
public sealed unsafe class ActionExecutor
{
    private readonly IPluginLog _log;

    public ActionExecutor(IPluginLog log) { _log = log; }

    // General Action IDs (from FFXIV's GeneralAction Lumina sheet)
    public const uint GA_MountRoulette = 9;
    public const uint GA_Sprint = 4;
    public const uint GA_Return = 8;       // also fires "OK" on the death "Return to ..." dialog
    public const uint GA_Dismount = 23;

    // Role-action IDs (Action sheet). Second Wind is the universal phys-class
    // self-heal we panic-check against. Bloodbath kept here for later use.
    public const uint Act_SecondWind = 7541;
    public const uint Act_Bloodbath  = 7542;

    /// <summary>True if the action is currently usable (GetActionStatus == 0).</summary>
    public bool IsActionReady(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(ActionType.Action, actionId) == 0;
    }

    /// <summary>Fire an Action by ID. Returns false if cooldown or unavailable.</summary>
    public bool UseAction(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (am->GetActionStatus(ActionType.Action, actionId) != 0) return false;
        return am->UseAction(ActionType.Action, actionId);
    }

    public bool UseMountRoulette()
    {
        var am = ActionManager.Instance();
        if (am == null) { _log.Warning("ActionManager null"); return false; }
        var status = am->GetActionStatus(ActionType.GeneralAction, GA_MountRoulette);
        if (status != 0)
        {
            _log.Debug($"Mount Roulette not usable, status={status:X}");
            return false;
        }
        return am->UseAction(ActionType.GeneralAction, GA_MountRoulette);
    }

    /// <summary>Fire Sprint (General Action 4). Returns false if on cooldown.</summary>
    public bool UseSprint()
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (am->GetActionStatus(ActionType.GeneralAction, GA_Sprint) != 0) return false;
        return am->UseAction(ActionType.GeneralAction, GA_Sprint);
    }

    public bool Dismount()
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->UseAction(ActionType.GeneralAction, GA_Dismount);
    }

    /// <summary>
    /// Fire General Action "Return". When alive, queues a teleport to home aetheryte.
    /// When dead with the "Return to X?" dialog showing (OK/Wait buttons), it
    /// triggers the OK button — same as the player pressing their Return keybind.
    /// </summary>
    public bool Return()
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->UseAction(ActionType.GeneralAction, GA_Return);
    }

    public bool InteractWith(IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null) { _log.Warning("TargetSystem null"); return false; }
        var go = (CSGameObject*)(void*)obj.Address;
        if (go == null) return false;
        return ts->InteractWithObject(go) != 0;
    }
}
