using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace AutoCat.Diagnostics {
public static class Diag {
    public static void Fault(string a, string b, Exception e) {
        Console.WriteLine("DIAGNOSTIC " + b + ": " + (e.InnerException ?? e).Message);
    }
}
}

namespace AutocatBridge {
public sealed class DictionaryCache {
    const BindingFlags Flags = (BindingFlags)60;
    public FieldInfo Field(Type type, string name) { return type.GetField(name, Flags); }

    public MethodInfo Method(Type type, string name, int count) {
        foreach (var method in type.GetMethods(Flags))
            if (method.Name == name && method.GetParameters().Length == count)return method;
        throw new MissingMethodException(type.FullName, name);
    }
}

// Cosmetics are separately compiled by the real build; here this records the
// interaction boundary so the actual NativeUnlock dispatcher can be exercised.
public sealed class CosmeticPreview {
    public int Selects, Clears, Adopts, Disables, Chosen = -1;
    public bool FailDisable;

    public void Select(int id) {
        Selects++;
        Chosen = id;
    }

    public void Clear(string slot) {
        Clears++;
        Chosen = -1;
    }

    public void AdoptAuthoritative(string slot) {
        Adopts++;
        Chosen = -1;
    }

    public void Disable() {
        Disables++;
        Chosen = -1;
        if (FailDisable)throw new Exception("Injected renderer cleanup failure");
    }
}
}

public static class FakePrefs {
    public static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
    public static int Writes;

    public static void Save(string key, string value) {
        Values[key] = value;
        Writes++;
    }

    public static string Get(string key) {
        string value;
        return Values.TryGetValue(key, out value) ? value : "absent";
    }
}

namespace BongoCat {
public sealed class SteamItemRuntime {
    public int Identifier, Quantity, DisplayedItemAmount = 1, Consumed;
    public string ItemSlot;
    public bool IsEquipped, IsHiddenInInventory, PassQuality = true, PassEvent = true;
    public object Icon = new object();
    public Action OnItemUpdated;
    public int Id { get { return Identifier; } }
    public bool IsOwned { get { return Quantity > 0; } }
    public bool IsConsumable { get { return ItemSlot == "consumable"; } }
    public bool IsEmote { get { return ItemSlot == "emote"; } }

    public SteamItemRuntime(int id, string slot, int amount) {
        Identifier = id;
        ItemSlot = slot;
        Quantity = amount;
    }
}

public sealed class InventoryItem : Component {
    public SteamItemRuntime SteamItem;
    public ItemCollection Collection, _collection;
    public bool _isPartOfBongoDex, _initialized;
    public GameObject _preview = new GameObject(), _counterIndicator = new GameObject();

    public Image _equippedIcon = new Image(), _background = new Image(), _disabledOverlay = new Image(),
        _unownedOverlay = new Image();

    public object _equippedColor = "equipped", _unequippedColor = "normal";
    public int NativeClicks, Transactions, Initializations;
    public bool FailInitialization;
    public Button Button;
    public UnityEngine.Events.UnityEvent Saved;

    public InventoryItem(SteamItemRuntime item, ItemCollection collection) {
        SteamItem = item;
        Collection = _collection = collection;
        transform.SetParent(collection._collectionRoot, false);
        Button = new Button();
        Components[typeof(Button)] = Button;
        Button.onClick.AddListener(OnClick);
        Saved = Button.onClick;
        item.OnItemUpdated += OnItemUpdated;
        collection.OnInitialized += OnItemUpdated;
        OnItemUpdated();
        OnItemUpdated();
    }

    public void OnItemUpdated() {
        if (Destroyed)throw new Exception("Destroyed native row was used");
        if (FailInitialization)throw new Exception("Injected native initialization failure");
        if (_initialized && !SteamItem.IsOwned && !_isPartOfBongoDex) {
            gameObject.SetActive(false);
            return;
        }
        Initializations++;
        _preview.SetActive(true);
        _counterIndicator.SetActive(true);
        _unownedOverlay.enabled = !SteamItem.IsOwned;
        _disabledOverlay.enabled = false;
        _equippedIcon.enabled = SteamItem.IsEquipped;
        _background.color = SteamItem.IsEquipped ? _equippedColor : _unequippedColor;
        UpdateInventoryItem();
        _initialized = true;
    }

    public void UpdateInventoryItem() {
        gameObject.SetActive(!SteamItem.IsHiddenInInventory && SteamItem.PassQuality && SteamItem.PassEvent);
    }

