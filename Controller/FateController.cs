using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FateWalker.Data;
using FateWalker.External;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using StatusFlags = Dalamud.Game.ClientState.Objects.Enums.StatusFlags;

namespace FateWalker.Controller;

public sealed class FateController : IDisposable
{
    private const int RecoverySeconds = 4;

    private readonly Configuration _config;
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;
    private readonly IFateTable _fateTable;
    private readonly NavmeshIpc _navmesh;
    private readonly BossModIpc _bossmod;
    private readonly RsrIpc _rsr;
    private readonly LifestreamIpc _lifestream;
    private readonly TextAdvanceIpc _textAdvance;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IChatGui _chatGui;
    private readonly SessionFileLogger _fileLogger;
    private readonly IPartyList _partyList;
    private readonly Controller.Party.PartyCoordinator _party;
    /// <summary>Public read-only access for the UI status line.</summary>
    public Controller.Party.PartyCoordinator Party => _party;
    private Action? _saveConfig;
    private readonly ActionExecutor _action;
    private readonly FateSelector _selector;
    private readonly IDataManager _dataManager;
    // Cache of (fateId → has-collect-item). Populated on first lookup.
    // True = this FATE has an EventItem in Lumina (i.e. it's a Collect FATE
    // where the player picks up items off the ground and turns them in).
    private readonly Dictionary<uint, bool> _collectFateCache = new();
    // Tracks the BossMod AutoTarget Retarget mode we last pushed so we
    // don't spam the IPC every tick. "Never" while picking up collect-FATE
    // items, "NoTarget" otherwise.
    private string _currentRetargetMode = "";
    private readonly IKeyState _keyState;
    private readonly IDutyState _dutyState;
    private readonly YesAlreadyIpc _yesAlready;
    private bool _rsrActivated;
    private bool _yesnoListenerRegistered;
    private bool? _yesAlreadyPriorState;   // null = we never disabled it; true/false = restore this on Stop
    private DateTime _lastIdleEmoteAt = DateTime.MinValue;
    private DateTime _nextIdleEmoteAt = DateTime.MinValue;
    private bool _pausedAfkSent;

    private DateTime _stateEnteredAt = DateTime.UtcNow;
    private DateTime _lastMountAttemptAt = DateTime.MinValue;
    private DateTime _lastDismountAttemptAt = DateTime.MinValue;
    private DateTime _lastInteractAt = DateTime.MinValue;
    private DateTime _lastPathfindAt = DateTime.MinValue;

    // Stuck detection (AutoDuty pattern, see StuckHelper.cs). Threshold = 5s
    // without significant movement while a path is running.
    private Vector3 _stuckLastPos = Vector3.Zero;
    private DateTime _stuckLastMoveAt = DateTime.UtcNow;
    private const float StuckMoveThresholdSq = 1f * 1f;   // need to move >1y
    private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(5);
    private ushort _targetFateId;
    private string _targetFateName = "";
    private Vector3 _targetFatePos;
    private float _targetFateRadius;
    private uint _targetMotivationNpcId;   // 0 if FATE is already Running
    private bool _bossmodActivated;

    // Cross-zone rotation state. Drought = time spent in Selecting with no
    // eligible FATE; when it exceeds Config.MinDroughtSeconds AND the working
    // set has >1 zone, the bot teleports to the next zone in rotation.
    private DateTime? _droughtStartedAt;
    private uint _pendingTeleportTerritory;
    private uint _pendingTeleportAetheryte;
    private uint _lastDepartedFromTerritory;   // avoid immediately bouncing back
    private bool _teleportFired;               // one-shot guard inside Teleporting

    // Death-recovery state. When player goes Unconscious we record the zone
    // they died in so we can teleport back after Return-to-home revival.
    private uint _diedInTerritory;
    private bool _diedReturnTriggered;      // we clicked Return, not raised in-place

    // Durability check throttle — read inventory at most every 30s in Recovering.
    private DateTime _lastDurabilityCheckAt = DateTime.MinValue;
    private int _lastDurabilityMin = 100;
    public int LastDurabilityMin => _lastDurabilityMin;

    // Repairing state — track which aetheryte we're routing to + which NPC we
    // latched onto, so subsequent ticks don't keep re-resolving.
    private uint _repairAetheryteId;
    private bool _repairTeleportFired;
    private DateTime _lastRepairInteractAt = DateTime.MinValue;
    // Counts how many times Lifestream.Teleport returned false in Repairing.
    // Combat lockout / S-rank aggro / interrupt all push this up; after the
    // threshold we kick a flee-from-combat sub-routine instead of looping.
    private int _repairTeleportRejections;
    private DateTime _lastRepairFleeAt = DateTime.MinValue;

    // PreparingPause — staged before entering Paused so the bot escapes any
    // mob aggro and teleports to a safe aetheryte before going AFK. Without
    // this the bot would die mid-pause and re-trigger Dying every tick.
    private int _pendingPauseMinutes;
    private string _pendingPauseReason = "";
    private bool _pendingPauseResetTimer;
    private DateTime _lastPreparePauseFleeAt = DateTime.MinValue;
    private string _lastFleeThreatName = "";
    private DateTime _lastFleeLogAt = DateTime.MinValue;
    private DateTime _lastPreparePauseTpAt = DateTime.MinValue;
    private bool _preparePauseTeleportFired;

    // Generic logic-loop watchdog. Tracks the last ~2 minutes of LogAction
    // calls as normalized "fingerprints" (numbers/IDs stripped). When the same
    // fingerprint fires too many times we treat it as a stuck loop and apply
    // a recovery, escalating from soft reset → safety pause on repeat hits.
    private readonly LinkedList<(DateTime At, string Fingerprint)> _logFingerprints = new();
    private DateTime _lastLoopCheckAt = DateTime.MinValue;
    private int _loopRecoveryCount;
    private int _sessionLoopRecoveries;
    private static readonly Regex LoopFingerprintStripper = new(
        @"-?\d+(?:\.\d+)?(?:y|s|ms|%|m|h)?|→\s*(True|False)|aetheryte=\d+|id=\d+|#\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Trading state — current vendor target + the item we're buying.
    private VendorNpc? _tradingVendor;
    private uint _tradingItemId;
    private bool _tradingTeleportFired;
    // True when the current Trading run was triggered by a Survey button while
    // the bot was Stopped — survey is one-shot data collection, so on
    // completion we return to Stopped instead of starting the farming loop.
    private bool _surveyOnlySession;
    // City-hub vendors live far from the main aetheryte plaza (Beryl at Nexus
    // Arcade, Kajeel Ja at Bayside Bevy, etc.). After the main teleport we
    // aethernet-hop to a near-vendor shard. This flag tracks "hop already done".
    private bool _tradingAethernetFired;
    // True when survey mode acquired TextAdvance itself (because Start() wasn't
    // called). We release on FinishTrading so we don't leak external control.
    private bool _surveyAcquiredTextAdvance;
    private DateTime _lastTradingActionAt = DateTime.MinValue;
    // When true, TickTrading reads + saves shop inventory instead of buying.
    private bool _tradingSurveyMode;

    // FATE-mob targeting override throttle (500ms).
    private DateTime _lastFateTargetAt = DateTime.MinValue;

    // Committed pull target — once we pick a mob to pull (no aggro yet),
    // stick with it until it aggros, dies, or vanishes. Without this, the
    // farthest-mob ordering keeps shuffling as we move toward the previous
    // pick, causing the bot to zig-zag between distant mobs and never engage.
    private ulong _pullCommitId;
    // Tracks when the current pull-commit was first issued. Used as a grace
    // window — we don't drop the commit just because the mob briefly
    // vanishes from the object table (streaming hiccup mid-flight, server
    // tick lag, etc.). After the grace passes we re-evaluate.
    private DateTime _pullCommitSetAt = DateTime.MinValue;
    // KILL-phase latch. Once the bot fills the configured pull size we
    // stick on KILL until aggro hits 0 (whole batch cleared). Without
    // this, the moment a mob dies aggro drops below MaxAggroCount and the
    // bot dashes off to pull a new one — leaving 3 still-aggro'd mobs
    // behind to leash + regen.
    private bool _killPhaseLatch;
    // Sustained-aggro-loss timer for the latch. aggro=0 in a single tick
    // isn't safe to act on — mobs briefly retarget the chocobo and the
    // aggro list flickers empty for a tick or two. We only release the
    // latch after 3 s of CONTINUOUS aggro=0, by which point the batch
    // really is dead.
    private DateTime _killPhaseAggroLossAt = DateTime.MinValue;

    // Throttle for the "no FATE mob in range" diagnostic log.
    private DateTime _lastNoFateMobLogAt = DateTime.MinValue;

    // Throttle for the "no eligible FATE; waiting" log in Selecting.
    private DateTime _lastDroughtLogAt = DateTime.MinValue;

    // Generic "no progress" detector — catches any state where the player
    // hasn't moved much in a while (dismount refused, addon stuck, etc).
    // Separate from CheckAndRecoverFromStuck which only fires while a vnav
    // path is running.
    private Vector3 _genericLastPos = Vector3.Zero;
    private DateTime _genericLastMoveAt = DateTime.UtcNow;

    // Dismount stuck recovery — count consecutive failed dismount attempts
    // (Mount flag never clears). When over threshold, re-pathfind to ground-
    // level FATE position to force a proper landing spot.
    private int _dismountFailCount;
    private DateTime _lastDismountRescueAt = DateTime.MinValue;

    // Tracks when we last saw in-combat in Engaging. If long enough without
    // combat while we still have a FATE-mob target, we're probably stranded on
    // terrain BossMod can't pathfind around (rock outcrop above the FATE).
    // Manual vnavmesh ground-walk to the target unsticks it.
    private DateTime _lastEngagingCombatAt = DateTime.MinValue;
    private DateTime _lastEngagingKickAt = DateTime.MinValue;
    // Force-pull: throttle for the manual basic-attack we fire when target is
    // set but combat hasn't started (defence FATE — mob is on the NPC).
    private DateTime _lastForcePullAt = DateTime.MinValue;
    // Random humanize jump during long walks (Traveling). Rolls a fresh
    // 25–75s interval after each fire so cadence isn't a giveaway.
    private DateTime _nextJumpAt = DateTime.MinValue;
    // Marker for the last time we issued a stuck-recovery navmesh call. If
    // a mob attacks mid-recovery we use this to know we should stop our
    // vnav path so BossMod's NormalMovement can take over for combat.
    private DateTime _stuckRecoveryIssuedAt = DateTime.MinValue;
    // Movement gate for humanize jump — we only tap jump when actually
    // walking (covered ≥ 5 y in the last 4 s window). Standing-still jumps
    // look more bot-like than the perfectly-still autorun we're trying to
    // mask, per tester feedback.
    private DateTime _jumpMoveSampleAt = DateTime.MinValue;
    private Vector3 _jumpMoveSamplePos = Vector3.Zero;
    private float _jumpMoveSampleDist;
    // Random humanize altitude nudge while flying (Traveling). Smaller
    // window than jump — pilots fidget more than walkers.
    private DateTime _nextAltitudeAt = DateTime.MinValue;
    private DateTime _altitudeHoldUntil = DateTime.MinValue;
    private byte _altitudeHoldKey;
    // True once we've already redirected navmesh to a specific FATE actor
    // for the current target, so we don't oscillate landing waypoints.
    private bool _landingRefined;
    // The actual entity position we're heading to (mob / MotivationNpc),
    // captured when RefineLandingTarget locks on. Used as the arrival check
    // in TickTraveling so we dismount RIGHT next to the entity instead of
    // at the rolled FATE-edge offset.
    private Vector3? _refinedLandingPos;
    // Tracks current "lazy dodge" mode in Engaging so we don't spam IPC
    // every tick when HP stays high.
    private string _currentMoveDelay = "None"; // matches BossMod track default

    // Deferred SelectYesno click — humanize delay before confirming.
    // Stores the time at which the auto-confirm should fire.
    private DateTime? _pendingSelectYesnoAt;
    // Approach delay before InteractWith — set when we first arrive in melee
    // range of the FATE NPC.
    private DateTime? _interactReadyAt;

    // Panic-escape state. Set when we bail from a FATE due to low HP + no
    // Second Wind; gates the Recovering→Selecting transition until HP regenerates.
    private bool _panicked;

    // Humanize: per-state randomised "think" delay rolled at Transition().
    // Selecting → think before committing to a pick.
    // Recovering → extra rest before re-selecting.
    // Drought trigger → hesitation before pulling the teleport trigger.
    private readonly Random _rng = new();
    private TimeSpan _humanizeDelay;            // applied to current state
    private DateTime? _droughtHesitateUntil;    // separate timer for teleport hesitate

    // Session cap — wall-clock seconds since Start() was last pressed.
    private DateTime? _sessionStartedAt;

    // Overnight tracking — counters logged on a 5-minute cadence + on Stop.
    private int _sessionFatesCompleted;
    private int _sessionFatesFailed;
    private int _sessionDeaths;
    private int _sessionStuckEvents;
    private int _sessionPanicEscapes;
    private int _sessionRepairTrips;
    private int _sessionStartGemCount;
    // Per-session randomised session cap (hours). Rolled fresh on Start()
    // and on every Paused → resume so a watcher can't predict break cadence.
    private double _sessionCapHoursRolled;
    private DateTime _lastStatsLogAt = DateTime.MinValue;

    // Plugin-availability change tracker (warn when an IPC dependency drops).
    private bool _lastNavAvail, _lastBossModAvail, _lastLifestreamAvail, _lastRsrAvail;

    // Paused state — bot is idle for a timed cool-down then auto-resumes.
    // resetSessionTimer = true for session-cap macro-breaks (fresh cycle on resume),
    // false for chat-stop pauses (still count toward session cap).
    private DateTime _pauseEndsAt;
    private string _pauseReason = "";
    private bool _pauseResetSessionTimer;

    // Randomized FATE landing offset, rolled once per Selecting commit.
    private Vector3 _targetFateLandingOffset;
    private readonly ITargetManager _targetManager;

    private readonly Queue<string> _logRing = new();
    private const int LogRingCapacity = 40;

    public FateBotState State { get; private set; } = FateBotState.Stopped;
    public void SetSaveConfigCallback(Action save) => _saveConfig = save;
    public string LastAction { get; private set; } = "(idle)";
    public IReadOnlyCollection<string> RecentLog => _logRing;
    public ushort TargetFateId => _targetFateId;
    public string TargetFateName => _targetFateName;

    /// <summary>
    /// Optional user-pinned FATE id. While set, the selector is bypassed and
    /// the controller targets this FATE regardless of priority/filter. Cleared
    /// automatically when the FATE ends or vanishes from the table.
    /// </summary>
    public ushort? ManuallyPickedFateId { get; private set; }

    /// <summary>
    /// Session-only "Disable" list. Auto-clears when a disabled FATE leaves the
    /// IFateTable (i.e. ends or despawns) — so a later re-spawn of the same
    /// FateId plays normally. NOT persisted across plugin reloads.
    /// </summary>
    private readonly HashSet<ushort> _sessionDisabledFateIds = new();
    public IReadOnlyCollection<ushort> SessionDisabledFateIds => _sessionDisabledFateIds;
    public bool IsDisabled(ushort fateId) => _sessionDisabledFateIds.Contains(fateId);

    public void ToggleDisable(ushort fateId)
    {
        if (_sessionDisabledFateIds.Remove(fateId))
            LogAction($"un-disabled fate id={fateId}");
        else
        {
            _sessionDisabledFateIds.Add(fateId);
            LogAction($"disabled fate id={fateId} (session, until despawn)");
        }
    }

    /// <summary>
    /// Called by the UI when the user toggles the random-rotation feature or
    /// changes its interval. Re-rolls the next-fire timer so a slider tweak
    /// takes effect immediately (no need to Stop/Start the bot).
    /// </summary>
    public void NotifyRandomRotateConfigChanged()
    {
        if (_sessionStartedAt == null) return;
        RollNextRandomRotate();
    }

    public void ToggleManualPick(ushort fateId)
    {
        if (ManuallyPickedFateId == fateId)
        {
            ManuallyPickedFateId = null;
            LogAction("manual pick cleared — back to auto");
        }
        else
        {
            ManuallyPickedFateId = fateId;
            // Picking implies "I want this one" — undo any prior Disable on it.
            if (_sessionDisabledFateIds.Remove(fateId))
                LogAction($"manual pick → fate id={fateId} (also removed from Disable list)");
            else
                LogAction($"manual pick → fate id={fateId}");
        }
    }

    public FateController(
        Configuration config,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IFateTable fateTable,
        ITargetManager targetManager,
        NavmeshIpc navmesh,
        BossModIpc bossmod,
        RsrIpc rsr,
        LifestreamIpc lifestream,
        TextAdvanceIpc textAdvance,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IChatGui chatGui,
        SessionFileLogger fileLogger,
        ActionExecutor action,
        FateSelector selector,
        IDataManager dataManager,
        IKeyState keyState,
        IDutyState dutyState,
        YesAlreadyIpc yesAlready,
        IPartyList partyList)
    {
        _config = config;
        _log = log;
        _framework = framework;
        _clientState = clientState;
        _condition = condition;
        _objectTable = objectTable;
        _fateTable = fateTable;
        _targetManager = targetManager;
        _navmesh = navmesh;
        _bossmod = bossmod;
        _rsr = rsr;
        _lifestream = lifestream;
        _textAdvance = textAdvance;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _chatGui = chatGui;
        _chatGui.ChatMessage += OnChatMessage;
        _fileLogger = fileLogger;
        _action = action;
        _selector = selector;
        _dataManager = dataManager;
        _keyState = keyState;
        _dutyState = dutyState;
        _yesAlready = yesAlready;
        _partyList = partyList;
        _party = new Controller.Party.PartyCoordinator(_config, _log, LogAction);

        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Fires once when a SelectYesno addon opens. We click "Yes" if the bot is
    /// in the Interacting state — that's the only place a FATE-start NPC will
    /// raise a confirmation prompt ("Keep an eye on the X?"). Outside Interacting
    /// we leave the user's other Yes/No prompts alone.
    /// </summary>
    private unsafe void OnSelectYesnoPostSetup(AddonEvent type, AddonArgs args)
    {
        // Three contexts in which the bot should auto-click Yes/OK on a SelectYesno:
        //   • Interacting — FATE-start NPC confirmation ("Keep an eye on X?")
        //   • Dying — and only AFTER the raise grace expired (_diedReturnTriggered).
        //   • Repairing — gil-cost confirmation when clicking Repair All.
        bool acceptInteracting = State == FateBotState.Interacting;
        bool acceptDying       = State == FateBotState.Dying && _diedReturnTriggered;
        bool acceptRepairing   = State == FateBotState.Repairing;
        bool acceptTrading     = State == FateBotState.Trading;
        if (!acceptInteracting && !acceptDying && !acceptRepairing && !acceptTrading) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        string? prompt = null;
        try
        {
            if (addon->AtkValuesCount > 0)
            {
                var v = addon->AtkValues[0];
                if (v.Type != AtkValueType.Undefined && v.String.HasValue)
                    prompt = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(v.String)).TextValue;
            }
        }
        catch { /* unreadable prompt is fine — we still confirm */ }

        // Dying = urgent (player can't wait while dead) → click immediately.
        if (acceptDying)
        {
            addon->FireCallbackInt(0);
            LogAction($"auto-confirmed SelectYesno [Dying-instant]: \"{prompt ?? "(unread)"}\"");
            return;
        }

        // Interacting / Repairing — defer the click by a random delay so the
        // dialog stays visible (looks like the player is reading). The Tick
        // pump re-finds the addon and fires when the timer expires.
        var delayMs = RollMs(_config.DialogClickDelayMinMs, _config.DialogClickDelayMaxMs);
        _pendingSelectYesnoAt = DateTime.UtcNow.AddMilliseconds(delayMs);
        LogAction($"SelectYesno [{State}] queued: \"{prompt ?? "(unread)"}\" — clicking in {delayMs}ms");
    }

    /// <summary>
    /// Log a compact session summary — total elapsed, FATEs completed, gems
    /// gained. Used for periodic 5-minute heartbeats and for the final STOP
    /// line so an overnight log can be reviewed quickly.
    /// </summary>
    private void LogSessionTotals(string prefix)
    {
        var elapsed = _sessionStartedAt != null
            ? DateTime.UtcNow - _sessionStartedAt.Value
            : TimeSpan.Zero;
        var gems = FateWalker.Data.CurrencyReader.GetBicolorGemstoneCount();
        var dur = _lastDurabilityMin;
        var deltaGems = gems >= 0 ? gems - _sessionStartGemCount : -1;
        var rate = elapsed.TotalHours > 0.01 ? _sessionFatesCompleted / elapsed.TotalHours : 0;
        LogAction(
            $"{prefix} · {elapsed:hh\\:mm\\:ss} · FATEs={_sessionFatesCompleted} ({rate:F1}/hr) · " +
            $"fail={_sessionFatesFailed} · death={_sessionDeaths} · panic={_sessionPanicEscapes} · " +
            $"stuck={_sessionStuckEvents} · repair={_sessionRepairTrips} · loops={_sessionLoopRecoveries} · " +
            $"gems={gems} (Δ{deltaGems:+#;-#;0}) · min-dur={dur}%");
    }

    /// <summary>
    /// Monitor IPC availability. If a dependency drops mid-session (e.g.
    /// vnavmesh crashed) the bot can't recover; log it so the user sees why
    /// things stopped working when they review the log.
    /// </summary>
    private void CheckPluginAvailability()
    {
        var nav = _navmesh.IsAvailable;
        var bm = _bossmod.IsAvailable;
        var ls = _lifestream.IsAvailable;
        var rsr = _rsr.IsAvailable;
        if (nav != _lastNavAvail)        { LogAction($"plugin status: vnavmesh {(nav  ? "available" : "LOST")}");        _lastNavAvail = nav; }
        if (bm  != _lastBossModAvail)    { LogAction($"plugin status: BossMod  {(bm   ? "available" : "LOST")}");        _lastBossModAvail = bm; }
        if (ls  != _lastLifestreamAvail) { LogAction($"plugin status: Lifestream {(ls ? "available" : "LOST")}");        _lastLifestreamAvail = ls; }
        if (rsr != _lastRsrAvail)        { LogAction($"plugin status: RSR     {(rsr  ? "available" : "LOST")}");        _lastRsrAvail = rsr; }
    }

    /// <summary>Roll a random integer in [min,max]. Returns min if humanize disabled.</summary>
    private int RollMs(int minMs, int maxMs)
    {
        if (!_config.EnableHumanize || maxMs <= minMs) return Math.Max(0, minMs);
        return _rng.Next(minMs, maxMs + 1);
    }

    /// <summary>Fire any pending deferred SelectYesno click. Called every Tick.</summary>
    private unsafe void ProcessPendingSelectYesno()
    {
        if (_pendingSelectYesnoAt == null) return;
        if (DateTime.UtcNow < _pendingSelectYesnoAt.Value) return;

        // Timer fired — find the addon (which may still be open) and click.
        var addonPtr = _gameGui.GetAddonByName("SelectYesno");
        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon != null && addon->IsVisible)
        {
            addon->FireCallbackInt(0);
            LogAction("SelectYesno: deferred click fired");
        }
        _pendingSelectYesnoAt = null;
    }

    /// <summary>
    /// Orphan-SelectYesno detector. The PostSetup AddonLifecycle listener can
    /// miss a fire when:
    ///   • the addon opened BEFORE we transitioned into the handling state
    ///   • a state transition cleared the pending-click timer mid-wait
    ///   • the user manually closed and re-opened the dialog
    /// We scan every tick and re-queue if we see a visible SelectYesno that we
    /// should auto-confirm but haven't queued.
    /// </summary>
    private unsafe void EnsureSelectYesnoHandled()
    {
        if (_pendingSelectYesnoAt != null) return; // already queued

        // FATE-start dialogs can pop on proximity to the MotivationNpc — not
        // only via our explicit Interact call. If we're chasing a Preparing
        // FATE in any travel-related state and a Yes/No dialog is up, treat
        // it the same as if we were in Interacting: queue a Yes click so we
        // commit to starting the FATE and unblock mount/movement actions
        // (which are blocked while the dialog is up).
        bool prepFateTraveling = _targetMotivationNpcId != 0
            && (State == FateBotState.Selecting
             || State == FateBotState.Mounting
             || State == FateBotState.Traveling);

        bool shouldHandle =
            State == FateBotState.Interacting ||
            (State == FateBotState.Dying && _diedReturnTriggered) ||
            State == FateBotState.Repairing ||
            State == FateBotState.Trading ||
            prepFateTraveling;
        if (!shouldHandle) return;

        var addonPtr = _gameGui.GetAddonByName("SelectYesno");
        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon == null || !addon->IsVisible) return;

