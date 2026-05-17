using FFXIVClientStructs.FFXIV.Client.Game;

namespace FateWalker.Data;

/// <summary>
/// Reads tradable currency counts from FFXIV inventory.
/// Bicolor Gemstone is an item (not in the "Currency" tab) — it sits in the
/// Crystals/Tomestones panel. <c>InventoryManager.GetInventoryItemCount</c>
/// walks every container and returns the total.
/// </summary>
public static class CurrencyReader
{
    // Item row IDs from the FFXIV Item Excel sheet.
    public const uint BicolorGemstoneItemId = 26807;
    // 1500 since patch 7.0 (was 1000 pre-DT).
    public const int BicolorGemstoneCap = 1500;

    public static unsafe int GetBicolorGemstoneCount()
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return -1;
        return inv->GetInventoryItemCount(BicolorGemstoneItemId);
    }
}
