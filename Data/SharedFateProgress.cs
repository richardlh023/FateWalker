using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FateWalker.Data;

/// <summary>
/// Reads per-zone Shared FATE rank/progress from <c>AgentFateProgress</c>.
/// 3 tabs × 6 zones = ShB/EW/DT × per-zone slot. Each zone has CurrentRank,
/// MaxRank (3 for ShB/EW, 4 for DT), FateProgress count and NeededFates total.
///
/// Data is populated once the player opens the Shared FATE UI at least once
/// per session. Before that, ranks may read as 0; we expose <see cref="IsLoaded"/>
/// so the UI can hint the user to open the in-game window if values look wrong.
/// </summary>
public sealed record SharedFateZoneState(
    uint TerritoryId,
    byte CurrentRank,
    byte MaxRank,
    ushort Progress,
    ushort Needed,
    string RankText,
    string ProgressText)
{
    /// <summary>
    /// Loaded = the agent has populated meaningful data for this zone.
    /// We can't trust the MaxRank byte (clientstructs offset is wrong against
    /// the live game — comes back as 0 even when rank is 2), so we infer
    /// "loaded" from any of the other three fields being nonzero.
    /// </summary>
    public bool HasValidRank => CurrentRank > 0 || Progress > 0 || Needed > 0;

    /// <summary>
    /// Maxed when the player has hit the rank cap for this expansion. Cap is
    /// 3 for ShB/EW and 4 for DT — derived from the territory's expansion via
    /// <see cref="TerritoryMap"/> rather than the unreliable MaxRank byte.
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

    private static byte ExpansionRankCap(uint territoryId)
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
    /// <summary>True if any zone reports a valid MaxRank (UI has been opened).</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>
    /// Diagnostic dump of every slot in the agent — produces one line per
    /// (tab, zone) showing raw bytes. Used by the "Refresh / Dump" button so
    /// the user can paste output back when reads look wrong (struct layout
    /// drift between game patch and FFXIVClientStructs).
    /// </summary>
    public static unsafe List<string> DumpRaw()
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
                string rankText = "<null>";
                string progressText = "<null>";
                try { rankText = zone.RankText.ToString(); } catch { }
                try { progressText = zone.ProgressText.ToString(); } catch { }
                string zoneName = "<null>";
                try { zoneName = zone.ZoneName.ToString(); } catch { }
                lines.Add($"  [{z}] tt={zone.TerritoryTypeId} disp={zone.DisplayOrder} rank={zone.CurrentRank}/{zone.MaxRank} prog={zone.FateProgress}/{zone.NeededFates} name='{zoneName}' rankText='{rankText}' progressText='{progressText}'");
            }
        }
        return lines;
    }

    /// <summary>
    /// Read all 18 known FATE zones' progress. Returns dictionary keyed by
    /// TerritoryTypeId. Zones not in the agent are omitted; zones whose MaxRank
    /// is outside the valid {3, 4} range are still returned but their
    /// <see cref="SharedFateZoneState.HasValidRank"/> reads false — callers
    /// should gate any rank display on that flag.
    /// </summary>
    public static unsafe Dictionary<uint, SharedFateZoneState> ReadAll()
    {
        var result = new Dictionary<uint, SharedFateZoneState>();
        var agent = AgentFateProgress.Instance();
        if (agent == null) return result;

        bool anyLoaded = false;
        for (int t = 0; t < 3; t++)
        {
            ref var tab = ref agent->Tabs[t];
            for (int z = 0; z < 6; z++)
            {
                ref var zone = ref tab.Zones[z];
                if (zone.TerritoryTypeId == 0) continue;
                var rankText = zone.RankText.ToString();
                var progressText = zone.ProgressText.ToString();
                var state = new SharedFateZoneState(
                    zone.TerritoryTypeId,
                    zone.CurrentRank,
                    zone.MaxRank,
                    zone.FateProgress,
                    zone.NeededFates,
                    rankText,
                    progressText);
                if (state.HasValidRank) anyLoaded = true;
                result[zone.TerritoryTypeId] = state;
            }
        }
        IsLoaded = anyLoaded;
        return result;
    }
}
