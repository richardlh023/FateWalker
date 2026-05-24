using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using FateWalker.Data;
using FateExt = FateWalker.Data.FateExtensions;

namespace FateWalker.Controller;

public sealed record FateCandidate(
    IFate Fate,
    float DistanceToPlayer,
    bool PassesFilter,
    string? RejectReason);

public sealed class FateSelector
{
    private readonly Configuration _config;

    public FateSelector(Configuration config)
    {
        _config = config;
    }

    /// <summary>
    /// Enumerate every active FATE with filter result + rejection reason, ordered the
    /// way the bot would pick (bonus first, then closest). Caller can take the first
    /// PassesFilter==true for the chosen target, or display all for diagnostics.
    /// </summary>
    public IReadOnlyList<FateCandidate> Evaluate(
        IEnumerable<IFate> fates,
        uint currentTerritory,
        Vector3 playerPosition,
        short playerLevel)
    {
        var zoneAllowed = IsCurrentZoneAllowed(currentTerritory);
        string? zoneRejectOverride = null;
        // Skip-maxed gate: when the user opts into focused progress farming,
        // treat zones whose Shared FATE rank is already capped as disabled.
        if (zoneAllowed && _config.SkipMaxedSharedFateZones)
        {
            var ranks = SharedFateProgress.ReadAll(_config);
            if (ranks.TryGetValue(currentTerritory, out var state) && state.IsMaxed)
            {
                zoneAllowed = false;
                zoneRejectOverride = $"rank maxed ({state.CurrentRank}/{state.ExpectedMaxRank})";
            }
        }

        var materialised = fates.ToList();
        // Record observation — runtime-grown catalog feeds the Banlist UI so
        // the user can blacklist a FATE by name from any zone they've visited.
        bool catalogChanged = false;
        foreach (var f in materialised)
        {
            var fid = f.FateId;
            var name = f.Name.TextValue;
            if (string.IsNullOrEmpty(name)) continue;
            if (_config.ObservedFates.TryGetValue(fid, out var existing))
            {
                // Update if name/level/territory changed (e.g. fate moved zones in a patch).
                if (existing.Name != name || existing.Level != f.Level || existing.TerritoryId != currentTerritory)
                {
                    existing.Name = name;
                    existing.Level = f.Level;
                    existing.TerritoryId = currentTerritory;
                    catalogChanged = true;
                }
            }
            else
            {
                _config.ObservedFates[fid] = new Configuration.ObservedFate
                {
                    Name = name,
                    Level = f.Level,
                    TerritoryId = currentTerritory,
                };
                catalogChanged = true;
            }
        }
        if (catalogChanged) _configChangedFlag = true;

        var prioritizeBonus = _config.PrioritizeBonusFates;
        var query = materialised
            .Select(f =>
            {
                var dist = Vector3.Distance(f.Position, playerPosition);
                var reason = RejectReason(f, zoneAllowed, playerLevel, zoneRejectOverride, dist);
                return new FateCandidate(f, dist, reason == null, reason);
            })
            .OrderByDescending(c => c.PassesFilter);

        return (prioritizeBonus
                ? query.ThenByDescending(c => c.Fate.HasBonus).ThenBy(c => c.DistanceToPlayer)
                : query.ThenBy(c => c.DistanceToPlayer))
            .ToList();
    }

    /// <summary>
    /// Set when <see cref="Evaluate"/> mutated <see cref="Configuration.ObservedFates"/>.
    /// The caller (UI / controller) checks and persists at its own cadence so we
    /// don't hit disk every tick.
    /// </summary>
    public bool ConsumeConfigDirty()
    {
        if (!_configChangedFlag) return false;
        _configChangedFlag = false;
        return true;
    }
    private bool _configChangedFlag;

    public IFate? Pick(
        IEnumerable<IFate> fates,
        uint currentTerritory,
        Vector3 playerPosition,
        short playerLevel)
    {
        return Evaluate(fates, currentTerritory, playerPosition, playerLevel)
            .FirstOrDefault(c => c.PassesFilter)?.Fate;
    }

    private bool IsCurrentZoneAllowed(uint territoryId)
    {
        var info = TerritoryMap.Lookup(territoryId);
        if (info == null) return false;
        return info.Expansion switch
        {
            Expansion.ShB => _config.EnableShB,
            Expansion.EW  => _config.EnableEW,
            Expansion.DT  => _config.EnableDT,
            _ => false
        };
    }

    private string? RejectReason(IFate fate, bool zoneAllowed, short playerLevel, string? zoneRejectOverride, float distanceToPlayer)
    {
        if (!zoneAllowed)                                          return zoneRejectOverride ?? "zone disabled";

        // Accept Running FATEs as before. Also accept Preparing FATEs that have
        // a MotivationNpc — these are escort/defense FATEs that we can start by
        // interacting with the NPC. Reject other states (WaitingForEnd, Ended, Failed).
        bool isRunning   = fate.State == FateState.Running;
        bool isStartable = fate.State == FateState.Preparing && fate.GetMotivationNpc() != 0;
        if (!isRunning && !isStartable)                            return $"state={fate.State}";

        if (fate.Level > playerLevel)                              return $"too high (lv{fate.Level})";
        // Bonus FATEs are rare and bypass MinLevelDelta — even a low-level bonus
        // FATE is worth taking because the 100% gem multiplier compensates.
        if (!fate.HasBonus && fate.Level < playerLevel + _config.MinLevelDelta)
            return $"too low (lv{fate.Level})";
        if (_config.BlacklistedFateIds.Contains(fate.FateId))      return "blacklisted";
        // Time-left filter: bonus + startable Preparing FATEs bypass entirely.
        // Otherwise threshold scales with distance — at flying ~20 y/s the bot
        // needs roughly dist/20 seconds to reach the FATE, so a 1500y FATE with
        // 60s left would be a wasted trip. base + dist/20 leaves a buffer.
        if (!fate.HasBonus && isRunning)
        {
            var dynamicMin = _config.FateTimeRemainingMinSec + (int)(distanceToPlayer / 20f);
            if (fate.TimeRemaining < dynamicMin)
                return $"<{dynamicMin}s left (dist {distanceToPlayer:F0}y)";
        }
        return null;
    }
}
