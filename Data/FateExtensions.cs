using Dalamud.Game.ClientState.Fates;
using CSFateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;

namespace FateWalker.Data;

/// <summary>
/// Extensions that pull fields off the raw FateContext that Dalamud's IFate
/// interface doesn't surface. MotivationNpc (offset 0x10) is the entity ID
/// of the NPC to interact with to START a Preparing FATE.
/// </summary>
public static class FateExtensions
{
    public static unsafe uint GetMotivationNpc(this IFate fate)
    {
        var ctx = (CSFateContext*)fate.Address;
        if (ctx == null) return 0u;
        var id = ctx->MotivationNpc;
        // 0xE0000000+ is the FFXIV "no entity" sentinel range (0xE0000000 = none,
        // 0xE0000001 = self, 0xFFFFFFFF = invalid). A FATE whose MotivationNpc
        // is in that range has no real start-NPC — treat as zero so the selector
        // doesn't try to fly somewhere to interact with nothing and loop on
        // mount/dismount/re-pick (Hoshimito session 2026-06-07 hit this with
        // FATE 1896 'La Selva se lo Llevó' for 22 seconds before logic-loop
        // blacklisted the FATE).
        return id >= 0xE0000000u ? 0u : id;
    }

    public static unsafe uint GetObjectiveNpc(this IFate fate)
    {
        var ctx = (CSFateContext*)fate.Address;
        return ctx == null ? 0u : ctx->ObjectiveNpc;
    }
}