    public void OnClick() {
        NativeClicks++;
        if (ItemExchange.Instance.IsVisible || Steam.TrashCan.Instance.InTrashMode) {
            Transactions++;
            return;
        }
        if (SteamItem.ItemSlot == "uitheme") {
            if (_isPartOfBongoDex && !SteamItem.IsOwned)UiThemes.UiThemeManager.Instance.Preview(SteamItem);
            else UiThemes.UiThemeManager.Instance.Toggle(SteamItem);
        } else if (SteamItem.ItemSlot == "emote" || SteamItem.ItemSlot == "consumable")
            EmoteDonut.Instance.ToggleItem(SteamItem);
        else CatCosmetics.Instance.Toggle(SteamItem);
    }
}

public sealed class ItemCollection {
    public List<InventoryItem> _collectionItems = new List<InventoryItem>();
    public Transform _collectionRoot = new GameObject().transform;
    public Action OnInitialized;
    public bool PassSearch = true;
    public bool IsItemVisible(InventoryItem row) { return PassSearch; }
}

public sealed class CatInventory {
    public static CatInventory Instance;
    public bool IsInitialized = true;

    public ItemCollection _hatsCollection = new ItemCollection(), _skinsCollection = new ItemCollection(),
        _emoteCollection = new ItemCollection(), _consumableCollection = new ItemCollection(),
        _uiThemeCollection = new ItemCollection();

    public int Refreshes;

    public InventoryItem Add(SteamItemRuntime item) {
        var collection = item.ItemSlot == "hat" ? _hatsCollection : item.ItemSlot == "skin" ?
            _skinsCollection : item.ItemSlot == "emote" ? _emoteCollection : item.ItemSlot == "consumable" ?
            _consumableCollection : _uiThemeCollection;
        var row = new InventoryItem(item, collection);
        collection._collectionItems.Add(row);
        return row;
    }

    public void SetSeparatorVisibility() { Refreshes++; }
}

public sealed class ItemExchange {
    public static ItemExchange Instance = new ItemExchange();
    public bool IsVisible { get; set; }
}

public sealed class CatCosmetics {
    public static CatCosmetics Instance = new CatCosmetics();
    public List<SteamItemRuntime> EquippedItems = new List<SteamItemRuntime>();

    public void Toggle(SteamItemRuntime item) {
        if (item.IsEquipped) {
            item.IsEquipped = false;
            EquippedItems.Remove(item);
        } else EquipItem(item, true, true, true);
        FakePrefs.Save("EQUIPPED_ITEMS_3",
            String.Join(",", EquippedItems.ConvertAll(i => i.Id.ToString()).ToArray()));
    }

    public void EquipItem(SteamItemRuntime item, bool a, bool b, bool c) {
        if (!item.IsOwned)throw new Exception("Fake native equip rejected");
        foreach (var prior in EquippedItems.ToArray())
            if (prior.ItemSlot == item.ItemSlot) {
                prior.IsEquipped = false;
                EquippedItems.Remove(prior);
            }
        item.IsEquipped = true;
        EquippedItems.Add(item);
        FakePrefs.Save("EQUIPPED_ITEMS_3",
            String.Join(",", EquippedItems.ConvertAll(i => i.Id.ToString()).ToArray()));
    }
}

namespace Multiplayer {
public static class SteamMultiplayer {
    public static int Sent;
    public static void SendEmote(SteamItemRuntime item) { Sent++; }
}
}

namespace UiThemes {
public sealed class UiTheme {
    public int ItemId;
    public UiTheme(int id) { ItemId = id; }
}

public sealed class UiThemeSet {
    public UiTheme Legacy = new UiTheme(0);
    public Dictionary<int, UiTheme> Themes = new Dictionary<int, UiTheme>();

    public UiTheme GetById(int id) {
        UiTheme result;
        return Themes.TryGetValue(id, out result) ? result : Legacy;
    }
}

public sealed class UiThemeManager {
    public static UiThemeManager Instance { get; set; }
    public UiTheme Current, Previewed;
    public UiThemeSet _themes = new UiThemeSet();
    public int PreviewCalls, EquipCalls, PushCalls;
    public bool ForceLegacy;
    public Action OnThemeChanged;

    public void Preview(SteamItemRuntime item) {
        PreviewCalls++;
        var asset = _themes.GetById(item.Id);
        Previewed = System.Object.ReferenceEquals(Previewed, asset) ? null : asset;
        PushAll();
    }

    public void Toggle(SteamItemRuntime item) {
        if (Current != null && Current.ItemId == item.Id)Equip(_themes.Legacy);
        else Equip(item);
    }

    public void Equip(SteamItemRuntime item) {
        if (!item.IsOwned)throw new Exception("Fake native theme rejected");
        Equip(_themes.GetById(item.Id));
    }

    void Equip(UiTheme asset) {
        EquipCalls++;
        Current = asset;
        Previewed = null;
        FakePrefs.Save("UI_THEME", asset.ItemId.ToString());
        PushAll();
    }

