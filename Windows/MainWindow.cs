using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FateWalker.Controller;
using FateWalker.Data;

namespace FateWalker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly IFateTable _fateTable;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly FateSelector _selector;

    // Throttled save for the ObservedFates catalog — selector marks dirty,
    // we save at most every 30s so disk thrashing stays bounded.
    private DateTime _lastCatalogSaveAt = DateTime.MinValue;
    // Banlist UI search filter.
    private string _banlistSearch = "";
    // Diagnostic dump of AgentFateProgress raw memory — populated when the
    // user clicks "Refresh / Dump" in the Zones tab.
    private List<string>? _sharedFateDump;

    public MainWindow(Plugin plugin, IFateTable fateTable, IClientState clientState, IObjectTable objectTable)
        : base("FateWalker##Main", ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        _fateTable = fateTable;
        _clientState = clientState;
        _objectTable = objectTable;
        _selector = new FateSelector(plugin.Config);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(1200, 1000)
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##fatewalker_tabs"))
        {
            if (ImGui.BeginTabItem("Run"))      { DrawRunTab();      ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Filters"))  { DrawFiltersTab();  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Zones"))    { DrawZonesTab();    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Combat"))   { DrawCombatTab();   ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Safety"))   { DrawSafetyTab();   ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Trading"))  { DrawTradingTab();  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Plugins"))  { DrawPluginsTab();  ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ─────────────────────────────── Header ───────────────────────────────

    private void DrawHeader()
    {
        var c = _plugin.Controller;
        var cfg = _plugin.Config;

        // Start/Stop button + Dry-run + State + Last action — all one row.
        if (c.State == FateBotState.Stopped)
        {
            if (ImGui.Button("▶ Start")) c.Start();
        }
        else
        {
            if (ImGui.Button("■ Stop")) c.Stop();
        }
        ImGui.SameLine();
        var dry = cfg.DryRun;
        if (ImGui.Checkbox("Dry-run", ref dry))
        {
            cfg.DryRun = dry;
            _plugin.SaveConfig();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Log what would happen, no game actions");

        ImGui.SameLine();
        ImGui.TextColored(StateColor(c.State), $"State: {c.State}");

        // Target + Last action — compact.
        if (c.State != FateBotState.Stopped && !string.IsNullOrEmpty(c.TargetFateName))
            ImGui.Text($"Target: {c.TargetFateName} (id={c.TargetFateId})");
        if (!string.IsNullOrEmpty(c.LastAction))
            ImGui.TextDisabled($"Last: {c.LastAction}");

        // Player one-liner.
        var player = _objectTable.LocalPlayer;
        if (player == null)
        {
            ImGui.TextDisabled("Not logged in.");
        }
        else
        {
            var pos = player.Position;
            ImGui.TextDisabled($"{player.Name.TextValue} (Lv {player.Level}) · Territory {_clientState.TerritoryType} · ({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})");
        }

        // Live tracking — Bicolor Gemstones + min equipped durability.
        var gems = CurrencyReader.GetBicolorGemstoneCount();
        if (gems >= 0)
        {
            var pct = gems / (float)CurrencyReader.BicolorGemstoneCap;
            Vector4 gemColor = pct >= 0.9f ? new(1f, 0.3f, 0.3f, 1f)
                             : pct >= 0.7f ? new(1f, 0.7f, 0.3f, 1f)
                             : new(0.4f, 0.9f, 0.4f, 1f);
            ImGui.TextColored(gemColor, $"Gems: {gems} / {CurrencyReader.BicolorGemstoneCap}");
            ImGui.SameLine();
        }
        var dur = _plugin.Controller.LastDurabilityMin;
        Vector4 durColor = dur < 30 ? new(1f, 0.3f, 0.3f, 1f)
                         : dur < 60 ? new(1f, 0.7f, 0.3f, 1f)
                         : new(0.6f, 0.6f, 0.6f, 1f);
        ImGui.TextColored(durColor, $"· Min durability: {dur}%");
    }

    private static Vector4 StateColor(FateBotState s) => s switch
    {
        FateBotState.Stopped     => new(0.6f, 0.6f, 0.6f, 1f),
        FateBotState.Selecting   => new(0.5f, 0.8f, 1.0f, 1f),
        FateBotState.Teleporting => new(0.6f, 0.5f, 1.0f, 1f),
        FateBotState.Mounting    => new(0.9f, 0.7f, 0.3f, 1f),
        FateBotState.Traveling   => new(0.9f, 0.7f, 0.3f, 1f),
        FateBotState.Interacting => new(0.7f, 0.8f, 0.4f, 1f),
        FateBotState.Engaging    => new(0.4f, 0.9f, 0.4f, 1f),
        FateBotState.Dying       => new(0.9f, 0.3f, 0.3f, 1f),
        FateBotState.Paused      => new(0.9f, 0.5f, 0.8f, 1f),
        FateBotState.Repairing   => new(0.5f, 0.9f, 0.9f, 1f),
        FateBotState.Recovering  => new(0.7f, 0.7f, 0.7f, 1f),
        _ => new(1f, 1f, 1f, 1f),
    };

    // ─────────────────────────────── Run tab ──────────────────────────────

    private void DrawRunTab()
    {
        DrawFateListBlock();
        ImGui.Spacing();
        DrawLogBlock();
    }

    private void DrawFateListBlock()
    {
        ImGui.Text($"Active FATEs in current zone ({_fateTable.Length})");

        if (_fateTable.Length == 0)
        {
            ImGui.TextDisabled("No active FATEs.");
            return;
        }

        var player = _objectTable.LocalPlayer;
        var playerPos = player?.Position ?? Vector3.Zero;
        var playerLevel = player?.Level ?? (byte)0;
        var candidates = _selector.Evaluate(_fateTable, _clientState.TerritoryType, playerPos, playerLevel);

        var headerManualId = _plugin.Controller.ManuallyPickedFateId;
        var manualCandidate = headerManualId.HasValue ? candidates.FirstOrDefault(c => c.Fate.FateId == headerManualId.Value) : null;
        var pick = candidates.FirstOrDefault(c => c.PassesFilter);

        if (manualCandidate != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f),
                $"● MANUAL pick: {manualCandidate.Fate.Name.TextValue} (Lv {manualCandidate.Fate.Level}, {manualCandidate.DistanceToPlayer:F0}y away) — auto resumes when ended");
        }
        else if (pick != null)
        {
            if (pick.Fate.HasBonus)
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f),
                    $"★ BONUS pick: {pick.Fate.Name.TextValue} (Lv {pick.Fate.Level}, {pick.DistanceToPlayer:F0}y away)");
            else
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                    $"Next pick: {pick.Fate.Name.TextValue} (Lv {pick.Fate.Level}, {pick.DistanceToPlayer:F0}y away)");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.3f, 1f), "No FATE passes filter right now.");
        }

        if (ImGui.BeginTable("fates", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Pick",   ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Off",    ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("Lv",     ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("Name",   ImGuiTableColumnFlags.WidthStretch, 4f);
            ImGui.TableSetupColumn("State",  ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Dist",   ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Time",   ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Bonus",  ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Filter", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableHeadersRow();

            var controller = _plugin.Controller;
            var manualId = controller.ManuallyPickedFateId;

            foreach (var c in candidates)
            {
                var fateId = c.Fate.FateId;
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var isManual = manualId == fateId;
                string pickSymbol;
                Vector4 pickColor;
                if (isManual)        { pickSymbol = "●"; pickColor = new Vector4(1f, 0.85f, 0.2f, 1f); }
                else if (c == pick)  { pickSymbol = "►"; pickColor = new Vector4(0.4f, 0.9f, 0.4f, 1f); }
                else                 { pickSymbol = "○"; pickColor = new Vector4(0.6f, 0.6f, 0.6f, 0.6f); }
                ImGui.PushStyleColor(ImGuiCol.Text, pickColor);
                if (ImGui.Selectable($"{pickSymbol}##pick_{fateId}", isManual, ImGuiSelectableFlags.None, new Vector2(28f, 0f)))
                    controller.ToggleManualPick(fateId);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isManual ? "Manual pick (click to clear)" : "Click to force-pick this FATE");

                ImGui.TableNextColumn();
                var disabled = controller.IsDisabled(fateId);
                if (ImGui.Checkbox($"##off_{fateId}", ref disabled))
                    controller.ToggleDisable(fateId);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Skip this FATE while it's spawned (cleared automatically when it despawns)");

                ImGui.TableNextColumn(); ImGui.Text($"{c.Fate.Level}");
                ImGui.TableNextColumn(); ImGui.Text(c.Fate.Name.TextValue);
                ImGui.TableNextColumn(); ImGui.Text(c.Fate.State.ToString());
                ImGui.TableNextColumn(); ImGui.Text($"{c.DistanceToPlayer:F0}y");
                ImGui.TableNextColumn();
                if (c.Fate.State == FateState.Running) ImGui.Text($"{c.Fate.TimeRemaining}s");
                else ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (c.Fate.HasBonus) ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "★");
                else ImGui.TextDisabled("-");
                ImGui.TableNextColumn();
                if (c.PassesFilter) ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "ok");
                else ImGui.TextColored(new Vector4(0.7f, 0.5f, 0.5f, 1f), c.RejectReason ?? "?");
            }
            ImGui.EndTable();
        }
    }

    private void DrawLogBlock()
    {
        ImGui.Text("Controller log (last 40 events):");
        if (ImGui.BeginChild("log", new Vector2(0, 160), true))
        {
            foreach (var line in _plugin.Controller.RecentLog)
                ImGui.TextUnformatted(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f)
                ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }

    // ─────────────────────────────── Filters tab ──────────────────────────

    private void DrawFiltersTab()
    {
        var cfg = _plugin.Config;
        var changed = false;

        ImGui.Text("Expansion filters:");
        ImGui.SameLine();
        var shb = cfg.EnableShB;
        if (ImGui.Checkbox("ShB", ref shb)) { cfg.EnableShB = shb; changed = true; }
        ImGui.SameLine();
        var ew = cfg.EnableEW;
        if (ImGui.Checkbox("EW", ref ew)) { cfg.EnableEW = ew; changed = true; }
        ImGui.SameLine();
        var dt = cfg.EnableDT;
        if (ImGui.Checkbox("DT", ref dt)) { cfg.EnableDT = dt; changed = true; }

        ImGui.Spacing();
        ImGui.Text("FATE level vs player:");
        var delta = cfg.MinLevelDelta;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("min level delta", ref delta, -50, 0, "player %d"))
        {
            cfg.MinLevelDelta = delta;
            changed = true;
        }
        ImGui.TextDisabled("Skip FATEs with level below (player + delta). 0 = skip nothing; -5 means ignore FATEs more than 5 levels below your Lv.");

        ImGui.Spacing();
        ImGui.Text("Minimum FATE time remaining:");
        var minTime = cfg.FateTimeRemainingMinSec;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("min time left (s)", ref minTime, 10, 300))
        {
            cfg.FateTimeRemainingMinSec = minTime;
            changed = true;
        }
        ImGui.TextDisabled("Skip FATEs about to expire. Bonus FATEs ignore this cap.");

        if (changed) _plugin.SaveConfig();
    }

    // ─────────────────────────────── Zones tab ────────────────────────────

    private void DrawZonesTab()
    {
        var cfg = _plugin.Config;
        var changed = false;

        ImGui.Text($"Cross-zone working set ({cfg.WorkingSetZones.Count} zones selected)");
        ImGui.TextDisabled("Bot rotates between checked zones. Starting outside any of them teleports to the first immediately; otherwise rotation waits for drought timeout.");

        var drought = cfg.MinDroughtSeconds;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("drought before rotate (s)", ref drought, 10, 600))
        {
            cfg.MinDroughtSeconds = drought;
            changed = true;
        }

        var skipMaxed = cfg.SkipMaxedSharedFateZones;
        if (ImGui.Checkbox("Shared FATE Progress (skip zones at max rank)", ref skipMaxed))
        {
            cfg.SkipMaxedSharedFateZones = skipMaxed;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("— focus farming on zones with rank progress remaining");

        var ranks = SharedFateProgress.ReadAll(_plugin.Config);
        if (!SharedFateProgress.IsLoaded)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                "Open the in-game Shared FATE window once to populate rank data.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh / Dump"))
        {
            _sharedFateDump = SharedFateProgress.DumpRaw(_plugin.Config);
        }
        if (_sharedFateDump != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(string.Join("\n", _sharedFateDump));
            ImGui.SameLine();
            if (ImGui.SmallButton("Close dump")) _sharedFateDump = null;
            if (_sharedFateDump != null && ImGui.BeginChild("##sf_dump",
                new Vector2(0, 180), true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                foreach (var line in _sharedFateDump)
                    ImGui.TextUnformatted(line);
                ImGui.EndChild();
            }
        }

        ImGui.Spacing();
        if (ImGui.BeginTable("##workingset", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            DrawExpansionColumn(Expansion.ShB, "Shadowbringers", cfg.EnableShB, ranks, ref changed);
            DrawExpansionColumn(Expansion.EW,  "Endwalker",      cfg.EnableEW,  ranks, ref changed);
            DrawExpansionColumn(Expansion.DT,  "Dawntrail",      cfg.EnableDT,  ranks, ref changed);
            ImGui.EndTable();
        }

        if (changed) _plugin.SaveConfig();

        ImGui.Spacing();
        ImGui.Separator();
        DrawBanlistBlock();
    }

    /// <summary>
    /// FATE banlist — collapsible per-zone list of every catalogued FATE with
    /// a tick to blacklist. Includes a search filter for the long list.
    /// </summary>
    private void DrawBanlistBlock()
    {
        var cfg = _plugin.Config;

        ImGui.Text($"FATE banlist ({cfg.BlacklistedFateIds.Count} banned)");
        ImGui.SameLine();
        ImGui.TextDisabled("— ticked FATEs are skipped by the selector");

        // Search box.
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##banlist_search", "search by name…", ref _banlistSearch, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear filter")) _banlistSearch = "";
        ImGui.SameLine();
        if (ImGui.SmallButton("Unban all"))
        {
            cfg.BlacklistedFateIds.Clear();
            _plugin.SaveConfig();
        }

        var query = _banlistSearch.Trim();
        bool filtering = query.Length > 0;

        // Pre-group catalog by territory for stable iteration order matching
        // TerritoryMap (so expansions appear in the right order).
        foreach (var zone in TerritoryMap.Zones)
        {
            // Catalog entries for this territory, optionally filtered by name.
            var entries = FateCatalog.All
                .Where(kv => kv.Value.TerritoryId == zone.TerritoryTypeId)
                .Where(kv => !filtering || kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.Level)
                .ThenBy(kv => kv.Value.Name)
                .ToList();
            if (entries.Count == 0) continue;

            int bannedInZone = entries.Count(kv => cfg.BlacklistedFateIds.Contains(kv.Key));
            var header = $"{zone.ZoneName}  ({bannedInZone}/{entries.Count} banned)##{zone.TerritoryTypeId}";

            // Auto-open zone when filtering so matches are visible.
            if (filtering) ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            if (ImGui.CollapsingHeader(header))
            {
                ImGui.Indent(12f);
                foreach (var (fateId, entry) in entries)
                {
                    var ticked = cfg.BlacklistedFateIds.Contains(fateId);
                    // Compact row — fixed-width level prefix for alignment.
                    if (ImGui.Checkbox($"Lv{entry.Level,-3}  {entry.Name}##ban_{fateId}", ref ticked))
                    {
                        if (ticked) cfg.BlacklistedFateIds.Add(fateId);
                        else        cfg.BlacklistedFateIds.Remove(fateId);
                        _plugin.SaveConfig();
                    }
                }
                ImGui.Unindent(12f);
            }
        }
    }

    private void DrawExpansionColumn(
        Expansion exp,
        string label,
        bool expansionEnabled,
        Dictionary<uint, SharedFateZoneState> ranks,
        ref bool changed)
    {
        var cfg = _plugin.Config;
        ImGui.TableNextColumn();
        if (!expansionEnabled) ImGui.BeginDisabled();
        ImGui.TextDisabled(label);
        foreach (var zone in TerritoryMap.Zones.Where(z => z.Expansion == exp))
        {
            var ticked = cfg.WorkingSetZones.Contains(zone.TerritoryTypeId);
            if (ImGui.Checkbox($"{zone.ZoneName}##{zone.TerritoryTypeId}", ref ticked))
            {
                if (ticked) cfg.WorkingSetZones.Add(zone.TerritoryTypeId);
                else        cfg.WorkingSetZones.Remove(zone.TerritoryTypeId);
                changed = true;
            }
            // Rank/progress hint inline. Greyed when not loaded; gold when maxed;
            // default white otherwise. Helps the user see at a glance which
            // zones are worth keeping in the working set under "Skip maxed".
            ImGui.SameLine();
            // Only trust the rank readout when MaxRank is in the valid range
            // (3 or 4). Other values mean the agent slot hasn't been opened
            // in-game yet and contains uninitialised bytes.
            if (ranks.TryGetValue(zone.TerritoryTypeId, out var state) && state.HasValidRank)
            {
                var color = state.IsMaxed
                    ? new Vector4(1f, 0.85f, 0.3f, 1f)   // gold = capped
                    : new Vector4(0.7f, 0.85f, 1f, 1f);  // light blue = in progress
                var max = state.ExpectedMaxRank;
                var label2 = state.IsMaxed
                    ? $"R{state.CurrentRank}/{max} MAX"
                    : $"R{state.CurrentRank}/{max} {state.Progress}/{state.Needed}";
                ImGui.TextColored(color, label2);
            }
            else
            {
                ImGui.TextDisabled("R?");
            }
        }
        if (!expansionEnabled) ImGui.EndDisabled();
    }

    // ─────────────────────────────── Combat tab ───────────────────────────

    private void DrawCombatTab()
    {
        var cfg = _plugin.Config;
        var current = cfg.CombatBackend;

        ImGui.Text("Combat backend:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##combatbackend", current.ToString()))
        {
            foreach (Configuration.CombatBackendKind kind in System.Enum.GetValues(typeof(Configuration.CombatBackendKind)))
            {
                if (ImGui.Selectable(kind.ToString(), kind == current))
                {
                    cfg.CombatBackend = kind;
                    _plugin.SaveConfig();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(current switch
        {
            Configuration.CombatBackendKind.BossMod => "(BossMod xan rotation + AI)",
            Configuration.CombatBackendKind.RSR     => "(BossMod AI + RSR rotation)",
            Configuration.CombatBackendKind.Manual  => "(BossMod AI only — you cast)",
            _ => "",
        });

        if (current == Configuration.CombatBackendKind.RSR)
        {
            ImGui.Indent(16f);
            ImGui.TextDisabled("RSR runs in Manual mode — casts on the mob the bot locks. " +
                              "Pull strategy is fixed to nearest-unaggro + sticky commit, " +
                              "respecting the Max aggro setting below.");
            ImGui.Unindent(16f);
        }

        ImGui.Separator();
        ImGui.Text("Targeting:");

        var restrict = cfg.RestrictTargetingToFateMobs;
        if (ImGui.Checkbox("Pin targeting to FATE mobs only", ref restrict))
        {
            cfg.RestrictTargetingToFateMobs = restrict;
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Ignore wandering aggro / non-FATE mobs during Engaging");

        if (restrict)
        {
            ImGui.Indent(16f);
            var maxAggro = cfg.MaxAggroCount;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("max aggro before clearing", ref maxAggro, 1, 10))
            {
                cfg.MaxAggroCount = maxAggro;
                _plugin.SaveConfig();
            }
            ImGui.TextDisabled("Stop pulling new mobs while ≥ N are already on you. Tanks doing wall-to-wall can crank to 5–8.");
            ImGui.Unindent(16f);
        }
    }

    // ─────────────────────────────── Safety tab ───────────────────────────

    private void DrawSafetyTab()
    {
        var cfg = _plugin.Config;

        // Humanize — random jitter to mask mechanically perfect cadence.
        ImGui.Text("Humanize");
        var hum = cfg.EnableHumanize;
        if (ImGui.Checkbox("Enable random jitter at decision points", ref hum))
        {
            cfg.EnableHumanize = hum;
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("anti-detection — mimics human deliberation");

        if (hum)
        {
            ImGui.Indent(16f);
            DrawIntRange("Think before pick (s)",       cfg.ThinkBeforePickMinSec,         cfg.ThinkBeforePickMaxSec,         0, 15,
                (lo, hi) => { cfg.ThinkBeforePickMinSec = lo;         cfg.ThinkBeforePickMaxSec = hi; });
            DrawIntRange("Hesitate before teleport (s)", cfg.HesitateBeforeTeleportMinSec, cfg.HesitateBeforeTeleportMaxSec, 0, 30,
                (lo, hi) => { cfg.HesitateBeforeTeleportMinSec = lo; cfg.HesitateBeforeTeleportMaxSec = hi; });
            DrawIntRange("Post-FATE rest (s)",           cfg.PostFateRestMinSec,           cfg.PostFateRestMaxSec,           0, 180,
                (lo, hi) => { cfg.PostFateRestMinSec = lo;           cfg.PostFateRestMaxSec = hi; });

            DrawIntRange("Targeting reaction (ms)", cfg.TargetingDelayMinMs, cfg.TargetingDelayMaxMs, 0, 3000,
                (lo, hi) => { cfg.TargetingDelayMinMs = lo; cfg.TargetingDelayMaxMs = hi; });
            DrawIntRange("NPC approach delay (ms)", cfg.InteractApproachDelayMinMs, cfg.InteractApproachDelayMaxMs, 0, 5000,
                (lo, hi) => { cfg.InteractApproachDelayMinMs = lo; cfg.InteractApproachDelayMaxMs = hi; });
            DrawIntRange("Dialog click delay (ms)", cfg.DialogClickDelayMinMs, cfg.DialogClickDelayMaxMs, 0, 5000,
                (lo, hi) => { cfg.DialogClickDelayMinMs = lo; cfg.DialogClickDelayMaxMs = hi; });

            var jitter = cfg.PathfindJitterMs;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("pathfind throttle jitter (ms)", ref jitter, 0, 1000))
            {
                cfg.PathfindJitterMs = jitter;
                _plugin.SaveConfig();
            }

            var waypoint = cfg.WaypointJitterYalms;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("landing waypoint jitter (y)", ref waypoint, 0, 20))
            {
                cfg.WaypointJitterYalms = waypoint;
                _plugin.SaveConfig();
            }
            ImGui.TextDisabled("FATE landing drops at a random offset within this radius (no two visits land on the same pixel).");
            ImGui.Unindent(16f);
        }

        ImGui.Separator();
        ImGui.Text("Safety stops");
        var chatStop = cfg.EnableChatSafetyStop;
        if (ImGui.Checkbox("React to /tell, /say with my name, or GM chat", ref chatStop))
        {
            cfg.EnableChatSafetyStop = chatStop;
            _plugin.SaveConfig();
        }
        if (chatStop)
        {
            ImGui.Indent(16f);
            var chatPause = cfg.ChatStopPauseMinutes;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("pause minutes after chat (0 = hard stop)", ref chatPause, 0, 60))
            {
                cfg.ChatStopPauseMinutes = chatPause;
                _plugin.SaveConfig();
            }
            ImGui.TextDisabled(chatPause == 0
                ? "Hard stop — manual restart required."
                : $"Stop combat + go idle for {chatPause} min, then resume. Session timer keeps counting.");
            ImGui.Unindent(16f);
        }

        ImGui.Spacing();
        var capH = cfg.SessionCapHours;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("session cap (hours)", ref capH, 0, 12))
        {
            cfg.SessionCapHours = capH;
            _plugin.SaveConfig();
        }
        if (capH > 0)
        {
            ImGui.Indent(16f);
            var capPause = cfg.SessionCapPauseMinutes;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("macro-break minutes (0 = hard stop)", ref capPause, 0, 120))
            {
                cfg.SessionCapPauseMinutes = capPause;
                _plugin.SaveConfig();
            }
            ImGui.TextDisabled(capPause == 0
                ? "Hard stop at cap. Manual restart required."
                : $"At cap, pause for {capPause} min then resume with a fresh {capH}h timer.");
            ImGui.Unindent(16f);
        }
        else
        {
            ImGui.TextDisabled("0 = no cap (not recommended).");
        }

        ImGui.Separator();
        ImGui.Text("Panic-escape on low HP");
        var panic = cfg.EnablePanicEscape;
        if (ImGui.Checkbox("Enable##panic", ref panic))
        {
            cfg.EnablePanicEscape = panic;
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Bail when HP < threshold AND Second Wind on cd");

        if (panic)
        {
            ImGui.Indent(16f);
            var panicHp = cfg.PanicHpPercent;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("panic at HP%", ref panicHp, 5, 50))
            {
                cfg.PanicHpPercent = panicHp;
                _plugin.SaveConfig();
            }
            var recoverHp = cfg.RecoverHpPercent;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("resume at HP%", ref recoverHp, 50, 100))
            {
                cfg.RecoverHpPercent = recoverHp;
                _plugin.SaveConfig();
            }
            ImGui.TextDisabled("Bot walks outside FATE radius to drop level sync, then waits for HP regen + out-of-combat.");
            ImGui.Unindent(16f);
        }

        ImGui.Separator();
        ImGui.Text("Gear durability");
        var repairPct = cfg.RepairAtDurabilityPercent;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("repair below %", ref repairPct, 5, 90))
        {
            cfg.RepairAtDurabilityPercent = repairPct;
            _plugin.SaveConfig();
        }
        var autoRepair = cfg.EnableAutoRepair;
        if (ImGui.Checkbox("Auto-route to in-zone Mender NPC", ref autoRepair))
        {
            cfg.EnableAutoRepair = autoRepair;
            _plugin.SaveConfig();
        }
        ImGui.TextDisabled($"Current min: {_plugin.Controller.LastDurabilityMin}%. Bot teleports to the closest Mender (per MenderMap), interacts, clicks Repair All, and resumes.");

        ImGui.Separator();
        ImGui.Text("Death recovery");
        var grace = cfg.RaiseGraceSeconds;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("raise grace (s)", ref grace, 5, 120))
        {
            cfg.RaiseGraceSeconds = grace;
            _plugin.SaveConfig();
        }
        ImGui.TextDisabled("Wait this long for a raise before clicking Return-to-home. Bot then teleports back to the FATE zone if it's in the working set.");
    }

    // ─────────────────────────────── Trading tab ─────────────────────────

    private void DrawTradingTab()
    {
        var cfg = _plugin.Config;

        // Header — enable + threshold.
        var enable = cfg.EnableAutoTrading;
        if (ImGui.Checkbox("Enable auto-trading", ref enable))
        {
            cfg.EnableAutoTrading = enable;
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("teleport to chosen NPC and buy from shopping list when gems hit trigger");

        var gems = CurrencyReader.GetBicolorGemstoneCount();
        if (gems >= 0)
        {
            ImGui.TextDisabled($"Current gems: {gems} / {CurrencyReader.BicolorGemstoneCap}");
        }

        var trigger = cfg.TradingTriggerGems;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("trigger at gems ≥", ref trigger, 100, CurrencyReader.BicolorGemstoneCap))
        {
            cfg.TradingTriggerGems = trigger;
            _plugin.SaveConfig();
        }
        ImGui.TextDisabled($"Bot will route to vendor when gemstone count reaches {trigger}.");

        ImGui.Separator();
        ImGui.Text($"Hub-vendor survey ({cfg.DiscoveredVendorItems.Count} items learned)");
        ImGui.TextDisabled("Hub vendors (Gramsol/Sajareen/Beryl) require MAX Shared FATE rank in ALL zones of their expansion. If you're not max-rank yet, skip these and use the per-vendor Survey buttons in the tree below instead.");
        if (ImGui.Button("Survey ShB (Gramsol)")) _plugin.Controller.TriggerSurvey(Expansion.ShB);
        ImGui.SameLine();
        if (ImGui.Button("Survey EW (Sajareen)")) _plugin.Controller.TriggerSurvey(Expansion.EW);
        ImGui.SameLine();
        if (ImGui.Button("Survey DT (Beryl)")) _plugin.Controller.TriggerSurvey(Expansion.DT);

        ImGui.Separator();
        ImGui.Text($"Shopping list ({cfg.TradingShoppingList.Count} items selected):");
        ImGui.TextDisabled("Tick items you want to buy. Discovered prices override the static placeholders.");

        // Vendor + item tree per expansion.
        foreach (var exp in new[] { Expansion.ShB, Expansion.EW, Expansion.DT })
        {
            var expansionName = exp switch
            {
                Expansion.ShB => "Shadowbringers",
                Expansion.EW  => "Endwalker",
                Expansion.DT  => "Dawntrail",
                _ => exp.ToString(),
            };
            if (!ImGui.CollapsingHeader(expansionName)) continue;

            ImGui.Indent(16f);
            foreach (var vendor in VendorCatalog.ByExpansion(exp))
            {
                bool open = ImGui.TreeNode($"{vendor.Name} — {vendor.Settlement}##{vendor.AetheryteId}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Survey##sv_{vendor.AetheryteId}_{vendor.TerritoryType}"))
                    _plugin.Controller.TriggerSurvey(vendor);
                if (open)
                {
                    ImGui.TextDisabled($"Aetheryte {vendor.AetheryteId} · Territory {vendor.TerritoryType}");
                    // Static catalog items (hardcoded for hub vendors).
                    var staticIds = new HashSet<uint>();
                    foreach (var item in vendor.Items)
                    {
                        staticIds.Add(item.ItemId);
                        var ticked = cfg.TradingShoppingList.Contains(item.ItemId);
                        string costStr;
                        if (cfg.DiscoveredVendorItems.TryGetValue(item.ItemId, out var disc))
                            costStr = $"{disc.GemCost} gems ✓";
                        else if (item.GemCost > 0)
                            costStr = $"{item.GemCost} gems";
                        else
                            costStr = "cost TBD — run Survey";
                        var label = $"{item.Name} ({costStr})";
                        if (item.IsMbTradable) label += "  ★ MB";
                        if (ImGui.Checkbox($"{label}##{vendor.AetheryteId}_{item.ItemId}", ref ticked))
                        {
                            if (ticked) cfg.TradingShoppingList.Add(item.ItemId);
                            else        cfg.TradingShoppingList.Remove(item.ItemId);
                            _plugin.SaveConfig();
                        }
                        if (!string.IsNullOrEmpty(item.Notes))
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"— {item.Notes}");
                        }
                        if (ticked) DrawItemLimitControls(cfg, item.ItemId);
                    }
                    // Discovered items belonging to this vendor that aren't in
                    // the static list — populated by Survey. Match by vendor
                    // name AND aetheryte (name alone collides if multiple
                    // vendors share a name across expansions).
                    var discovered = cfg.DiscoveredVendorItems
                        .Where(kv => kv.Value.VendorAetheryteId == vendor.AetheryteId
                                  && kv.Value.VendorName == vendor.Name
                                  && !staticIds.Contains(kv.Key))
                        .OrderBy(kv => kv.Value.GemCost)
                        .ThenBy(kv => kv.Value.ItemName)
                        .ToList();
                    foreach (var kv in discovered)
                    {
                        var itemId = kv.Key;
                        var disc = kv.Value;
                        var displayName = string.IsNullOrEmpty(disc.ItemName)
                            ? $"Item #{itemId}"
                            : disc.ItemName;
                        var ticked = cfg.TradingShoppingList.Contains(itemId);
                        var label = $"{displayName} ({disc.GemCost} gems ✓)";
                        if (ImGui.Checkbox($"{label}##disc_{vendor.AetheryteId}_{itemId}", ref ticked))
                        {
                            if (ticked) cfg.TradingShoppingList.Add(itemId);
                            else        cfg.TradingShoppingList.Remove(itemId);
                            _plugin.SaveConfig();
                        }
                        if (ticked) DrawItemLimitControls(cfg, itemId);
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.Unindent(16f);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Buy priority: cheapest gem cost first (Vouchers drain before high-cost items). " +
                          "Items at their inventory cap are skipped — bot moves on to the next item on the list.");
    }

    /// <summary>
    /// Renders the per-item "Limit" checkbox + count input on a sub-line under
    /// a ticked shopping-list entry. When unchecked, no cap is enforced and
    /// the bot buys until gems hit the configured floor.
    /// </summary>
    private void DrawItemLimitControls(Configuration cfg, uint itemId)
    {
        ImGui.Indent(20f);
        bool hasLimit = cfg.TradingItemLimits.TryGetValue(itemId, out var cap) && cap > 0;
        if (ImGui.Checkbox($"Limit##lim_{itemId}", ref hasLimit))
        {
            if (hasLimit)
            {
                if (cap <= 0) cap = 99;
                cfg.TradingItemLimits[itemId] = cap;
            }
            else
            {
                cfg.TradingItemLimits.Remove(itemId);
            }
            _plugin.SaveConfig();
        }
        if (hasLimit)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            int v = cap;
            if (ImGui.InputInt($"##lim_v_{itemId}", ref v))
            {
                if (v < 1) v = 1;
                if (v > 9999) v = 9999;
                cfg.TradingItemLimits[itemId] = v;
                _plugin.SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("max in inventory before bot stops buying");
        }
        ImGui.Unindent(20f);
    }

    // ─────────────────────────────── Plugins tab ──────────────────────────

    private void DrawPluginsTab()
    {
        ImGui.Text("External plugins:");
        DrawIpcRow("vnavmesh",   _plugin.Navmesh.IsAvailable,    _plugin.Navmesh.IsReady ? " (ready)" : " (not ready)");
        DrawIpcRow("BossMod",    _plugin.BossMod.IsAvailable,    "");
        DrawIpcRow("Lifestream", _plugin.Lifestream.IsAvailable, _plugin.Lifestream.IsBusy ? " (busy)" : "");

        var taAvail = _plugin.TextAdvance.IsAvailable;
        string taSuffix;
        if (!taAvail) taSuffix = " — required to auto-confirm FATE-start NPC dialog";
        else if (_plugin.TextAdvance.HoldsExternalControl) taSuffix = " (forced on by FateWalker)";
        else if (_plugin.TextAdvance.IsActive) taSuffix = " (active)";
        else taSuffix = " — installed but inactive; FateWalker will force it on at Start";
        DrawIpcRow("TextAdvance", taAvail, taSuffix);

        var rsrNeeded = _plugin.Config.CombatBackend == Configuration.CombatBackendKind.RSR;
        var rsrAvail  = _plugin.Rsr.IsAvailable;
        DrawIpcRow("RSR", rsrAvail, rsrNeeded && !rsrAvail ? " — REQUIRED (CombatBackend=RSR but plugin not loaded)" : "");
    }

    /// <summary>Side-by-side min/max sliders that clamp min ≤ max via a setter callback.</summary>
    private void DrawIntRange(string label, int minVal, int maxVal, int hardMin, int hardMax, Action<int, int> set)
    {
        var lo = minVal;
        var hi = maxVal;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.SliderInt($"{label} min", ref lo, hardMin, hardMax))
        {
            if (lo > hi) hi = lo;
            set(lo, hi);
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        if (ImGui.SliderInt($"##{label}_max", ref hi, hardMin, hardMax))
        {
            if (hi < lo) lo = hi;
            set(lo, hi);
            _plugin.SaveConfig();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("max");
    }

    private static void DrawIpcRow(string name, bool available, string suffix)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        if (available)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"{name}: installed{suffix}");
        else
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1f), $"{name}: NOT installed");
    }

    public void Dispose() { }
}
