using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace AutocatBridge {
// Native rows and native wheel keys remain game-owned. Each click checks real
// ownership; temporary rendering and isolated wheel children are session-only.
public sealed class NativeUnlock {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;

    sealed class Row {
        public object Collection,Component,Item,Button,NativeClicks;
        public PropertyInfo ClickProperty;
        public Action Updated;
        public bool Dex;
    }

    readonly DictionaryCache lookup = new DictionaryCache();
    readonly Assembly game;
    readonly CosmeticPreview visuals;
    readonly Action<string> log;
    readonly List<Row> rows = new List<Row>();
    readonly Dictionary<int, Row> rowById = new Dictionary<int, Row>();
    readonly HashSet<object> adopted = new HashSet<object>();
    readonly Dictionary<int, object> emotes = new Dictionary<int, object>();
    readonly Dictionary<string, int> selected = new Dictionary<string, int>();
    readonly object inventory, donut, themes;
    readonly Type lobbyType, buttonType;
    IEnumerator builder, dexBuilder;
    int tickCursor, dexCursor;
    ulong lastLobby;
    bool disabled;
    object selectedTheme, selectedThemeAsset, dexView;

    public bool Loading { get { return builder != null || dexBuilder != null; } }
    public string Error = "";
    public int Added { get { return rows.Count; } }

    object F(object x, string n) { return lookup.Field(x.GetType(), n).GetValue(x); }
    object C(object x, string n, params object[] args) {
        return lookup.Method(x.GetType(), n, args.Length).Invoke(x, args);
    }
    object S(string type, string field) {
        return game.GetType(type, true, false).GetField(field, Flags).GetValue(null);
    }
    object Instance(string type) {
        return game.GetType(type, true, false).GetProperty("Instance", Flags).GetValue(null, null);
    }
    bool Owned(object item) { return (bool)C(item, "get_IsOwned"); }
    int Id(object item) { return (int)C(item, "get_Id"); }
    string Slot(object item) { return F(item, "ItemSlot").ToString(); }
    bool Alive(object value) { return (value as Component) != null; }
    bool Blocked() {
        return (bool)C(S("BongoCat.ItemExchange", "Instance"), "get_IsVisible") ||
            (bool)C(S("Steam.TrashCan", "Instance"), "get_InTrashMode");
    }
    void Fault(string operation, Exception e) {
        Error = (e.InnerException ?? e).Message;
        AutoCat.Diagnostics.Diag.Fault("UnlockAll", operation, e);
    }

    public NativeUnlock(Assembly assembly, CosmeticPreview renderer, Action<string> logger) {
        game = assembly;
        visuals = renderer;
        log = logger;
        inventory = S("BongoCat.CatInventory", "Instance");
        donut = S("EmoteDonut", "Instance");
        themes = Instance("BongoCat.UiThemes.UiThemeManager");
        lobbyType = Assembly.Load("Heathen.Steamworks").GetType(
            "Heathen.SteamworksIntegration.LobbyData", true, false);
        buttonType = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.Button", true, false);
        if (!(bool)F(inventory, "IsInitialized"))throw new Exception("Inventory not ready");
        FindDexView();
        builder = AdoptRows();
    }
    IEnumerator AdoptRows() {
        foreach (string name in new[] {
            "_hatsCollection", "_skinsCollection", "_emoteCollection",
            "_consumableCollection", "_uiThemeCollection"
        }) {
            var collection = F(inventory, name);
            foreach (var component in (IEnumerable)F(collection, "_collectionItems")) {
                var item = F(component, "SteamItem");
                if (item == null)continue;
                string slot = Slot(item);
                if (slot != "hat" && slot != "skin" && slot != "emote" &&
                            slot != "consumable" && slot != "uitheme")continue;
                if (slot == "uitheme" && !SupportedTheme(item))continue;
                AdoptRow(collection, component, item);
                yield return null;
            }
        }
        var dex = AdoptDexRows();
        while (dex.MoveNext())yield return null;
        C(inventory, "SetSeparatorVisibility");
        log("native inventory rows adopted=" + rows.Count + " ownership unchanged");
    }
    void FindDexView() {
        if (Alive(dexView))return;
        dexView = null;
        dexCursor = 0;
        foreach (var view in Resources.FindObjectsOfTypeAll(
                game.GetType("BongoCat.UI.BongoDexView", true, false))) {
            if (!Alive(view))continue;
            var scene = C(((Component)view).gameObject, "get_scene");
            if (!(bool)C(scene, "IsValid"))continue;
            dexView = view;
            break;
        }
    }
    IEnumerator AdoptDexRows() {
        if (dexView == null)yield break;
        var collections = (IList)C(dexView, "get_BongoDexCollections");
        while (dexCursor < collections.Count) {
            var collection = F(collections[dexCursor++], "ItemCollection");
            foreach (var component in (IEnumerable)F(collection, "_collectionItems")) {
                var item = F(component, "SteamItem");
                if (item == null || Slot(item) != "uitheme" || !SupportedTheme(item))continue;
                AdoptRow(collection, component, item);
                yield return null;
            }
        }
    }
    void AdoptRow(object collection, object component, object item) {
        if (adopted.Contains(component))return;
        var button = ((Component)component).GetComponent(buttonType);
        if (button == null)throw new Exception("Native item button missing");
        var property = buttonType.GetProperty("onClick");
        var row = new Row {
            Collection = collection,
            Component = component,
            Item = item,
            Button = button,
            ClickProperty = property,
            NativeClicks = property.GetValue(button, null),
            Dex = (bool)F(component, "_isPartOfBongoDex")
        };
        // Save before mutation, so partial construction and failures can restore it.
        rows.Add(row);
        adopted.Add(component);
        rowById[Id(item)] = row;
        var clicks = Activator.CreateInstance(property.PropertyType);
        UnityAction click = () => {
            if (disabled)return;
            try {
                Dispatch(row);
            } catch (Exception e) {
                Fault("selection.failed", e);
            }
        };
        C(clicks, "AddListener", click);
        property.SetValue(button, clicks, null);
        row.Updated = () => {
            if (disabled)return;
            try {
                Present(row);
            } catch (Exception e) {
                Fault("row.refresh.failed", e);
            }
        };
        if (!Owned(item)) {
            lookup.Field(component.GetType(), "_initialized").SetValue(component, false);
            C(component, "OnItemUpdated");
        }
        AddCallback(item, "OnItemUpdated", row.Updated);
        AddCallback(collection, "OnInitialized", row.Updated);
        Present(row);
    }
    void AddCallback(object source, string name, Action callback) {
        var field = lookup.Field(source.GetType(), name);
        field.SetValue(source, Delegate.Combine((Delegate)field.GetValue(source), callback));
    }
    void RemoveCallback(object source, string name, Action callback) {
        var field = lookup.Field(source.GetType(), name);
        field.SetValue(source, Delegate.Remove((Delegate)field.GetValue(source), callback));
    }
    public void Advance() {
        if (disabled)return;
        float start = Time.realtimeSinceStartup;
        try {
            for (int count = 0; count < 8 && Time.realtimeSinceStartup - start < 0.002f; count++) {
                var active = builder ?? dexBuilder;
                if (active == null)break;
                if (!active.MoveNext()) {
                    if (System.Object.ReferenceEquals(active, builder))builder = null;
                    else dexBuilder = null;
                }
            }
        } catch {
            Disable();
            throw;
        }
    }
    bool SupportedTheme(object item) {
        var asset = C(F(themes, "_themes"), "GetById", Id(item));
        return asset != null && Convert.ToInt32(F(asset, "ItemId")) == Id(item);
    }
    void InvokeNative(Row row) { C(row.NativeClicks, "Invoke"); }
    void Dispatch(Row row) {
        Error = "";
        var item = row.Item;
        int id = Id(item);
        string slot = Slot(item);
        if (!Owned(item)) {
            if (Blocked())return;
            if (row.Dex) {
                InvokeNative(row);
                var preview = F(themes, "Previewed");
                var asset = C(F(themes, "_themes"), "GetById", id);
                if (System.Object.ReferenceEquals(preview, asset)) {
                    selectedTheme = item;
                    selectedThemeAsset = asset;
                } else if (preview == null || !System.Object.ReferenceEquals(preview, selectedThemeAsset)) {
                    selectedTheme = null;
                    selectedThemeAsset = null;
                }
                RefreshSlot(slot);
            } else Choose(item);
            return;
        }
        // Native owned transaction routing stays intact.
        if (Blocked()) {
            InvokeNative(row);
            return;
        }
        if (slot == "emote" || slot == "consumable") {
            RetireEmote(id);
            InvokeNative(row);
            MaintainWheel();
            RefreshSlot(slot);
            return;
        }
        if (slot == "uitheme") {
            var current = F(themes, "Current");
            bool repair = selectedTheme != null && current != null &&
                Convert.ToInt32(F(current, "ItemId")) == id;
            ClearTheme();
            if (repair) {
                var method = themes.GetType().GetMethod("Equip", Flags, null,
                    new[] {game.GetType("BongoCat.SteamItemRuntime", true, false)}, null);
                method.Invoke(themes, new[] {item});
            } else {
                InvokeNative(row);
            }
            RefreshSlot(slot);
            return;
        }
        int prior;
        bool hadVirtual = selected.TryGetValue(slot, out prior);
        InvokeNative(row);
        if (InventoryHandoff.NeedsOwnedReequip(hadVirtual, prior, id,
                (bool)F(item, "IsEquipped"))) {
            C(S("BongoCat.CatCosmetics", "Instance"), "EquipItem", item, true, true, true);
            if (!(bool)F(item, "IsEquipped"))throw new Exception("Owned cosmetic re-equip was rejected");
        }
        visuals.AdoptAuthoritative(slot);
        selected.Remove(slot);
        RefreshSlot(slot);
        Broadcast();
    }
    // Legacy callers follow the same actual-ownership dispatch boundary.
    public void Choose(object item) {
        if (disabled)return;
        Row row;
        if (Owned(item)) {
            if (rowById.TryGetValue(Id(item), out row))Dispatch(row);
            return;
        }
        if (Blocked())return;
        Error = "";
        int id = Id(item);
        string slot = Slot(item);
        if (slot == "emote" || slot == "consumable") {
            ToggleEmote(item, id);
            return;
        }
        if (slot == "uitheme") {
            if (!SupportedTheme(item))throw new Exception("Unsupported UI theme asset");
            if (selectedTheme != null && Id(selectedTheme) == id) {
                ClearTheme();
            } else {
                selectedTheme = item;
                selectedThemeAsset = C(F(themes, "_themes"), "GetById", id);
                if (!System.Object.ReferenceEquals(F(themes, "Previewed"), selectedThemeAsset)) {
                    C(themes, "Preview", item);
                }
            }
            RefreshSlot(slot);
            return;
        }
        int prior;
        bool clear = selected.TryGetValue(slot, out prior) && prior == id;
        if (clear) {
            visuals.Clear(slot);
            selected[slot] = -1;
        } else{
            visuals.Select(id);
            selected[slot] = id;
        }
        RefreshSlot(slot);
        Broadcast();
    }
    void ClearTheme() {
        var asset = selectedThemeAsset;
        selectedTheme = null;
        selectedThemeAsset = null;
        if (asset == null || !System.Object.ReferenceEquals(F(themes, "Previewed"), asset))return;
        var restore = themes.GetType().GetMethod("RestoreEquipped", Flags);
        restore.Invoke(themes, new[] {
            Enum.ToObject(restore.GetParameters()[0].ParameterType, 1)
        });
    }
    public void MaintainTheme() {
        if (!disabled && selectedTheme != null &&
                    !System.Object.ReferenceEquals(F(themes, "Previewed"), selectedThemeAsset)) {
            C(themes, "Preview", selectedTheme);
        }
    }
    void SetRowEquippedVisual(object row, bool equipped) {
        var badge = (Behaviour)F(row, "_equippedIcon");
        if (badge.enabled != equipped)badge.enabled = equipped;
        var background = F(row, "_background");
        background.GetType().GetProperty("color").SetValue(
            background,
            F(row, equipped ? "_equippedColor" : "_unequippedColor"),
            null);
    }
    void Present(Row row) {
        if (!Alive(row.Component))return;
        var item = row.Item;
        int id = Id(item);
        string slot = Slot(item);
        bool owned = Owned(item);
        if (!owned && !row.Dex) {
            // Reuse the game's visibility gates (quality, hidden, favorites/event,
            // transaction category), then respect its collection's text/reveal filter.
            C(row.Component, "UpdateInventoryItem");
            var component = (Component)row.Component;
            bool visible = component.gameObject.activeSelf && !Blocked() &&
                (bool)C(row.Collection, "IsItemVisible", row.Component);
            component.gameObject.SetActive(visible);
            if (visible)component.transform.parent.gameObject.SetActive(true);
            ((GameObject)F(row.Component, "_preview")).SetActive(false);
            ((GameObject)F(row.Component, "_counterIndicator")).SetActive(false);
            ((Behaviour)F(row.Component, "_disabledOverlay")).enabled = false;
            ((Behaviour)F(row.Component, "_unownedOverlay")).enabled = false;
        }
        if (owned && !row.Dex) {
            var component = (Component)row.Component;
            component.gameObject.SetActive(component.gameObject.activeSelf &&
                (bool)C(row.Collection, "IsItemVisible", row.Component));
        }
        bool equipped;
        if (slot == "emote" || slot == "consumable") {
            equipped = emotes.ContainsKey(id) || (bool)F(item, "IsEquipped");
        } else if (slot == "uitheme") {
            equipped = selectedTheme != null ? Id(selectedTheme) == id : (bool)F(item, "IsEquipped");
        } else {
            equipped = InventoryHandoff.BadgeState((bool)F(item, "IsEquipped"), slot, id, selected);
        }
        SetRowEquippedVisual(row.Component, equipped);
    }
    void RefreshSlot(string slot) {
        foreach (var row in rows) {
            if (Slot(row.Item) == slot)Present(row);
        }
    }
    void ToggleEmote(object item, int id) {
        MaintainWheel();
        if (emotes.ContainsKey(id)) {
            RetireEmote(id);
            MaintainWheel();
            C(donut, "RearrangeEmotes");
            if (emotes.Count > 0)LayoutWheel();
            RefreshSlot(Slot(item));
            return;
        }
        CreateEmote(item, id);
    }
    void CreateEmote(object item, int id) {
        var wheel = (IDictionary)F(donut, "IDtoEmoteEntry");
        if (wheel.Contains(id))return;
        if (wheel.Count + emotes.Count >= Convert.ToInt32(F(donut, "maxEmoteCount"))) {
            throw new Exception("Emote wheel full: remove an emote first");
        }
        var copy = (Component)UnityEngine.Object.Instantiate(
            (UnityEngine.Object)F(donut, "emoteEntryPrefab"));
        try {
            copy.transform.SetParent((Transform)F(donut, "emoteEntryParent"), false);
            if ((bool)C(item, "get_IsConsumable")) {
                InitConsumable(copy, item);
            } else {
                C(copy, "SetItem", item, F(donut, "emoteSpawner"), donut);
            }
            // Native preference saves must never see virtual keys.
            emotes.Add(id, copy);
            copy.gameObject.SetActive((bool)F(donut, "_isOpen"));
            LayoutWheel();
            RefreshSlot(Slot(item));
        } catch {
            emotes.Remove(id);
            DestroyEntry(copy);
            C(donut, "RearrangeEmotes");
            if (emotes.Count > 0)LayoutWheel();
            throw;
        }
    }
    void DestroyEntry(object value) {
        var entry = value as Component;
        if (entry == null)return;
        entry.gameObject.SetActive(false);
        entry.transform.SetParent(null, false);
        UnityEngine.Object.Destroy(entry.gameObject);
    }
    void RetireEmote(int id) {
        object entry;
        if (!emotes.TryGetValue(id, out entry))return;
        emotes.Remove(id);
        DestroyEntry(entry);
    }
    void InitConsumable(Component entry, object item) {
        lookup.Field(entry.GetType(), "_steamItem").SetValue(entry, item);
        lookup.Field(entry.GetType(), "_emoteSpawner").SetValue(entry, F(donut, "emoteSpawner"));
        lookup.Field(entry.GetType(), "_emoteDonut").SetValue(entry, donut);
        var image = F(entry, "image");
        image.GetType().GetProperty("sprite").SetValue(image, F(item, "Icon"), null);
        ((GameObject)F(entry, "counter")).SetActive(false);
        var button = entry.GetComponent(buttonType);
        if (button == null)throw new Exception("Consumable wheel button missing");
        var property = buttonType.GetProperty("onClick");
        var clicks = Activator.CreateInstance(property.PropertyType);
        UnityAction click = () => {
            if (disabled)return;
            try {
                int id = Id(item);
                if (Owned(item)) {
                    RetireEmote(id);
                    MaintainWheel();
                    C(donut, "RearrangeEmotes");
                    if (emotes.Count > 0)LayoutWheel();
                    RefreshSlot("consumable");
                    return;
                }
                if (Blocked())return;
                C(F(donut, "emoteSpawner"), "SpawnEmoteParticle", item);
                game.GetType("BongoCat.Multiplayer.SteamMultiplayer", true, false)
                    .GetMethod("SendEmote", Flags).Invoke(null, new[] {item});
            } catch (Exception e) {
                Fault("consumable.playback.failed", e);
            }
        };
        C(clicks, "AddListener", click);
        property.SetValue(button, clicks, null);
    }
    public object TemporaryReusable() {
        if (disabled)return null;
        foreach (var pair in emotes) {
            if (!Alive(pair.Value))continue;
            var item = F(pair.Value, "_steamItem");
            if (item != null && !Owned(item) && !(bool)C(item, "get_IsConsumable"))return pair.Value;
        }
        return null;
    }
    void MaintainWheel() {
        if (disabled || donut == null)return;
        var wheel = (IDictionary)F(donut, "IDtoEmoteEntry");
        var remove = new List<int>();
        var recover = new List<object>();
        foreach (var pair in emotes) {
            if (!Alive(pair.Value)) {
                remove.Add(pair.Key);
                Row row;
                if (rowById.TryGetValue(pair.Key, out row) && !Owned(row.Item) && !wheel.Contains(pair.Key)) {
                    recover.Add(row.Item);
                }
            } else if (Owned(F(pair.Value, "_steamItem")) || wheel.Contains(pair.Key)) {
                remove.Add(pair.Key);
            }
        }
        foreach (int id in remove)RetireEmote(id);
        bool changed = remove.Count > 0;
        int capacity = Convert.ToInt32(F(donut, "maxEmoteCount"));
        while (wheel.Count + emotes.Count > capacity && emotes.Count > 0) {
            int id = 0;
            foreach (var pair in emotes) {
                id = pair.Key;
                break;
            }
            RetireEmote(id);
            changed = true;
        }
        foreach (var item in recover) {
            if (wheel.Count + emotes.Count >= capacity)continue;
            try {
                CreateEmote(item, Id(item));
            } catch (Exception e) {
                Fault("wheel.repair.failed", e);
            }
        }
        if (emotes.Count > 0)LayoutWheel();
        else if (changed)C(donut, "RearrangeEmotes");
    }
    void LayoutWheel() {
        var entries = new List<Component>();
        foreach (DictionaryEntry pair in (IDictionary)F(donut, "IDtoEmoteEntry")) {
            var component = pair.Value as Component;
            if (component != null)entries.Add(component);
        }
        foreach (var pair in emotes) {
            var component = pair.Value as Component;
            if (component != null)entries.Add(component);
        }
        float radius = (float)F(donut, "radius");
        for (int i = 0; i < entries.Count; i++) {
            float angle = InventoryHandoff.WheelAngle(i, entries.Count) * 0.0174532924f;
            entries[i].transform.localPosition = new Vector3(Mathf.Sin(angle), Mathf.Cos(angle), 0) * radius;
        }
    }
    void Broadcast() {
        var lobby = lobbyType.GetField("Current", Flags).GetValue(null);
        if (!(bool)C(lobby, "get_IsValid"))return;
        foreach (var pair in selected) {
            C(lobby, pair.Key == "hat" ? "set_MyHat" : "set_MySkin", pair.Value);
        }
    }
    public void BroadcastSlot(string slot, int id) {
        RefreshSlot(slot);
        var lobby = lobbyType.GetField("Current", Flags).GetValue(null);
        if ((bool)C(lobby, "get_IsValid")) {
            C(lobby, slot == "hat" ? "set_MyHat" : "set_MySkin", id);
        }
    }
    public void Tick() {
        if (disabled)return;
        float start = Time.realtimeSinceStartup;
        MaintainWheel();
        int processed = 0;
        FindDexView();
        if (builder == null && dexBuilder == null && dexView != null &&
                    ((IList)C(dexView, "get_BongoDexCollections")).Count > dexCursor) {
            dexBuilder = AdoptDexRows();
        }
        while (rows.Count > 0 && processed < Math.Min(rows.Count, 32) &&
                                Time.realtimeSinceStartup - start < 0.0015f) {
            if (tickCursor >= rows.Count)tickCursor = 0;
            Present(rows[tickCursor++]);
            processed++;
        }
        var lobby = lobbyType.GetField("Current", Flags).GetValue(null);
        ulong id = (ulong)F(lobby, "_id");
        if (id != lastLobby) {
            lastLobby = id;
            if (selected.Count > 0)Broadcast();
        }
    }
    public void Disable() {
        if (disabled)return;
        disabled = true;
        builder = null;
        dexBuilder = null;
        Exception failure = null;
        Action<Action> cleanup = action => {
            try {
                action();
            } catch (Exception e) {
                if (failure == null)failure = e;
                AutoCat.Diagnostics.Diag.Fault("UnlockAll", "cleanup.unit.failed", e);
            }
        };
        foreach (var row in rows) {
            var entry = row;
            cleanup(() => entry.ClickProperty.SetValue(entry.Button, entry.NativeClicks, null));
            if (entry.Updated != null) {
                cleanup(() => RemoveCallback(entry.Item, "OnItemUpdated", entry.Updated));
                cleanup(() => RemoveCallback(entry.Collection, "OnInitialized", entry.Updated));
            }
        }
        foreach (var pair in emotes) {
            var entry = pair.Value;
            cleanup(() => DestroyEntry(entry));
        }
        emotes.Clear();
        cleanup(() => C(donut, "RearrangeEmotes"));
        cleanup(() => ClearTheme());
        cleanup(() => visuals.Disable());
        if (selected.Count > 0)cleanup(() => {
            var slots = new List<string>(selected.Keys);
            foreach (string slot in slots) {
                selected[slot] = -1;
                foreach (var item in (IEnumerable)F(S("BongoCat.CatCosmetics", "Instance"), "EquippedItems")) {
                    if (Slot(item) == slot)selected[slot] = Id(item);
                }
            }
            Broadcast();
        });
        selected.Clear();
        foreach (var row in rows) {
            var entry = row;
            if (!Alive(entry.Component))continue;
            cleanup(() => {
                lookup.Field(entry.Component.GetType(), "_initialized").SetValue(entry.Component, false);
                C(entry.Component, "OnItemUpdated");
                if (!Owned(entry.Item))C(entry.Component, "OnItemUpdated");
                if (!entry.Dex) {
                    var component = (Component)entry.Component;
                    component.gameObject.SetActive(component.gameObject.activeSelf &&
                        (bool)C(entry.Collection, "IsItemVisible", entry.Component));
                }
            });
        }
        // Recalculate inventory roots without emitting item-wide callbacks: those
        // also include native consumable depletion/quantity listeners.
        foreach (string name in new[] {
            "_hatsCollection", "_skinsCollection", "_emoteCollection",
            "_consumableCollection", "_uiThemeCollection"
        }) {
            string collectionName = name;
            cleanup(() => {
                var root = (Transform)F(F(inventory, collectionName), "_collectionRoot");
                bool active = false;
                foreach (Transform child in root) {
                    if (child.gameObject.activeSelf) {
                        active = true;
                        break;
                    }
                }
                root.gameObject.SetActive(active);
            });
        }
        rows.Clear();
        rowById.Clear();
        adopted.Clear();
        cleanup(() => C(inventory, "SetSeparatorVisibility"));
        log("native unlock disabled; original row events and legitimate presentation restored");
        if (failure != null)throw new Exception("Native unlock cleanup completed with failures", failure);
    }
}
}
