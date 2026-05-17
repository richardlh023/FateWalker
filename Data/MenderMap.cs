using System.Collections.Generic;

namespace FateWalker.Data;

/// <summary>
/// Per-FATE-zone Mender NPC routing. Maps the originating <c>TerritoryType</c>
/// to the Lumina <c>Aetheryte</c> row of the closest in-zone Mender. Source:
/// <c>reference_mender_npcs.md</c>. Some entries point to a different aetheryte
/// from <see cref="TerritoryMap"/> because:
///   • Il Mheg: FATE primary 145 Pla Enni has no Mender → 144 Lydha Lran
///   • Mare Lamentorum: FATE primary 174 Sinus has no Mender → 175 Bestways Burrow
///   • Ultima Thule: only 181 Base Omicron has a Mender
///   • Living Memory: no field Mender — cross-zone fallback to 212 Electrope Strike (Heritage Found)
/// </summary>
public static class MenderMap
{
    public static readonly IReadOnlyDictionary<uint, uint> RepairAetheryteByTerritory = new Dictionary<uint, uint>
    {
        // Shadowbringers
        { 813, 132 },   // Lakeland          → Fort Jobb
        { 814, 138 },   // Kholusia          → Wright
        { 815, 140 },   // Amh Araeng        → Mord Souq
        { 816, 144 },   // Il Mheg           → Lydha Lran  (NOT Pla Enni)
        { 817, 142 },   // Rak'tika          → Slitherbough
        { 818, 148 },   // Tempest           → Macarenses Angle

        // Endwalker
        { 956, 167 },   // Labyrinthos       → Sharlayan Hamlet
        { 957, 169 },   // Thavnair          → Yedlihmad
        { 958, 172 },   // Garlemald         → Camp Broken Glass
        { 959, 175 },   // Mare Lamentorum   → Bestways Burrow  (NOT Sinus)
        { 960, 176 },   // Elpis             → Anagnorisis
        { 961, 181 },   // Ultima Thule      → Base Omicron

        // Dawntrail
        { 1187, 200 },  // Urqopacha         → Wachunpelo
        { 1188, 204 },  // Kozama'uka        → Earthenshire
        { 1189, 205 },  // Yak T'el          → Iq Br'aax (Mamook MSQ-gated)
        { 1190, 207 },  // Shaaloani         → Hhusatahwi
        { 1191, 212 },  // Heritage Found    → Electrope Strike
        { 1192, 212 },  // Living Memory     → Electrope Strike (CROSS-ZONE to Heritage Found)
    };

    /// <summary>Returns the aetheryte to teleport to for repair from the given territory. 0 if unknown.</summary>
    public static uint Resolve(uint territoryId) =>
        RepairAetheryteByTerritory.TryGetValue(territoryId, out var a) ? a : 0u;
}
