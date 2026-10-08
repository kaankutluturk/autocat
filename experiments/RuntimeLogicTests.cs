using System;
using System.Collections.Generic;
using AutocatBridge;

class RuntimeLogicTests {
    static void Main() {
        if (InventoryHandoff.WheelAngle(0, 1) != 0 || InventoryHandoff.WheelAngle(0, 2) != -20 ||
                InventoryHandoff.WheelAngle(1, 2) != 20 || InventoryHandoff.WheelAngle(1, 3) != 0)
            throw new Exception("Native wheel geometry");
        var selected = new Dictionary<string, int>();
        if (!InventoryHandoff.BadgeState(true, "skin", 10, selected))
            throw new Exception("Authoritative badge lost without override");
        selected["skin"] = 20;
        if (InventoryHandoff.BadgeState(true, "skin", 10, selected) ||
                !InventoryHandoff.BadgeState(false, "skin", 20, selected))
            throw new Exception("Owned to temporary transition shows two badges");
        selected["skin"] = -1;
        if (InventoryHandoff.BadgeState(true, "skin", 10, selected) ||
                InventoryHandoff.BadgeState(false, "skin", 20, selected))
            throw new Exception("Temporary clear leaves a badge selected");
        selected["skin"] = 10;
        if (!InventoryHandoff.BadgeState(true, "skin", 10, selected) ||
                InventoryHandoff.BadgeState(false, "skin", 20, selected))
            throw new Exception("Temporary to owned transition invalid");
        if (!InventoryHandoff.NeedsOwnedReequip(true, 20, 10, false))
            throw new Exception("Owned reselect after temporary must repair native toggle");
        if (!InventoryHandoff.NeedsOwnedReequip(true, -1, 10, false))
            throw new Exception("Owned reselect after virtual clear must repair native toggle");
        if (InventoryHandoff.NeedsOwnedReequip(false, 0, 10, false) ||
                InventoryHandoff.NeedsOwnedReequip(true, 10, 10, false) ||
                InventoryHandoff.NeedsOwnedReequip(true, 20, 10, true))
            throw new Exception("Owned reselect repair triggered outside stale-authoritative transition");
        var waiting = new PreviewNotReadyException("Cat not ready");
        if (!PreviewNotReadyException.Retry(waiting, 1) ||
                !PreviewNotReadyException.Retry(waiting, PreviewNotReadyException.Attempts - 1))
            throw new Exception("Preview startup wait must be retried");
        if (PreviewNotReadyException.Retry(waiting, PreviewNotReadyException.Attempts))
            throw new Exception("Preview startup wait must be bounded");
        if (PreviewNotReadyException.Retry(new Exception("Invalid/duplicate cosmetic ID"), 1) ||
                PreviewNotReadyException.Retry(new System.IO.IOException("backup"), 1))
            throw new Exception("Preview construction failure must not be retried");
        Console.WriteLine(
            "PASS: native merged wheel geometry and exclusive owned/temporary selection transitions");
        Console.WriteLine("PASS: preview startup retries only a bounded not-ready wait; other failures stop");
    }
}
