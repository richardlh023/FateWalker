using System.Collections.Generic;
using System.Linq;

namespace FateWalker.Data;

public enum Expansion { ShB, EW, DT }

public sealed record ZoneInfo(
    uint TerritoryTypeId,
    uint AetheryteId,
    string AetheryteName,
    string ZoneName,
    Expansion Expansion);

public static class TerritoryMap
{
    public static readonly IReadOnlyList<ZoneInfo> Zones =
    [
        // Shadowbringers — sources: Minion ffxiv_helpers.lua:4416-4582, consolegameswiki
        new(813, 132, "Fort Jobb",        "Lakeland",                Expansion.ShB),
        new(814, 137, "Stilltide",        "Kholusia",                Expansion.ShB),
        new(815, 140, "Mord Souq",        "Amh Araeng",              Expansion.ShB),
        new(816, 144, "Lydha Lran",       "Il Mheg",                 Expansion.ShB),
        new(817, 142, "Slitherbough",     "The Rak'tika Greatwood",  Expansion.ShB),
        new(818, 147, "The Ondo Cups",    "The Tempest",             Expansion.ShB),

        // Endwalker
        new(956, 166, "The Archeion",     "Labyrinthos",             Expansion.EW),
        new(957, 169, "Yedlihmad",        "Thavnair",                Expansion.EW),
        new(958, 172, "Camp Broken Glass","Garlemald",               Expansion.EW),
        new(959, 174, "Sinus Lacrimarum", "Mare Lamentorum",         Expansion.EW),
        // NOTE: previous version had territory IDs swapped here. Lumina TerritoryType:
        // 960 = Elpis, 961 = Ultima Thule. Cross-checked against questionable + community wiki.
        new(960, 176, "Anagnorisis",      "Elpis",                   Expansion.EW),
        new(961, 179, "Reah Tahra",       "Ultima Thule",            Expansion.EW),

        // Dawntrail
        new(1187, 200, "Wachunpelo",       "Urqopacha",               Expansion.DT),
        new(1188, 202, "Ok'hanu",          "Kozama'uka",              Expansion.DT),
        new(1189, 205, "Iq Br'aax",        "Yak T'el",                Expansion.DT),
        new(1190, 207, "Hhusatahwi",       "Shaaloani",               Expansion.DT),
        new(1191, 210, "Yyasulani Station","Heritage Found",          Expansion.DT),
        new(1192, 213, "Leynode Mnemo",    "Living Memory",           Expansion.DT),
    ];

    public static readonly IReadOnlyDictionary<uint, ZoneInfo> ByTerritoryId =
        Zones.ToDictionary(z => z.TerritoryTypeId);

    public static ZoneInfo? Lookup(uint territoryId) =>
        ByTerritoryId.TryGetValue(territoryId, out var info) ? info : null;

    public static IEnumerable<ZoneInfo> EnabledZones(Configuration cfg) =>
        Zones.Where(z =>
            (z.Expansion == Expansion.ShB && cfg.EnableShB) ||
            (z.Expansion == Expansion.EW  && cfg.EnableEW)  ||
            (z.Expansion == Expansion.DT  && cfg.EnableDT));
}
