using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FateWalker.Data;

/// <summary>
/// Per-rank FATE thresholds — FATE count required to advance FROM
/// <c>fromRank</c> TO the next rank. Numbers verified against
/// ConsoleGamesWiki's Shared FATE page (66 FATEs total per zone to max in
/// every expansion — just distributed differently). The agent's
/// <c>NeededFates</c>, when loaded, always wins over these defaults.
///
///   ShB / EW (max R3, 66 total): R1→R2 = 6,  R2→R3 = 60
///   DT       (max R4, 66 total): R1→R2 = 6,  R2→R3 = 20,  R3→R4 = 40
/// </summary>
public static class FateRankThresholds
{
    public static ushort DefaultNeeded(Expansion exp, byte fromRank)
    {
        return (exp, fromRank) switch
        {
            (Expansion.ShB, 1) => 6,
            (Expansion.ShB, 2) => 60,
            (Expansion.EW,  1) => 6,
            (Expansion.EW,  2) => 60,
            (Expansion.DT,  1) => 6,
            (Expansion.DT,  2) => 20,
            (Expansion.DT,  3) => 40,
            _ => 0,
        };
    }
}

public sealed record SharedFateZoneState(
    uint TerritoryId,
    byte CurrentRank,
    byte MaxRank,
    ushort Progress,
    ushort Needed,
    string RankText,
    string ProgressText,
    bool IsLocallyTracked = false)
{
    /// <summary>
    /// True when we have enough info to display rank — either the agent
    /// returned non-zero values, or our local snapshot is non-empty.
    /// </summary>
    public bool HasValidRank => CurrentRank > 0 || Progress > 0 || Needed > 0;

    /// <summary>
    /// Maxed = player hit the rank cap for this expansion.
    /// Cap = 3 for ShB/EW, 4 for DT (derived from TerritoryMap; the agent's
    /// MaxRank byte reads as 0 in current clientstructs, so we can't trust it).
    /// </summary>
    public bool IsMaxed
    {
        get
        {
            if (!HasValidRank) return false;
            var cap = ExpansionRankCap(TerritoryId);
            return cap > 0 && CurrentRank >= cap;
        }
    }

    public byte ExpectedMaxRank => ExpansionRankCap(TerritoryId);
    public float ProgressFraction => Needed == 0 ? 0f : (float)Progress / Needed;

    public static byte ExpansionRankCap(uint territoryId)
    {
        var info = TerritoryMap.Lookup(territoryId);
        if (info == null) return 0;
        return info.Expansion switch
        {
            Expansion.ShB => 3,
            Expansion.EW  => 3,
            Expansion.DT  => 4,
            _ => 0,
        };
    }
}

public static class SharedFateProgress
{
    /// <summary>True if any zone has either agent-loaded OR locally-tracked rank data.</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>
    /// Diagnostic dump of every agent slot — raw bytes plus our local snapshot
    /// merged in for comparison. Used by the "Refresh / Dump" UI button.
    /// </summary>
    public static unsafe List<string> DumpRaw(Configuration? cfg = null)
    {
        var lines = new List<string>();
        var agent = AgentFateProgress.Instance();
        if (agent == null) { lines.Add("AgentFateProgress.Instance() == null"); return lines; }
        lines.Add($"agent={(nint)agent:X}  TabIndex={agent->TabIndex}");
        for (int t = 0; t < 3; t++)
        {
            ref var tab = ref agent->Tabs[t];
            lines.Add($"-- Tab[{t}]  TabIndex={tab.TabIndex} --");
            for (int z = 0; z < 6; z++)
            {
                ref var zone = ref tab.Zones[z];
                uint tt = zone.TerritoryTypeId;
                byte disp = zone.DisplayOrder;
                byte curR = zone.CurrentRank, maxR = zone.MaxRank;
                ushort prog = zone.FateProgress, needed = zone.NeededFates;
                string rankText = "<null>", progressText = "<null>", zoneName = "<null>";
                try { rankText = zone.RankText.ToString(); } catch { }
                try { progressText = zone.ProgressText.ToString(); } catch { }
                try { zoneName = zone.ZoneName.ToString(); } catch { }
                var local = cfg != null && cfg.LocalSharedFateProgress.TryGetValue(tt, out var l)
                    ? $" local={l.Rank}/?  prog={l.Progress}/{l.Needed} (+{l.IncrementsSinceSync} since {l.LastSyncedIso})"
                    : "";
                lines.Add($"  [{z}] tt={tt} disp={disp} rank={curR}/{maxR} prog={prog}/{needed} name='{zoneName}' rankText='{rankText}' progressText='{progressText}'{local}");
            }
        }
        return lines;
    }

