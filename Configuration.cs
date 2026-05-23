using System.Collections.Generic;
using Dalamud.Configuration;

namespace FateWalker;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool LevelSyncAlwaysEnable { get; set; } = true;

    public bool EnableShB { get; set; } = true;
    public bool EnableEW { get; set; } = true;
    public bool EnableDT { get; set; } = true;

    /// <summary>
    /// When true, skip FATEs in zones where the player has already maxed their
    /// Shared FATE rank (3 for ShB/EW, 4 for DT). Lets the user focus farming
    /// on zones with remaining Shared FATE progress to gain.
    ///
    /// Rank values come from <see cref="Data.SharedFateProgress.ReadAll"/>
    /// which merges live agent data (only fresh while the in-game SharedFate
    /// window is open) with a locally tracked count snapshotted from the
    /// agent and incremented on every FATE completion (see
    /// <see cref="LocalSharedFateProgress"/>).
    /// </summary>
    public bool SkipMaxedSharedFateZones { get; set; } = false;

    /// <summary>
    /// Locally tracked Shared FATE progress per territory. Snapshotted from
    /// AgentFateProgress whenever the user opens the in-game SharedFate
    /// window (its values are live then), then incremented by the bot every
    /// time a FATE completes in that territory. Lets us know the player's
    /// rank without re-opening the window after each FATE.
    /// </summary>
    public Dictionary<uint, LocalRankSnapshot> LocalSharedFateProgress { get; set; } = new();

    public sealed class LocalRankSnapshot
    {
        public byte Rank { get; set; }
        public ushort Progress { get; set; }
        public ushort Needed { get; set; }   // 0 means "not yet known from agent"
        public string LastSyncedIso { get; set; } = "";
        public int IncrementsSinceSync { get; set; }  // bot-counted +1s; resets on agent sync
    }

    public HashSet<ushort> BlacklistedFateIds { get; set; } = new();

    /// <summary>
    /// Catalog of FATEs the bot has observed at least once, keyed by FateId.
    /// Populated automatically as the selector enumerates active FATEs. Used
    /// to build the Banlist UI so the user can blacklist FATEs by name even
    /// when not currently in that zone. Persisted with config.
    /// </summary>
    public Dictionary<ushort, ObservedFate> ObservedFates { get; set; } = new();

    public sealed class ObservedFate
    {
        public string Name { get; set; } = "";
        public ushort Level { get; set; }
        public uint TerritoryId { get; set; }
    }

    public bool PrioritizeBonusFates { get; set; } = true;

    public int MinLevelDelta { get; set; } = -12;

    public int FateTimeRemainingMinSec { get; set; } = 60;

    /// <summary>
    /// When true, the controller logs what it would do but performs no game actions.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Name substring patterns (case-insensitive) that auto-reject the FATE.
    /// Defaults seeded with the "chain boss" FATEs in DT/EW/ShB that have
    /// mechanics dangerous for an auto-bot.
    /// </summary>
    public List<string> BlacklistedFateNamePatterns { get; set; } =
    [
        "Ttokrrone",       // DT Shaaloani world boss
        "Mascot Murder",   // DT Living Memory chain
        "Mica",            // alt name for Mascot Murder chain boss
        "Chi",             // EW Garlemald (be careful — substring match)
        "Daivadipa",       // EW Thavnair
        "Formidable",      // ShB Lakeland
        "Archaeotania",    // ShB Tempest
    ];

    /// <summary>
    /// How close to a FATE's position is "in range" — multiplier of FATE radius.
    /// 0.7 = stop just inside the boundary so we're guaranteed level-synced.
    /// </summary>
    public float EngageRangeMultiplier { get; set; } = 0.7f;

    public enum CombatBackendKind
    {
        BossMod,    // BossMod xan job module casts skills. Simplest, single dependency.
        RSR,        // BossMod for AI (target + movement + AoE avoid) + RotationSolverReborn for rotation.
        Manual,     // BossMod for AI only. User controls all skill output (or uses another plugin).
    }

    public CombatBackendKind CombatBackend { get; set; } = CombatBackendKind.BossMod;

    /// <summary>
    /// When CombatBackend = RSR, use RSR's Auto mode with "Farthest" targeting
    /// instead of Manual mode (which casts on BossMod-selected target). Auto+
    /// Farthest pulls along the way — bot moves toward farthest mob and AoEs
    /// closer ones en route. When false (default), BossMod picks the closest
    /// target and RSR casts on it.
    /// </summary>
    public bool RsrUseAutoFarthest { get; set; } = false;

    /// <summary>
    /// Cross-zone working set. TerritoryType IDs (from TerritoryMap) the bot is
    /// allowed to rotate between. Empty = current zone only (no cross-zone hops).
    /// Recommended: 3–4 zones per session — covers drought without paying excessive
    /// teleport cost. See reference_zone_rotation_strategy.md.
    /// </summary>
    public HashSet<uint> WorkingSetZones { get; set; } = new();

    /// <summary>
    /// Seconds with no eligible FATE in current zone before triggering rotation
    /// to the next zone in WorkingSetZones. 180s matches manual farmer cadence
    /// (Comfywizard / Accomp guides).
    /// </summary>
    public int MinDroughtSeconds { get; set; } = 180;

    /// <summary>
    /// In-zone long-range teleport threshold (yalms). When the picked FATE is
    /// farther than this, the bot teleports to the zone's primary aetheryte
    /// first and flies the rest, instead of flying the full distance.
    ///
    /// Math: flying ≈ 20 y/s. Teleport overhead (cast + loading) ≈ 12 s, so
    /// teleport saves time once it cuts > 240 y of flying. 1500 y is a safe
    /// default — by then teleport is faster by a comfortable margin even if
    /// the aetheryte sits halfway between player and FATE.
    /// </summary>
    public int LongRangeTeleportYalms { get; set; } = 1500;

    /// <summary>
    /// How long to wait after death for a raise before giving up and clicking
    /// "Return to home aetheryte". 30s is the convention.
    /// </summary>
    public int RaiseGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Gear durability % below which the bot pauses farming to repair. 30% is
    /// the standard "still safe, not yet broken" threshold (items break at 0).
    /// </summary>
    public int RepairAtDurabilityPercent { get; set; } = 30;

    /// <summary>
    /// Auto-route to the in-zone Mender NPC (per <see cref="Data.MenderMap"/>)
    /// when min durability drops below <see cref="RepairAtDurabilityPercent"/>.
    /// </summary>
    public bool EnableAutoRepair { get; set; } = true;

    /// <summary>
    /// While engaging, force the player's target to a BattleNpc whose
    /// <c>GameObject.FateId</c> matches the current FATE — overriding RSR/BossMod
    /// picks that might land on a non-FATE aggressive mob (e.g. wandering
    /// Anemones during an Il Mheg Morpho FATE). Highly recommended.
    /// </summary>
    public bool RestrictTargetingToFateMobs { get; set; } = true;

    /// <summary>
    /// Maximum number of FATE mobs that may have aggro on the player before the
    /// bot stops pulling new ones and clears existing aggro first. Prevents the
    /// constant target-switching loop when the bot tries to run between many
    /// distant mobs without committing to a kill.
    /// 3 is a safe default; tanks doing wall-to-wall can crank to 5–8.
    /// </summary>
    public int MaxAggroCount { get; set; } = 3;

    /// <summary>
    /// When enabled, the bot bails out of a FATE if HP drops dangerously low
    /// AND Second Wind is on cooldown. Walks outside the FATE radius to drop
    /// level sync, then waits for HP to recover before re-engaging.
    /// </summary>
    public bool EnablePanicEscape { get; set; } = true;

    /// <summary>
    /// HP % below which panic-escape triggers (default 25 = 25% of MaxHP).
    /// </summary>
    public int PanicHpPercent { get; set; } = 25;

    /// <summary>
    /// HP % the bot must recover above before resuming after a panic. Should be
    /// well above PanicHpPercent so we don't oscillate.
    /// </summary>
    public int RecoverHpPercent { get; set; } = 90;

    // ─────────────────────────────── Humanize ─────────────────────────────
    // Random jitter at decision points to avoid mechanically perfect cadence
    // (anti-detection signal per reference_bot_safety_detection.md). All
    // ranges are inclusive-low, exclusive-high.

    /// <summary>Master toggle for humanize delays.</summary>
    public bool EnableHumanize { get; set; } = true;

    /// <summary>"Thinking" delay before committing to a picked FATE (seconds).</summary>
    public int ThinkBeforePickMinSec { get; set; } = 1;
    public int ThinkBeforePickMaxSec { get; set; } = 4;

    /// <summary>Hesitation after drought hits, before firing the teleport.</summary>
    public int HesitateBeforeTeleportMinSec { get; set; } = 2;
    public int HesitateBeforeTeleportMaxSec { get; set; } = 6;

    /// <summary>Post-FATE rest (added on top of base RecoverySeconds).</summary>
    public int PostFateRestMinSec { get; set; } = 2;
    public int PostFateRestMaxSec { get; set; } = 6;

    /// <summary>
    /// Variance applied to the 1500 ms pathfind-throttle. 0 = none; 500 means
    /// the throttle randomly lands between 1000 and 2000 ms each call.
    /// </summary>
    public int PathfindJitterMs { get; set; } = 500;

    /// <summary>FATE-mob retarget delay range (ms). Simulates human reaction time.</summary>
    public int TargetingDelayMinMs { get; set; } = 300;
    public int TargetingDelayMaxMs { get; set; } = 1500;

    /// <summary>
    /// Random delay (ms) before clicking through NPC dialog confirmations
    /// (Interacting + Repairing SelectYesno). Makes the FATE-start / Repair
    /// flow look like a human reading the prompt, not a bot. Dying-state
    /// Return prompt is kept instant — no reason to make the player wait dead.
    /// </summary>
    public int DialogClickDelayMinMs { get; set; } = 500;
    public int DialogClickDelayMaxMs { get; set; } = 2000;

    /// <summary>
    /// Random delay (ms) after closing in on an NPC, before firing InteractWith.
    /// Simulates "look at NPC, decide to talk".
    /// </summary>
    public int InteractApproachDelayMinMs { get; set; } = 800;
    public int InteractApproachDelayMaxMs { get; set; } = 2200;

    /// <summary>
    /// Random offset applied to the FATE landing destination (yalms). Each
    /// FATE visit will pick a slightly different drop spot — prevents pixel-
    /// perfect repeated landings.
    /// </summary>
    public int WaypointJitterYalms { get; set; } = 5;

    // ─────────────────────────────── Safety stops ─────────────────────────

    /// <summary>
    /// Auto-stop the bot if a tell/say chat line containing the player's name
    /// arrives, or any GM-channel chat (XivChatType 80–94). Reference docs
    /// flag player reports as the dominant ban vector.
    /// </summary>
    public bool EnableChatSafetyStop { get; set; } = true;

    /// <summary>
    /// Minutes to pause after a chat safety trigger before auto-resuming.
    /// 0 = hard stop (manual restart only). 10 min = "AFK reading chat" cover.
    /// </summary>
    public int ChatStopPauseMinutes { get; set; } = 10;

    /// <summary>
    /// Hard session cap (hours). Bot stops or pauses after this many continuous
    /// hours. 4 hours matches Miqobot-era community consensus.
    ///
    /// At runtime the actual cap is rolled in [SessionCapHours − Jitter,
    /// SessionCapHours + Jitter] so a watcher can't predict the bot's
    /// break cadence — a fixed 4h-on / 30m-off cycle is a strong bot signal.
    /// </summary>
    public int SessionCapHours { get; set; } = 4;

    /// <summary>± jitter applied to <see cref="SessionCapHours"/> per session
    /// (in HOURS, fractional). 0 = no jitter. Default 0.5 → effective cap
    /// lands somewhere in [3.5 h, 4.5 h] each time.</summary>
    public double SessionCapHoursJitter { get; set; } = 0.5;

    /// <summary>
    /// Minutes to pause (macro-break) when the session cap is hit before
    /// auto-resuming with a fresh session timer. 0 = hard stop. 30 min matches
    /// the article's "macro-break" pattern (return to city + AFK).
    ///
    /// As with SessionCapHours, the actual pause duration is rolled within
    /// ± Jitter on each trigger.
    /// </summary>
    public int SessionCapPauseMinutes { get; set; } = 30;

    /// <summary>± jitter applied to <see cref="SessionCapPauseMinutes"/> per
    /// trigger. Default 10 → 20–40 min pauses.</summary>
    public int SessionCapPauseMinutesJitter { get; set; } = 10;

    // ─────────────────────────────── Trading ─────────────────────────────

    /// <summary>
    /// Enable auto-trading: when Bicolor gemstone count reaches the trigger,
    /// the bot teleports to the configured vendor and buys items on the
    /// <see cref="TradingShoppingList"/>.
    /// </summary>
    public bool EnableAutoTrading { get; set; } = false;

    /// <summary>
    /// Gem count at which auto-trading is triggered. Default 1350 = 90% of
    /// the 1500 cap (safe margin so we don't waste FATE rewards).
    /// </summary>
    public int TradingTriggerGems { get; set; } = 1350;

    /// <summary>
    /// Shopping list — set of Item IDs (Lumina row) the user wants to buy.
    /// Iteration order is by ascending gem cost (cheapest first) so Vouchers
    /// drain before the bot moves to high-cost items.
    /// </summary>
    public HashSet<uint> TradingShoppingList { get; set; } = new();

    /// <summary>
    /// Per-item buy cap. Key = item ID, value = max stack the bot is allowed
    /// to hold across the entire inventory (including Armory/Equipped). When
    /// the player's count of that item reaches the cap, the bot skips it and
    /// moves to the next item on the shopping list. Items without an entry
    /// here (or value &lt;= 0) buy until gem reserve hits the configured floor.
    /// </summary>
    public Dictionary<uint, int> TradingItemLimits { get; set; } = new();

    /// <summary>
    /// Runtime-discovered catalog of items each Bicolor vendor actually
    /// stocks at the player's current Shared FATE rank. Filled by the
    /// "Survey" buttons in the Trading tab; supersedes the static
    /// <c>VendorCatalog.cs</c> placeholder costs once observed.
    /// Key = Item ID.
    /// </summary>
    public Dictionary<uint, DiscoveredItem> DiscoveredVendorItems { get; set; } = new();

    public sealed class DiscoveredItem
    {
        public uint VendorAetheryteId { get; set; }
        public string VendorName { get; set; } = "";
        public int GemCost { get; set; }
        public string LastSeenIso { get; set; } = "";   // store as string for JSON simplicity
        /// <summary>Item name resolved via Lumina at survey time. Older
        /// entries surveyed before this field existed will read empty.</summary>
        public string ItemName { get; set; } = "";
    }
}
