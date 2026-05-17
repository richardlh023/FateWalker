using System.Collections.Generic;

namespace FateWalker.Data;

/// <summary>
/// Per-class "pull spell" — a low-cost basic GCD that we can fire to start
/// combat with a target the player hasn't yet aggro'd. Used when bot is in
/// Engaging state with a FATE-mob target but isn't in combat (e.g. defence
/// FATEs where mobs attack the NPC, not the player). Once we land one cast,
/// combat starts and the configured rotation backend (BossMod / RSR) takes
/// over.
///
/// Action IDs come from Lumina <c>Action</c> sheet. We pick the lowest-level
/// damage GCD available for each class so it works at any level. Auto-attack
/// (id 7) is the universal fallback for melee + phys ranged.
/// </summary>
public static class JobPullSpellMap
{
    /// <summary>Auto-attack toggle — works for melee + physical ranged.</summary>
    public const uint AutoAttack = 7;

    public static readonly IReadOnlyDictionary<uint, uint> Map = new Dictionary<uint, uint>
    {
        // ─── Tanks (auto-attack handles it once in melee range) ───
        {  1, 9 },        // GLA — Fast Blade
        { 19, 9 },        // PLD — Fast Blade
        {  3, 31 },       // MRD — Heavy Swing
        { 21, 31 },       // WAR — Heavy Swing
        { 32, 3617 },     // DRK — Hard Slash
        { 37, 16137 },    // GNB — Keen Edge

        // ─── Melee DPS ───
        {  2, 53 },       // PGL — Bootshine
        { 20, 53 },       // MNK — Bootshine
        {  4, 75 },       // LNC — True Thrust
        { 22, 75 },       // DRG — True Thrust
        { 29, 2240 },     // ROG — Spinning Edge
        { 30, 2240 },     // NIN — Spinning Edge
        { 34, 7477 },     // SAM — Hakaze
        { 39, 24373 },    // RPR — Slice
        { 41, 34606 },    // VPR — Steel Fangs

        // ─── Physical Ranged (auto-attack works at range too) ───
        {  5, 97 },       // ARC — Heavy Shot
        { 23, 97 },       // BRD — Heavy Shot
        { 31, 2866 },     // MCH — Split Shot
        { 38, 15989 },    // DNC — Cascade

        // ─── Casters (auto-attack does NOT work — must use a spell) ───
        {  6, 119 },      // CNJ — Stone (yes, fits the conjurer kit)
        { 24, 119 },      // WHM — Stone
        {  7, 142 },      // THM — Blizzard? — actually 142 is Blizzard. Use Fire (141)
        { 25, 141 },      // BLM — Fire
        { 26, 163 },      // ACN — Ruin
        { 27, 163 },      // SMN — Ruin
        { 28, 17869 },    // SCH — Broil III (Broil = 3584, Broil II = 7435 — but 17869 is Broil III)
        { 33, 3596 },     // AST — Malefic
        { 35, 7503 },     // RDM — Jolt
        { 36, 11385 },    // BLU — Water Cannon (always learnable)
        { 40, 24283 },    // SGE — Dosis
        { 42, 34650 },    // PCT — Fire in Red

        // Hand & Land — no combat, defaults to auto-attack which won't fire.
    };

    /// <summary>
    /// Resolve a usable basic-attack action for this job. Returns auto-attack
    /// (id 7) for unknown / DoH+DoL classes; caller can still try it.
    /// </summary>
    public static uint Resolve(uint classJobId)
    {
        return Map.TryGetValue(classJobId, out var actionId) ? actionId : AutoAttack;
    }
}
