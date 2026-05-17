using FFXIVClientStructs.FFXIV.Client.Game;

namespace FateWalker.Data;

/// <summary>
/// Reads equipped gear durability via ClientStructs <c>InventoryManager</c>.
/// FFXIV stores condition as 0–30000 internally (= raw / 300 to get %).
/// Slots 0–12 of <c>InventoryType.EquippedItems</c> are the 13 gear slots
/// (main hand, off, head, body, hands, waist, legs, feet, ears, neck, wrists,
/// ring1, ring2). Slot 13 exists in the container (soul crystal) but has no
/// real durability — we explicitly stop at index 12. Empty slots have
/// <c>ItemId == 0</c> and are skipped.
/// </summary>
public static class GearDurability
{
    public sealed record DurabilityReport(int MinPercent, int SlotIndex, uint ItemId);

    public static unsafe DurabilityReport? GetMinDurability()
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return null;

        var container = inv->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded) return null;

        int worst = 101;
        int worstSlot = -1;
        uint worstItem = 0;
        // Slots 0–12 only — slot 13 is soul crystal, no durability.
        // Use Items[i] direct array access (same pattern as AutoDuty
        // InventoryHelper.LowestEquippedItem) rather than GetInventorySlot,
        // which can return a symbolic wrapper that bypasses real condition.
        for (int i = 0; i < 13; i++)
        {
            var item = container->Items[i];
            if (item.ItemId == 0) continue;
            int pct = item.Condition / 300;   // 0..30000 → 0..100
            if (pct < worst)
            {
                worst = pct;
                worstSlot = i;
                worstItem = item.ItemId;
            }
        }

        return worstSlot < 0 ? null : new DurabilityReport(worst, worstSlot, worstItem);
    }
}
