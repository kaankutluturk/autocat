using System;using System.Collections.Generic;
namespace AutocatBridge {
// Wait for the game's real row before retiring only the trainer-owned row.
public static class InventoryHandoff {
 public static bool BadgeState(bool authoritative,string slot,int itemId,IDictionary<string,int> selected){int chosen;return selected.TryGetValue(slot,out chosen)?chosen==itemId:authoritative;}
 public static bool NeedsOwnedReequip(bool hasVirtualSelection,int virtualItemId,int clickedItemId,bool nativeEquipped){return hasVirtualSelection&&virtualItemId!=clickedItemId&&!nativeEquipped;}
 // Ownership calls cross the reflected game boundary. Scan a bounded slice so a
 // large temporary catalog cannot produce a periodic main-thread hitch.
 public static int ReconcileSlice(List<object[]> rows,ref int cursor,int maximum,Func<object,bool> owned,Func<object[],object> replacement,Action<object> bind,Action<object[]> remove){int retired=0,checkedRows=0;if(rows.Count==0){cursor=0;return 0;}while(rows.Count>0&&checkedRows<maximum){if(cursor>=rows.Count)cursor=0;var entry=rows[cursor];checkedRows++;if(owned(entry[2])){var native=replacement(entry);if(native!=null){bind(native);remove(entry);rows.RemoveAt(cursor);retired++;continue;}}cursor++;}if(cursor>=rows.Count)cursor=0;return retired;}
}
}
