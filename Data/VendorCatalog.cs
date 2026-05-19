using System.Collections.Generic;

namespace FateWalker.Data;

/// <summary>
/// Bicolor Gemstone vendor catalog. One entry per vendor NPC with the items
/// the user may want to buy. Item IDs are Lumina <c>Item</c> row IDs; gem
/// costs are placeholders pending full datamining (see TODO in body).
///
/// Source: <c>reference_bicolor_gemstone_vendors.md</c> for vendor locations
/// + <c>reference_bicolor_vendor_items.md</c> (background-agent output) for
/// item IDs. Update when the agent's report lands.
/// </summary>
public sealed record VendorItem(
    string Name,
    uint ItemId,
    int GemCost,
    bool IsMbTradable = false,
    string? Notes = null);

public sealed record VendorNpc(
    string Name,
    string Settlement,
    uint AetheryteId,            // Lumina Aetheryte row to teleport to
    uint TerritoryType,          // expected territory after teleport
    Expansion Expansion,
    IReadOnlyList<VendorItem> Items,
    uint AethernetShardId = 0);  // optional in-zone aethernet shard to hop to
                                 // after the main teleport (city hubs only)

public static class VendorCatalog
{
    /// <summary>
    /// All known Bicolor Gemstone vendors. Hub vendors first per expansion
    /// (they have the best gil-per-gem trades: Voucher), then zone vendors.
    /// Item costs marked as 0 are TODO — to be filled from background agent
    /// output in <c>reference_bicolor_vendor_items.md</c>.
    /// </summary>
    public static readonly IReadOnlyList<VendorNpc> Vendors = new VendorNpc[]
    {
        // ────────────── Shadowbringers ──────────────
        // Item IDs verified via Universalis / Gamer Escape (see
        // reference_bicolor_vendor_items.md).
        new("Gramsol", "The Crystarium", 133, 819, Expansion.ShB, new VendorItem[]
        {
            new("Bicolor Gemstone Voucher",     35833, 100, IsMbTradable: true, Notes: "primary gil sink"),
            new("Heavens' Eye Materia VII",     26727, 60),
            new("Savage Aim Materia VII",       26728, 60),
            new("Knowledge Never Sleeps Roll",  28878, 350, IsMbTradable: true, Notes: "high MB demand"),
        }, AethernetShardId: 149),  // Crystarium Markets
        new("Pedronille", "Eulmore", 134, 820, Expansion.ShB, new VendorItem[]
        {
            new("Sharlayan Diadema (minion)", 35860, 800, Notes: "one-time, rank-max"),
        }, AethernetShardId: 157),  // Eulmore Mainstay
        // Per-zone ShB vendors — accessible at lower rank than Gramsol/Pedronille
        // (which require Rank 3 in ALL ShB zones). TerritoryType is the FATE zone
        // they live in, not a city. Items left empty — fill via Survey button.
        new("Siulmet",       "Lakeland",                132, 813, Expansion.ShB, System.Array.Empty<VendorItem>()),
        new("Zumutt",        "Kholusia",                139, 814, Expansion.ShB, System.Array.Empty<VendorItem>()),
        new("Halden",        "Amh Araeng",              140, 815, Expansion.ShB, System.Array.Empty<VendorItem>()),
        new("Sul Lad",       "Il Mheg",                 144, 816, Expansion.ShB, System.Array.Empty<VendorItem>()),
        new("Nacille",       "The Rak'tika Greatwood",  143, 817, Expansion.ShB, System.Array.Empty<VendorItem>()),
        new("Goushs Ooan",   "The Tempest",             147, 818, Expansion.ShB, System.Array.Empty<VendorItem>()),

        // ────────────── Endwalker ──────────────
        new("Sajareen", "Radz-at-Han", 183, 963, Expansion.EW, new VendorItem[]
        {
            new("Bicolor Gemstone Voucher",   35833, 100, IsMbTradable: true),
            new("Heavens' Eye Materia VIII",  26728, 60),
            new("Savage Aim Materia VIII",    26729, 60),
            new("Perfumed Eves Roll",         36809, 350, IsMbTradable: true),
        }, AethernetShardId: 191),  // Radz-at-Han Meghaduta (West Balshahn Bazaar area)
        new("Gadfrid", "Old Sharlayan", 182, 962, Expansion.EW, new VendorItem[]
        {
            new("Bicolor Gemstone Voucher", 35833, 100, IsMbTradable: true),
            new("Materia VIII",             26728, 60),
        }, AethernetShardId: 184),  // Old Sharlayan Studium
        // Per-zone EW vendors.
        new("Faezbroes",  "Labyrinthos",     166, 956, Expansion.EW, System.Array.Empty<VendorItem>()),
        new("Mahveydah",  "Thavnair",        169, 957, Expansion.EW, System.Array.Empty<VendorItem>()),
        new("Zawawa",     "Garlemald",       172, 958, Expansion.EW, System.Array.Empty<VendorItem>()),
        new("Tradingway", "Mare Lamentorum", 175, 959, Expansion.EW, System.Array.Empty<VendorItem>()),
        new("Aisara",     "Elpis",           176, 960, Expansion.EW, System.Array.Empty<VendorItem>()),
        new("N-1499",     "Ultima Thule",    181, 961, Expansion.EW, System.Array.Empty<VendorItem>()),

        // ────────────── Dawntrail ──────────────
        new("Beryl", "Solution Nine", 217, 1186, Expansion.DT, new VendorItem[]
        {
            new("Turali Bicolor Gemstone Voucher", 43961, 100, IsMbTradable: true, Notes: "DT primary sink"),
            new("Heavens' Eye Materia IX",         41757, 60),
            new("Savage Aim Materia IX",           41758, 60),
            new("Heavens' Eye Materia X",          33930, 60),
            new("Savage Aim Materia X",            33932, 60),
            new("Morrow's Might Orchestrion Roll", 46870, 450, IsMbTradable: true, Notes: "DT marquee roll"),
        }, AethernetShardId: 235),  // Solution Nine Nexus Arcade
        new("Kajeel Ja", "Tuliyollal", 216, 1185, Expansion.DT, new VendorItem[]
        {
            new("Turali Bicolor Gemstone Voucher", 43961, 100, IsMbTradable: true),
            new("Materia IX",                       41757, 60),
        }, AethernetShardId: 221),  // Tuliyollal Bayside Bevy Marketplace
        // Per-zone DT vendors. Ok'hanu / Gate of Remembrance aetherytes don't
        // exist in the Lifestream-friendly enum yet — fall back to the closest
        // zone aetheryte (Many Fires / Leynode Mnemo) and let the bot walk.
        new("Tepli",         "Urqopacha",       200, 1187, Expansion.DT, System.Array.Empty<VendorItem>()),
        new("Kunuhali",      "Kozama'uka",      203, 1188, Expansion.DT, System.Array.Empty<VendorItem>()),
        new("Rral Wuruq",    "Yak T'el",        205, 1189, Expansion.DT, System.Array.Empty<VendorItem>()),
        new("Mitepe",        "Shaaloani",       207, 1190, Expansion.DT, System.Array.Empty<VendorItem>()),
        new("Toashana",      "Heritage Found",  211, 1191, Expansion.DT, System.Array.Empty<VendorItem>()),
        new("Clerk PX-0029", "Living Memory",   213, 1192, Expansion.DT, System.Array.Empty<VendorItem>()),
    };

    public static IEnumerable<VendorNpc> ByExpansion(Expansion exp)
    {
        foreach (var v in Vendors) if (v.Expansion == exp) yield return v;
    }

    public static VendorNpc? FindByItemId(uint itemId)
    {
        foreach (var v in Vendors)
            foreach (var item in v.Items)
                if (item.ItemId == itemId) return v;
        return null;
    }
}
