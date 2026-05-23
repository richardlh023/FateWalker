using System.Collections.Generic;

namespace FateWalker.Data;

/// <summary>
/// Maps a player's ClassJob row ID (from Lumina) to the full type name of the
/// BossmodReborn autorotation module to enable for that job. xan provides most
/// jobs; WAR is covered by veyn (under flat BossMod.Autorotation namespace).
///
/// Base classes share the same module as their upgraded job (e.g. CNJ→WHM,
/// MRD→WAR) since BossMod's modules already declare both via BitMask.Build.
/// </summary>
public static class JobModuleMap
{
    public static readonly IReadOnlyDictionary<uint, string> JobToModule = new Dictionary<uint, string>
    {
        // Tanks
        {  1, "BossMod.Autorotation.xan.PLD" },   // Gladiator
        { 19, "BossMod.Autorotation.xan.PLD" },   // Paladin
        {  3, "BossMod.Autorotation.VeynWAR" },   // Marauder
        { 21, "BossMod.Autorotation.VeynWAR" },   // Warrior
        { 32, "BossMod.Autorotation.xan.DRK" },   // Dark Knight
        { 37, "BossMod.Autorotation.xan.GNB" },   // Gunbreaker

        // Healers
        {  6, "BossMod.Autorotation.xan.WHM" },   // Conjurer
        { 24, "BossMod.Autorotation.xan.WHM" },   // White Mage
        { 26, "BossMod.Autorotation.xan.SMN" },   // Arcanist (default→SMN; SCH player needs job stone)
        { 28, "BossMod.Autorotation.xan.SCH" },   // Scholar
        { 33, "BossMod.Autorotation.xan.AST" },   // Astrologian
        { 40, "BossMod.Autorotation.xan.SGE" },   // Sage

        // Melee
        {  2, "BossMod.Autorotation.xan.MNK" },   // Pugilist
        { 20, "BossMod.Autorotation.xan.MNK" },   // Monk
        {  4, "BossMod.Autorotation.xan.DRG" },   // Lancer
        { 22, "BossMod.Autorotation.xan.DRG" },   // Dragoon
        { 29, "BossMod.Autorotation.xan.NIN" },   // Rogue
        { 30, "BossMod.Autorotation.xan.NIN" },   // Ninja
        { 34, "BossMod.Autorotation.xan.SAM" },   // Samurai
        { 39, "BossMod.Autorotation.xan.RPR" },   // Reaper
        { 41, "BossMod.Autorotation.xan.VPR" },   // Viper

        // Phys Ranged
        {  5, "BossMod.Autorotation.xan.BRD" },   // Archer
        { 23, "BossMod.Autorotation.xan.BRD" },   // Bard
        { 31, "BossMod.Autorotation.xan.MCH" },   // Machinist
        { 38, "BossMod.Autorotation.xan.DNC" },   // Dancer

        // Casters
        {  7, "BossMod.Autorotation.xan.BLM" },   // Thaumaturge
        { 25, "BossMod.Autorotation.xan.BLM" },   // Black Mage
        { 27, "BossMod.Autorotation.xan.SMN" },   // Summoner
        { 35, "BossMod.Autorotation.xan.RDM" },   // Red Mage
        { 42, "BossMod.Autorotation.xan.PCT" },   // Pictomancer

        // Limited
        { 36, "BossMod.Autorotation.xan.BLU" },   // Blue Mage
    };

    public static string? Resolve(uint classJobId) =>
        JobToModule.TryGetValue(classJobId, out var name) ? name : null;

    // ClassJob IDs grouped by role. Used to pick the appropriate
    // StayCloseToTarget range so melee jobs chase mobs and ranged jobs stand
    // still. Limited jobs (BLU) treated as caster.
    private static readonly HashSet<uint> MeleeOrTankJobs = new()
    {
        // Tanks
        1, 19, 3, 21, 32, 37,
        // Melee
        2, 20, 4, 22, 29, 30, 34, 39, 41,
    };

    /// <summary>
    /// BossMod StayCloseToTarget "Range" option value for the given job.
    ///
    /// Melee / tank → "OnHitbox": donut at hitbox radius ± 1 y. Forces the
    /// bot to walk all the way to the mob's actual hitbox surface so the
    /// melee combo fires instead of the rotation falling back to ranged
    /// fillers (Holy Spirit / Confiteor / Tomahawk / etc.) from 6 y away.
    /// Earlier numeric values (2.6 → 3.2) computed goal radius as
    /// "value + mob_hitbox", which for large-hitbox mobs (~4 y) gave a
    /// goal radius of ~7 y — wide enough that the bot considered itself
    /// "in range" without ever actually closing in.
    ///
    /// Ranged / caster → "25" (yalms): stops at 25 y, well inside spell
    /// range and out of the mob's hitbox.
    /// </summary>
    public static string GetTargetRangeOption(uint classJobId) =>
        MeleeOrTankJobs.Contains(classJobId) ? "OnHitbox" : "25";
}
