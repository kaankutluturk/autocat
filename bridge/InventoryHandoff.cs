using System;
using System.Collections.Generic;
namespace AutocatBridge {
// Pure presentation helpers. Ownership and transactions remain native.
public static class InventoryHandoff {
    public static bool BadgeState(bool authoritative, string slot, int itemId,
        IDictionary<string, int> selected) {
        int chosen;
        return selected.TryGetValue(slot, out chosen) ? chosen == itemId : authoritative;
    }
    public static bool NeedsOwnedReequip(bool hasVirtualSelection, int virtualItemId,
        int clickedItemId, bool nativeEquipped) {
        return hasVirtualSelection && virtualItemId != clickedItemId && !nativeEquipped;
    }
    public static float WheelAngle(int index, int count) {
        return count < 2 ? 0 : (float)index / (count - 1) * (20 * count) - (20 * count) / 2f;
    }
}
}