        var delayMs = State == FateBotState.Dying
            ? 0
            : RollMs(_config.DialogClickDelayMinMs, _config.DialogClickDelayMaxMs);
        _pendingSelectYesnoAt = DateTime.UtcNow.AddMilliseconds(delayMs);
        LogAction($"SelectYesno orphan detected in {State} — queueing click in {delayMs}ms");
    }

    public void Start()
    {
        if (State != FateBotState.Stopped) { LogAction("already running"); return; }
        _sessionStartedAt = DateTime.UtcNow;
        _sessionFatesCompleted = 0;
        _sessionFatesFailed = 0;
        _sessionDeaths = 0;
        _sessionStuckEvents = 0;
        _sessionPanicEscapes = 0;
        _sessionRepairTrips = 0;
        _sessionStartGemCount = Math.Max(0, FateWalker.Data.CurrencyReader.GetBicolorGemstoneCount());
        _sessionCapHoursRolled = RollSessionCapHours();
        RollNextRandomRotate();
        _sessionLoopRecoveries = 0;
        _loopRecoveryCount = 0;
        _logFingerprints.Clear();
        // Reset session teleport budget counters; these get persisted so a
        // crash mid-session doesn't lose the running cost (resumed via the
        // Status tab readout next launch).
        _config.SessionTeleportCount = 0;
        _config.SessionTeleportCostGil = 0;
        // Snapshot YesAlready state and disable while running so its ambient
        // auto-clicks don't race with our dialog handling.
        if (_config.DisableYesAlreadyWhileRunning && _yesAlready.IsInstalled)
        {
            _yesAlreadyPriorState = _yesAlready.IsEnabled;
            if (_yesAlreadyPriorState == true)
            {
                _yesAlready.SetEnabled(false);
                LogAction("YesAlready disabled for session (restored on Stop)");
            }
        }
        _currentMoveDelay = "None"; // matches BossMod track default; re-applied on activation
        _currentRetargetMode = "";  // force re-apply on first EnforceFateMobTarget
        _lastStatsLogAt = DateTime.UtcNow;
        _lastNavAvail = _navmesh.IsAvailable;
        _lastBossModAvail = _bossmod.IsAvailable;
        _lastLifestreamAvail = _lifestream.IsAvailable;
        _lastRsrAvail = _rsr.IsAvailable;
        _fileLogger.BeginSession(BuildCharacterTag(), _config.EnableFileLog);
        LogAction(_config.DryRun ? "START (DRY RUN — no actions)" : $"START · gems={_sessionStartGemCount}");
        if (!_config.DryRun)
        {
            // Force TextAdvance to handle FATE-start NPC dialogs regardless of
            // the user's local/territory enable state.
            if (_textAdvance.IsAvailable && _textAdvance.Acquire())
                LogAction("TextAdvance external control acquired");
            else if (_textAdvance.IsAvailable)
                LogAction("warn: TextAdvance present but external control denied");
            else
                LogAction("warn: TextAdvance not installed — NPC dialogs will stall");

            // TextAdvance doesn't handle SelectYesno — we click Yes ourselves
            // when one appears during Interacting.
            if (!_yesnoListenerRegistered)
            {
                _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup);
                _yesnoListenerRegistered = true;
            }
        }
        Transition(FateBotState.Selecting);
    }

    public void Stop()
    {
        if (State == FateBotState.Stopped) return;
        var previous = State;
        LogAction($"STOP requested by user (was in {previous})");
        LogSessionTotals(prefix: "STOP");
        _sessionStartedAt = null;
        // Clear trading scratch state so a manual Stop during a Survey or
        // auto-trade run doesn't leak into the next run.
        _tradingVendor = null;
        _tradingItemId = 0;
        _tradingTeleportFired = false;
        _tradingAethernetFired = false;
        _tradingSurveyMode = false;
        _surveyOnlySession = false;
        if (_surveyAcquiredTextAdvance)
        {
            _textAdvance.Release();
            _surveyAcquiredTextAdvance = false;
        }
        _fileLogger.End();
        // Cleanup is intentionally unconditional (no DryRun guard): even in
        // dry-run we want a Stop click to halt any in-flight pathing or
        // Lifestream task immediately.
        if (_rsrActivated)     { try { _rsr.Deactivate();     } catch {} _rsrActivated = false; }
        if (_bossmodActivated) { try { _bossmod.Deactivate(); } catch {} _bossmodActivated = false; }
        try { _textAdvance.Release(); } catch {}
        if (_yesnoListenerRegistered)
        {
            try { _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup); } catch {}
            _yesnoListenerRegistered = false;
        }
        try { _navmesh.Stop(); } catch {}
        try { _lifestream.Abort(); } catch {}
        // Restore YesAlready to whatever the user had set before Start (if we
        // touched it). _yesAlreadyPriorState == null means we never disabled.
        if (_yesAlreadyPriorState == true && _yesAlready.IsInstalled)
        {
            _yesAlready.SetEnabled(true);
            LogAction("YesAlready restored");
        }
        _yesAlreadyPriorState = null;
        _pausedAfkSent = false;
        Transition(FateBotState.Stopped);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _chatGui.ChatMessage -= OnChatMessage;
        try { _party.Dispose(); } catch { }
        if (!_config.DryRun)
        {
            if (_rsrActivated)     _rsr.Deactivate();
            if (_bossmodActivated) _bossmod.Deactivate();
        }
        if (_yesnoListenerRegistered)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup);
            _yesnoListenerRegistered = false;
        }
        _textAdvance.Dispose();
        _fileLogger.Dispose();
    }

    /// <summary>
    /// Stop the bot on any whisper-class message that names the player, or any
    /// GM-channel chat. Player-report driven enforcement is the dominant ban
    /// vector for open-world bots (see reference_bot_safety_detection.md).
    /// </summary>
    // Chat safety dedup — once a chat trigger fires, ignore further triggers
    // for this cooldown window (or until the pause we created expires). Prevents
    // a hostile player spamming /tell from holding the bot in a pause loop.
    private DateTime _chatStopCooldownUntil = DateTime.MinValue;

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (!_config.EnableChatSafetyStop) return;
        // Already stopped/paused → nothing useful to do; spam can't extend us.
        if (State == FateBotState.Stopped || State == FateBotState.Paused) return;
        // Inside cooldown window (e.g. just resumed from a pause) → ignore.
        if (DateTime.UtcNow < _chatStopCooldownUntil) return;

        var type = message.LogKind;
        var typeId = (ushort)type;
        bool isGm = typeId >= 80 && typeId <= 94;
        bool isTell = type == Dalamud.Game.Text.XivChatType.TellIncoming
                   || type == Dalamud.Game.Text.XivChatType.TellOutgoing;
        bool isSay = type == Dalamud.Game.Text.XivChatType.Say;
        if (!isGm && !isTell && !isSay) return;

        var text = message.Message.TextValue;
        if (isSay)
        {
            var player = _objectTable.LocalPlayer;
            var name = player?.Name.TextValue;
            if (string.IsNullOrEmpty(name) || !text.Contains(name, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var senderName = message.Sender.TextValue;
        var minutes = _config.ChatStopPauseMinutes;
        // Apply cooldown so any spam during the pause / for a while after it
        // ends won't immediately re-pause us. Cooldown covers the pause itself
        // plus 5 min so the bot can do a meaningful chunk of farming before
        // being eligible to re-trigger.
        _chatStopCooldownUntil = DateTime.UtcNow.AddMinutes(minutes > 0 ? minutes + 5 : 5);

        LogAction($"SAFETY {(minutes > 0 ? "PAUSE" : "STOP")} — {type} from {senderName}: \"{text}\"");
        _chatGui.PrintError($"[FateWalker] safety {(minutes > 0 ? $"pause {minutes}m" : "stop")} triggered by {type} from {senderName}");
        if (minutes > 0)
            EnterPauseSafely(minutes, $"chat from {senderName}", resetSessionTimer: false);
        else
            Stop();
    }

    private void OnFrameworkUpdate(IFramework f)
    {
        try { PumpPartyCoordinator(); } catch (Exception ex) { _log.Error(ex, "PartyCoordinator pump crashed"); }
        try { Tick(); }
        catch (Exception ex) { _log.Error(ex, "FateController tick crashed"); }
    }

    /// <summary>Refresh PartyCoordinator's view of "who am I, who's in my party"
    /// from Dalamud's IPartyList + IClientState, then tick it so heartbeats /
    /// host election / message drain all happen at most once per framework frame.</summary>
    private unsafe void PumpPartyCoordinator()
    {
        var me = _objectTable.LocalPlayer;
        // Dalamud SDK 15 doesn't surface LocalContentId on IClientState yet — read
        // it from FFXIVClientStructs PlayerState directly. Same ulong, same source.
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        ulong myCid = ps != null ? ps->ContentId : 0;
        _party.MyCid       = myCid;
        _party.MyTerritory = GetCurrentTerritory();
        // Solo (party size 0 or 1 with just me) → CurrentPartyCids is empty so
        // IsLowestCidInParty() short-circuits to true and Host election always
        // succeeds. Otherwise, snapshot the cross-realm-safe content-ids.
        if (_partyList.Length <= 1)
        {
            _party.CurrentPartyCids = me != null && myCid != 0 ? new[] { myCid } : System.Array.Empty<ulong>();
        }
        else
        {
            var cids = new System.Collections.Generic.List<ulong>(_partyList.Length);
            for (int i = 0; i < _partyList.Length; i++)
            {
                var pm = _partyList[i];
                if (pm == null) continue;
                if (pm.ContentId != 0) cids.Add((ulong)pm.ContentId);
            }
            _party.CurrentPartyCids = cids;
        }
        _party.Tick();
    }

    /// <summary>
    /// Prefer Lifestream's real territory (instance-resilient) over Dalamud's
    /// IClientState.TerritoryType. The latter can be wrong in instanced zones
    /// (Eureka/Bozja, some open-world post-EW) or briefly during loading.
    /// Falls back to ClientState when Lifestream isn't available or returns 0.
    /// </summary>
    private uint GetCurrentTerritory()
    {
        if (_lifestream.IsAvailable)
        {
            var t = _lifestream.CurrentTerritory;
            if (t != 0) return t;
        }
        return _clientState.TerritoryType;
    }

    private void Tick()
    {
        if (State == FateBotState.Stopped) return;

        // Universal guards
        if (!_clientState.IsLoggedIn) { Stop(); return; }
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]) return;
        if (_condition[ConditionFlag.OccupiedInCutSceneEvent]) return;

        // Duty safety: if Dalamud says we're in an instanced duty (dungeon /
        // trial / raid) — the bot was never meant for that. Halt rather than
        // AFK in dungeon (= guaranteed ban-vector).
        if (_config.AutoStopInDuty && _dutyState.IsDutyStarted)
        {
            LogAction("SAFETY STOP — duty active (IDutyState.IsDutyStarted=true)");
            _chatGui.PrintError("[FateWalker] duty detected — bot stopped to avoid AFK-in-dungeon flag.");
            Stop();
            return;
        }

        // Durability sample — run regardless of state, every 5s.
        if (DateTime.UtcNow - _lastDurabilityCheckAt > TimeSpan.FromSeconds(5))
        {
            _lastDurabilityCheckAt = DateTime.UtcNow;
            var report = GearDurability.GetMinDurability();
            if (report != null) _lastDurabilityMin = report.MinPercent;
        }

        // Trigger auto-repair when min durability drops below threshold. We do
        // it from the orchestration layer (here, not inside a Tick* method)
        // because it should preempt almost any state — except those we should
        // never interrupt: any "going to a safe place" or "already at one"
        // state. PreparingPause is the one we missed and it caused a loop
        // with session-cap: cap fires from Repairing → PreparingPause → cap
        // can't fire (excluded) but auto-repair could → Repairing → cap → …
        if (_config.EnableAutoRepair
            && _lastDurabilityMin > 0
            && _lastDurabilityMin < _config.RepairAtDurabilityPercent
            && State != FateBotState.Repairing
            && State != FateBotState.Dying
            && State != FateBotState.Paused
            && State != FateBotState.PreparingPause
            && State != FateBotState.Teleporting
            && State != FateBotState.Interacting
            && State != FateBotState.Trading)
        {
            var aetheryte = MenderMap.Resolve(_clientState.TerritoryType);
            if (aetheryte != 0 && _lifestream.IsAvailable)
            {
                _sessionRepairTrips++;
                LogAction($"durability {_lastDurabilityMin}% < {_config.RepairAtDurabilityPercent}% — routing to Mender (aetheryte {aetheryte}, session trips={_sessionRepairTrips})");
                _repairAetheryteId = aetheryte;
                _repairTeleportFired = false;
                _repairTeleportRejections = 0;
                if (!_config.DryRun)
                {
                    _navmesh.Stop();
                    if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
                    if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
                }
                Transition(FateBotState.Repairing);
                return;
            }
        }

        // Auto-trade trigger — when gem count reaches configured threshold and
        // user has items on the shopping list, route to a vendor before doing
        // anything else (preserving the gem cap is more important than the
        // next FATE). Same exclusion list as repair: don't interrupt critical
        // states.
        if (_config.EnableAutoTrading
            && _config.TradingShoppingList.Count > 0
            && State != FateBotState.Trading
            && State != FateBotState.Repairing
            && State != FateBotState.Dying
            && State != FateBotState.Paused
            && State != FateBotState.PreparingPause
            && State != FateBotState.Teleporting
            && State != FateBotState.Interacting)
        {
            int gems = FateWalker.Data.CurrencyReader.GetBicolorGemstoneCount();
            // Effective trigger drops by 100 when a Twist of Fate buff is
            // active — the next FATE will yield bonus gems, so we want extra
            // headroom to avoid capping mid-buff.
            int effectiveTrigger = _config.TradingTriggerGems;
            if (_config.EnableAdaptiveTradeTrigger && IsTwistOfFateActive())
                effectiveTrigger = Math.Max(100, effectiveTrigger - 100);
            // Hard floor: if we're already at or past cap, trade immediately
            // regardless of trigger (next FATE = 0 reward).
            bool atCap = gems >= _config.BicolorGemCap;
            if (gems >= effectiveTrigger || atCap)
            {
                if (atCap)
                    LogAction($"trading: gems {gems} at/over cap {_config.BicolorGemCap} — forced trade");
                else if (effectiveTrigger != _config.TradingTriggerGems)
                    LogAction($"trading: adaptive trigger {effectiveTrigger} (ToF buff active)");
                // Build prioritized buy queue: cheapest gem cost first (so
                // Vouchers drain before high-cost items), skip items already
                // at their inventory cap.
                VendorNpc? vendor = null;
                uint itemId = 0;
                var ordered = _config.TradingShoppingList
                    .Select(id => (Id: id, Cost: ResolveGemCost(id), Vendor: VendorCatalog.FindByItemId(id) ?? FindVendorByDiscovery(id)))
                    .Where(x => x.Vendor != null && x.Cost > 0 && !IsItemAtLimit(x.Id))
                    .OrderBy(x => x.Cost)
                    .ToList();
                if (ordered.Count > 0)
                {
                    var pick = ordered[0];
                    vendor = pick.Vendor;
                    itemId = pick.Id;
                }
                if (vendor != null && _lifestream.IsAvailable)
                {
                    _tradingVendor = vendor;
                    _tradingItemId = itemId;
                    bool walkOnly = _clientState.TerritoryType == vendor.TerritoryType
                                 && IsVendorWalkClose(vendor, out _);
                    _tradingTeleportFired = walkOnly;
                    _lastTradingActionAt = DateTime.MinValue;
                    // Remember the FATE zone we're leaving so the post-trade rotate
                    // can come back here instead of jumping to the lowest-id zone
                    // in the working set (which desyncs party members still farming
                    // the original map).
                    if (TerritoryMap.Lookup(_clientState.TerritoryType) != null)
                        _lastDepartedFromTerritory = _clientState.TerritoryType;
                    var hop = walkOnly ? " (vendor close by, walking)" : "";
                    LogAction($"trading: gems {gems} ≥ {_config.TradingTriggerGems} → routing to {vendor.Name} ({vendor.Settlement}) for item {itemId}{hop}");
                    if (!_config.DryRun)
                    {
                        _navmesh.Stop();
                        if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
                        if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
                    }
                    Transition(FateBotState.Trading);
                    return;
                }
            }
        }

        // Session cap — pause for macro-break OR hard stop after configured hours.
        // While Paused we already wait; don't re-trigger the cap. Also skip
        // while Dying — when the player is Unconscious the death/raise/return
        // flow MUST run to completion. Otherwise pause↔dying ping-pongs every
        // tick (death override pulls us back to Dying, session cap re-pauses)
        // and the corpse never gets to click the Return dialog.
        // Session cap also skips Repairing / Trading — those flows must complete
        // before we pause, otherwise the bot bounces between "go repair" and
        // "go pause" forever (auto-repair re-fires from PreparingPause, cap
        // re-fires from Repairing, loop). Let the in-flight transactional
        // state finish; the cap check fires again on the next non-excluded
        // tick.
        if (State != FateBotState.Paused
            && State != FateBotState.PreparingPause
            && State != FateBotState.Dying
            && State != FateBotState.Repairing
            && State != FateBotState.Trading
            && _sessionStartedAt != null && _sessionCapHoursRolled > 0
            && DateTime.UtcNow - _sessionStartedAt.Value > TimeSpan.FromHours(_sessionCapHoursRolled))
        {
            var pause = RollSessionPauseMinutes();
            var capHrsDisplay = _sessionCapHoursRolled.ToString("F1");
            if (pause > 0)
            {
                _chatGui.Print($"[FateWalker] session cap reached ({capHrsDisplay}h) — macro-break {pause}m.");
                EnterPauseSafely(pause, $"session cap {capHrsDisplay}h", resetSessionTimer: true);
            }
            else
            {
                LogAction($"session cap reached ({capHrsDisplay}h) — stopping");
                _chatGui.Print($"[FateWalker] session cap reached ({capHrsDisplay}h) — stopped.");
                Stop();
            }
            return;
        }

        // Death override — any state except Stopped/Dying can be interrupted.
        // No `Dead` ConditionFlag exists; Unconscious is the canonical signal
        // (cross-checked with AutoDuty / Questionable, see reference_condition_flags.md).
        if (State != FateBotState.Stopped && State != FateBotState.Dying
            && _condition[ConditionFlag.Unconscious])
        {
            _diedInTerritory = _clientState.TerritoryType;
            _diedReturnTriggered = false;
            _sessionDeaths++;
            LogAction($"player died in territory {_diedInTerritory} during {(_targetFateName ?? "(no FATE)")} — entering Dying (session deaths={_sessionDeaths})");
            // Free up the AI so we're not still trying to fight while dead.
            if (!_config.DryRun)
            {
                _navmesh.Stop();
                if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
                if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
            }
            Transition(FateBotState.Dying);
            return;
        }

        // Random zone rotation — anti-detection. Fires every N±jitter min
        // even when current zone is productive ("looks like player got bored
        // and moved"). Trigger ordering rationale:
        //   • Runs AFTER auto-repair / auto-trade / session cap / death
        //     override — those are higher-priority transactional flows that
        //     must complete first.
        //   • Runs ONLY when State == Selecting — guarantees no active
        //     FATE, no mid-flight teleport, no NPC dialog. Other states
        //     naturally cycle through Selecting, so deferring is safe and
        //     never abandons mid-pull / mid-trade / mid-repair.
        //   • Also deferred while Twist of Fate buff active — the chain
        //     priority already refuses zone rotation; same logic applies.
        if (_config.EnableRandomZoneRotation
            && _sessionStartedAt != null
            && State == FateBotState.Selecting
            && DateTime.UtcNow >= _nextRandomRotateAt)
        {
            if (_config.EnableTwistOfFateChain && IsTwistOfFateActive())
            {
                // Buff first — re-poll in 30s; rotate after chain done.
                _nextRandomRotateAt = DateTime.UtcNow.AddSeconds(30);
            }
            else if (TryForceRandomZoneRotate())
            {
                RollNextRandomRotate();
                return;
            }
            else
            {
                // Couldn't fire (no Lifestream, no other zones, etc.) —
                // try again in 2 min. If WorkingSet < 2 the next Roll
                // will permanently disable until the user adds a zone.
                _nextRandomRotateAt = DateTime.UtcNow.AddMinutes(2);
            }
        }

        // Deferred SelectYesno auto-confirm (humanize delay) — always runs.
        // EnsureSelectYesnoHandled catches dialogs the PostSetup listener missed.
        EnsureSelectYesnoHandled();
        ProcessPendingSelectYesno();

        // Periodic overnight-friendly stats heartbeat (every 5 min).
        if (_sessionStartedAt != null
            && DateTime.UtcNow - _lastStatsLogAt > TimeSpan.FromMinutes(5))
        {
            _lastStatsLogAt = DateTime.UtcNow;
            LogSessionTotals(prefix: "stats");
        }

        // Notice when a critical IPC dependency drops/restores.
        if (_sessionStartedAt != null) CheckPluginAvailability();

        // Clear dismount fail counter once we're actually on the ground.
        if (!_condition[ConditionFlag.Mounted]) _dismountFailCount = 0;

        // Generic stuck watchdog — log every 15s of no-movement in active states.
        GenericStuckWatchdog();
        CheckLogicLoop();

        switch (State)
        {
            case FateBotState.Selecting:   TickSelecting();   break;
            case FateBotState.Teleporting: TickTeleporting(); break;
            case FateBotState.Mounting:    TickMounting();    break;
            case FateBotState.Traveling:   TickTraveling();   break;
            case FateBotState.Interacting: TickInteracting(); break;
            case FateBotState.Engaging:    TickEngaging();    break;
            case FateBotState.Dying:       TickDying();       break;
            case FateBotState.PreparingPause: TickPreparingPause(); break;
            case FateBotState.Paused:      TickPaused();      break;
            case FateBotState.Repairing:   TickRepairing();   break;
            case FateBotState.Trading:     TickTrading();     break;
            case FateBotState.Recovering:  TickRecovering();  break;
        }
    }

    private void TickSelecting()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // 0. Outside the working set entirely (e.g. user pressed Start in
        //    Thavnair while only ShB zones are checked) → rotate immediately,
        //    even if local FATEs are eligible. Manual pick still wins below.
        //    Also rotate immediately when SkipMaxedSharedFateZones is on AND
        //    the current zone's Shared FATE rank is capped — no point sitting
        //    through the drought timer when no FATE here will ever be eligible.
        var workingSet = _config.WorkingSetZones;
        bool outsideWorkingSet = workingSet.Count > 0
                              && !workingSet.Contains(_clientState.TerritoryType);
        bool currentZoneMaxed = false;
        if (_config.SkipMaxedSharedFateZones)
        {
            var ranks = SharedFateProgress.ReadAll(_config);
            if (ranks.TryGetValue(_clientState.TerritoryType, out var st) && st.IsMaxed)
                currentZoneMaxed = true;
        }
        if ((outsideWorkingSet || currentZoneMaxed) && !ManuallyPickedFateId.HasValue)
        {
            if (currentZoneMaxed && _droughtStartedAt == null)
                LogAction("rotate: current zone Shared FATE rank maxed — skipping drought wait");
            if (TryRotateZone(player.Position)) return;
            // Rotation impossible (Lifestream missing etc.) — fall through so
            // we at least farm local FATEs instead of standing idle.
        }

        FateCandidate? chosen = null;

        // 1. Manual pick override — bypasses ALL filters (zone, level, time, blacklist).
        //    Cleared automatically once the FATE ends or vanishes.
        if (ManuallyPickedFateId.HasValue)
        {
            var manual = _fateTable.FirstOrDefault(f => f.FateId == ManuallyPickedFateId.Value);
            if (manual == null || manual.State == FateState.Ended || manual.State == FateState.Failed)
            {
                LogAction($"manual pick id={ManuallyPickedFateId} no longer valid — clearing, resume auto");
                ManuallyPickedFateId = null;
            }
            else
            {
                var dist = Vector3.Distance(manual.Position, player.Position);
                chosen = new FateCandidate(manual, dist, true, null);
            }
        }

        // Maintenance: drop session-disabled ids whose FATEs are no longer in
        // the table, so a re-spawn of the same FateId plays as normal.
        if (_sessionDisabledFateIds.Count > 0)
        {
            var liveIds = _fateTable.Select(f => f.FateId).ToHashSet();
            _sessionDisabledFateIds.RemoveWhere(id => !liveIds.Contains(id));
        }

        // 1b. Party Follower override — bypasses ALL filters (level/time/blacklist),
        // mirroring the Manual-pick rules. The Host has decided; we follow.
        // Falls through to auto-pick if the assigned FATE isn't in this zone's
        // table or has already ended (Follower will travel separately).
        if (chosen == null
            && _party.Role == Controller.Party.PartyCoordinator.EffectiveRole.Follower
            && _party.AssignedFateId != 0)
        {
            var assigned = _fateTable.FirstOrDefault(f => f.FateId == _party.AssignedFateId);
            if (assigned != null && assigned.State is FateState.Running or FateState.Preparing)
            {
                var dist = Vector3.Distance(assigned.Position, player.Position);
                chosen = new FateCandidate(assigned, dist, true, null);
            }
        }

        // 2. Auto-pick via selector if no manual override active.
        if (chosen == null)
        {
            var picks = _selector.Evaluate(_fateTable, _clientState.TerritoryType, player.Position, player.Level)
                .Where(c => c.PassesFilter
                            && !MatchesBlacklistPattern(c.Fate.Name.TextValue)
                            && !_sessionDisabledFateIds.Contains(c.Fate.FateId))
                .ToList();
            // Twist of Fate chain: ignore Bonus preference, pick the closest
            // valid FATE in the current zone — minimum travel time = maximum
            // chance to consume the buff before it expires.
            if (_config.EnableTwistOfFateChain && IsTwistOfFateActive() && picks.Count > 1)
            {
                chosen = picks
                    .OrderBy(c => c.DistanceToPlayer)
                    .First();
                LogAction($"ToF chain: picking closest ({chosen.DistanceToPlayer:F0}y) over bonus priority");
            }
            else
            {
                chosen = picks.FirstOrDefault();
            }
        }

        if (chosen == null)
        {
            // We already handled "outside working set" at the top of TickSelecting,
            // so we're either inside the set or the rotation failed (no Lifestream).
            // Inside working set: drought-timer-based rotation.
            if (_droughtStartedAt == null)
            {
                _droughtStartedAt = DateTime.UtcNow;
                LogAction("no eligible FATE; drought timer started");
            }
            else if (TryRotateZone(player.Position))
            {
                return;
            }
            else if (DateTime.UtcNow - _lastDroughtLogAt > TimeSpan.FromSeconds(15))
            {
                _lastDroughtLogAt = DateTime.UtcNow;
                LogAction($"no eligible FATE; waiting… (drought {(DateTime.UtcNow - _droughtStartedAt.Value).TotalSeconds:F0}s)");
            }
            return;
        }

        // Found a FATE — clear drought tracker.
        _droughtStartedAt = null;

        // Humanize: pause in Selecting for ThinkBeforePick seconds before
        // committing. Simulates a player glancing at the FATE list before
        // engaging instead of instant decision.
        // Twist of Fate chain: bypass the humanize delay so we don't waste
        // the buff window (~30s remaining is typical after Forlorn kill).
        bool chainSkipHumanize = _config.EnableTwistOfFateChain && IsTwistOfFateActive();
        if (!chainSkipHumanize && DateTime.UtcNow - _stateEnteredAt < _humanizeDelay)
            return;

        _targetFateId = chosen.Fate.FateId;
        _targetFateName = chosen.Fate.Name.TextValue;
        _targetFatePos = chosen.Fate.Position;
        // Party Mode (Host): immediately broadcast this assignment so Followers
        // converge within one heartbeat rather than waiting the full beat. The
        // coordinator keeps re-publishing on its own clock for late joiners.
        if (_party.Role == Controller.Party.PartyCoordinator.EffectiveRole.Host)
            _party.PublishFateAssign(_targetFateId, GetCurrentTerritory());
        // Humanize: roll a random landing offset within the configured radius.
        // Same offset is used the entire travel so we don't shuffle mid-flight.
        _targetFateLandingOffset = ChooseLandingOffset(chosen.Fate);
        _landingRefined = false; // fresh target — let RefineLandingTarget retarget once
        _refinedLandingPos = null;

        // For Preparing FATEs we store the MotivationNpc id but do NOT resolve
        // the NPC's IGameObject yet — Dalamud's ObjectTable only contains entities
        // within streaming range (~250y), so a FATE 800y away will have no NPC
        // loaded. Travel to the FATE center using FATE.Radius (same as Running
        // FATEs); precise approach to the NPC happens later in TickInteracting,
        // once we're inside streaming range and the NPC is queryable.
        _targetMotivationNpcId = chosen.Fate.State == FateState.Preparing
            ? chosen.Fate.GetMotivationNpc() : 0u;
        _targetFateRadius = chosen.Fate.Radius > 0 ? chosen.Fate.Radius : 20f;

        if (_targetMotivationNpcId != 0)
            LogAction($"target = {_targetFateName} (Preparing, NPC id={_targetMotivationNpcId}, {chosen.DistanceToPlayer:F0}y away, fate radius={_targetFateRadius:F0})");
        else
            LogAction($"target = {_targetFateName} (Lv{chosen.Fate.Level}, {chosen.DistanceToPlayer:F0}y, bonus={chosen.Fate.HasBonus})");

        if (IsInFateRange(player.Position))
        {
            Transition(_targetMotivationNpcId != 0 ? FateBotState.Interacting : FateBotState.Engaging);
            return;
        }

        // Long-range in-zone teleport: when the FATE is far enough that a
        // teleport + short fly beats a direct fly (default threshold 1800 y),
        // hop to the zone's primary aetheryte first. After arrival we re-enter
        // Selecting and the FATE is picked again at much shorter range.
        //
        // Gates: master toggle off, gil below floor, or Twist of Fate buff
        // active (chain mode forbids leaving the zone) all force a fly.
        var zone = TerritoryMap.Lookup(_clientState.TerritoryType);
        bool tofChainActive = _config.EnableTwistOfFateChain && IsTwistOfFateActive();
        bool gilOk = !_config.EnableLongRangeTeleport
            ? false
            : (_config.MinGilReserve <= 0
                || FateWalker.Data.CurrencyReader.GetGil() >= _config.MinGilReserve);
        if (zone != null
            && _config.EnableLongRangeTeleport
            && _lifestream.IsAvailable
            && !_lifestream.IsBusy
            && gilOk
            && !tofChainActive
            && chosen.DistanceToPlayer > _config.LongRangeTeleportYalms
            && !_condition[ConditionFlag.InCombat])
        {
            _config.SessionTeleportCount++;
            _config.SessionTeleportCostGil += EstimateInZoneTeleportCostGil();
            _saveConfig?.Invoke();
            LogAction($"long-range hop: FATE {chosen.DistanceToPlayer:F0}y > {_config.LongRangeTeleportYalms}y — teleport to {zone.AetheryteName} (aetheryte {zone.AetheryteId}) · session #{_config.SessionTeleportCount}, est cost {_config.SessionTeleportCostGil}g");
            _pendingTeleportTerritory = _clientState.TerritoryType;
            _pendingTeleportAetheryte = zone.AetheryteId;
            _teleportFired = false;
            if (!_config.DryRun)
            {
                _navmesh.Stop();
                if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
                if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
            }
            // If currently mounted, dismount so the cast doesn't fight the mount.
            // Teleporting state's own retry loop will handle it if dismount stalls.
            Transition(FateBotState.Teleporting);
            return;
        }
        else if (zone != null
            && chosen.DistanceToPlayer > _config.LongRangeTeleportYalms
            && (!_config.EnableLongRangeTeleport || !gilOk || tofChainActive))
        {
            string why = !_config.EnableLongRangeTeleport ? "disabled" : tofChainActive ? "ToF chain" : "low gil";
            LogAction($"long-range eligible ({chosen.DistanceToPlayer:F0}y) but skipped — {why}");
        }

        if (_condition[ConditionFlag.Mounted])
        {
            Transition(FateBotState.Traveling);
        }
        else
        {
            Transition(FateBotState.Mounting);
        }
    }

    /// <summary>
    /// Try to pick another zone from the working set and queue a Lifestream
    /// teleport. Two trigger paths:
    ///   • Player is outside the working set entirely (e.g. started bot in a
    ///     city like the Crystarium) → teleport immediately, no drought wait.
    ///   • Player is inside the working set but current zone is dry → wait
    ///     for <c>MinDroughtSeconds</c> then rotate round-robin.
    /// Returns true if the bot transitioned to Teleporting.
    /// </summary>
    private bool TryRotateZone(Vector3 playerPos)
    {
        var currentTerritory = _clientState.TerritoryType;
        var workingSet = _config.WorkingSetZones;
        if (workingSet.Count == 0)
        {
            // Surface this — a silent return makes drought-stalls hard to
            // diagnose when we end up in a city (e.g. after auto-trading)
            // with no FATE-zone candidates configured.
            if (DateTime.UtcNow - _lastDroughtLogAt > TimeSpan.FromSeconds(15))
            {
                _lastDroughtLogAt = DateTime.UtcNow;
                LogAction("rotate: working set is empty — tick zones in the Zones tab to enable rotation");
            }
            return false;
        }

        bool outsideWorkingSet = !workingSet.Contains(currentTerritory);
        // Cities, instances, dungeons etc. aren't FATE zones (not in
        // TerritoryMap). Treat those as outside-working-set too — we should
        // never sit on a drought timer in a city.
        bool inFateTerritory = TerritoryMap.Lookup(currentTerritory) != null;
        if (!inFateTerritory) outsideWorkingSet = true;

        // Twist of Fate chain: refuse to rotate while the buff is active —
        // leaving the zone drops the buff (it's zone-scoped) and wastes the
        // gem multiplier. Wait it out in current zone instead.
        if (_config.EnableTwistOfFateChain && IsTwistOfFateActive() && inFateTerritory)
        {
            if (DateTime.UtcNow - _lastDroughtLogAt > TimeSpan.FromSeconds(10))
            {
                _lastDroughtLogAt = DateTime.UtcNow;
                LogAction("rotate: refused — Twist of Fate buff active (chain priority)");
            }
            return false;
        }
        // Maxed zones are equivalent to "outside working set" for rotation
        // purposes — we want OUT immediately, no drought wait.
        var ranks = _config.SkipMaxedSharedFateZones
            ? SharedFateProgress.ReadAll(_config)
            : new Dictionary<uint, SharedFateZoneState>();
        bool currentZoneMaxed = ranks.TryGetValue(currentTerritory, out var curState) && curState.IsMaxed;
        bool urgentRotate = outsideWorkingSet || currentZoneMaxed;

        // Inside working set & not maxed: enforce drought timer + need >1 zone.
        // Urgent: skip both checks — get the bot to a productive FATE zone ASAP.
        if (!urgentRotate)
        {
            var droughtSeconds = (DateTime.UtcNow - (_droughtStartedAt ?? DateTime.UtcNow)).TotalSeconds;
            if (droughtSeconds < _config.MinDroughtSeconds) return false;
            if (workingSet.Count < 2)
            {
                _droughtStartedAt = DateTime.UtcNow;
                return false;
            }

            // Humanize: once drought has elapsed, hesitate a random extra
            // window before committing to a teleport. Simulates a player
            // glancing at the map / "let me try one more thing" before bailing.
            if (_droughtHesitateUntil == null)
            {
                var jitter = RollSeconds(_config.HesitateBeforeTeleportMinSec, _config.HesitateBeforeTeleportMaxSec);
                _droughtHesitateUntil = DateTime.UtcNow + jitter;
                if (jitter > TimeSpan.Zero)
                    LogAction($"drought met — hesitating {jitter.TotalSeconds:F0}s before rotating");
            }
            if (DateTime.UtcNow < _droughtHesitateUntil.Value) return false;
        }

        if (!_lifestream.IsAvailable)
        {
            LogAction(outsideWorkingSet
                ? "rotate: outside working set but Lifestream not installed — staying put"
                : "rotate: drought hit but Lifestream not installed — staying put");
            _droughtStartedAt = DateTime.UtcNow;
            return false;
        }

        // Build deterministic rotation order (sorted), exclude current and the
        // zone we just departed from to avoid immediate ping-pong. Also drop
        // any zone whose Shared FATE rank is already maxed when the toggle is on.
        // Exception: when urgentRotate (city/maxed/outside-set), we want OUT
        // — ignore the _lastDepartedFromTerritory anti-pingpong filter so we
        // don't get stuck if the only candidate happens to be the one we
        // just left (e.g. teleported to city to trade, returning to the only
        // ticked FATE zone).
        bool IsZoneMaxed(uint t) => ranks.TryGetValue(t, out var s) && s.IsMaxed;
        var candidates = workingSet
            .Where(t => t != currentTerritory
                     && (urgentRotate || t != _lastDepartedFromTerritory)
                     && !IsZoneMaxed(t))
            .OrderBy(t => t)
            .ToList();
        if (candidates.Count == 0)
        {
            // Only viable target is the one we just left — allow it again
            // (still skipping maxed zones, since farming them is pointless).
            candidates = workingSet.Where(t => t != currentTerritory && !IsZoneMaxed(t)).OrderBy(t => t).ToList();
            if (candidates.Count == 0)
            {
                // Auto-disable Shared FATE Progress mode when EVERY ticked zone
                // (including the one we're standing in) is at max rank — there
                // is no productive Progress work left within the user's chosen
                // working set. Switch to normal farming so the bot keeps
                // rotating those zones for gemstones / gil instead of stalling.
                if (_config.SkipMaxedSharedFateZones
                    && workingSet.All(t => IsZoneMaxed(t)))
                {
                    _config.SkipMaxedSharedFateZones = false;
                    _saveConfig?.Invoke();
                    LogAction("All ticked zones at MAX rank — auto-disabled Shared FATE Progress mode, resuming normal farming");
                    // Reset drought so the next Selecting tick re-evaluates
                    // FATEs without the skip-maxed filter (and stays in the
                    // current zone if it has eligible FATEs).
                    _droughtStartedAt = null;
                    return false;
                }
                LogAction("rotate: no non-maxed candidate zone available — staying put");
                _droughtStartedAt = DateTime.UtcNow;
                return false;
            }
        }

        uint nextTerritory;
        if (urgentRotate)
        {
            // Prefer returning to the zone we just departed (the FATE zone we
            // were farming before going to the city to trade, or before getting
            // stranded). Without this, sorted-ascending candidates[0] always
            // sends us to the lowest-id zone in the working set — which is the
            // wrong zone any time the user's chosen "return" zone isn't the
            // numerically-smallest one, and desyncs party members still on the
            // original map.
            if (_lastDepartedFromTerritory != 0 && candidates.Contains(_lastDepartedFromTerritory))
                nextTerritory = _lastDepartedFromTerritory;
            else
                nextTerritory = candidates[0];
        }
        else
        {
            // Round-robin: next one after current in sorted order.
            var sortedAll = workingSet.OrderBy(t => t).ToList();
            var currentIdx = sortedAll.IndexOf(currentTerritory);
            nextTerritory = sortedAll[(currentIdx + 1) % sortedAll.Count];
            // If the round-robin pick is invalid (current/maxed) fall back.
            if (nextTerritory == currentTerritory || IsZoneMaxed(nextTerritory))
                nextTerritory = candidates[0];
        }

        var info = TerritoryMap.Lookup(nextTerritory);
        if (info == null)
        {
            LogAction($"rotate: target territory {nextTerritory} not in TerritoryMap — skip");
            _droughtStartedAt = DateTime.UtcNow;
            return false;
        }

        LogAction(outsideWorkingSet
            ? $"rotate: outside working set → teleport to {info.ZoneName} (aetheryte {info.AetheryteId})"
            : $"rotate: drought hit → teleport to {info.ZoneName} (aetheryte {info.AetheryteId})");
        _pendingTeleportTerritory = nextTerritory;
        _pendingTeleportAetheryte = info.AetheryteId;
        _lastDepartedFromTerritory = currentTerritory;
        _droughtStartedAt = null;
        Transition(FateBotState.Teleporting);
        return true;
    }

    private void TickTeleporting()
    {
        // Arrived? We only consider "arrived" AFTER the teleport has fired,
        // otherwise an in-zone teleport (current territory == pending)
        // exits this state immediately without ever calling Lifestream.
        // BetweenAreas (loading) must have cleared too so we don't drop out
        // mid-loading-screen. Use Lifestream's GetRealTerritoryType — it's
        // instance-resilient where ClientState.TerritoryType can wobble.
        if (_teleportFired
            && GetCurrentTerritory() == _pendingTeleportTerritory
            && !_condition[ConditionFlag.BetweenAreas]
            && !_condition[ConditionFlag.BetweenAreas51])
        {
            // Make sure the world is ready before we hand off to Selecting.
            if (_objectTable.LocalPlayer == null) return;
            LogAction($"teleport arrived: territory={_pendingTeleportTerritory}");
            _pendingTeleportTerritory = 0;
            _pendingTeleportAetheryte = 0;
            Transition(FateBotState.Selecting);
            return;
        }

        if (_config.DryRun)
        {
            // Pretend we landed after 2s so the rest of the flow can be exercised.
            if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(2))
            {
                LogAction($"[DRY] would arrive at territory {_pendingTeleportTerritory}");
                Transition(FateBotState.Selecting);
            }
            return;
        }

        // Fire the teleport with retry — Lifestream's bare Teleport doesn't
        // set IsBusy, so we poll territory ourselves. Wait ~1s after entering
        // the state to avoid colliding with prior animation locks (dismount etc.).
        // Combat-end lockout (~5-10s) is a common false return; throttle to 5s
        // and stay in Teleporting until success or the overall timeout.
        var sinceEnter = DateTime.UtcNow - _stateEnteredAt;
        if (!_teleportFired && sinceEnter > TimeSpan.FromSeconds(1)
            && DateTime.UtcNow - _lastPathfindAt > TimeSpan.FromSeconds(5))
        {
            _lastPathfindAt = DateTime.UtcNow; // reuse as "last teleport attempt"
            if (_pendingTeleportAetheryte == 0)
            {
                LogAction("teleport: no aetheryte set — abort");
                Transition(FateBotState.Selecting);
                return;
            }
            // Don't stomp Lifestream's own queue — if it's busy from a prior
            // operation (aethernet hop, vnav follow-path), wait one more cycle.
            if (_lifestream.IsBusy)
            {
                LogAction("teleport: Lifestream busy, waiting one cycle");
                return;
            }
            // Belt-and-suspenders: close any leftover shop/vendor/dialog addon
            // before firing. An open modal counts as "player occupied" to the
            // game and Lifestream rejects every teleport attempt until closed.
            TryCloseShopExchangeCurrency();
            var ok = _lifestream.Teleport(_pendingTeleportAetheryte, 0);
            LogAction($"Lifestream.Teleport(aetheryte={_pendingTeleportAetheryte}) → {ok}");
            if (ok)
            {
                _teleportFired = true;
            }
            else
            {
                // Likely not-attuned, combat-lockout, gil, or animation-lock.
                // Stay in Teleporting; the 30s overall timeout will bail.
                LogAction("teleport rejected — will retry in 5s");
            }
        }

        // Timeout — 30s total to load + arrive.
        if (sinceEnter > TimeSpan.FromSeconds(30))
        {
            LogAction("teleport timeout — back to Selecting");
            _lifestream.Abort();
            _pendingTeleportTerritory = 0;
            _pendingTeleportAetheryte = 0;
            Transition(FateBotState.Selecting);
        }
    }

    private unsafe void TickMounting()
    {
        if (_condition[ConditionFlag.Mounted])
        {
            LogAction("mounted ✓");
            Transition(FateBotState.Traveling);
            return;
        }
        if (_condition[ConditionFlag.InCombat])
        {
            // Distance gate: only transition to Engaging if we're actually
            // at the picked FATE. Otherwise the combat is ambient (leftover
            // mob from previous FATE chase) and Engaging would stall — it
            // would look for FateId=X mobs within 40y and find none, then
            // the watchdog / logic-loop would cascade into a 15-min pause
            // (see v1.2.3.0 regression: bot picked FATE 878y away, ambient
            // mob blocked mount, Engaging stranded, paused).
            //
            // Fix: activate combat AI to clear the ambient mob right here.
            // Once combat ends, the Mounted flag check above will let us
            // mount and continue. _targetFateId stays set — same FATE pick
            // resumes after combat.
            var player = _objectTable.LocalPlayer;
            bool atFate = player != null && IsInFateRange(player.Position);
            if (atFate)
            {
                LogAction("in combat at FATE — engaging");
                Transition(FateBotState.Engaging);
                return;
            }
            // Far from FATE → ambient combat. Activate combat AI in-place
            // to kill aggressor(s) without abandoning the FATE pick.
            if (!_bossmodActivated && !_config.DryRun)
            {
                var preset = BossModPresetData.ForBackend(_config.CombatBackend);
                LogAction($"Mounting: in combat far from FATE ({(player == null ? "?" : Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(_targetFatePos.X, _targetFatePos.Z)).ToString("F0"))}y) — clearing aggro in-place");
                _bossmod.Activate(preset);
                _bossmodActivated = true;
                if (_config.CombatBackend == Configuration.CombatBackendKind.RSR)
                {
                    _rsr.Activate();
                    _rsrActivated = true;
                }
            }
            return; // stay in Mounting; mount when combat clears
        }
        // Out of combat — if BossMod was activated above for ambient clear,
        // deactivate before mounting (combat AI prevents mount cast).
        if (_bossmodActivated && !_config.DryRun)
        {
            _bossmod.Deactivate();
            _bossmodActivated = false;
            if (_rsrActivated) { _rsr.Deactivate(); _rsrActivated = false; }
            LogAction("Mounting: ambient combat cleared — mounting now");
        }
        // Blocked by a SelectYesno dialog (e.g. FATE-start prompt auto-popped
        // by player proximity to the MotivationNpc). Mount actions silently
        // fail while the dialog is up — would loop until the 10s timeout.
        // Switch to Interacting so the dialog handler queues a Yes click and
        // we start the FATE without ever needing to mount.
        if (_targetMotivationNpcId != 0)
        {
            var sy = _gameGui.GetAddonByName("SelectYesno");
            var syAddon = (AtkUnitBase*)sy.Address;
            if (syAddon != null && syAddon->IsVisible)
            {
                LogAction("Mounting: SelectYesno open — switching to Interacting to confirm FATE-start");
                Transition(FateBotState.Interacting);
                return;
            }
        }

        // Throttle mount attempts to once per 2s (cast time + animation)
        if (DateTime.UtcNow - _lastMountAttemptAt < TimeSpan.FromSeconds(2)) return;

        if (_config.DryRun)
        {
            LogAction("[DRY] would UseAction(GA_MountRoulette)");
            _lastMountAttemptAt = DateTime.UtcNow;
            // In dry run, fake-advance to Traveling after 1s so logic can continue
            if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(1))
                Transition(FateBotState.Traveling);
            return;
        }

        if (_action.UseMountRoulette())
        {
            LogAction("UseAction(Mount Roulette) called");
            _lastMountAttemptAt = DateTime.UtcNow;
        }

        // Timeout: 10s without success → abort to Selecting (different FATE may help)
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(10))
        {
            LogAction("mount timeout — back to Selecting");
            Transition(FateBotState.Selecting);
        }
    }

    private void TickTraveling()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // Random humanize jump on long ground walks. Two reasons:
        //   1) Looks human — fixed-cadence mouse autorun never jumps.
        //   2) Unsticks the player from small terrain bumps that vnavmesh
        //      can't path around (low rocks, debris).
        // Skip when flying / mounted (jump press is a no-op or dismount),
        // skip when in combat (already busy), skip during cutscenes / loads.
        TryHumanizeJump();
        // Altitude wobble was removed in v1.0.0.16: vnavmesh hooks the
        // game's flight input vector directly, so any keystate we set for
        // SPACE / Z / W is overridden every frame while pathing. No visible
        // effect on altitude. If we want flight wobble later it has to go
        // through a different mechanism (jitter the desired Y in waypoints,
        // for example) rather than fighting the input hook.
        // Once we're within streaming range of the FATE, retarget navmesh to
        // the actual entity (mob or MotivationNpc) instead of the bot-rolled
        // landing offset. Lands the player right next to combat rather than
        // a generic patch of ground at the FATE edge — no human grinder
        // walks 15y on foot from their dismount spot.
        RefineLandingTarget(player.Position);

        // Arrival check: prefer the refined entity position so we dismount
        // right on top of the mob/NPC instead of at the FATE-edge offset.
        // Fall back to the standard radius-based "in range" test when we
        // never managed to lock onto a specific entity (e.g. fly-in was so
        // fast the object table hadn't streamed yet).
        if (_refinedLandingPos.HasValue
            && Vector3.Distance(player.Position, _refinedLandingPos.Value) < 5f)
        {
            LogAction("in range ✓ (next to refined target)");
            if (!_config.DryRun) _navmesh.Stop();
            Transition(_targetMotivationNpcId != 0 ? FateBotState.Interacting : FateBotState.Engaging);
            return;
        }
        if (IsInFateRange(player.Position))
        {
            LogAction("in range ✓");
            if (!_config.DryRun) _navmesh.Stop();
            Transition(_targetMotivationNpcId != 0 ? FateBotState.Interacting : FateBotState.Engaging);
            return;
        }

        // Arrival fallback: vnav may consider us "close enough" (path idle)
        // while we're still hovering in the air above the FATE — strict
        // 2D-distance × 0.7 threshold misses us by a few yards. If vnav has
        // settled AND we're inside the actual FATE circle (2D dist ≤ full
        // radius), engage anyway. Wait 2s in Traveling to avoid false hits
        // before the first pathfind even runs.
        if (!_config.DryRun
            && DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(2)
            && !_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress
            && _targetFateRadius > 0)
        {
            var dist2D = Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(_targetFatePos.X, _targetFatePos.Z));
            if (dist2D <= _targetFateRadius)
            {
                LogAction($"arrived ✓ (vnav idle, 2D dist {dist2D:F1}y ≤ radius {_targetFateRadius:F0}y)");
                _navmesh.Stop();
                Transition(_targetMotivationNpcId != 0 ? FateBotState.Interacting : FateBotState.Engaging);
                return;
            }
        }

        var range = MathF.Max(4f, _targetFateRadius * _config.EngageRangeMultiplier);

        if (_config.DryRun)
        {
            // Fake-advance distance: just transition after 3s so logic can be tested
            if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(3))
            {
                LogAction("[DRY] would PathfindAndMoveCloseTo + transition to Engaging");
                Transition(FateBotState.Engaging);
            }
            return;
        }

        // Stuck recovery (corner-snags, obstacles): stop + re-pathfind same tick.
        if (CheckAndRecoverFromStuck(player.Position)) return;

        // Kick off pathfinding when path is idle. Throttle to ~1.5s ± jitter
        // (humanize) to avoid spamming vnavmesh when paths complete quickly.
        if (!_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress
            && DateTime.UtcNow - _lastPathfindAt > PathfindThrottle())
        {
            var dest = _targetFatePos + _targetFateLandingOffset;
            LogAction($"PathfindAndMoveCloseTo(fly=true, range={range:F0})");
            _navmesh.PathfindAndMoveCloseTo(dest, fly: true, range: range);
            _lastPathfindAt = DateTime.UtcNow;
        }

        // Stuck timeout: 90s without arriving → re-select
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(90))
        {
            LogAction("travel timeout — back to Selecting");
            _navmesh.Stop();
            Transition(FateBotState.Selecting);
        }
    }

    private void TickInteracting()
    {
        // Dismount first — must be on ground to interact with an NPC.
        if (_condition[ConditionFlag.Mounted])
        {
            if (TryDismountOrRescue("pre-interact")) return;
        }

        // Check FATE state — maybe someone else started it for us.
        var fate = FindFate(_targetFateId);
        if (fate == null)
        {
            LogAction("FATE vanished while interacting — re-select");
            Transition(FateBotState.Selecting);
            return;
        }
        if (fate.State == FateState.Running)
        {
            LogAction("FATE started ✓");
            Transition(FateBotState.Engaging);
            return;
        }
        if (fate.State == FateState.Ended || fate.State == FateState.Failed)
        {
            LogAction($"FATE ended before interact (state={fate.State})");
            Transition(FateBotState.Recovering);
            return;
        }

        // Resolve NPC each tick (entity can move; escort NPC paces around).
        var npc = _objectTable.SearchByEntityId(_targetMotivationNpcId);
        if (npc == null)
        {
            LogAction($"MotivationNpc {_targetMotivationNpcId} not in object table — abort");
            Transition(FateBotState.Selecting);
            return;
        }

        // Close-in if not within interact range (~3.5y for most NPCs).
        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        var dist = Vector3.Distance(player.Position, npc.Position);
        if (dist > 4f)
        {
            // Out of melee — reset approach-delay timer so it re-rolls once we
            // arrive (next entry into the close-enough branch).
            _interactReadyAt = null;

            // Stuck recovery for ground-walk close-in (corner-snag is more likely here).
            if (CheckAndRecoverFromStuck(player.Position)) return;

            if (!_config.DryRun && !_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress
                && DateTime.UtcNow - _lastPathfindAt > PathfindThrottle())
            {
                LogAction($"close-in to NPC ({dist:F1}y → 2y)");
                _navmesh.PathfindAndMoveCloseTo(npc.Position, fly: false, range: 2f);
                _lastPathfindAt = DateTime.UtcNow;
            }
            return;
        }

        // Humanize: random "look at NPC" delay on first arrival in melee range,
        // before firing InteractWith. Re-rolled each time we get back in range.
        if (_interactReadyAt == null)
        {
            var delayMs = RollMs(_config.InteractApproachDelayMinMs, _config.InteractApproachDelayMaxMs);
            _interactReadyAt = DateTime.UtcNow.AddMilliseconds(delayMs);
            // Set target now so the NPC nameplate highlights — looks more natural.
            if (!_config.DryRun) _targetManager.Target = npc;
            LogAction($"in range of {npc.Name.TextValue} — waiting {delayMs}ms before InteractWith");
            return;
        }
        if (DateTime.UtcNow < _interactReadyAt.Value) return;

        // Throttle interact attempts to 1.5s (covers dialog + TextAdvance advance).
        if (DateTime.UtcNow - _lastInteractAt < TimeSpan.FromMilliseconds(1500)) return;

        if (_config.DryRun)
        {
            LogAction($"[DRY] would InteractWith(NPC id={_targetMotivationNpcId}, name={npc.Name.TextValue})");
        }
        else
        {
            _targetManager.Target = npc;
            var ok = _action.InteractWith(npc);
            LogAction($"InteractWith(id={_targetMotivationNpcId}, name={npc.Name.TextValue}) → {ok}");
        }
        _lastInteractAt = DateTime.UtcNow;

        // Timeout: 12s in Interacting without state change → give up, re-select.
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(12))
        {
            LogAction("interact timeout — re-selecting");
            Transition(FateBotState.Selecting);
        }
    }

    private void TickEngaging()
    {
        var fate = FindFate(_targetFateId);
        if (fate == null)
        {
            LogAction("target FATE no longer in table");
            Transition(FateBotState.Recovering);
            return;
        }

        // FateState.Ending = 5 (objectives complete, mobs despawning, brief
        // grace before "Ended"). We treat it as done so the bot stops pulling
        // dying mobs. FateUtils may have already handed in collect items by
        // this point; any leftover hand-in window is sacrificed for cleaner
        // exit behaviour.
        if (fate.State == FateState.Ended
            || fate.State == FateState.Failed
            || fate.State == FateState.Ending)
        {
            if (fate.State == FateState.Failed) _sessionFatesFailed++;
            else
            {
                _sessionFatesCompleted++;
                // Local rank tracker only matters when the user is running in
                // Shared FATE Progress mode (skip-maxed) — otherwise the rank
                // info isn't consulted for any decision and we'd be writing
                // dead config on every kill.
                if (_config.SkipMaxedSharedFateZones)
                {
                    SharedFateProgress.IncrementLocal(_config, _clientState.TerritoryType);
                    _saveConfig?.Invoke();
                }
            }
            _loopRecoveryCount = 0; // FATE completed = clear progress; reset escalation chain
            var gems = FateWalker.Data.CurrencyReader.GetBicolorGemstoneCount();
            LogAction($"FATE done: \"{_targetFateName}\" (Lv{fate.Level}, state={fate.State}, bonus={fate.HasBonus}) · session FATEs={_sessionFatesCompleted}, failed={_sessionFatesFailed}, gems={gems}");
            Transition(FateBotState.Recovering);
            return;
        }

        // Stranded check — we may have entered Engaging because the player was
        // already in combat (e.g. ambient mob aggro'd before Start was pressed).
        // The combat AI will kill it; once combat ends, we're far from the FATE
        // and need to resume normal travel rather than sit idle.
        var playerForStranded = _objectTable.LocalPlayer;
        if (playerForStranded != null && _targetFateRadius > 0)
        {
            var distToFate = Vector2.Distance(
                new Vector2(playerForStranded.Position.X, playerForStranded.Position.Z),
                new Vector2(_targetFatePos.X, _targetFatePos.Z));
            // 2× radius = generous "we're well outside the FATE area" check.
            if (distToFate > _targetFateRadius * 2 && !_condition[ConditionFlag.InCombat])
            {
                LogAction($"stranded from FATE ({distToFate:F0}y out, radius {_targetFateRadius:F0}) — back to Traveling");
                if (!_config.DryRun)
                {
                    if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
                    if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
                }
                // Go through Mounting so we re-mount + fly back to the FATE.
                Transition(FateBotState.Mounting);
                return;
            }
        }

        // Boss-FATE escape protection: a pull commit bypasses the per-mob FATE
        // radius gate (line ~2562), which means once we've committed to a mob
        // that wanders out, we'll chase it. In a boss FATE the boss follows
        // the tank — both end up slightly outside the radius, FATE level-sync
        // drops, damage stops registering, and the bot stands still trying to
        // hit something it can't affect. Fire earlier (1.05×) than the
        // stranded check (2×) so it kicks before we get pinned, and only
        // while a commit is actually active (no false-positive on legit
        // edge-of-radius patrols by a non-committed mob).
        if (_pullCommitId != 0
            && playerForStranded != null
            && _targetFateRadius > 0
            && _targetFatePos != Vector3.Zero)
        {
            var distOut = Vector2.Distance(
                new Vector2(playerForStranded.Position.X, playerForStranded.Position.Z),
                new Vector2(_targetFatePos.X, _targetFatePos.Z));
            if (distOut > _targetFateRadius * 1.05f)
            {
                // Cooldown so this doesn't refire every tick — restarting the
                // pathfind each frame prevents vnavmesh from making progress.
                if ((DateTime.UtcNow - _lastOutsideFateRecoveryAt).TotalSeconds < 15) return;
                _lastOutsideFateRecoveryAt = DateTime.UtcNow;
                LogAction($"outside FATE radius ({distOut:F0}/{_targetFateRadius:F0}y) with active pull commit — dropping commit, returning to centre");
                _pullCommitId = 0;
                _pullCommitSetAt = DateTime.MinValue;
                _targetManager.Target = null;
                if (_navmesh.IsAvailable)
                {
                    try { _navmesh.Stop(); } catch { }
                    try { _navmesh.PathfindAndMoveCloseTo(_targetFatePos, fly: true, range: _targetFateRadius * 0.4f); } catch { }
                }
                return;
            }
        }

        // Panic-escape: HP critically low AND Second Wind on cooldown → bail.
        if (_config.EnablePanicEscape && !_config.DryRun && CheckPanic(fate))
            return;

        // Step 0: dismount before fighting. vnavmesh leaves us airborne when
        // flight was used; many combat actions don't work mid-air, and BossMod
        // AI can't run a rotation while mounted. Force descent first.
        if (_condition[ConditionFlag.Mounted])
        {
            if (TryDismountOrRescue("pre-engage")) return; // wait for Conditions.Mounted to clear
        }

        // For Collect FATEs we deliberately keep BossMod active during WaitingForEnd
        // so FateUtils can hand in remaining items (~1 minute grace window).

        // Activate BossMod preset + (optionally) RSR. Preset variant depends on
        // CombatBackend: BossMod-driven combat needs job modules; RSR/Manual
        // strip them so the lean AI runs movement/target only.
        if (!_bossmodActivated)
        {
            var preset = BossModPresetData.ForBackend(_config.CombatBackend);
            if (_config.DryRun)
            {
                LogAction($"[DRY] would BossMod.Activate (backend={_config.CombatBackend})");
                if (_config.CombatBackend == Configuration.CombatBackendKind.RSR)
                    LogAction("[DRY] would RSR.Activate (Manual mode + TargetFreely override)");
            }
            else
            {
                LogAction($"BossMod.Activate (backend={_config.CombatBackend})");
                _bossmod.Activate(preset);

                // Defensive reset: a previous build's lazy-dodge bias could
                // have left NormalMovement.DelayMovement at "Short", which
                // makes BossMod sluggish about ALL reactive movement (not
                // just AOE dodge). Force it back to "None" on every activate.
                _bossmod.AddTransientStrategy(
                    "FateWalker - FATE",
                    "BossMod.Autorotation.MiscAI.NormalMovement",
                    "DelayMovement",
                    "None");

                // Override StayCloseToTarget range based on the player's job:
                // melee = OnHitbox (donut on hitbox surface so combo lands);
                // ranged = 25 y. See JobModuleMap.GetTargetRangeOption for
                // the rationale behind dropping the old numeric melee range
                // (it computed goal as "value + hitbox", letting big-hitbox
                // mobs leave the bot stuck at 6 y throwing ranged fillers).
                var player = _objectTable.LocalPlayer;
                if (player != null)
                {
                    var opt = JobModuleMap.GetTargetRangeOption(player.ClassJob.RowId);
                    LogAction($"BossMod.SetTargetRangeOption({opt}) for job id={player.ClassJob.RowId}");
                    _bossmod.SetTargetRangeOption(opt);
                }

                if (_config.CombatBackend == Configuration.CombatBackendKind.RSR)
                {
                    // Always Manual + TargetFreely. RSR casts on whatever
                    // target WE locked — keeps the pull-nearest + sticky
                    // commit honest. The old RsrUseAutoFarthest "wall-to-
                    // wall" mode let RSR pick its own farthest target,
                    // which fought our pick.
                    LogAction("RSR.Activate (Manual mode + TargetFreely)");
                    _rsr.Activate();
                    _rsrActivated = true;
                }

                // Whenever WE pick the target (FATE-only override), BossMod's
                // AutoTarget must NOT retarget away from our pick. NoTarget
                // lets it set an initial target on first pull, then steps back.
                // This is the dominant control regardless of which RSR mode.
                if (_config.RestrictTargetingToFateMobs)
                {
                    LogAction("BossMod AutoTarget Retarget=NoTarget (we own targeting)");
                    _bossmod.SetAutoTargetRetarget("NoTarget");
                    _currentRetargetMode = "NoTarget";
                }
            }
            _bossmodActivated = true;
        }

        // Re-pin player target to a FATE-matching mob if RSR/BossMod picked a
        // wandering aggressive (e.g. Il Mheg Anemones during a Morpho FATE).
        if (_config.RestrictTargetingToFateMobs && !_config.DryRun)
            EnforceFateMobTarget();

        // Terrain-stranded recovery: bot landed on a rock above the FATE area
        // and BossMod's NormalMovement can't pathfind down. If we have a FATE
        // target, are not in combat for a while, and the mob is reachably far,
        // kick vnavmesh to ground-walk us toward it.
        if (!_config.DryRun) KickIfStuckInEngaging();

        // Force-pull: defence/escort FATEs spawn mobs that aggro the NPC, not
        // the player. RSR/BossMod won't auto-cast on them because the player
        // isn't in combat. Manually fire a basic GCD to start the fight; the
        // configured rotation backend then takes over.
        if (!_config.DryRun) ForcePullIfStuck();

        // Lazy-dodge bias removed — DelayMovement in BossMod's NormalMovement
        // delays ALL reactive movement, not just AOE dodge. The side effect
        // was the bot ignoring nearby FATE mobs and standing in place after
        // each kill. Keep the function around for future per-action tuning
        // but don't apply by default. See ApplyLazyDodgeBias() doc.
    }

    /// <summary>
    /// Random humanize jump — fires GeneralAction.Jump (id 2) on a rolling
    /// 25–75 s cadence ONLY while the player has been continuously walking.
    /// Humans tap jump during long runs; an idle player standing still
    /// suddenly bouncing in place is strange. Skipped while mounted /
    /// flying / in combat / cutscene / loading. Also skipped when the
    /// player isn't really moving (haven't covered > 5 y in the last 4 s).
    /// </summary>
    private void TryHumanizeJump()
    {
        if (_config.DryRun) return;
        if (_condition[ConditionFlag.Mounted]) return;
        if (_condition[ConditionFlag.InFlight]) return;
        if (_condition[ConditionFlag.Diving]) return;
        if (_condition[ConditionFlag.InCombat]) return;
        if (_condition[ConditionFlag.OccupiedInCutSceneEvent]) return;
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]) return;

        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // Movement-gate: only count "walking" if we've covered at least 5 y
        // in the last ~4 s. Reset distance tracker every 4 s of sampling.
        if (DateTime.UtcNow - _jumpMoveSampleAt > TimeSpan.FromSeconds(4))
        {
            _jumpMoveSampleAt = DateTime.UtcNow;
            _jumpMoveSampleDist = Vector3.Distance(player.Position, _jumpMoveSamplePos);
            _jumpMoveSamplePos = player.Position;
        }
        if (_jumpMoveSampleDist < 5f) return;

        if (DateTime.UtcNow < _nextJumpAt)
        {
            if (_nextJumpAt == DateTime.MinValue)
                _nextJumpAt = DateTime.UtcNow + TimeSpan.FromSeconds(_rng.Next(25, 76));
            return;
        }

        try { _action.Jump(); LogAction("humanize: tap jump"); }
        catch (Exception ex) { _log.Warning(ex, "humanize jump failed"); }
        _nextJumpAt = DateTime.UtcNow + TimeSpan.FromSeconds(_rng.Next(25, 76));
    }

    // (altitude wobble removed in v1.0.0.16 — vnavmesh hooks the flight
    // input vector; keystate injection has no effect during pathing.)

    /// <summary>
    /// When the bot is within Dalamud's object-table streaming radius of the
    /// FATE, locate the actual entity we want to engage (the MotivationNpc
    /// for Preparing FATEs, otherwise the nearest hostile FATE-tagged mob)
    /// and retarget vnavmesh to land RIGHT NEXT to it. Removes the giveaway
    /// "dismount at consistent FATE-edge offset, walk 15y on foot" pattern
    /// — humans land next to the action. Triggers from 350 y out (object
    /// table is loaded by then) and retries while travelling closer if the
    /// first attempt found nothing.
    /// </summary>
    private void RefineLandingTarget(Vector3 playerPos)
    {
        if (_config.DryRun) return;
        if (_landingRefined) return;
        if (_targetFateId == 0) return;
        // 2D distance to FATE center; start trying at 350 y so we lock on
        // before reaching the 0.7 × radius "in range" check.
        var dist2D = Vector2.Distance(
            new Vector2(playerPos.X, playerPos.Z),
            new Vector2(_targetFatePos.X, _targetFatePos.Z));
        if (dist2D > 350f) return;

        Vector3? landAt = null;
        string label = "";

        if (_targetMotivationNpcId != 0)
        {
            foreach (var obj in _objectTable)
            {
                if (obj.EntityId == _targetMotivationNpcId)
                {
                    landAt = obj.Position;
                    label = $"NPC '{obj.Name.TextValue}'";
                    break;
                }
            }
        }
        else
        {
            unsafe
            {
                IBattleNpc? best = null;
                float bestDist = float.MaxValue;
                foreach (var obj in _objectTable)
                {
                    if (obj is not IBattleNpc npc) continue;
                    if (npc.ObjectKind != ObjectKind.BattleNpc) continue;
                    if (npc.IsDead) continue;
                    if (npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
                    if ((npc.StatusFlags & StatusFlags.Hostile) == 0) continue;
                    var go = (CSGameObject*)(void*)npc.Address;
                    if (go == null) continue;
                    if (go->FateId != _targetFateId) continue;
                    var d = Vector3.Distance(npc.Position, playerPos);
                    if (d < bestDist) { best = npc; bestDist = d; }
                }
                if (best != null)
                {
                    landAt = best.Position;
                    label = $"FATE mob '{best.Name.TextValue}'";
                }
            }
        }

        if (landAt == null) return;
        // Snap to nearest reachable mesh point — fixes Forlorn-on-cliff and
        // mob-on-unreachable-island edge cases where vnav would otherwise
        // path-fail. Halts on null → falls back to the raw entity position.
        var snapped = _navmesh.NearestPointReachable(landAt.Value, 8f, 8f);
        var dest = snapped ?? landAt.Value;
        try { _navmesh.PathfindAndMoveCloseTo(dest, fly: true, range: 3f); }
        catch (Exception ex) { _log.Warning(ex, "refine landing pathfind failed"); }
        _landingRefined = true;
        _refinedLandingPos = dest;
        var snapNote = snapped.HasValue ? " (snapped)" : "";
        LogAction($"refine landing: heading to {label} at ({dest.X:F0},{dest.Y:F0},{dest.Z:F0}){snapNote}");
    }

    /// <summary>
    /// True iff the player currently has the "Twist of Fate" status. The buff
    /// is applied after killing Forlorn Maiden / The Forlorn rare and adds
    /// +50% / +300% gem reward on the NEXT FATE in the same zone. While
    /// active the bot should chain aggressively (skip humanize delays, refuse
    /// zone rotation) to cash in.
    /// </summary>
    private bool IsTwistOfFateActive()
    {
        var p = _objectTable.LocalPlayer;
        if (p == null) return false;
        var statusId = _config.TwistOfFateStatusId;
        if (statusId == 0) return false;
        foreach (var s in p.StatusList)
        {
            if (s == null) continue;
            if (s.StatusId == statusId && s.RemainingTime > 0.5f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Rough estimate for the gil cost of a single in-zone aetheryte teleport
    /// (Lifestream picks the nearest aethernet for the player's current
    /// position). Used only for the session cost tracker — the game's actual
    /// invoice is read off the inventory but only after the cast. We log a
    /// conservative 200 g estimate per hop so the UI shows worst-case spend.
    /// </summary>
    private int EstimateInZoneTeleportCostGil() => 200;

    /// <summary>
    /// "Lazy dodge" humanize — when HP is comfortable (≥ 90 %), tell BossMod
    /// to delay reaction movement by a tick so the bot eats minor AOEs the
    /// way a confident human would. Drops back to instant dodge when HP
    /// falls below the comfort threshold. Implemented via the BossMod
    /// NormalMovement <c>DelayMovement</c> track (None / Short / Long), so
    /// the bot never STOPS dodging — it just reacts slower when safe.
    /// </summary>
    private void ApplyLazyDodgeBias()
    {
        if (_config.DryRun) return;
        if (!_bossmodActivated) return;
        var player = _objectTable.LocalPlayer;
        if (player == null || player.MaxHp == 0) return;
        int hpPct = (int)(100L * player.CurrentHp / player.MaxHp);

        string desired;
        if (hpPct >= 90)      desired = "Short";   // comfortable — coast a bit
        else if (hpPct >= 70) desired = "None";    // attentive
        else                  desired = "None";    // hurt — full attention to dodging

        if (desired == _currentMoveDelay) return;
        _currentMoveDelay = desired;
        try
        {
            _bossmod.AddTransientStrategy(
                "FateWalker - FATE",
                "BossMod.Autorotation.MiscAI.NormalMovement",
                "DelayMovement",
                desired);
            LogAction($"humanize: dodge delay → {desired} (HP {hpPct}%)");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "lazy-dodge bias failed");
        }
    }

    private void ForcePullIfStuck()
    {
        // Collect FATEs (bundle pickups + 10-stack hand-ins) want the bot to
        // walk to ground items, not pull mobs. BossMod's FateUtils handles
        // that flow but only while Player.InCombat is false — if we fire a
        // force-pull we kick combat on and Pickup goal turns off.
        if (IsCollectFate(_targetFateId)) return;

        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        if (_condition[ConditionFlag.Mounted]) return;
        if (_condition[ConditionFlag.InCombat]) { _lastEngagingCombatAt = DateTime.UtcNow; return; }
        var target = _targetManager.Target;
        if (target == null) return;
        // Only fire after we've been idle ≥ 6s — gives the rotation backend a
        // chance to start naturally first, and prevents spam between GCDs.
        if (DateTime.UtcNow - _lastEngagingCombatAt < TimeSpan.FromSeconds(6)) return;
        // Throttle ourselves to once per 3s (GCD recast is ~2.5s).
        if (DateTime.UtcNow - _lastForcePullAt < TimeSpan.FromSeconds(3)) return;
        _lastForcePullAt = DateTime.UtcNow;
        var actionId = JobPullSpellMap.Resolve(player.ClassJob.RowId);
        var ok = _action.UseAction(actionId);
        LogAction($"Engaging: force-pull → UseAction({actionId}) on {target.Name.TextValue} → {ok}");
    }

    /// <summary>
    /// Push a BossMod AutoTarget Retarget value only when it actually
    /// changes — avoids spamming the IPC every tick.
    /// </summary>
    private void EnsureRetargetMode(string mode)
    {
        if (_currentRetargetMode == mode) return;
        if (!_bossmodActivated) return;
        if (!_config.RestrictTargetingToFateMobs) return;
        try
        {
            _bossmod.SetAutoTargetRetarget(mode);
            _currentRetargetMode = mode;
            LogAction($"BossMod AutoTarget Retarget → {mode}");
        }
        catch (Exception ex) { _log.Warning(ex, $"SetAutoTargetRetarget({mode}) failed"); }
    }

    /// <summary>
    /// True if the given FATE id has an associated EventItem in Lumina —
    /// i.e. it's a "collect" FATE (pick items off the ground, turn in 10 at
    /// a time to the objective NPC). Result is cached per id.
    /// </summary>
    private bool IsCollectFate(uint fateId)
    {
        if (fateId == 0) return false;
        if (_collectFateCache.TryGetValue(fateId, out var cached)) return cached;
        bool result = false;
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>();
            if (sheet != null && sheet.TryGetRow(fateId, out var row))
                result = row.EventItem.RowId != 0;
        }
        catch { }
        _collectFateCache[fateId] = result;
        return result;
    }

    private void KickIfStuckInEngaging()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // Track in-combat so we don't kick during active fighting.
        if (_condition[ConditionFlag.InCombat])
        {
            _lastEngagingCombatAt = DateTime.UtcNow;
            return;
        }

        var target = _targetManager.Target;
        if (target == null) return;

        var dist = Vector3.Distance(target.Position, player.Position);
        var dy = target.Position.Y - player.Position.Y;
        bool yMismatch = Math.Abs(dy) > 5f;

        // BossMod's StayCloseToTarget reports "in range" based on XZ distance
        // + hitbox surface, so a mob that's 3 y away horizontally but 8 y
        // below us reads as "close enough" — bot stands on the ledge
        // glaring at the mob, never attacks. Detect that case here and
        // re-pathfind regardless of the 6 y horizontal cutoff.
        if (!yMismatch && dist < 6f) return;

        // Need ≥ 8s of "out of combat with a far FATE target" before kicking,
        // and only retry every 5s so we don't fight BossMod's own movement.
        if (DateTime.UtcNow - _lastEngagingCombatAt < TimeSpan.FromSeconds(8)) return;
        if (DateTime.UtcNow - _lastEngagingKickAt   < TimeSpan.FromSeconds(5)) return;
        _lastEngagingKickAt = DateTime.UtcNow;

        // Y-mismatch escape: mob is on a different terrain layer. Ground
        // pathing can't always get there (vnav stops at cliff edges, etc.).
        // Mount → take off → fly path → land near mob.
        if (yMismatch)
        {
            if (!_condition[ConditionFlag.Mounted])
            {
                LogAction($"Engaging: target {dy:+0.0;-0.0}y vertical offset — mounting to fly across");
                _action.UseMountRoulette();
                return;
            }
            if (!_condition[ConditionFlag.InFlight])
            {
                LogAction("Engaging: mounted, jumping to take off");
                _action.Jump();
                return;
            }
            LogAction($"Engaging: fly-path to {target.Name.TextValue} (dy={dy:+0.0;-0.0}, dist={dist:F1}y)");
            _navmesh.PathfindAndMoveCloseTo(target.Position, fly: true, range: 3f);
            return;
        }

        // Same-level but far away → ground walk. Dismount handler will land
        // us when navmesh stops near the mob.
        if (_condition[ConditionFlag.Mounted]) return; // dismount handler in charge
        // Sprint covers ~10-30y of the walk; cheap to fire and the cooldown
        // (60s) is harmless if we don't use the speed boost. UseSprint returns
        // false on cooldown so no spam.
        if (_action.UseSprint()) LogAction("Engaging: Sprint fired for ground walk");
        LogAction($"Engaging: stranded ({dist:F1}y from target, OOC) — kick vnavmesh ground-walk to {target.Name.TextValue}");
        // Snap target to a reachable mesh point — guards against the mob
        // standing on a small ledge/pile that vnav can't path directly to.
        var snapped = _navmesh.NearestPointReachable(target.Position, 6f, 6f);
        var dest = snapped ?? target.Position;
        _navmesh.PathfindAndMoveCloseTo(dest, fly: false, range: 3f);
    }

    /// <summary>
    /// HP-based bail-out. Returns true if we triggered a panic-escape this tick
    /// (caller should skip the rest of TickEngaging — we've already transitioned).
    /// Condition: HP% &lt; threshold AND Second Wind not ready. We deactivate combat
    /// AI and walk outside the FATE radius so level sync drops; Recovering then
    /// gates the resume on HP regen.
    /// </summary>
    // Diagnostic — periodic HP log so we can prove the panic check is running
    // even when it returns false.
    private DateTime _lastHpLogAt = DateTime.MinValue;
    private int _lastHpLogPct = 100;
    // Watchdog stuck-event de-dup: increments to next tier (5/15/30s) only
    // once per continuous stuck period. Resets when the player moves >2y.
    private int _stuckTierLogged;   // 0 = none, 1 = 5s logged, 2 = 15s, 3 = 30s

    // Cooldown for the 30s "stuck + in combat — flee before teleport" action.
    // Without this gate the action fires every framework tick (~60/sec) once
    // stillSec crosses 30s, racking up dozens of identical-fingerprint log
    // events that trip the logic-loop watchdog and falsely session-disable the
    // current FATE. With it, the flee can re-arm only after a real cooldown.
    private DateTime _lastInCombatFleeAt = DateTime.MinValue;

    // Same cooldown idea, but for the 15s "cancel path + re-pathfind" recovery.
    // Without it, that LogAction + Pathfind pair fires every framework tick
    // (~60/sec) for as long as the stuck condition persists. Two clients hit
    // this together when the party formation point landed slightly off-mesh and
    // both bots burned dozens of fingerprints in seconds → logic-loop trip →
    // FATE wrongly session-disabled. 20s lets a re-pathfind actually take
    // effect before we issue another one.
    private DateTime _lastRepathfindAt = DateTime.MinValue;

    // Collect-FATE pickup is delegated to BossMod's FateUtils ("Pickup" goal)
    // via Hints.InteractWithTarget — we just clear targets so the AI can drive.
    // When FateUtils stalls or doesn't engage (e.g. items spawn outside its
    // line-of-sight) the bot stands still indefinitely. This timer drives a
    // periodic active nudge — pathfind to a random point inside the FATE
    // radius — so BossMod has fresh stimulus and the bot doesn't camp one spot.
    private DateTime _lastCollectNudgeAt = DateTime.MinValue;

    // Cooldown on the v1.4.2 boss-FATE-escape action. Without it the
    // "outside FATE radius … dropping commit, returning to centre" recovery
    // fires every framework tick once the player is 1.05× past the radius;
    // each fire calls _navmesh.PathfindAndMoveCloseTo which RESTARTS the
    // pathfind, so the bot never actually moves while the log fills with
    // identical fingerprints that trip the logic-loop watchdog and wrongly
    // session-disable the FATE.
    private DateTime _lastOutsideFateRecoveryAt = DateTime.MinValue;

    // Anti-detection: timer that fires a forced random zone rotation even when
    // current zone still has FATEs. DateTime.MaxValue = disabled / not yet
    // rolled. Rolled fresh on Start() and after each fire.
    private DateTime _nextRandomRotateAt = DateTime.MaxValue;

    private bool CheckPanic(IFate fate)
    {
        if (_panicked) return false; // already bailing
        var player = _objectTable.LocalPlayer;
        if (player == null || player.MaxHp == 0) return false;

        int hpPct = (int)(100L * player.CurrentHp / player.MaxHp);

        // Diagnostic: only log when HP enters a region of interest. Healthy
        // ticks (HP > 70%) are silent — they spam the 40-line log ring with
        // no useful signal. Below 70% we log on first crossing + every 5s of
        // continued danger so the tester can see HP descending into panic.
        bool inDanger = hpPct < 70;
        bool firstDangerCrossing = inDanger && _lastHpLogPct >= 70;
        bool firstRecovery       = !inDanger && _lastHpLogPct < 70 && _lastHpLogPct > 0;
        bool periodicDangerHeartbeat = inDanger
            && DateTime.UtcNow - _lastHpLogAt > TimeSpan.FromSeconds(5);
        if (firstDangerCrossing || firstRecovery || periodicDangerHeartbeat)
        {
            _lastHpLogAt = DateTime.UtcNow;
            _lastHpLogPct = hpPct;
            var arrow = firstDangerCrossing ? "↓" : firstRecovery ? "↑ recovered" : "";
            LogAction($"HP {hpPct}% {arrow} (panic threshold {_config.PanicHpPercent}%)");
        }
        else
        {
            _lastHpLogPct = hpPct;
        }

        if (hpPct >= _config.PanicHpPercent) return false;

        _sessionPanicEscapes++;
        LogAction($"PANIC: HP {hpPct}% < {_config.PanicHpPercent}% — bailing out of FATE (session panics={_sessionPanicEscapes})");
        _panicked = true;

        // Stop combat AI immediately.
        if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
        if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }

        // Sprint as we bolt — every second of aggro counts.
        if (_action.UseSprint()) LogAction("panic: Sprint used");

        // Path to a point just outside the FATE radius so level sync drops and
        // we leave combat (mobs return). vnavmesh ground-walks (fly = false) so
        // we don't waste time mounting.
        var dir = player.Position - fate.Position;
        if (dir.LengthSquared() < 0.01f) dir = new Vector3(1, 0, 0);
        dir = Vector3.Normalize(dir);
        var escape = fate.Position + dir * (fate.Radius + 25f);
        _navmesh.PathfindAndMoveCloseTo(escape, fly: false, range: 3f);

        Transition(FateBotState.Recovering);
        return true;
    }

    private TimeSpan _nextTargetingDelay = TimeSpan.FromMilliseconds(500);

    private unsafe void EnforceFateMobTarget()
    {
        if (DateTime.UtcNow - _lastFateTargetAt < _nextTargetingDelay) return;
        _lastFateTargetAt = DateTime.UtcNow;
        // Roll the NEXT delay so each retarget interval is different
        // (300–1500ms by default, humanlike reaction time).
        if (_config.EnableHumanize && _config.TargetingDelayMaxMs > _config.TargetingDelayMinMs)
            _nextTargetingDelay = TimeSpan.FromMilliseconds(
                _rng.Next(_config.TargetingDelayMinMs, _config.TargetingDelayMaxMs + 1));
        else
            _nextTargetingDelay = TimeSpan.FromMilliseconds(500);

        if (_targetFateId == 0) return;
        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        var playerId = player.GameObjectId;

        // Collect-FATE branch: don't pull. The objective is to pick items
        // off the ground and turn in 10 at a time. We just clear any pulled
        // target so BossMod's FateUtils (Pickup/HandIn goals) can drive the
        // movement via Hints.InteractWithTarget. We only re-engage if a mob
        // happens to aggro on us — in which case the targeting loop below
        // still finds it and locks in aggro/kill mode.
        bool isCollect = IsCollectFate(_targetFateId);

        // Battalion = friend/enemy team. Player and allies share the same value;
        // hostile mobs use a different one. Used as the strict ally-exclusion
        // gate below in addition to the StatusFlags.Hostile check (some defence
        // FATE escort NPCs report Hostile=true while still being on our side).
        byte playerBattalion = 0;
        var playerCharaPtr = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)(void*)player.Address;
        if (playerCharaPtr != null) playerBattalion = playerCharaPtr->Battalion;

        // Walk the object table once, partitioning FATE mobs into aggro'd-on-us
        // vs the rest. We treat "TargetObjectId == playerId" as the aggro signal —
        // accurate enough for FATE trash, which switches target the moment we
        // pull.
        var aggro = new List<(IBattleNpc npc, float dist)>();
        var unaggro = new List<(IBattleNpc npc, float dist)>();
        // Forlorn / Forlorn Maiden — special aggressive elites that spawn at
        // ~50 % FATE progress and drop the "Twist of Fate" bonus-gemstone
        // buff (Maiden = +25 %, The Forlorn rare = +300 %). NameIDs come
        // from BossMod's AutoTarget (which also prioritises them).
        // Pre-scan: if one is in the object table for our active FATE, lock
        // onto it BEFORE the normal pull/kill logic — bypassing the radius
        // filter and distance pick so we don't end up chasing trash while
        // a Forlorn rots untouched at the FATE edge.
        const uint NameId_Forlorn = 6737;
        const uint NameId_ForlornMaiden = 6738;
        IBattleNpc? forlorn = null;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            if (npc.ObjectKind != ObjectKind.BattleNpc) continue;
            if (npc.IsDead) continue;
            if (npc.NameId != NameId_Forlorn && npc.NameId != NameId_ForlornMaiden) continue;
            var go = (CSGameObject*)(void*)npc.Address;
            if (go == null || go->FateId != _targetFateId) continue;
            forlorn = npc;
            break;
        }
        if (forlorn != null)
        {
            // Direct lock — committed for the rest of this Forlorn's life.
            if (_pullCommitId != forlorn.GameObjectId)
            {
                _pullCommitId = forlorn.GameObjectId;
                _pullCommitSetAt = DateTime.UtcNow;
                _killPhaseLatch = true;  // bypass the "pull more first" logic
                if (_targetManager.Target?.GameObjectId != forlorn.GameObjectId)
                    _targetManager.Target = forlorn;
                LogAction($"FATE-target [forlorn priority] → {forlorn.Name.TextValue} (NameId={forlorn.NameId}) — +Twist of Fate gem bonus");
            }
            return;
        }

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            if (npc.ObjectKind != ObjectKind.BattleNpc) continue;
            if (npc.IsDead) continue;
            // Exclude pets / chocobos / parts. Keep IsTargetable OUT of the
            // filter — defence FATEs / scatter FATEs spawn mobs that are
            // briefly untargetable during the spawn animation; we still want
            // to walk toward them and engage when they become targetable.
            if (npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            // Hostile-only — FATE start/escort NPCs (e.g. Herb-picking Maiden
            // that opens Preparing-FATEs) also get Combatant subkind + FateId
            // tag but are FRIENDLY. StatusFlags.Hostile is the distinguisher.
            if ((npc.StatusFlags & StatusFlags.Hostile) == 0) continue;
            // Strict ally exclusion: a defence-FATE escort NPC has the same
            // Battalion as the player and CAN be flagged Hostile during the
            // FATE (game uses Hostile=true to mean "engaged in combat", not
            // "enemy of the player"). Skip anything sharing our Battalion.
            var charaPtr = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)(void*)npc.Address;
            if (charaPtr != null && charaPtr->Battalion == playerBattalion) continue;
            var go = (CSGameObject*)(void*)npc.Address;
            // Game stamps non-zero FateId only on FATE-spawned actors
            // (see reference_fate_mob_detection.md). FateId==0 → ambient.
            if (go == null) continue;
            if (go->FateId == 0 || go->FateId != _targetFateId) continue;

            // Position defence: ignore mobs WAY outside the FATE zone. Use
            // radius × 1.5 as a buffer — defence FATEs have mobs walking IN
            // from the periphery that legitimately sit outside the strict
            // radius for a few seconds.
            //
            // Exemption: the mob we ALREADY committed to (_pullCommitId) is
            // always allowed through. Otherwise when we chase a wandering
            // pull target out past 1.5 × radius, the filter drops it, the
            // commit dies, and the next tick picks a different mob —
            // exactly the "target keeps swapping mid-pull" tester report.
            if (_targetFateRadius > 0 && npc.GameObjectId != _pullCommitId)
            {
                var fateDist2D = Vector2.Distance(
                    new Vector2(npc.Position.X, npc.Position.Z),
                    new Vector2(_targetFatePos.X, _targetFatePos.Z));
                if (fateDist2D > _targetFateRadius * 1.5f) continue;
            }

            var dist = Vector3.Distance(npc.Position, player.Position);
            if (npc.TargetObjectId == playerId) aggro.Add((npc, dist));
            else                                 unaggro.Add((npc, dist));
        }

        // Phase decision:
        //   • aggro count >= MaxAggroCount  → CLEAR (stick to closest aggro'd mob; ignore new pulls)
        //   • aggro count > 0 && < max     → KILL CURRENT (closest aggro'd; only pull more if RSR-farthest mode wants to chain)
        //   • aggro count == 0             → PULL (farthest if RsrUseAutoFarthest, else nearest unaggro'd)
        var current = _targetManager.Target;
        var currentGo = current != null ? (CSGameObject*)(void*)current.Address : null;
        bool currentIsFateMob = currentGo != null && currentGo->FateId == _targetFateId;

        IBattleNpc? pick = null;
        string mode;
        bool inClearMode = aggro.Count >= _config.MaxAggroCount;

        // Unified commit: if we already chose a target on a previous tick and
        // it's still alive (in either list), KEEP IT — regardless of mode —
        // until it dies/vanishes. This stops the rapid swap that happens when
        // a mob briefly retargets the chocobo and flickers between aggro and
        // unaggro lists.
        //
        // Exception: in clear-mode (we have ≥ MaxAggroCount on us) we MUST
        // focus an aggro'd mob — otherwise we keep walking toward an unaggro
        // pull target while getting hit from multiple sides. Drop the commit
        // in that case and pick the closest aggro'd.
        bool commitStillValid = false;
        IBattleNpc? committedMob = null;
        bool commitIsAggro = false;
        if (_pullCommitId != 0)
        {
            var agHit = aggro.FirstOrDefault(x => x.npc.GameObjectId == _pullCommitId);
            if (agHit.npc != null) { committedMob = agHit.npc; commitIsAggro = true; commitStillValid = true; }
            else
            {
                var unHit = unaggro.FirstOrDefault(x => x.npc.GameObjectId == _pullCommitId);
                if (unHit.npc != null) { committedMob = unHit.npc; commitStillValid = true; }
            }
            if (!commitStillValid)
            {
                // Grace period: hold the commit for up to 4 s of "mob not
                // visible" before giving up. Brief object-table churn
                // shouldn't be enough to flip our pull target.
                var commitAge = DateTime.UtcNow - _pullCommitSetAt;
                if (commitAge < TimeSpan.FromSeconds(4))
                {
                    // Stay committed but no live mob ref this tick — just
                    // return so we keep walking toward the last known
                    // direction without picking a new mob.
                    return;
                }
                LogAction($"pull commit dropped: target {_pullCommitId} not visible after {commitAge.TotalSeconds:F1}s grace");
                _pullCommitId = 0;
            }
        }

        // Two-phase pull-then-kill with a KILL latch:
        //   PULL: aggro count below MaxAggroCount → keep picking up more
        //         mobs. Commit to one until it aggros, then immediately
        //         start the next pull.
        //   KILL: we've either filled the pull size or run out of unaggro
        //         targets. Stays locked here until aggro hits 0 (whole
        //         batch dead) — otherwise the moment one mob dies the
        //         bot would dash off to grab a new pull and the rest of
        //         the batch would leash + regen on the way back.
        if (aggro.Count >= _config.MaxAggroCount)
        {
            _killPhaseLatch = true;
            _killPhaseAggroLossAt = DateTime.MinValue;
        }
        if (aggro.Count == 0)
        {
            // Only release the latch after 3 s of sustained aggro=0. A
            // single-tick flicker (mob briefly retargets chocobo etc.)
            // would otherwise drop the kill-batch latch mid-fight and let
            // the bot dash off after a fresh pull.
            if (_killPhaseLatch)
            {
                if (_killPhaseAggroLossAt == DateTime.MinValue)
                    _killPhaseAggroLossAt = DateTime.UtcNow;
                else if (DateTime.UtcNow - _killPhaseAggroLossAt > TimeSpan.FromSeconds(3))
                {
                    _killPhaseLatch = false;
                    _killPhaseAggroLossAt = DateTime.MinValue;
                }
            }
        }
        else
        {
            // Any aggro at all → forget the loss timer, the batch is alive.
            _killPhaseAggroLossAt = DateTime.MinValue;
        }

        // On a collect FATE we never *pull* — the goal is items, not kills.
        // We still focus and kill anything that already aggro'd on us
        // (defensive). When nothing is aggro'd we early-out so FateUtils'
        // Pickup goal can take over movement.
        bool stillPulling = !isCollect
                         && !_killPhaseLatch
                         && aggro.Count < _config.MaxAggroCount
                         && unaggro.Count > 0;
        if (isCollect && aggro.Count == 0)
        {
            // Drop any leftover commit and let FateUtils drive.
            if (_pullCommitId != 0)
            {
                LogAction("collect-FATE: clearing pull commit so FateUtils can pick up items");
                _pullCommitId = 0;
                _pullCommitSetAt = DateTime.MinValue;
            }
            // Tell BossMod's AutoTarget to NEVER auto-pick a target. Default
            // "NoTarget" lets BossMod set an initial target — which here
            // means it grabs a nearby mob the instant we clear ours, and
            // RSR ranged-casts on it from where we're standing (visible as
            // "bot keeps shooting mobs while supposedly picking stuff up").
            // Switch back to "NoTarget" only when we need to fight.
            EnsureRetargetMode("Never");
            // ALSO clear the hard target every tick we're in pickup, in case
            // anything (Maiden pre-scan, manual click, etc.) sets it again.
            if (_targetManager.Target != null)
            {
                LogAction($"collect-FATE: clearing stale target '{_targetManager.Target.Name.TextValue}' so pickup can run");
                _targetManager.Target = null;
            }

            // Stuck-nudge: if FateUtils hasn't moved us in 20s+, fire a fresh
            // pathfind to a random point inside the FATE radius. Throttled to
            // once per ~25s so we don't dogpile vnavmesh.
            var stillSec = (DateTime.UtcNow - _genericLastMoveAt).TotalSeconds;
            var sinceNudge = (DateTime.UtcNow - _lastCollectNudgeAt).TotalSeconds;
            if (stillSec > 20 && sinceNudge > 25 && _navmesh.IsAvailable && _targetFatePos != Vector3.Zero)
            {
                _lastCollectNudgeAt = DateTime.UtcNow;
                // Pull radius from the live IFate when available; fall back to
                // a conservative 18y if the FATE isn't in the table this tick.
                var fateRow = _fateTable.FirstOrDefault(f => f.FateId == _targetFateId);
                float radius = fateRow != null ? fateRow.Radius * 0.6f : 18f;
                if (radius < 6f) radius = 6f;
                var angle = (float)(_rng.NextDouble() * Math.Tau);
                var r = radius * (0.4f + (float)_rng.NextDouble() * 0.6f);
                var nudgePos = new Vector3(
                    _targetFatePos.X + r * MathF.Cos(angle),
                    _targetFatePos.Y,
                    _targetFatePos.Z + r * MathF.Sin(angle));
                var snap = _navmesh.NearestPointReachable(nudgePos, halfExtentXZ: 6f, halfExtentY: 4f);
                if (snap.HasValue) nudgePos = snap.Value;
                LogAction($"collect-FATE: stuck {stillSec:F0}s, nudging to ({nudgePos.X:F0},{nudgePos.Y:F0},{nudgePos.Z:F0}) to wake FateUtils");
                try { _navmesh.Stop(); } catch { }
                try { _navmesh.PathfindAndMoveCloseTo(nudgePos, fly: true, range: 2f); } catch { }
            }
            return;
        }

        // Combat path on this FATE → re-enable BossMod's initial auto-pick
        // so the rotation backend has something to cast on while we fight.
        EnsureRetargetMode("NoTarget");

        // Special case: committed mob just aggro'd while we still want more
        // pulls. Drop the commit so we can pick the next unaggro target.
        if (commitStillValid && stillPulling && commitIsAggro)
        {
            _pullCommitId = 0;
            _pullCommitSetAt = DateTime.MinValue;
            commitStillValid = false;
            committedMob = null;
            commitIsAggro = false;
        }

        // Clear-mode safety (too many aggro on us): focus aggro'd over an
        // unaggro commit, even mid-pull. Otherwise we keep wandering toward
        // a new pull while getting clobbered.
        if (commitStillValid && inClearMode && !commitIsAggro)
        {
            _pullCommitId = 0;
            commitStillValid = false;
            committedMob = null;
        }

        // Party Mode: if I'm a DPS / healer and there's a tank in the party
        // with a live target, focus-fire that mob — overrides any local commit
        // so the party converges on one target at a time instead of spreading
        // damage. Tank's own target picking is unchanged. Skipped if the tank's
        // target is dead, not loaded in our object table, or is not a battle NPC.
        IBattleNpc? tankFocus = null;
        if (_config.PartyDpsFollowTank
            && _party.Role != Controller.Party.PartyCoordinator.EffectiveRole.Off
            && !IsCurrentJobTank())
        {
            tankFocus = FindMobAggroedOnTank();
        }

        if (tankFocus != null)
        {
            pick = tankFocus;
            mode = "assist tank (aggro'd on tank)";
            _pullCommitId = pick.GameObjectId;
            _pullCommitSetAt = DateTime.UtcNow;
        }
        else if (commitStillValid)
        {
            pick = committedMob;
            mode = commitIsAggro ? "kill (commit)" : "pull (commit)";
        }
        else if (stillPulling)
        {
            pick = unaggro.OrderBy(x => x.dist).First().npc;
            mode = $"pull nearest ({aggro.Count}/{_config.MaxAggroCount} aggro)";
            _pullCommitId = pick.GameObjectId;
            _pullCommitSetAt = DateTime.UtcNow;
        }
        else if (aggro.Count > 0)
        {
            pick = aggro.OrderBy(x => x.dist).First().npc;
            mode = inClearMode
                ? $"clear ({aggro.Count}/{_config.MaxAggroCount})"
                : $"kill ({aggro.Count} aggro)";
            _pullCommitId = pick.GameObjectId;
            _pullCommitSetAt = DateTime.UtcNow;
        }
        else
        {
            // Nothing aggro'd AND nothing to pull → idle.
            if (unaggro.Count == 0)
            {
                if (DateTime.UtcNow - _lastNoFateMobLogAt > TimeSpan.FromSeconds(10))
                {
                    _lastNoFateMobLogAt = DateTime.UtcNow;
                    LogAction($"no FATE mobs visible (fateId={_targetFateId}, radius={_targetFateRadius:F0}y) — standing by");
                }
                return;
            }
            return;
        }

        // Compare by GameObjectId — ITargetManager.Target wrappers may not be
        // reference-stable across calls, so ReferenceEquals returns false for
        // the same underlying mob and spams the log.
        ulong currentObjId = current?.GameObjectId ?? 0;
        if (pick != null && pick.GameObjectId != currentObjId)
        {
            _targetManager.Target = pick;
            var pickGo = (CSGameObject*)(void*)pick.Address;
            ushort pickFateId = pickGo != null ? pickGo->FateId : (ushort)0;
            uint pickNameId = pick.NameId;
            LogAction($"FATE-target [{mode}] → {pick.Name.TextValue} (NameId={pickNameId}, FateId={pickFateId}, active={_targetFateId})");
        }
    }

    /// <summary>
    /// Enter the Paused state for <paramref name="minutes"/> with a stated reason.
    /// Deactivates combat AI immediately; resumes to Selecting after the timer.
    /// </summary>
    private void EnterPause(int minutes, string reason, bool resetSessionTimer)
    {
        if (minutes <= 0) { Stop(); return; }
        _pauseEndsAt = DateTime.UtcNow.AddMinutes(minutes);
        _pauseReason = reason;
        _pauseResetSessionTimer = resetSessionTimer;
        LogAction($"PAUSE for {minutes} min — {reason}");

        if (!_config.DryRun)
        {
            _navmesh.Stop();
            if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
            if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
        }
        Transition(FateBotState.Paused);
    }

    /// <summary>
    /// Safer wrapper around <see cref="EnterPause"/>: instead of pausing in
    /// place (often mid-FATE, in mob aggro radius), route through
    /// <see cref="FateBotState.PreparingPause"/> which flees combat and
    /// teleports to the zone's primary aetheryte before settling into the
    /// actual Pause. Prevents the death loop seen during 2h session-cap
    /// macro-breaks when a tester was still being attacked.
    /// </summary>
    private void EnterPauseSafely(int minutes, string reason, bool resetSessionTimer)
    {
        if (minutes <= 0) { Stop(); return; }
        _pendingPauseMinutes = minutes;
        _pendingPauseReason = reason;
        _pendingPauseResetTimer = resetSessionTimer;
        _preparePauseTeleportFired = false;
        _lastPreparePauseFleeAt = DateTime.MinValue;
        _lastPreparePauseTpAt = DateTime.MinValue;
        LogAction($"PreparingPause: {minutes}m — {reason} (flee + teleport to home aetheryte before pausing)");
        if (!_config.DryRun)
        {
            if (_rsrActivated)     { try { _rsr.Deactivate();     } catch {} _rsrActivated = false; }
            if (_bossmodActivated) { try { _bossmod.Deactivate(); } catch {} _bossmodActivated = false; }
        }
        Transition(FateBotState.PreparingPause);
    }

    private void TickPreparingPause()
    {
        // Hard timeout — never spend more than 2 min on prep; if we genuinely
        // can't escape (terrain stuck, infinite respawn), just pause in place
        // rather than blocking forever. The death loop fix handles the
        // worst-case fallout if we die during the pause anyway.
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromMinutes(2))
        {
            LogAction("PreparingPause: timeout (2m) — pausing in place");
            EnterPause(_pendingPauseMinutes, _pendingPauseReason, _pendingPauseResetTimer);
            return;
        }

        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]) return;

        // Step 1: if we're still in combat, run away from the closest hostile
        // until aggro drops. Reuse the same direction-away vector we built for
        // Repairing — Lifestream.Teleport returns false while InCombat, so we
        // have to clear aggro before step 2.
        if (_condition[ConditionFlag.InCombat])
        {
            if (DateTime.UtcNow - _lastPreparePauseFleeAt < TimeSpan.FromSeconds(4)) return;
            _lastPreparePauseFleeAt = DateTime.UtcNow;
            IGameObject? threat = null;
            float threatDist = float.MaxValue;
            var playerId = player.GameObjectId;
            foreach (var obj in _objectTable)
            {
                if (obj is not IBattleNpc npc) continue;
                if ((npc.StatusFlags & StatusFlags.Hostile) == 0) continue;
                if (npc.IsDead) continue;
                var d = Vector3.Distance(npc.Position, player.Position);
                bool aggrod = npc.TargetObjectId == playerId;
                if (aggrod && (threat == null || d < threatDist)) { threat = npc; threatDist = d; }
                else if (threat == null && d < threatDist) { threat = npc; threatDist = d; }
            }
            if (threat != null)
            {
                var awayX = player.Position.X - threat.Position.X;
                var awayZ = player.Position.Z - threat.Position.Z;
                var len = MathF.Sqrt(awayX * awayX + awayZ * awayZ);
                if (len < 0.001f) { awayX = 1; awayZ = 0; len = 1; }
                var dst = new Vector3(
                    player.Position.X + (awayX / len) * 80f,
                    player.Position.Y,
                    player.Position.Z + (awayZ / len) * 80f);
                try { _navmesh.PathfindAndMoveCloseTo(dst, fly: false, range: 1f); } catch {}
                // Log only when the threat changes or every 15s — keeps the
                // ring buffer readable when the same mob clings for 30+ seconds.
                var threatName = threat.Name.TextValue;
                if (threatName != _lastFleeThreatName
                    || DateTime.UtcNow - _lastFleeLogAt > TimeSpan.FromSeconds(15))
                {
                    _lastFleeThreatName = threatName;
                    _lastFleeLogAt = DateTime.UtcNow;
                    LogAction($"PreparingPause: flee — {threatName} aggroed @ {threatDist:F1}y, running 80y");
                }
            }
            return;
        }

        // Step 2: out of combat — teleport to the current zone's primary
        // aetheryte. Aetheryte plazas are safe AFK spots (no hostile mobs in
        // range) and match the "macro-break = return to city" reference
        // pattern from reference_bot_safety_detection.md.
        if (!_preparePauseTeleportFired && _lifestream.IsAvailable)
        {
            // Skip teleport if we're already very close to the aetheryte
            // (Lifestream.GetActiveAetheryte != 0 means we're standing on it).
            if (DateTime.UtcNow - _lastPreparePauseTpAt < TimeSpan.FromSeconds(5)) return;
            if (_lifestream.IsBusy) return;
            _lastPreparePauseTpAt = DateTime.UtcNow;
            var info = TerritoryMap.Lookup(_clientState.TerritoryType);
            if (info != null && info.AetheryteId != 0)
            {
                TryCloseShopExchangeCurrency();
                var ok = _lifestream.Teleport(info.AetheryteId, 0);
                LogAction($"PreparingPause: Lifestream.Teleport(aetheryte={info.AetheryteId}, {info.ZoneName}) → {ok}");
                if (ok) _preparePauseTeleportFired = true;
            }
            else
            {
                LogAction("PreparingPause: no primary aetheryte known for current zone — pausing in place");
                EnterPause(_pendingPauseMinutes, _pendingPauseReason, _pendingPauseResetTimer);
            }
            return;
        }

        // Step 3: teleport fired — wait for the loading screen to finish
        // (BetweenAreas already gated above). Then commit to the pause.
        if (_preparePauseTeleportFired)
        {
            // Give one second after territory stabilises for the player to
            // actually land at the aetheryte before we lock into Paused.
            if (DateTime.UtcNow - _stateEnteredAt < TimeSpan.FromSeconds(2)) return;
            EnterPause(_pendingPauseMinutes, _pendingPauseReason, _pendingPauseResetTimer);
            return;
        }

        // Lifestream unavailable — pause in place as fallback.
        EnterPause(_pendingPauseMinutes, _pendingPauseReason, _pendingPauseResetTimer);
    }

    private void TickPaused()
    {
        // Idle humanize: send /afk once on entry, then random /stretch or /sit
        // every 4-9 min while paused. A bot standing motionless for 30 min in
        // an aetheryte plaza is a strong report signal; a player typing /afk
        // and occasionally emoting is invisible.
        if (_config.EnablePausedIdleBehavior && !_config.DryRun)
        {
            if (!_pausedAfkSent)
            {
                _pausedAfkSent = true;
                _action.ExecuteChatCommand("/afk");
                LogAction("Paused: sent /afk");
                _nextIdleEmoteAt = DateTime.UtcNow.AddMinutes(_rng.Next(4, 10));
            }
            else if (DateTime.UtcNow >= _nextIdleEmoteAt)
            {
                // One-shot animations ONLY — no stateful pose-changing emotes.
                // /sit (was here) put the character in a sitting pose; when
                // the pause ended the bot would try to mount/move while still
                // sitting and silently stall. Toggle-emotes are unsafe.
                var pool = new[] { "/stretch", "/lookout", "/yawn", "/doze", "/lookout2" };
                var pick = pool[_rng.Next(pool.Length)];
                _action.ExecuteChatCommand(pick);
                LogAction($"Paused: idle emote {pick}");
                _nextIdleEmoteAt = DateTime.UtcNow.AddMinutes(_rng.Next(4, 10));
            }
        }

        if (DateTime.UtcNow < _pauseEndsAt) return;

        // Timer elapsed — resume. Defensive stand-up: if any prior emote
        // (or the user themselves before Pause started) left the character
        // in a sitting / doze / sleep pose, a Jump force-stands them.
        // Mount / move actions silently fail while seated, which previously
        // caused a hard stall ("stuck in sitting pose" tester report).
        if (_config.EnablePausedIdleBehavior && !_config.DryRun)
        {
            _action.Jump();
            LogAction("Paused: resume — Jump to force stand-up");
        }
        _pausedAfkSent = false;
        if (_pauseResetSessionTimer)
        {
            _sessionStartedAt = DateTime.UtcNow;
            _sessionCapHoursRolled = RollSessionCapHours(); // fresh roll for the new chunk
            RollNextRandomRotate();                          // fresh cadence for the new chunk
            LogAction($"resume after pause ({_pauseReason}) — next cap rolled to {_sessionCapHoursRolled:F1}h");
        }
        else
        {
            LogAction($"resume after pause ({_pauseReason})");
        }
        _pauseReason = "";
        Transition(FateBotState.Selecting);
    }

    /// <summary>
    /// Roll the per-session "actual" cap in hours: base ± Jitter (uniform).
    /// Returning 0 disables the cap. Jitter is clipped so we never roll
    /// below 0.5 h to avoid pathological tight loops.
    /// </summary>
    private double RollSessionCapHours()
    {
        if (_config.SessionCapHours <= 0) return 0;
        var baseH = (double)_config.SessionCapHours;
        var jitter = Math.Max(0, _config.SessionCapHoursJitter);
        if (jitter <= 0) return baseH;
        // _rng.NextDouble() is [0,1); shift to [-1,1) then scale.
        var offset = (_rng.NextDouble() * 2.0 - 1.0) * jitter;
        return Math.Max(0.5, baseH + offset);
    }

    /// <summary>
    /// Roll the next random zone-rotation fire time. Disabled (= MaxValue)
    /// when the feature is off, the base is 0, or the working set has fewer
    /// than 2 zones (no zone to rotate TO). Each call rolls a fresh interval
    /// so a watcher can't predict the bot's hop cadence.
    /// </summary>
    private void RollNextRandomRotate()
    {
        if (!_config.EnableRandomZoneRotation
            || _config.RandomZoneRotationMinutes <= 0
            || _config.WorkingSetZones.Count < 2)
        {
            _nextRandomRotateAt = DateTime.MaxValue;
            return;
        }
        var baseMin = _config.RandomZoneRotationMinutes;
        var jitter = Math.Max(0, _config.RandomZoneRotationJitterMinutes);
        var rolled = jitter > 0 ? _rng.Next(baseMin - jitter, baseMin + jitter + 1) : baseMin;
        rolled = Math.Max(5, rolled); // safety floor — 5 min minimum cadence
        _nextRandomRotateAt = DateTime.UtcNow.AddMinutes(rolled);
        LogAction($"random rotate: next fire in {rolled} min ({_config.RandomZoneRotationMinutes}±{jitter})");
    }

    /// <summary>
    /// Force a hop to a different working-set zone right now. Anti-detection
    /// trigger — invoked from Tick() when the random rotate timer expires
    /// AND State == Selecting. Picks uniformly from working set minus the
    /// current zone. Returns true if the bot transitioned to Teleporting;
    /// false if it couldn't (no Lifestream, no candidates, etc.) and the
    /// caller should reschedule the trigger.
    /// </summary>
    private bool TryForceRandomZoneRotate()
    {
        if (!_lifestream.IsAvailable) return false;
        if (_lifestream.IsBusy) return false;
        if (_config.WorkingSetZones.Count < 2) return false;
        var currentTerritory = _clientState.TerritoryType;
        var candidates = _config.WorkingSetZones
            .Where(t => t != currentTerritory)
            .Select(t => TerritoryMap.Lookup(t))
            .Where(z => z != null)
            .ToList();
        if (candidates.Count == 0) return false;
        var pick = candidates[_rng.Next(candidates.Count)]!;

        LogAction($"random rotate fired — hopping to {pick.ZoneName} (aetheryte {pick.AetheryteId})");
        _pendingTeleportTerritory = pick.TerritoryTypeId;
        _pendingTeleportAetheryte = pick.AetheryteId;
        _teleportFired = false;
        _lastDepartedFromTerritory = currentTerritory; // matches normal rotate path
        _droughtStartedAt = null;                       // fresh start in new zone
        if (!_config.DryRun)
        {
            _navmesh.Stop();
            if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
            if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
        }
        Transition(FateBotState.Teleporting);
        return true;
    }

    /// <summary>Roll one session-cap pause duration: base ± Jitter (uniform, minutes).</summary>
    private int RollSessionPauseMinutes()
    {
        var baseMin = _config.SessionCapPauseMinutes;
        if (baseMin <= 0) return 0;
        var jitter = Math.Max(0, _config.SessionCapPauseMinutesJitter);
        if (jitter <= 0) return baseMin;
        var offset = _rng.Next(-jitter, jitter + 1);
        return Math.Max(1, baseMin + offset);
    }

    /// <summary>
    /// Scan a SelectIconString addon's options for one whose label contains
    /// the given substring (case-insensitive). Returns the 0-indexed option
    /// number for FireCallbackInt, or -1 if not found.
    ///
    /// SelectIconString layout (from Questionable's reader):
    ///   AtkValues[5].Int = option count
    ///   AtkValues[7], [10], [13], … = option text strings (every 3rd slot)
    /// </summary>
    private static unsafe int FindMenuOptionContaining(AtkUnitBase* addon, string needle)
    {
        if (addon == null) return -1;
        if (addon->AtkValuesCount < 8) return -1;
        int count = addon->AtkValues[5].Int;
        if (count <= 0 || count > 32) return -1;
        for (int i = 0; i < count; i++)
        {
            int idx = i * 3 + 7;
            if (idx >= addon->AtkValuesCount) break;
            var v = addon->AtkValues[idx];
            if (v.Type == AtkValueType.Undefined || !v.String.HasValue) continue;
            try
            {
                var s = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(v.String)).TextValue;
                if (s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch { /* unreadable slot — skip */ }
        }
        return -1;
    }

    /// <summary>
    /// Dispatch a real mouse-click event to the Repair All button on the
    /// Repair addon, matching ECommons' <c>AddonMaster.Repair.RepairAll</c>.
    /// FireCallbackInt does not work for this addon — the game expects an
    /// AtkEvent-style ReceiveEvent dispatched from the button's own node.
    /// </summary>
    private static unsafe void ClickRepairAllButton(FFXIVClientStructs.FFXIV.Client.UI.AddonRepair* addon)
    {
        var btn = addon->RepairAllButton;
        if (btn == null) return;
        if (!btn->IsEnabled) return;
        var node = btn->AtkComponentBase.OwnerNode;
        if (node == null) return;
        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null) return;
        addon->AtkUnitBase.ReceiveEvent(evt->State.EventType, (int)evt->Param,
            (FFXIVClientStructs.FFXIV.Component.GUI.AtkEvent*)evt);
    }

    /// <summary>
    /// Buy our target item from an open ShopExchangeCurrency addon.
    /// AtkValue layout (per <c>reference_shop_exchange_currency.md</c>):
    ///   [4]            = NumEntries
    ///   [86]           = current currency on hand
    ///   [456 + i]      = cost for entry i
    ///   [1066 + i]     = item id for entry i
    ///   [1310 + i]     = shopIndex — pass THIS to FireCallback, not the loop i
    /// 99 / purchase hard cap → loop here drives one purchase per tick.
    /// </summary>
    private unsafe void BuyFromShopExchangeCurrency(AtkUnitBase* shop)
    {
        if (shop->AtkValuesCount < 1311)
        {
            LogAction($"trading: shop addon has only {shop->AtkValuesCount} AtkValues — schema mismatch, abort");
            shop->FireCallbackInt(-1);
            FinishTrading();
            return;
        }

        int n = shop->AtkValues[4].Int;
        int currency = shop->AtkValues[86].Int;

        int shopIdx = -1;
        int cost = 0;
        for (int i = 0; i < n && (1310 + i) < shop->AtkValuesCount; i++)
        {
            uint id = (uint)shop->AtkValues[1066 + i].Int;
            if (id == _tradingItemId)
            {
                shopIdx = shop->AtkValues[1310 + i].Int;
                cost    = shop->AtkValues[456 + i].Int;
                break;
            }
        }

        if (shopIdx < 0)
        {
            LogAction($"trading: item {_tradingItemId} NOT in {_tradingVendor?.Name}'s inventory (perhaps rank-gated) — closing shop");
            shop->FireCallbackInt(-1);
            FinishTrading();
            return;
        }
        if (cost <= 0)
        {
            LogAction($"trading: item {_tradingItemId} cost reads 0 — schema issue, abort");
            shop->FireCallbackInt(-1);
            FinishTrading();
            return;
        }
        if (currency < cost)
        {
            LogAction($"trading: done — currency {currency} < cost {cost}; closing shop");
            shop->FireCallbackInt(-1);
            FinishTrading();
            return;
        }

        int affordable = Math.Min(99, currency / cost);
        // Cap by per-item buy limit if configured: don't exceed
        // (limit - currentInventoryCount).
        if (_config.TradingItemLimits.TryGetValue(_tradingItemId, out var limit) && limit > 0)
        {
            var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            int have = inv != null ? inv->GetInventoryItemCount(_tradingItemId) : 0;
            int headroom = limit - have;
            if (headroom <= 0)
            {
                LogAction($"trading: item {_tradingItemId} at limit ({have}/{limit}) — closing shop");
                shop->FireCallbackInt(-1);
                FinishTrading();
                return;
            }
            if (headroom < affordable) affordable = headroom;
        }
        var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[3];
        values[0].Type = AtkValueType.Int; values[0].Int = 0;
        values[1].Type = AtkValueType.Int; values[1].Int = shopIdx;
        values[2].Type = AtkValueType.Int; values[2].Int = affordable;
        shop->FireCallback(3, values, true);
        LogAction($"trading: bought {affordable}× item {_tradingItemId} @ {cost} gems each (currency {currency} → ~{currency - affordable * cost})");
    }

    /// <summary>
    /// Survey mode — read every item the vendor sells, update DiscoveredVendorItems
    /// in config, then close the shop. No purchases.
    /// </summary>
    private unsafe void SurveyShopExchangeCurrency(AtkUnitBase* shop)
    {
        if (shop->AtkValuesCount < 1311 || _tradingVendor == null)
        {
            LogAction("survey: shop addon schema unexpected — abort");
            shop->FireCallbackInt(-1);
            FinishTrading();
            return;
        }

        int n = shop->AtkValues[4].Int;
        int learned = 0;
        var nowIso = DateTime.UtcNow.ToString("o");
        var itemSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        for (int i = 0; i < n && (1310 + i) < shop->AtkValuesCount; i++)
        {
            uint itemId = (uint)shop->AtkValues[1066 + i].Int;
            int cost    = shop->AtkValues[456 + i].Int;
            if (itemId == 0 || cost <= 0) continue;
            string itemName = "";
            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row))
                itemName = row.Name.ToString();
            _config.DiscoveredVendorItems[itemId] = new Configuration.DiscoveredItem
            {
                VendorAetheryteId = _tradingVendor.AetheryteId,
                VendorName        = _tradingVendor.Name,
                GemCost           = cost,
                LastSeenIso       = nowIso,
                ItemName          = itemName,
            };
            learned++;
        }
        // Persist via Plugin's saveConfig (next config touch flushes anyway, but
        // we want immediate persistence after a survey).
        _saveConfig?.Invoke();
        LogAction($"survey: learned {learned} items from {_tradingVendor.Name} — saved to DiscoveredVendorItems");
        shop->FireCallbackInt(-1);
        FinishTrading();
    }

    /// <summary>
    /// Distance (yalms) below which we walk to a same-zone vendor instead of
    /// re-teleporting via the aetheryte. The cap is set at the rough boundary
    /// of the object-table load radius — beyond it we couldn't navmesh
    /// reliably anyway and the aetheryte hop is faster.
    /// </summary>
    private const float WalkInsteadOfTeleportYalms = 120f;

    /// <summary>
    /// True if the vendor NPC is currently loaded in the object table AND
    /// within walking distance of the player. Outputs the measured distance
    /// (or float.MaxValue if vendor isn't loaded). Caller must already have
    /// confirmed same-territory.
    /// </summary>
    private bool IsVendorWalkClose(VendorNpc vendor, out float distance)
    {
        distance = float.MaxValue;
        var player = _objectTable.LocalPlayer;
        if (player == null) return false;
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc) continue;
            if (!obj.Name.TextValue.Contains(vendor.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var d = Vector3.Distance(obj.Position, player.Position);
            if (d < distance) distance = d;
        }
        return distance < WalkInsteadOfTeleportYalms;
    }

    /// <summary>
    /// Resolve gem cost for an item: prefer surveyed cost, fall back to static
    /// catalog entry. Returns 0 if neither known (caller treats as ineligible).
    /// </summary>
    private int ResolveGemCost(uint itemId)
    {
        if (_config.DiscoveredVendorItems.TryGetValue(itemId, out var disc) && disc.GemCost > 0)
            return disc.GemCost;
        foreach (var v in VendorCatalog.Vendors)
            foreach (var item in v.Items)
                if (item.ItemId == itemId && item.GemCost > 0) return item.GemCost;
        return 0;
    }

    /// <summary>
    /// Find which vendor sells this item by surveyed-data lookup. Used as a
    /// fallback for per-zone vendor items that aren't in the static catalog.
    /// </summary>
    private VendorNpc? FindVendorByDiscovery(uint itemId)
    {
        if (!_config.DiscoveredVendorItems.TryGetValue(itemId, out var disc)) return null;
        foreach (var v in VendorCatalog.Vendors)
            if (v.AetheryteId == disc.VendorAetheryteId && v.Name == disc.VendorName)
                return v;
        return null;
    }

    /// <summary>
    /// True when the player's inventory count of this item already meets or
    /// exceeds the configured per-item cap. Items without a cap entry never
    /// hit the limit and read false.
    /// </summary>
    private unsafe bool IsItemAtLimit(uint itemId)
    {
        if (!_config.TradingItemLimits.TryGetValue(itemId, out var cap) || cap <= 0) return false;
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return false;
        var have = inv->GetInventoryItemCount(itemId);
        return have >= cap;
    }

    private unsafe void FinishTrading()
    {
        _tradingVendor = null;
        _tradingItemId = 0;
        _tradingTeleportFired = false;
        _tradingAethernetFired = false;
        _tradingSurveyMode = false;

        // Defensive close — if the ShopExchangeCurrency addon is still
        // visible (single FireCallbackInt sometimes doesn't latch on the
        // first try), close it. An open shop addon counts as "player
        // occupied" and blocks Lifestream.Teleport for the next rotation —
        // tester hit a 15-minute teleport-loop because the shop window
        // stayed open after a survey completed.
        TryCloseShopExchangeCurrency();
        // Survey-triggered runs return to Stopped — they were never part of a
        // farming session. Auto-trade-triggered runs return to Selecting to
        // resume the FATE rotation.
        if (_surveyOnlySession)
        {
            _surveyOnlySession = false;
            if (_surveyAcquiredTextAdvance)
            {
                _textAdvance.Release();
                _surveyAcquiredTextAdvance = false;
                LogAction("survey: TextAdvance released");
            }
            LogAction("survey: complete — returning to Stopped");
            Transition(FateBotState.Stopped);
        }
        else
        {
            Transition(FateBotState.Selecting);
        }
    }

    /// <summary>
    /// Force-close the ShopExchangeCurrency addon if it's still up. The
    /// per-transaction FireCallbackInt(-1) we send during Buy/Survey
    /// occasionally drops on the floor — leaving the shop visible
    /// counts as "player occupied" to the game, which makes Lifestream
    /// reject every Teleport for the next rotation.
    /// </summary>
    private unsafe void TryCloseShopExchangeCurrency()
    {
        // Close every modal that blocks Lifestream.Teleport. The game flags
        // the player as "occupied" while any of these are visible, so the cast
        // is silently rejected. Hitting them all defensively before the
        // teleport call eliminates an entire class of "teleport rejected loop"
        // bugs (Talk, SelectIconString, vendor menus, Repair addon, etc).
        TryCloseAddon("ShopExchangeCurrency");
        TryCloseAddon("Shop");
        TryCloseAddon("SelectIconString");
        TryCloseAddon("SelectString");
        TryCloseAddon("Talk");
        TryCloseAddon("Repair");
        TryCloseAddon("SelectYesno");
    }

    private unsafe void TryCloseAddon(string name)
    {
        try
        {
            var ptr = _gameGui.GetAddonByName(name);
            var addon = (AtkUnitBase*)ptr.Address;
            if (addon != null && addon->IsVisible)
            {
                addon->FireCallbackInt(-1);
                LogAction($"force-close addon '{name}' before teleport");
            }
        }
        catch (Exception ex) { _log.Warning(ex, $"TryCloseAddon('{name}') failed"); }
    }

    /// <summary>
    /// One-shot: route to the hub Bicolor vendor for the given expansion and
    /// read the shop inventory into <see cref="Configuration.DiscoveredVendorItems"/>.
    /// Hub vendors are rank-gated (Gramsol = ShB max-rank, etc.) — for
    /// lower-rank characters use the per-vendor overload from the UI tree.
    /// </summary>
    public void TriggerSurvey(Expansion exp)
    {
        var hub = exp switch
        {
            Expansion.ShB => VendorCatalog.Vendors.FirstOrDefault(v => v.Name == "Gramsol"),
            Expansion.EW  => VendorCatalog.Vendors.FirstOrDefault(v => v.Name == "Sajareen"),
            Expansion.DT  => VendorCatalog.Vendors.FirstOrDefault(v => v.Name == "Beryl"),
            _ => null,
        };
        if (hub == null)
        {
            LogAction($"survey: no hub vendor known for {exp}");
            return;
        }
        TriggerSurvey(hub);
    }

    /// <summary>
    /// One-shot survey of a specific vendor — independent of the farming loop.
    /// Runs from Stopped, returns to Stopped on completion.
    /// </summary>
    public void TriggerSurvey(VendorNpc vendor)
    {
        if (State == FateBotState.Trading || State == FateBotState.Repairing
            || State == FateBotState.Dying || State == FateBotState.Paused)
        {
            LogAction($"survey: ignored — busy in {State}");
            return;
        }
        if (!_lifestream.IsAvailable)
        {
            LogAction("survey: Lifestream not installed — abort");
            return;
        }
        _tradingVendor = vendor;
        _tradingItemId = 0;
        _tradingSurveyMode = true;
        _tradingTeleportFired = false;
        _tradingAethernetFired = false;
        _lastTradingActionAt = DateTime.MinValue;
        _surveyOnlySession = (State == FateBotState.Stopped);
        // Same-zone optimisation: skip the aetheryte hop only if the vendor is
        // close enough to walk. We probe the object table for the NPC; if it's
        // loaded (within ~400y radius) AND closer than WalkInsteadOfTeleportYalms,
        // walk. Otherwise teleport even though we're "already there" — saves
        // the cross-map run when the player happens to be far from the vendor.
        bool walkOnly = false;
        if (_clientState.TerritoryType == vendor.TerritoryType)
        {
            walkOnly = IsVendorWalkClose(vendor, out var d);
            if (walkOnly)
                LogAction($"survey: routing to {vendor.Name} — same zone, vendor {d:F0}y away, walking");
            else
                LogAction($"survey: routing to {vendor.Name} — same zone but far from vendor, teleporting via aetheryte");
        }
        else
        {
            LogAction($"survey: routing to {vendor.Name} ({vendor.Settlement}) (surveyOnly={_surveyOnlySession})");
        }
        _tradingTeleportFired = walkOnly;
        if (!_config.DryRun)
        {
            _navmesh.Stop();
            if (_rsrActivated)     { _rsr.Deactivate();     _rsrActivated = false; }
            if (_bossmodActivated) { _bossmod.Deactivate(); _bossmodActivated = false; }
            // Acquire TextAdvance so first-time NPC greeting dialogs (Talk
            // addon) auto-advance — otherwise the shop addon never opens and
            // the bot times out. Only acquire if we're not already part of a
            // farming session (Start() handles that path).
            if (_surveyOnlySession && _textAdvance.IsAvailable)
            {
                if (_textAdvance.Acquire())
                {
                    _surveyAcquiredTextAdvance = true;
                    LogAction("survey: TextAdvance external control acquired");
                }
                else
                {
                    LogAction("survey: warn — TextAdvance present but Acquire denied; first-time Talk dialogs may stall");
                }
            }
        }
        Transition(FateBotState.Trading);
    }

    /// <summary>
    /// Auto-trade flow (Phase 7 — initial scaffold; addon-buy logic lands when
    /// the ShopExchangeCurrency research agent returns):
    ///   1. Teleport to vendor.AetheryteId (with retry, like Repair)
    ///   2. Find vendor NPC by name in ObjectTable, walk to it, interact
    ///   3. Navigate any SelectIconString menu (pick category if present)
    ///   4. In ShopExchangeCurrency addon: select target item, buy max qty
    ///   5. Loop until gem count is well below threshold OR no more items
    ///   6. Exit to Selecting
    /// 4-min overall timeout to bail.
    /// </summary>
    private unsafe void TickTrading()
    {
        if (_tradingVendor == null) { Transition(FateBotState.Selecting); return; }

        // Step 1 — teleport to vendor's aetheryte (with retry, same pattern as Repair).
        if (!_config.DryRun && !_tradingTeleportFired)
        {
            if (DateTime.UtcNow - _stateEnteredAt < TimeSpan.FromSeconds(1)) return;
            if (DateTime.UtcNow - _lastTradingActionAt < TimeSpan.FromSeconds(5)) return;
            _lastTradingActionAt = DateTime.UtcNow;
            var ok = _lifestream.Teleport(_tradingVendor.AetheryteId, 0);
            LogAction($"trading: Lifestream.Teleport(aetheryte={_tradingVendor.AetheryteId}) → {ok}");
            if (ok) _tradingTeleportFired = true;
            else LogAction("trading: teleport rejected — will retry in 5s");
            return;
        }

        // Step 2 — wait for territory + dismount.
        if (_objectTable.LocalPlayer == null) return;
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]) return;
        if (_condition[ConditionFlag.Mounted])
        {
            if (TryDismountOrRescue("trading-pre-shop")) return;
        }

        // Step 2.5 — aethernet sub-shard hop for city-hub vendors. After the
        // main aetheryte teleport we're at the central plaza; the vendor may
        // live 500+y away at a specific shard (Beryl @ Nexus Arcade, Kajeel
        // Ja @ Bayside Bevy Marketplace, etc.). Aethernet hop is cheap and
        // lands us right next to the vendor's object-table radius.
        if (!_tradingAethernetFired && _tradingVendor.AethernetShardId != 0
            && _clientState.TerritoryType == _tradingVendor.TerritoryType
            && !_config.DryRun)
        {
            if (DateTime.UtcNow - _lastTradingActionAt < TimeSpan.FromSeconds(2)) return;
            _lastTradingActionAt = DateTime.UtcNow;
            var ok = _lifestream.AethernetHop(_tradingVendor.AethernetShardId);
            LogAction($"trading: AethernetHop(shard={_tradingVendor.AethernetShardId}) → {ok}");
            if (ok) _tradingAethernetFired = true;
            return;
        }

        // Step 3 — find vendor NPC nearby + interact.
        var player = _objectTable.LocalPlayer;
        IGameObject? vendorObj = null;
        float vendorDist = float.MaxValue;
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc) continue;
            if (!obj.Name.TextValue.Contains(_tradingVendor.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var d = Vector3.Distance(obj.Position, player.Position);
            if (d < vendorDist) { vendorObj = obj; vendorDist = d; }
        }

        if (vendorObj == null)
        {
            // Diagnostic — every 5s while we wait, dump the closest EventNpcs
            // so we can see what the area actually contains. Without this the
            // bot just stands silent until the 45s abort fires.
            if (DateTime.UtcNow - _lastTradingActionAt > TimeSpan.FromSeconds(5))
            {
                _lastTradingActionAt = DateTime.UtcNow;
                var nearby = _objectTable
                    .Where(o => o.ObjectKind == ObjectKind.EventNpc)
                    .Select(o => (Name: o.Name.TextValue, Dist: Vector3.Distance(o.Position, player.Position), Id: o.BaseId))
                    .Where(x => !string.IsNullOrEmpty(x.Name))
                    .OrderBy(x => x.Dist)
                    .Take(8)
                    .ToList();
                var pos = player.Position;
                LogAction($"trading: vendor '{_tradingVendor.Name}' not in object table at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}). Nearby EventNpcs: {string.Join(", ", nearby.Select(n => $"{n.Name}#{n.Id}@{n.Dist:F0}y"))}");
            }
            if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(45))
            {
                LogAction($"trading: vendor '{_tradingVendor.Name}' not found in zone after 45s — abort");
                FinishTrading();
            }
            return;
        }

        if (vendorDist > 3.5f)
        {
            if (!_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress
                && DateTime.UtcNow - _lastPathfindAt > PathfindThrottle())
            {
                LogAction($"trading: walk to {vendorObj.Name.TextValue} ({vendorDist:F1}y)");
                _navmesh.PathfindAndMoveCloseTo(vendorObj.Position, fly: false, range: 2f);
                _lastPathfindAt = DateTime.UtcNow;
            }
            return;
        }

        // Step 4 — interact + handle ShopExchangeCurrency addon.
        // Bicolor vendors open the shop directly (no SelectIconString menu).
        var shopPtr = _gameGui.GetAddonByName("ShopExchangeCurrency");
        var shopAddon = (AtkUnitBase*)shopPtr.Address;
        if (shopAddon != null && shopAddon->IsVisible)
        {
            if (DateTime.UtcNow - _lastTradingActionAt > TimeSpan.FromSeconds(2))
            {
                _lastTradingActionAt = DateTime.UtcNow;
                if (_tradingSurveyMode)
                    SurveyShopExchangeCurrency(shopAddon);
                else
                    BuyFromShopExchangeCurrency(shopAddon);
            }
            return;
        }

        // No shop yet — try interact (throttled).
        if (DateTime.UtcNow - _lastTradingActionAt > TimeSpan.FromMilliseconds(1500))
        {
            _lastTradingActionAt = DateTime.UtcNow;
            _targetManager.Target = vendorObj;
            var ok = _action.InteractWith(vendorObj);
            LogAction($"trading: InteractWith({vendorObj.Name.TextValue}) → {ok}");
        }

        // Hard timeout: 4 min.
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromMinutes(4))
        {
            LogAction("trading: timeout (4 min) — abort");
            FinishTrading();
        }
    }

    /// <summary>
    /// Auto-repair flow: teleport to the in-zone Mender's aetheryte → find the
    /// Mender NPC in the object table (any EventNpc whose name contains
    /// "Mender") → interact → wait for the <c>Repair</c> addon to open → click
    /// "Repair All" (callback index 4 per FFXIV's repair addon) → confirm the
    /// gil-cost SelectYesno via the existing handler (which we extend to
    /// recognise the Repairing state). Done when min durability returns above
    /// <see cref="Configuration.RepairAtDurabilityPercent"/> + 50.
    /// </summary>
    /// <summary>
    /// Flee-from-combat sub-routine for Repairing: Lifestream.Teleport
    /// unconditionally returns false while the player is InCombat. We path
    /// 80y in the direction AWAY from the nearest hostile so aggro drops,
    /// then retry the teleport once OOC. Throttled to a 4-second cadence so
    /// vnavmesh has time to execute each leg before we re-issue.
    /// </summary>
    private void FleeCombatForRepair()
    {
        if (DateTime.UtcNow - _lastRepairFleeAt < TimeSpan.FromSeconds(4)) return;
        _lastRepairFleeAt = DateTime.UtcNow;
        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        // Pick the closest hostile that's targeting us — that's the one
        // anchoring our InCombat flag. Fall back to the closest hostile if
        // none target us (S-rank in transition between targets etc.).
        IGameObject? threat = null;
        float threatDist = float.MaxValue;
        var playerId = player.GameObjectId;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            if ((npc.StatusFlags & StatusFlags.Hostile) == 0) continue;
            if (npc.IsDead) continue;
            var d = Vector3.Distance(npc.Position, player.Position);
            // Prioritise an aggro'd mob — it's the one keeping us in combat.
            bool aggrod = npc.TargetObjectId == playerId;
            if (aggrod && (threat == null || d < threatDist))
            {
                threat = npc; threatDist = d;
            }
            else if (threat == null && d < threatDist)
            {
                threat = npc; threatDist = d;
            }
        }
        if (threat == null)
        {
            LogAction("Repair: in combat but no hostile found nearby — just waiting 5s");
            return;
        }
        // Direction away from threat (XZ only — Y handled by navmesh terrain).
        var awayX = player.Position.X - threat.Position.X;
        var awayZ = player.Position.Z - threat.Position.Z;
        var len = MathF.Sqrt(awayX * awayX + awayZ * awayZ);
        if (len < 0.001f) { awayX = 1; awayZ = 0; len = 1; } // fallback
        const float fleeDistance = 80f;
        var dst = new Vector3(
            player.Position.X + (awayX / len) * fleeDistance,
            player.Position.Y,
            player.Position.Z + (awayZ / len) * fleeDistance);
        // Stop combat AI so we actually run, not stand and trade hits.
        if (_rsrActivated)     { try { _rsr.Deactivate();     } catch {} _rsrActivated = false; }
        if (_bossmodActivated) { try { _bossmod.Deactivate(); } catch {} _bossmodActivated = false; }
        try { _navmesh.PathfindAndMoveCloseTo(dst, fly: false, range: 1f); } catch {}
        LogAction($"Repair: flee combat — {threat.Name.TextValue} aggroed @ {threatDist:F1}y, running 80y away");
    }

    private unsafe void TickRepairing()
    {
        // Hard timeout: 4 min for the whole flow. Checked FIRST so a stuck
        // teleport-retry loop (e.g. S-rank aggro that never times out) still
        // bails instead of looping forever. Originally lived at the bottom
        // but the teleport block returns early, so it never ran in practice.
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromMinutes(4))
        {
            LogAction("Repair: timeout (4 min) — abort, returning to Selecting");
            _repairAetheryteId = 0;
            _repairTeleportRejections = 0;
            Transition(FateBotState.Selecting);
            return;
        }
        // Step 1: ensure we're in the Mender's zone — fire teleport once.
        // We compare aetheryte id by inspection of LocalPlayer's territory and
        // the fact that we just landed; if the player is still in the home
        // territory, fire the teleport.
        if (!_config.DryRun && !_repairTeleportFired && _repairAetheryteId != 0)
        {
            // Wait 1s for any prior animation lock (dismount, etc.) to clear.
            if (DateTime.UtcNow - _stateEnteredAt < TimeSpan.FromSeconds(1)) return;

            // Combat-lockout escape: if Lifestream has rejected 3+ times and
            // we're still InCombat (e.g. S-rank aggro, hunt mob, ambient FATE
            // adds), running away to drop aggro is the only way forward —
            // Lifestream.Teleport unconditionally fails while InCombat.
            if (_repairTeleportRejections >= 3 && _condition[ConditionFlag.InCombat])
            {
                FleeCombatForRepair();
                return;
            }

            // Throttle to once per 5s — combat-end teleport lockout (~5-10s)
            // is the most common reason Lifestream.Teleport returns false.
            if (DateTime.UtcNow - _lastRepairInteractAt < TimeSpan.FromSeconds(5)) return;
            _lastRepairInteractAt = DateTime.UtcNow;
            var ok = _lifestream.Teleport(_repairAetheryteId, 0);
            LogAction($"Repair: Lifestream.Teleport(aetheryte={_repairAetheryteId}) → {ok}");
            if (ok)
            {
                _repairTeleportFired = true;
                _repairTeleportRejections = 0;
            }
            else
            {
                _repairTeleportRejections++;
                LogAction($"Repair: teleport rejected (#{_repairTeleportRejections}) — will retry in 5s (still in Repairing)");
            }
            return;
        }

        // Step 2: wait for loading screen to finish (territory stabilises).
        if (_objectTable.LocalPlayer == null) return;
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51]) return;

        // Step 3: dismount if mounted (NPCs can't be interacted with from a mount).
        if (_condition[ConditionFlag.Mounted])
        {
            if (DateTime.UtcNow - _lastDismountAttemptAt > TimeSpan.FromMilliseconds(700))
            {
                if (_config.DryRun) LogAction("[DRY] Repair: would dismount");
                else { LogAction("Repair: dismount"); _action.Dismount(); }
                _lastDismountAttemptAt = DateTime.UtcNow;
            }
            return;
        }

        // Step 4: check if repair is done. The Repair addon stays open until
        // the user closes it; we detect "done" by min durability climbing back
        // above the threshold + a healthy margin.
        if (_lastDurabilityMin >= _config.RepairAtDurabilityPercent + 50
            || _lastDurabilityMin >= 95)
        {
            LogAction($"Repair done — min durability {_lastDurabilityMin}%; closing addon");
            // Close the Repair addon if still open.
            var repairAddon = _gameGui.GetAddonByName("Repair");
            var rep = (AtkUnitBase*)repairAddon.Address;
            if (rep != null && rep->IsVisible) rep->FireCallbackInt(-1); // -1 = cancel/close
            _repairAetheryteId = 0;
            Transition(FateBotState.Selecting);
            return;
        }

        // Step 5a: combined "Merchant & Mender" NPCs open a SelectIconString
        // menu first (Purchase Weapons / Repair Gear / Nothing). Pick the
        // option whose label contains "Repair" before falling through to the
        // Repair-addon click.
        var menuPtr = _gameGui.GetAddonByName("SelectIconString");
        var menuAddon = (AtkUnitBase*)menuPtr.Address;
        if (menuAddon != null && menuAddon->IsVisible)
        {
            if (DateTime.UtcNow - _lastRepairInteractAt > TimeSpan.FromMilliseconds(1500))
            {
                int repairIdx = FindMenuOptionContaining(menuAddon, "Repair");
                if (repairIdx >= 0)
                {
                    _lastRepairInteractAt = DateTime.UtcNow;
                    menuAddon->FireCallbackInt(repairIdx);
                    LogAction($"Repair: SelectIconString — picked option {repairIdx} (\"Repair Gear\")");
                }
                else
                {
                    LogAction("Repair: SelectIconString visible but no 'Repair' option found — abort");
                    _repairAetheryteId = 0;
                    Transition(FateBotState.Selecting);
                }
            }
            return;
        }

        // Step 5b: if the Repair addon is already open, click "Repair All" by
        // dispatching the button's own event to the addon (the same path the
        // game runs when the user clicks). FireCallbackInt is not sufficient
        // for this addon — it ignores integer callbacks.
        var addonPtr = _gameGui.GetAddonByName("Repair");
        var addonRepair = (FFXIVClientStructs.FFXIV.Client.UI.AddonRepair*)addonPtr.Address;
        if (addonRepair != null && addonRepair->AtkUnitBase.IsVisible)
        {
            if (DateTime.UtcNow - _lastRepairInteractAt > TimeSpan.FromMilliseconds(1500))
            {
                _lastRepairInteractAt = DateTime.UtcNow;
                ClickRepairAllButton(addonRepair);
                LogAction("Repair: clicked Repair All button — waiting for gil-confirm SelectYesno");
            }
            return;
        }

        // Step 6: find the Mender NPC nearby and interact.
        var player = _objectTable.LocalPlayer;
        if (player == null) return;
        var playerPos = player.Position;

        IGameObject? mender = null;
        float menderDist = float.MaxValue;
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc) continue;
            var name = obj.Name.TextValue;
            // English client. Localised clients would need extra strings here.
            if (!name.Contains("Mender", StringComparison.OrdinalIgnoreCase)) continue;
            var d = Vector3.Distance(obj.Position, playerPos);
            if (d < menderDist) { mender = obj; menderDist = d; }
        }

        if (mender == null)
        {
            // Streaming may not have loaded the NPC yet right after teleport.
            // Give it a moment.
            if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromSeconds(45))
            {
                LogAction("Repair: no Mender NPC found in zone — abort");
                _repairAetheryteId = 0;
                Transition(FateBotState.Selecting);
            }
            return;
        }

        // Step 7: close the distance, then interact.
        if (menderDist > 3.5f)
        {
            if (!_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress
                && DateTime.UtcNow - _lastPathfindAt > PathfindThrottle())
            {
                LogAction($"Repair: close-in to {mender.Name.TextValue} ({menderDist:F1}y)");
                _navmesh.PathfindAndMoveCloseTo(mender.Position, fly: false, range: 2f);
                _lastPathfindAt = DateTime.UtcNow;
            }
            return;
        }

        // Step 8: in range — interact, throttled.
        if (DateTime.UtcNow - _lastRepairInteractAt > TimeSpan.FromMilliseconds(1500))
        {
            _lastRepairInteractAt = DateTime.UtcNow;
            _targetManager.Target = mender;
            var ok = _action.InteractWith(mender);
            LogAction($"Repair: InteractWith({mender.Name.TextValue}) → {ok}");
        }

        // Step 9: hard timeout — 4 minutes for the whole flow.
        if (DateTime.UtcNow - _stateEnteredAt > TimeSpan.FromMinutes(4))
        {
            LogAction("Repair: timeout (4 min) — abort");
            _repairAetheryteId = 0;
            Transition(FateBotState.Selecting);
        }
    }

    /// <summary>
    /// Player is Unconscious. Wait up to <c>RaiseGraceSeconds</c> for someone
    /// to raise us; if no raise comes, fire General Action 8 (Return) to click
    /// OK on the "Return to home aetheryte" dialog. The dialog is NOT a plain
    /// SelectYesno — it has OK/Wait buttons and its addon is backed by
    /// AgentRevive, so the SelectYesno addon-lifecycle listener never matches.
    /// We use the action route which the OK button is bound to.
    /// Once we're alive again, jump to Recovering — the regular flow there will
    /// teleport back to <c>_diedInTerritory</c> if needed.
    /// </summary>
    private void TickDying()
    {
        // Raised in-place? Unconscious cleared → recover normally, no zone hop needed.
        if (!_condition[ConditionFlag.Unconscious])
        {
            LogAction(_diedReturnTriggered ? "revived after Return" : "raised in place ✓");
            Transition(FateBotState.Recovering);
            return;
        }

        var sinceEnter = DateTime.UtcNow - _stateEnteredAt;

        // After grace, mark Return as eligible and start hammering the dialog.
        if (!_diedReturnTriggered && sinceEnter > TimeSpan.FromSeconds(_config.RaiseGraceSeconds))
        {
            _diedReturnTriggered = true;
            LogAction($"no raise after {_config.RaiseGraceSeconds}s — will accept Return prompt");
        }

        // While Return-eligible, probe for the prompt every 500ms and click OK.
        // The death dialog opens at T+0 (before we entered Dying), so the
        // PostSetup AddonLifecycle event has already fired and won't re-fire —
        // we must look up the open addon ourselves.
        if (_diedReturnTriggered && DateTime.UtcNow - _lastInteractAt > TimeSpan.FromMilliseconds(500))
        {
            _lastInteractAt = DateTime.UtcNow;
            if (!_config.DryRun) TryClickReturnPrompt();
        }

        // Safety timeout — 120s total. If something's gone wrong (death dialog
        // never appeared, raise stalled), stop and let the user intervene.
        if (sinceEnter > TimeSpan.FromSeconds(120))
        {
            LogAction("dying timeout (120s) — stopping bot for manual recovery");
            Stop();
        }
    }

    /// <summary>
    /// The death "Return to X?" dialog. Different game versions/contexts have
    /// used different addon names: <c>SelectYesno</c>, <c>_NotificationDeath</c>,
    /// or the addon owned by <c>AgentRevive</c>. Probe each candidate and click
    /// the first one that responds. Index 0 = OK / Yes / Return.
    /// </summary>
    private unsafe void TryClickReturnPrompt()
    {
        // 1) AgentRevive's owned addon (canonical for open-world death).
        var agentRevive = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRevive.Instance();
        if (agentRevive != null && agentRevive->IsAddonShown())
        {
            var addonId = agentRevive->GetAddonId();
            if (addonId != 0)
            {
                var addon = (AtkUnitBase*)FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
                if (addon != null && addon->IsVisible)
                {
                    addon->FireCallbackInt(0);
                    LogAction($"clicked Return prompt via AgentRevive addon (id={addonId})");
                    return;
                }
            }
        }

        // 2) Fallback: a localised SelectYesno that looks identical.
        var yesPtr = _gameGui.GetAddonByName("SelectYesno");
        var yes = (AtkUnitBase*)yesPtr.Address;
        if (yes != null && yes->IsVisible)
        {
            yes->FireCallbackInt(0);
            LogAction("clicked Return prompt via SelectYesno");
        }
    }

    private void TickRecovering()
    {
        if (_rsrActivated)
        {
            if (_config.DryRun) LogAction("[DRY] would RSR.Deactivate");
            else { LogAction("RSR.Deactivate"); _rsr.Deactivate(); }
            _rsrActivated = false;
            // Stop any in-flight movement immediately — BossMod's NormalMovement
            // had us walking toward a target which is now invalid (FATE done →
            // mobs vanish → NPC despawns). Without this the bot trails the
            // disappearing escort NPC for a few seconds.
            if (!_config.DryRun) _navmesh.Stop();
        }
        if (_bossmodActivated)
        {
            if (_config.DryRun) LogAction("[DRY] would BossMod.Deactivate + /vbmai off");
            else { LogAction("BossMod.Deactivate"); _bossmod.Deactivate(); }
            _bossmodActivated = false;
            if (!_config.DryRun) _navmesh.Stop();
        }

        // Restore-after-Return: if we Returned to home aetheryte while dying,
        // teleport back to the original FATE zone before resuming. Only do this
        // if the zone is in the working set (else user has navigated us out
        // intentionally) and we're not already there.
        if (_diedReturnTriggered && _diedInTerritory != 0)
        {
            var here = _clientState.TerritoryType;
            if (here != _diedInTerritory && _config.WorkingSetZones.Contains(_diedInTerritory))
            {
                var info = TerritoryMap.Lookup(_diedInTerritory);
                if (info != null && _lifestream.IsAvailable)
                {
                    LogAction($"post-Return restore: teleport back to {info.ZoneName}");
                    _pendingTeleportTerritory = _diedInTerritory;
                    _pendingTeleportAetheryte = info.AetheryteId;
                    _diedInTerritory = 0;
                    _diedReturnTriggered = false;
                    Transition(FateBotState.Teleporting);
                    return;
                }
            }
            // Either we're already back, or the zone isn't in working set, or
            // Lifestream is gone. Clear flags and let normal flow proceed.
            _diedInTerritory = 0;
            _diedReturnTriggered = false;
        }

        // After a panic-escape, hold here until HP recovers AND we're out of
        // combat. While still in combat (random mobs aggro'd us during the
        // flee), keep running further away — standing still gets us killed.
        if (_panicked)
        {
            var player = _objectTable.LocalPlayer;
            if (player == null) return;
            int hpPct = player.MaxHp == 0 ? 0 : (int)(100L * player.CurrentHp / player.MaxHp);
            bool inCombat = _condition[ConditionFlag.InCombat];

            if (inCombat)
            {
                // Sprint refresh — fire whenever the action is off cooldown.
                _action.UseSprint();

                // If we hit terrain and stopped moving, the regular stuck
                // detector will kick the path. While stuck, also re-roll the
                // escape vector with a perpendicular twist so we go around.
                bool wasStuck = CheckAndRecoverFromStuck(player.Position);

                // Continue fleeing. Recompute escape direction every ~2.5s so
                // we adapt as different mobs aggro / drop off, and re-pathfind
                // if the current path completed OR stuck-recovery just fired.
                bool needNewPath = (DateTime.UtcNow - _lastPathfindAt > TimeSpan.FromSeconds(2.5)
                                    && !_navmesh.IsPathRunning && !_navmesh.IsPathfindInProgress)
                                   || wasStuck;
                if (needNewPath)
                {
                    var playerId = player.GameObjectId;
                    Vector3 aggroCenter = Vector3.Zero;
                    int aggroCount = 0;
                    foreach (var obj in _objectTable)
                    {
                        if (obj is not IBattleNpc npc) continue;
                        if (npc.IsDead) continue;
                        if (npc.TargetObjectId != playerId) continue;
                        aggroCenter += npc.Position;
                        aggroCount++;
                    }
                    Vector3 awayFrom = aggroCount > 0 ? aggroCenter / aggroCount : player.Position;
                    var dir = player.Position - awayFrom;
                    if (dir.LengthSquared() < 0.01f) dir = new Vector3(1, 0, 0);
                    dir = Vector3.Normalize(dir);
                    // If we just got unstuck, rotate the escape vector 60° so
                    // vnav picks a different way around the obstacle instead of
                    // banging the same wall.
                    if (wasStuck)
                    {
                        var rot = MathF.PI / 3f; // 60°
                        var (sin, cos) = (MathF.Sin(rot), MathF.Cos(rot));
                        dir = new Vector3(dir.X * cos - dir.Z * sin, 0, dir.X * sin + dir.Z * cos);
                    }
                    var escape = player.Position + dir * 40f;
                    LogAction($"panic flee: {aggroCount} mob(s){(wasStuck ? ", was stuck, rotated 60°" : "")} — running 40y");
                    _navmesh.PathfindAndMoveCloseTo(escape, fly: false, range: 3f);
                    _lastPathfindAt = DateTime.UtcNow;
                }
                return;
            }

            if (hpPct >= _config.RecoverHpPercent)
            {
                LogAction($"panic recovered: HP {hpPct}%, out of combat — resuming");
                _panicked = false;
                _navmesh.Stop();
                Transition(FateBotState.Selecting);
            }
            return;
        }

        var wait = TimeSpan.FromSeconds(RecoverySeconds) + _humanizeDelay;
        if (DateTime.UtcNow - _stateEnteredAt > wait)
            Transition(FateBotState.Selecting);
    }

    private bool IsInFateRange(Vector3 playerPos)
    {
        if (_targetFateId == 0) return false;
        var dist = Vector2.Distance(new Vector2(playerPos.X, playerPos.Z), new Vector2(_targetFatePos.X, _targetFatePos.Z));
        return dist <= _targetFateRadius * _config.EngageRangeMultiplier;
    }

    private IFate? FindFate(ushort id) => _fateTable.FirstOrDefault(f => f.FateId == id);

    private bool MatchesBlacklistPattern(string fateName)
    {
        foreach (var p in _config.BlacklistedFateNamePatterns)
            if (!string.IsNullOrWhiteSpace(p) && fateName.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Transition(FateBotState next)
    {
        if (next == State) return;
        _log.Information($"FateWalker: {State} → {next}");
        State = next;
        _stateEnteredAt = DateTime.UtcNow;
        // Reset stuck detection on every state change — the previous state's
        // movement profile (e.g. mounted travel) doesn't carry into the new one.
        _stuckLastPos = Vector3.Zero;
        _stuckLastMoveAt = DateTime.UtcNow;

        // Each entry into Teleporting must fire Lifestream.Teleport exactly once.
        if (next == FateBotState.Teleporting) _teleportFired = false;

        // Clear pull-commit + kill latch on every state change — both are
        // only valid within an active Engaging cycle.
        _pullCommitId = 0;
        _killPhaseLatch = false;
        _killPhaseAggroLossAt = DateTime.MinValue;

        // Clear humanize timers — they belong to specific actions, not states.
        _pendingSelectYesnoAt = null;
        _interactReadyAt = null;

        // Reset dismount stuck tracker — each state has its own dismount.
        _dismountFailCount = 0;

        // Reset generic stuck watchdog — fresh state = fresh "no progress" timer.
        _genericLastPos = Vector3.Zero;
        _genericLastMoveAt = DateTime.UtcNow;

        // Reset Engaging stuck trackers on entry — give BossMod a fresh window
        // before considering "stranded".
        if (next == FateBotState.Engaging)
        {
            _lastEngagingCombatAt = DateTime.UtcNow;
            _lastEngagingKickAt = DateTime.UtcNow;
        }

        // Roll a humanize "think delay" for states with a deliberation moment.
        _humanizeDelay = next switch
        {
            FateBotState.Selecting  => RollSeconds(_config.ThinkBeforePickMinSec, _config.ThinkBeforePickMaxSec),
            FateBotState.Recovering => RollSeconds(_config.PostFateRestMinSec,    _config.PostFateRestMaxSec),
            _ => TimeSpan.Zero,
        };
        // Reset the drought-hesitate timer on every transition.
        _droughtHesitateUntil = null;

        if (next == FateBotState.Stopped)
        {
            _targetFateId = 0;
            _targetFateName = "";
            _targetMotivationNpcId = 0;
        }
    }

    /// <summary>
    /// AutoDuty-style stuck detection (see <c>.dalamud/autoduty/.../StuckHelper.cs</c>).
    /// Call each tick while a path is actively running. Returns true if a stuck
    /// recovery was triggered this tick (caller should skip its normal logic).
    /// Recovery = stop the path and clear the pathfind throttle so the next tick
    /// kicks off a fresh pathfind toward the same destination.
    /// </summary>
    private bool CheckAndRecoverFromStuck(Vector3 playerPos)
    {
        if (_config.DryRun) return false;
        if (!_navmesh.IsPathRunning)
        {
            // Path isn't running — slide the marker forward so a freshly-started
            // path doesn't immediately count as stuck.
            _stuckLastPos = playerPos;
            _stuckLastMoveAt = DateTime.UtcNow;
            return false;
        }

        if (_stuckLastPos == Vector3.Zero || Vector3.DistanceSquared(_stuckLastPos, playerPos) > StuckMoveThresholdSq)
        {
            _stuckLastPos = playerPos;
            _stuckLastMoveAt = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow - _stuckLastMoveAt < StuckTimeout) return false;

        _sessionStuckEvents++;
        LogAction($"stuck for {StuckTimeout.TotalSeconds:F0}s — stopping path, will re-pathfind (session stuck count={_sessionStuckEvents})");
        _navmesh.Stop();
        _lastPathfindAt = DateTime.MinValue;       // bypass the 1.5s throttle next tick
        _stuckLastPos = Vector3.Zero;              // reset detector
        _stuckLastMoveAt = DateTime.UtcNow;
        return true;
    }

    // ─── Humanize helpers ────────────────────────────────────────────────

    /// <summary>Roll a random seconds delay in [minSec, maxSec]. Returns Zero if humanize disabled.</summary>
    private TimeSpan RollSeconds(int minSec, int maxSec)
    {
        if (!_config.EnableHumanize) return TimeSpan.Zero;
        if (maxSec <= minSec) return TimeSpan.FromSeconds(Math.Max(0, minSec));
        // _rng.Next is exclusive-high; use Next on ms granularity for finer jitter.
        var ms = _rng.Next(minSec * 1000, maxSec * 1000 + 1);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Random XZ offset within WaypointJitterYalms (yards). 0 if disabled.</summary>
    private Vector3 RollWaypointOffset()
    {
        if (!_config.EnableHumanize || _config.WaypointJitterYalms <= 0)
            return Vector3.Zero;
        var r = _config.WaypointJitterYalms;
        return new Vector3(
            (float)(_rng.NextDouble() * 2 - 1) * r,
            0f,
            (float)(_rng.NextDouble() * 2 - 1) * r);
    }

    /// <summary>Final landing offset relative to the FATE centre. In solo mode
    /// (or when this client has no formation slot) it's the humanize jitter.
    /// In Party Mode (Host/Follower with a slot) it's the formation stand-point
    /// so the party spreads around the FATE instead of stacking on the centre.</summary>
    private Vector3 ChooseLandingOffset(Dalamud.Game.ClientState.Fates.IFate fate)
    {
        if (_party.Role == Controller.Party.PartyCoordinator.EffectiveRole.Off || _party.MySlotIdx < 0)
            return RollWaypointOffset();

        // Use either the Host's broadcast count or the live party size, whichever
        // is bigger (resilient to a Follower with no recent FATE_ASSIGN yet).
        var liveCount = _party.CurrentPartyCids.Count;
        var n = System.Math.Max(_party.PartyCount, liveCount);
        if (n <= 1) return RollWaypointOffset();

        var stand = Controller.Party.PartyFormation.ComputeStandPoint(
            fate.Position, fate.Radius, fate.FateId,
            n, _party.MySlotIdx,
            _config.PartyFormationRadius, _config.PartyFormationJitter);
        // Snap to walkable mesh — the geometric ring point might be in water /
        // on a rock / through a wall, and the re-pathfind watchdog will fire
        // every tick if vnavmesh can't path to it. NearestPointReachable returns
        // null when nothing is in range; fall back to the FATE centre then.
        if (_navmesh.IsAvailable)
        {
            var snapped = _navmesh.NearestPointReachable(stand, halfExtentXZ: 6f, halfExtentY: 4f);
            if (snapped.HasValue) stand = snapped.Value;
            else stand = fate.Position;
        }
        return stand - fate.Position;
    }

    /// <summary>Pathfind throttle with humanize jitter — 1500 ms ±PathfindJitterMs/2.</summary>
    private TimeSpan PathfindThrottle()
    {
        var baseMs = 1500;
        if (!_config.EnableHumanize || _config.PathfindJitterMs <= 0)
            return TimeSpan.FromMilliseconds(baseMs);
        var half = _config.PathfindJitterMs / 2;
        return TimeSpan.FromMilliseconds(_rng.Next(baseMs - half, baseMs + half + 1));
    }

    /// <summary>
    /// Generic "no progress" watchdog — log when the player hasn't moved
    /// meaningfully for a while in a state that should be progressing. Doesn't
    /// auto-recover (state machines have their own timeouts/rescues) but gives
    /// the log a clear "stuck since 12s ago in State X" trail for review.
    /// Skip states that are intentionally idle (Stopped, Paused, Recovering).
    /// </summary>
    private void GenericStuckWatchdog()
    {
        // Skip states where the player is legitimately stationary (cast bar,
        // loading screen, mount animation, NPC dialog, etc.). Watchdog is
        // for detecting navmesh-stuck during ACTIVE traversal —
        //   • Stopped / Paused / PreparingPause — bot idle
        //   • Teleporting — Lifestream cast (~5 s) + loading screen
        //   • Repairing / Trading — both include their own teleport hop
        //   • Mounting — mount roulette animation
        //   • Interacting — NPC dialog / talk addon
        //   • Recovering — HP regen wait
        //   • Dying — wait for raise or click Return
        if (State == FateBotState.Stopped
         || State == FateBotState.Paused
         || State == FateBotState.PreparingPause
         || State == FateBotState.Teleporting
         || State == FateBotState.Repairing
         || State == FateBotState.Trading
         || State == FateBotState.Mounting
         || State == FateBotState.Interacting
         || State == FateBotState.Recovering
         || State == FateBotState.Dying)
        {
            _genericLastPos = Vector3.Zero;
            return;
        }
        var player = _objectTable.LocalPlayer;
        if (player == null) return;

        // Activity gate: only run the stuck watchdog when the bot is
        // *expecting* the player to move — vnavmesh has an active path, or
        // we're in Engaging with a locked target (BossMod NormalMovement
        // is driving). Standing still in Selecting waiting for the next
        // FATE to spawn is intentional, not stuck — no need to jump.
        bool vnavBusy = _navmesh.IsPathRunning || _navmesh.IsPathfindInProgress;
        bool combatMoving = State == FateBotState.Engaging && _targetManager.Target != null;
        if (!vnavBusy && !combatMoving)
        {
            _genericLastPos = Vector3.Zero;
            _genericLastMoveAt = DateTime.UtcNow;
            return;
        }

        // If combat started during our recent stuck-recovery navmesh kick,
        // hand control back to BossMod / RSR so they can engage the mob
        // instead of fighting our vnav path. One-shot.
        if (_stuckRecoveryIssuedAt != DateTime.MinValue
            && _condition[ConditionFlag.InCombat]
            && DateTime.UtcNow - _stuckRecoveryIssuedAt < TimeSpan.FromSeconds(12))
        {
            LogAction("watchdog: combat started during recovery — stopping vnav so combat AI takes over");
            try { _navmesh.Stop(); } catch {}
            _stuckRecoveryIssuedAt = DateTime.MinValue;
        }

        if (_genericLastPos == Vector3.Zero
            || Vector3.DistanceSquared(_genericLastPos, player.Position) > 4f) // >2y move
        {
            _genericLastPos = player.Position;
            _genericLastMoveAt = DateTime.UtcNow;
            _stuckTierLogged = 0;   // moved → reset tier so next stuck logs from 5s again
            _lastStuckJumpAt = DateTime.MinValue; // ditto — fresh stuck event can jump immediately
            _lastInCombatFleeAt = DateTime.MinValue; // ditto — fresh stuck event can flee immediately
            _lastRepathfindAt   = DateTime.MinValue; // ditto — fresh stuck event can re-pathfind immediately
            _lastOutsideFateRecoveryAt = DateTime.MinValue; // ditto — fresh outside-FATE event can recover immediately
            return;
        }

        var stillSec = (DateTime.UtcNow - _genericLastMoveAt).TotalSeconds;
        if (stillSec < 5) return; // tight human-reaction grace
        // Tier-based de-dup: log once when crossing 5s, once at 15s, once at
        // 30s. Same stuck event no longer re-logs every 8s — the recovery
        // action it triggers does, which is the signal worth keeping.
        int tier = stillSec >= 30 ? 3 : stillSec >= 15 ? 2 : 1;
        if (tier > _stuckTierLogged)
        {
            _stuckTierLogged = tier;
            LogAction($"watchdog: no movement for {stillSec:F0}s in {State} at ({player.Position.X:F0}, {player.Position.Y:F0}, {player.Position.Z:F0})");
        }

        // Progressive stuck-recovery escalation, tuned to feel human (a real
        // player notices and reacts within seconds, not 30+):
        //    5 s — stuck-jump (cheap try to pop off geometry)
        //   15 s — cancel current path + re-pathfind with fly=true. vnav
        //          sometimes locks on a path the player can't physically
        //          execute (rock not in navmesh, unintended ledge). A
        //          fresh plan often picks a route OVER the obstacle.
        //   30 s — full relocate: Lifestream.Teleport to the zone's
        //          primary aetheryte. We've exhausted local recovery.
        // Lightweight gate — only blocks the truly disruptive states (cutscene,
        // loading screen). Combat / mounted / flying are handled per-tier so
        // we can still flee-then-teleport when a mob pins us mid-recovery.
        bool canActLightweight = !_config.DryRun
                   && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
                   && !_condition[ConditionFlag.BetweenAreas]
                   && !_condition[ConditionFlag.BetweenAreas51];
        if (!canActLightweight) return;

        if (stillSec >= 30)
        {
            var zone = TerritoryMap.Lookup(_clientState.TerritoryType);
            if (zone == null || !_lifestream.IsAvailable) return;
            // Lifestream.Teleport rejects InCombat unconditionally. Flee
            // 80 y away from the nearest hostile first (same routine the
            // Repairing flow uses), then the next watchdog tick lands the
            // teleport when we drop out of combat.
            if (_condition[ConditionFlag.InCombat])
            {
                // Cooldown: gate so this can't re-fire every tick while stillSec
                // stays past 30. ~30s is roughly one flee+return cycle; if the
                // bot is still stuck after that the action can run again.
                if ((DateTime.UtcNow - _lastInCombatFleeAt).TotalSeconds < 30) return;
                _lastInCombatFleeAt = DateTime.UtcNow;
                LogAction($"watchdog: stuck {stillSec:F0}s + in combat — flee before teleport");
                FleeCombatForRepair();
                return;
            }
            LogAction($"watchdog: stuck {stillSec:F0}s — teleport to {zone.AetheryteName} to relocate");
            try { _navmesh.Stop(); } catch {}
            try { _lifestream.Teleport(zone.AetheryteId, 0); } catch {}
            _genericLastMoveAt = DateTime.UtcNow; // pause escalation; teleport gives a clean slate
            return;
        }

        // Tier 1 & 2 want the player on foot and out of combat — a jump
        // mid-cast or a re-pathfind during an aggro pull would do more harm
        // than good. Skip until the heavyweight conditions clear.
        bool canActHeavy = !_condition[ConditionFlag.Mounted]
                        && !_condition[ConditionFlag.InFlight]
                        && !_condition[ConditionFlag.InCombat];
        if (!canActHeavy) return;

        if (stillSec >= 15)
        {
            Vector3? dst = _targetManager.Target?.Position
                        ?? (_targetFatePos != Vector3.Zero ? _targetFatePos : (Vector3?)null);
            if (dst.HasValue)
            {
                // Cooldown so this can't re-fire every tick while stillSec stays
                // ≥15. 20s ≈ one realistic re-pathfind + travel attempt.
                if ((DateTime.UtcNow - _lastRepathfindAt).TotalSeconds < 20) return;
                _lastRepathfindAt = DateTime.UtcNow;
                LogAction($"watchdog: stuck {stillSec:F0}s — cancel path + re-pathfind to ({dst.Value.X:F0},{dst.Value.Y:F0},{dst.Value.Z:F0})");
                try { _navmesh.Stop(); } catch {}
                try { _navmesh.PathfindAndMoveCloseTo(dst.Value, fly: true, range: 3f); } catch {}
                _stuckRecoveryIssuedAt = DateTime.UtcNow;
                return;
            }
        }
        // 5s tier: jump — throttled to once per 2s. Without this, the watchdog
        // fires Jump every framework tick (~60Hz) for 10 straight seconds,
        // which both spams the game with key presses AND fills the log ring
        // with "stuck-jump" lines that falsely trip CheckLogicLoop's
        // fingerprint detector → session-disabling the FATE the bot was
        // happily killing 5s earlier.
        if (DateTime.UtcNow - _lastStuckJumpAt < TimeSpan.FromSeconds(2)) return;
        _lastStuckJumpAt = DateTime.UtcNow;
        try { _action.Jump(); LogAction("watchdog: stuck-jump"); }
        catch (Exception ex) { _log.Warning(ex, "stuck-jump failed"); }
    }
    private DateTime _lastStuckJumpAt = DateTime.MinValue;

    /// <summary>
    /// Attempt dismount; on persistent failure (game refuses because we're
    /// hovering above unsafe terrain) re-pathfind to the FATE ground position
    /// so vnavmesh drops us into a flat landing spot. Returns true to indicate
    /// caller should `return` and retry next tick.
    /// </summary>
    private bool TryDismountOrRescue(string context)
    {
        // Throttle dismount fires to once per 700ms.
        if (DateTime.UtcNow - _lastDismountAttemptAt <= TimeSpan.FromMilliseconds(700))
            return true;

        if (_config.DryRun)
        {
            LogAction($"[DRY] would Dismount ({context})");
            _lastDismountAttemptAt = DateTime.UtcNow;
            return true;
        }

        // Fire the dismount.
        LogAction($"Dismount ({context})");
        _action.Dismount();
        _lastDismountAttemptAt = DateTime.UtcNow;
        _dismountFailCount++;

        // Mid-air-stuck rescue: when vnavmesh's fly-path settled hovering over
        // an NPC / building / tall mob, the spot directly below the player
        // isn't navmeshable ground — Dismount triggers a descent that gets
        // cancelled and the bot floats forever. After 3 failures (~2 s), kick
        // a ground-walk re-pathfind (fly=false). vnav will find the nearest
        // walkable surface and the actual landing happens cleanly there.
        if (_dismountFailCount >= 3
            && _condition[ConditionFlag.InFlight]
            && DateTime.UtcNow - _lastDismountRescueAt > TimeSpan.FromSeconds(5))
        {
            _lastDismountRescueAt = DateTime.UtcNow;
            var player = _objectTable.LocalPlayer;
            if (player != null)
            {
                // Prefer the refined entity position (mob/NPC we're going to)
                // if available, otherwise the FATE center.
                var dest = _refinedLandingPos ?? _targetFatePos;
                LogAction($"dismount stuck mid-air ({_dismountFailCount} tries) — ground-pathfind to ({dest.X:F0},{dest.Y:F0},{dest.Z:F0}) (fly=false)");
                try { _navmesh.Stop(); } catch {}
                try { _navmesh.PathfindAndMoveCloseTo(dest, fly: false, range: 3f); } catch {}
                _dismountFailCount = 0;
                return true;
            }
        }

        // Fallback rescue: 5+ tries on the ground (rare — terrain ledge etc.).
        // Re-fly to FATE center, range 5y for a more central landing.
        if (_dismountFailCount >= 5
            && _targetFateRadius > 0
            && DateTime.UtcNow - _lastDismountRescueAt > TimeSpan.FromSeconds(8))
        {
            _lastDismountRescueAt = DateTime.UtcNow;
            var dest = _targetFatePos;
            LogAction($"dismount stuck after {_dismountFailCount} tries — re-pathfind to FATE center for safer landing");
            _navmesh.PathfindAndMoveCloseTo(dest, fly: true, range: 5f);
            _dismountFailCount = 0;
        }
        return true;
    }

    /// <summary>FFXIV ClassJob row ids for the six tank classes/jobs. Used by
    /// the party "DPS follow tank target" feature to figure out which member of
    /// the party is THE tank (or whether this client should follow one).
    ///   1  Gladiator    19 Paladin
    ///   3  Marauder     21 Warrior
    ///                   32 Dark Knight
    ///                   37 Gunbreaker</summary>
    private static bool IsTankJob(uint classJobId) =>
        classJobId is 1u or 3u or 19u or 21u or 32u or 37u;

    private bool IsCurrentJobTank()
    {
        var p = _objectTable.LocalPlayer;
        return p != null && IsTankJob(p.ClassJob.RowId);
    }

    /// <summary>Find the nearest FATE mob that is currently AGGRO'D ON the party
    /// tank — i.e. a mob whose TargetObjectId == tank.GameObjectId. Returning
    /// the tank's hard target (the old behaviour) caused the DPS to steal the
    /// tank's pull while the tank was still walking toward an un-engaged mob.
    /// Now the DPS waits until the mob has actually engaged the tank, then
    /// dog-piles on it. Stays scoped to the active FATE so we don't drag the
    /// DPS off-target onto ambient combat in someone else's pull.</summary>
    private unsafe IBattleNpc? FindMobAggroedOnTank()
    {
        if (_partyList.Length <= 1) return null;
        ulong tankObjId = 0;
        for (int i = 0; i < _partyList.Length; i++)
        {
            var pm = _partyList[i];
            if (pm == null) continue;
            if (!IsTankJob(pm.ClassJob.RowId)) continue;
            var tankGo = pm.GameObject;
            if (tankGo == null) continue;
            tankObjId = tankGo.GameObjectId;
            break;
        }
        if (tankObjId == 0) return null;

        var me = _objectTable.LocalPlayer;
        var myPos = me?.Position ?? Vector3.Zero;
        IBattleNpc? best = null;
        float bestDist = float.MaxValue;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc bn) continue;
            if (bn.IsDead) continue;
            if (bn.TargetObjectId != tankObjId) continue;
            var go = (CSGameObject*)(void*)bn.Address;
            if (go == null) continue;
            if (go->FateId == 0 || go->FateId != _targetFateId) continue;
            var d = Vector3.Distance(bn.Position, myPos);
            if (d < bestDist) { best = bn; bestDist = d; }
        }
        return best;
    }

    /// <summary>Identity stamp folded into the session log filename so two clients
    /// running side-by-side produce one file per character ("…_RefiaSaronia@Mumi.log").
    /// Falls back to "anon" if we're called before login.</summary>
    private string BuildCharacterTag()
    {
        var p = _objectTable.LocalPlayer;
        if (p == null) return "anon";
        var name = p.Name.TextValue;
        string world = "";
        try { world = p.HomeWorld.Value.Name.ExtractText(); } catch { /* lumina shape varies */ }
        return string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
    }

    private void LogAction(string msg)
    {
        LastAction = msg;
        var line = $"[{DateTime.Now:HH:mm:ss}] {State}: {msg}";
        _logRing.Enqueue(line);
        while (_logRing.Count > LogRingCapacity) _logRing.Dequeue();
        _log.Information($"FateWalker [{State}] {msg}");
        _fileLogger.Append(line);
        RecordLogFingerprint(msg);
    }

    /// <summary>
    /// Track a normalized fingerprint of this log message for the anti-loop
    /// watchdog. We strip numbers, IDs, durations and True/False booleans so
    /// repeated calls with different values (e.g. retry counters, distances)
    /// collapse to the same key.
    ///
    /// Excludes pure heartbeat lines (HP %, "stats ·") so they don't drown
    /// out real loop signals — those fire on a fixed cadence anyway.
    /// </summary>
    private void RecordLogFingerprint(string msg)
    {
        // ── Diagnostics on a fixed cadence — not loop signal ──
        if (msg.StartsWith("stats ", StringComparison.Ordinal)) return;
        if (msg.StartsWith("HP ", StringComparison.Ordinal)) return;
        if (msg.Contains("panic threshold", StringComparison.Ordinal)) return;
        if (msg.Contains("watchdog: no movement", StringComparison.Ordinal)) return;
        if (msg.StartsWith("logic-loop", StringComparison.Ordinal)) return; // self-emit

        // ── Normal-combat patterns — these fire on every kill / retarget in a
        // healthy farming run and would false-positive the watchdog when the
        // bot is happily chaining mobs. A real combat stall already shows up
        // via the movement watchdog and via HP/state-transition silence.
        if (msg.Contains("FATE-target [", StringComparison.Ordinal)) return;
        if (msg.StartsWith("force-pull ", StringComparison.Ordinal)) return;
        if (msg.Contains("BossMod AutoTarget", StringComparison.Ordinal)) return;
        if (msg.StartsWith("BossMod.SetTargetRange", StringComparison.Ordinal)) return;
        if (msg.StartsWith("Dismount (pre-engage)", StringComparison.Ordinal)) return;
        if (msg.StartsWith("RSR.ActivateAuto", StringComparison.Ordinal)) return;
        if (msg.StartsWith("RSR.Activate", StringComparison.Ordinal)) return;
        if (msg.StartsWith("BossMod.Activate", StringComparison.Ordinal)) return;
        if (msg.StartsWith("RSR.Deactivate", StringComparison.Ordinal)) return;
        if (msg.StartsWith("BossMod.Deactivate", StringComparison.Ordinal)) return;
        // FATE-done lines vary by name but always represent forward progress.
        if (msg.StartsWith("FATE done:", StringComparison.Ordinal)) return;
        // Pull commit drops naturally every time a mob dies and falls out
        // of the object table — during an AoE-heavy fight against 4-5 mobs
        // we get one of these every few seconds. Not a stuck signal.
        if (msg.StartsWith("pull commit dropped", StringComparison.Ordinal)) return;
        // stuck-jump is the watchdog's response to OTHER stuck signals — if
        // those higher-level signals warrant escalation, they'll trip the
        // detector themselves. Counting our own recovery action as a "loop"
        // double-counts the same event and prematurely session-disables the
        // FATE (see v1.2.2.0 regression where bot abandoned active FATEs).
        if (msg.StartsWith("watchdog: stuck-jump", StringComparison.Ordinal)) return;
        // collect-FATE per-tick chatter — fires every loop while we wait
        // for FateUtils to walk us to the next pickup.
        if (msg.StartsWith("collect-FATE:", StringComparison.Ordinal)) return;
        // Forlorn / Forlorn Maiden re-target logs fire repeatedly as long
        // as the elite is alive but in committed-target check loop.
        if (msg.Contains("[forlorn priority]", StringComparison.Ordinal)) return;
        // BossMod retarget mode flips between Never / NoTarget every time
        // we enter/leave a collect-pickup window — chatter, not stuck.
        if (msg.StartsWith("BossMod AutoTarget Retarget →", StringComparison.Ordinal)) return;

        var fp = State + "|" + LoopFingerprintStripper.Replace(msg, "*");
        _logFingerprints.AddLast((DateTime.UtcNow, fp));
        // Cap by both window length and count.
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(120);
        while (_logFingerprints.First != null && _logFingerprints.First.Value.At < cutoff)
            _logFingerprints.RemoveFirst();
        while (_logFingerprints.Count > 80) _logFingerprints.RemoveFirst();
    }

    /// <summary>
    /// Anti-loop watchdog. Runs every ~15s from Tick. If any single fingerprint
    /// fires ≥ 8 times within the 2-minute window, treat it as a stuck loop
    /// and apply recovery:
    ///   1) Soft reset (up to 2x): blacklist current FATE for the session,
    ///      clear all in-flight state, transition back to Selecting.
    ///   2) After 3 hits without success: escalate to a 15-minute
    ///      PreparingPause — the safe macro-break to a city aetheryte.
    /// Diagnostic-only states (Paused, Dying, Stopped) are skipped so genuine
    /// "waiting" loops don't trigger the watchdog.
    /// </summary>
    private void CheckLogicLoop()
    {
        if (State == FateBotState.Stopped) return;
        if (State == FateBotState.Paused) return;
        if (State == FateBotState.PreparingPause) return;
        if (State == FateBotState.Dying) return;

        if (DateTime.UtcNow - _lastLoopCheckAt < TimeSpan.FromSeconds(15)) return;
        _lastLoopCheckAt = DateTime.UtcNow;
        if (_logFingerprints.Count < 10) return;

        var stuck = _logFingerprints
            .GroupBy(x => x.Fingerprint)
            .Where(g => g.Count() >= 8)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (stuck == null) return;

        _loopRecoveryCount++;
        _sessionLoopRecoveries++;
        var preview = stuck.Key.Length > 90 ? stuck.Key.Substring(0, 90) + "…" : stuck.Key;
        LogAction($"logic-loop watchdog: '{preview}' fired {stuck.Count()}× in 2 min — recovery #{_loopRecoveryCount}");
        _logFingerprints.Clear();

        // Escalate after 3 strikes in a row.
        if (_loopRecoveryCount >= 3)
        {
            _loopRecoveryCount = 0;
            LogAction("logic-loop: 3rd recovery in a row — escalating to PreparingPause(15m)");
            EnterPauseSafely(15, "logic-loop watchdog", resetSessionTimer: false);
            return;
        }

        // Soft recovery: blacklist current FATE for the session so we don't
        // re-pick it, abort any in-flight pathing/teleport, deactivate combat
        // AI, drop back to Selecting for a clean re-pick.
        //
        // BUT: if the trip came from a stuck-in-combat / panic-flee pattern,
        // the FATE itself isn't broken — a tough fight against a high-HP boss
        // (esp. with two clients sharing the framerate) just took long enough
        // for the recovery to fire repeatedly. Blacklisting would lock us out
        // of every legitimate hard FATE in the zone. Skip the disable in that
        // case and let Selecting re-pick the same FATE on the next cycle, by
        // which time HP is restored and aggro is shed.
        // Patterns that indicate transient geometry / combat / panic issues, NOT a
        // broken FATE. Blacklisting on these would lock the bot out of every
        // legitimate-but-slightly-off-mesh FATE (especially with party formation
        // where an offset point can land slightly off-walkable terrain).
        var k = stuck.Key;
        bool transient =
               k.Contains("in combat",          StringComparison.Ordinal)
            || k.Contains("panic",              StringComparison.Ordinal)
            || k.Contains("cancel path",        StringComparison.Ordinal)   // 15s re-pathfind
            || k.Contains("re-pathfind",        StringComparison.Ordinal)
            || k.Contains("flee before",        StringComparison.Ordinal)   // 30s flee-before-teleport
            || k.Contains("outside FATE radius",StringComparison.Ordinal)   // v1.4.2 boss-escape recovery
            || k.Contains("dropping commit",    StringComparison.Ordinal)
            || k.Contains("returning to centre",StringComparison.Ordinal);
        if (_targetFateId != 0 && !transient)
        {
            _sessionDisabledFateIds.Add(_targetFateId);
            LogAction($"logic-loop: session-disabling FATE {_targetFateId} '{_targetFateName}' so a different pick happens");
        }
        else if (_targetFateId != 0)
        {
            LogAction($"logic-loop: NOT disabling FATE {_targetFateId} '{_targetFateName}' " +
                      "(transient pattern — combat/panic/repathfind, FATE itself is fine)");
        }
        _targetFateId = 0;
        _targetFateName = "";
        _targetMotivationNpcId = 0;
        try { _navmesh.Stop(); } catch {}
        try { _lifestream.Abort(); } catch {}
        if (_rsrActivated)     { try { _rsr.Deactivate();     } catch {} _rsrActivated = false; }
        if (_bossmodActivated) { try { _bossmod.Deactivate(); } catch {} _bossmodActivated = false; }
        Transition(FateBotState.Selecting);
    }
}