    public void RestoreEquipped(MenuTabs.MenuTab tab) {
        if (tab == MenuTabs.MenuTab.Bongodecks && Previewed != null) {
            Previewed = null;
            PushAll();
        }
    }

    void PushAll() {
        PushCalls++;
        if (OnThemeChanged != null)OnThemeChanged();
    }
}
}

namespace UI {
public sealed class BongoDexView : Component {
    public List<BongoDexCollection> BongoDexCollections { get; set; }
    public BongoDexView() { BongoDexCollections = new List<BongoDexCollection>(); }
}

public sealed class BongoDexCollection {
    public ItemCollection ItemCollection;
    public BongoDexCollection(ItemCollection value) { ItemCollection = value; }
}
}
}

public static class MenuTabs {
    public enum MenuTab { Inventory = 0, Bongodecks = 1 }
}

namespace Steam {
public sealed class TrashCan {
    public static TrashCan Instance = new TrashCan();
    public bool InTrashMode { get; set; }
}
}

public sealed class EmoteSpawner : Component {
    public int Spawns;
    public void SpawnEmoteParticle(BongoCat.SteamItemRuntime item) { Spawns++; }
}

public sealed class EmoteDonutEntry : Component, ICloneable {
    public BongoCat.SteamItemRuntime _steamItem;
    public EmoteSpawner _emoteSpawner;
    public EmoteDonut _emoteDonut;
    public Image image = new Image();
    public GameObject counter = new GameObject();
    public Button Button = new Button();

    public EmoteDonutEntry() {
        Components[typeof(Button)] = Button;
        Button.onClick.AddListener(SpawnEmoteParticle);
    }

    public void SetItem(BongoCat.SteamItemRuntime item, EmoteSpawner spawner, EmoteDonut donut) {
        if (donut.FailSetItem)throw new Exception("Injected entry initialization failure");
        _steamItem = item;
        _emoteSpawner = spawner;
        _emoteDonut = donut;
        if (item.IsConsumable) {
            counter.SetActive(true);
            if (item.Quantity == 0)donut.ToggleItem(item);
        } else counter.SetActive(false);
    }

    public void SpawnEmoteParticle() {
        _emoteSpawner.SpawnEmoteParticle(_steamItem);
        BongoCat.Multiplayer.SteamMultiplayer.SendEmote(_steamItem);
        if (_steamItem.IsConsumable) {
            _steamItem.Consumed++;
            _steamItem.Quantity--;
            if (_steamItem.OnItemUpdated != null)_steamItem.OnItemUpdated();
        }
    }

    public object Clone() { return new EmoteDonutEntry(); }
}

public sealed class EmoteDonut {
    public static EmoteDonut Instance;
    public Dictionary<int, EmoteDonutEntry> IDtoEmoteEntry = new Dictionary<int, EmoteDonutEntry>();
    public int maxEmoteCount = 4;
    public float radius = 100;
    public bool _isOpen = true, FailSetItem;
    public Transform emoteEntryParent = new GameObject().transform;
    public EmoteDonutEntry emoteEntryPrefab = new EmoteDonutEntry();
    public EmoteSpawner emoteSpawner = new EmoteSpawner();
    public int Rearranges;

    public void ToggleItem(BongoCat.SteamItemRuntime item) {
        if (IDtoEmoteEntry.ContainsKey(item.Id)) {
            var old = IDtoEmoteEntry[item.Id];
            old.transform.SetParent(null, false);
            IDtoEmoteEntry.Remove(item.Id);
        } else {
            if (!item.IsOwned)throw new Exception("Fake native emote rejected");
            var entry = new EmoteDonutEntry();
            entry.SetItem(item, emoteSpawner, this);
            entry.transform.SetParent(emoteEntryParent, false);
            IDtoEmoteEntry.Add(item.Id, entry);
        }
        FakePrefs.Save("EQUIPPED_EMOTES_3",
            String.Join(",", new List<int>(IDtoEmoteEntry.Keys).ConvertAll(i => i.ToString()).ToArray()));
        RearrangeEmotes();
    }

    public void RearrangeEmotes() {
        Rearranges++;
        var entries = new List<EmoteDonutEntry>(IDtoEmoteEntry.Values);
        for (int i = 0; i < entries.Count; i++) {
            float angle = entries.Count < 2 ? 0 :
                ((float)i / (entries.Count - 1) * 20 * entries.Count - 10 * entries.Count) * (float)Math.PI / 180;
            entries[i].transform.localPosition = new Vector3((float)Math.Sin(angle) * radius,
                (float)Math.Cos(angle) * radius, 0);
        }
    }
}
