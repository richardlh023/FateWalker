// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FateWalker.Controller.Party;

/// <summary>
/// Pure-function geometry for spreading party members around a FATE centre so
/// they don't stack on the same tile (which is the dead-giveaway visual when
/// running multiple clients). All inputs are simple primitives; outputs are
/// world-space positions ready to feed into vnavmesh.
///
/// Algorithm:
///   1. Sort party member content-ids ascending → deterministic across clients.
///   2. Slot index 0..n-1 from that ordering.
///   3. Base angle = (2π / n) × index  + per-FATE seed (so different FATEs
///                                       don't always put member 0 at due-east).
///   4. Apply ± jitter to angle and radius so the ring isn't a perfect polygon.
///
/// The per-FATE seed is derived from the FATE id alone — every client computes
/// the same seed without exchanging it, so there's no broadcast cost.
/// </summary>
public static class PartyFormation
{
    /// <summary>Stable slot index for <paramref name="myCid"/> within the
    /// given member-cid set; -1 if the cid isn't present. Caller passes the
    /// raw list (any order, dupes allowed) — we dedupe + sort here.</summary>
    public static int AssignSlot(ulong myCid, IEnumerable<ulong> partyCids)
    {
        var ordered = partyCids.Where(c => c != 0).Distinct().OrderBy(c => c).ToArray();
        for (int i = 0; i < ordered.Length; i++)
            if (ordered[i] == myCid) return i;
        return -1;
    }

    public static IReadOnlyList<(ulong Cid, int Idx)> AssignSlots(IEnumerable<ulong> partyCids)
    {
        var ordered = partyCids.Where(c => c != 0).Distinct().OrderBy(c => c).ToArray();
        var list = new List<(ulong, int)>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++) list.Add((ordered[i], i));
        return list;
    }

    /// <summary>Compute the stand-point for one member relative to the FATE centre.</summary>
    /// <param name="fateCentre">World-space FATE epicentre.</param>
    /// <param name="fateRadius">FATE radius (yalms). Used as the cap for the spread.</param>
    /// <param name="fateId">Used to seed the rotation so different FATEs don't all
    ///   put the host at the same compass bearing.</param>
    /// <param name="memberCount">Total party members in formation.</param>
    /// <param name="myIdx">My slot (0..memberCount-1).</param>
    /// <param name="cfgRadius">Configured spread radius (clamped against fateRadius).</param>
    /// <param name="cfgJitter">±yalm jitter on radius + angle.</param>
    public static Vector3 ComputeStandPoint(
        Vector3 fateCentre,
        float fateRadius,
        uint fateId,
        int memberCount,
        int myIdx,
        float cfgRadius,
        float cfgJitter)
    {
        if (memberCount <= 1 || myIdx < 0) return fateCentre;

        // Stay inside the FATE ring; a 60 % bound leaves the player attackable
        // without straddling the ring border. Don't shrink to nothing for tiny FATEs.
        var maxR = Math.Max(3f, fateRadius * 0.6f);
        var radius = Math.Clamp(cfgRadius, 3f, maxR);

        // Seed the rotation deterministically from the FATE id. Two clients with
        // the same fateId compute the same seed → no broadcast needed.
        var seedAngle = (fateId * 137u % 360u) * (MathF.PI / 180f);

        var baseAngle = (MathF.Tau / memberCount) * myIdx + seedAngle;

        // Jitter: derive from (fateId, myIdx) so it's also deterministic per
        // (FATE, slot). Different members get different jitter; the same member
        // returning to the same FATE gets the same jitter (no Brownian wander).
        var jitterRng = new Random(unchecked((int)(fateId * 2654435761u) ^ myIdx));
        var angJ = ((float)jitterRng.NextDouble() - 0.5f) * 2f * (cfgJitter / radius); // rad
        var radJ = ((float)jitterRng.NextDouble() - 0.5f) * 2f * cfgJitter;            // yalm
        var angle = baseAngle + angJ;
        var r = MathF.Max(2f, radius + radJ);

        return new Vector3(
            fateCentre.X + r * MathF.Cos(angle),
            fateCentre.Y,
            fateCentre.Z + r * MathF.Sin(angle));
    }
}
