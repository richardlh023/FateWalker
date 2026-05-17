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
        return ctx == null ? 0u : ctx->MotivationNpc;
    }

    public static unsafe uint GetObjectiveNpc(this IFate fate)
    {
        var ctx = (CSFateContext*)fate.Address;
        return ctx == null ? 0u : ctx->ObjectiveNpc;
    }
}