    /// <summary>
    /// Read all FATE zones' progress, merging agent data with the local
    /// snapshot. If the agent has loaded values for a zone (window is/was
    /// open), those are authoritative AND we snapshot them into the local
    /// store. Otherwise we fall back to the local snapshot — which the bot
    /// keeps current by calling <see cref="IncrementLocal"/> on every FATE
    /// completion.
    /// </summary>
    public static unsafe Dictionary<uint, SharedFateZoneState> ReadAll(Configuration cfg)
    {
        var result = new Dictionary<uint, SharedFateZoneState>();
        var agent = AgentFateProgress.Instance();
        bool anyLoaded = false;

        // Pass 1 — read agent. If a zone has data, sync local snapshot and
        // emit a SharedFateZoneState backed by agent values.
        if (agent != null)
        {
            for (int t = 0; t < 3; t++)
            {
                ref var tab = ref agent->Tabs[t];
                for (int z = 0; z < 6; z++)
                {
                    ref var zone = ref tab.Zones[z];
                    if (zone.TerritoryTypeId == 0) continue;
                    string rankText = "", progressText = "";
                    try { rankText = zone.RankText.ToString(); } catch { }
                    try { progressText = zone.ProgressText.ToString(); } catch { }
                    var state = new SharedFateZoneState(
                        zone.TerritoryTypeId,
                        zone.CurrentRank,
                        zone.MaxRank,
                        zone.FateProgress,
                        zone.NeededFates,
                        rankText,
                        progressText);
                    if (state.HasValidRank)
                    {
                        anyLoaded = true;
                        // Sync local snapshot — agent always wins when fresh.
                        cfg.LocalSharedFateProgress[zone.TerritoryTypeId] = new Configuration.LocalRankSnapshot
                        {
                            Rank = state.CurrentRank,
                            Progress = state.Progress,
                            Needed = state.Needed,
                            LastSyncedIso = DateTime.UtcNow.ToString("o"),
                            IncrementsSinceSync = 0,
                        };
                    }
                    result[zone.TerritoryTypeId] = state;
                }
            }
        }

        // Pass 2 — for any tracked territory not already emitted by the agent
        // (or emitted with no useful data), substitute the local snapshot.
        foreach (var kv in cfg.LocalSharedFateProgress)
        {
            var tt = kv.Key;
            var local = kv.Value;
            // Compute effective progress = snapshot.Progress + post-snapshot
            // increments. If we cross the threshold, bump rank and roll over.
            var (effRank, effProg, effNeeded) = ApplyIncrements(tt, local);
            var fresh = new SharedFateZoneState(
                TerritoryId: tt,
                CurrentRank: effRank,
                MaxRank: 0,
                Progress: effProg,
                Needed: effNeeded,
                RankText: "",
                ProgressText: "",
                IsLocallyTracked: true);
            // Only overwrite if agent didn't already give us a HasValidRank result.
            if (result.TryGetValue(tt, out var existing) && existing.HasValidRank) continue;
            if (fresh.HasValidRank) anyLoaded = true;
            result[tt] = fresh;
        }

        IsLoaded = anyLoaded;
        return result;
    }

    /// <summary>
    /// Apply the +1 counter to a snapshot, rolling over ranks if the local
    /// count crosses Needed. Returns the effective (rank, progress, needed)
    /// the user/bot should see now.
    /// </summary>
    private static (byte rank, ushort prog, ushort needed) ApplyIncrements(uint territoryId, Configuration.LocalRankSnapshot local)
    {
        byte rank = local.Rank;
        int prog = local.Progress + local.IncrementsSinceSync;
        ushort needed = local.Needed;
        var info = TerritoryMap.Lookup(territoryId);
        if (info == null) return (rank, (ushort)Math.Min(prog, ushort.MaxValue), needed);
        var cap = SharedFateZoneState.ExpansionRankCap(territoryId);

        // Resolve Needed if zero (e.g. user opened window mid-rank-fill).
        if (needed == 0) needed = FateRankThresholds.DefaultNeeded(info.Expansion, rank > 0 ? rank : (byte)1);

        // Roll over ranks until progress fits OR we hit cap.
        while (needed > 0 && prog >= needed && rank < cap)
        {
            prog -= needed;
            rank++;
            needed = FateRankThresholds.DefaultNeeded(info.Expansion, rank);
            if (rank >= cap) { prog = 0; needed = 0; break; }
        }
        return (rank, (ushort)Math.Clamp(prog, 0, ushort.MaxValue), needed);
    }

    /// <summary>
    /// Called by the FateController when a FATE completes in <paramref name="territoryId"/>.
    /// Increments the local snapshot's pending counter; the rank-rollover math
    /// runs lazily in <see cref="ReadAll"/>.
    /// </summary>
    public static void IncrementLocal(Configuration cfg, uint territoryId)
    {
        if (!cfg.LocalSharedFateProgress.TryGetValue(territoryId, out var local))
        {
            // No baseline yet — seed an empty snapshot. Rank will read as 0
            // until the user opens the SharedFate window at least once.
            local = new Configuration.LocalRankSnapshot
            {
                Rank = 0, Progress = 0, Needed = 0,
                LastSyncedIso = "",
                IncrementsSinceSync = 0,
            };
            cfg.LocalSharedFateProgress[territoryId] = local;
        }
        local.IncrementsSinceSync++;
    }

    private static string Safe(Func<string> f) { try { return f(); } catch { return ""; } }
}
