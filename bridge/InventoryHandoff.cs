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
// Unlock All can already be on while the game is still creating its cat or filling its item catalog.
// Only that wait is retried, and only for a bounded number of attempts.
public sealed class PreviewNotReadyException : Exception {
    public const int Attempts = 60;
    public PreviewNotReadyException(string message) : base(message) { }
    public static bool Retry(Exception error, int failures) {
        return error is PreviewNotReadyException && failures < Attempts;
    }
}
}
