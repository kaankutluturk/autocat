using System;
using System.Collections.Generic;
using System.Reflection;
using AutocatBridge;
using BongoCat;
using BongoCat.UiThemes;
using UnityEngine;
using UnityEngine.UI;

class UnlockSessionTests {
    static int passed;
    static void Assert(bool value, string name) { if (!value)throw new Exception(name); }

    sealed class Fixture {
        public NativeUnlock Unlock;
        public CosmeticPreview Visuals = new CosmeticPreview();
        public CatInventory Inventory;
        public EmoteDonut Wheel;
        public UiThemeManager Themes;
        public BongoCat.UI.BongoDexView DexView;
        public Dictionary<int, SteamItemRuntime> Items = new Dictionary<int, SteamItemRuntime>();
        public Dictionary<int, InventoryItem> Rows = new Dictionary<int, InventoryItem>();

        public Fixture(bool build) {
            Resources.Objects.Clear();
            FakePrefs.Values.Clear();
            FakePrefs.Writes = 0;
            ItemExchange.Instance.IsVisible = false;
            Steam.TrashCan.Instance.InTrashMode = false;
            CatCosmetics.Instance = new CatCosmetics();
            CatInventory.Instance = Inventory = new CatInventory();
            EmoteDonut.Instance = Wheel = new EmoteDonut();
            UiThemeManager.Instance = Themes = new UiThemeManager();
            BongoCat.Multiplayer.SteamMultiplayer.Sent = 0;
            DexView = new BongoCat.UI.BongoDexView();
            Add(1, "hat", 1);
            Add(2, "hat", 0);
            Add(3, "uitheme", 1);
            Add(4, "uitheme", 0);
            Add(5, "emote", 0);
            Add(6, "consumable", 0);
            Add(7, "uitheme", 0);
            Add(8, "uitheme", 0);
            Add(9, "emote", 1);
            Add(10, "consumable", 1);
            Add(11, "skin", 1);
            Add(12, "skin", 0);
            CatCosmetics.Instance.EquipItem(Items[11], true, true, true);
            foreach (int id in new[] {3, 4, 8})Themes._themes.Themes[id] = new UiTheme(id);
            Themes.Current = Themes._themes.GetById(3);
            Items[3].IsEquipped = true;
            CatCosmetics.Instance.EquipItem(Items[1], true, true, true);
            FakePrefs.Save("UI_THEME", "3");
            FakePrefs.Save("EQUIPPED_EMOTES_3", "");
            Unlock = new NativeUnlock(Assembly.GetExecutingAssembly(), Visuals, s => {
            });
            if (build)Build();
        }

        void Add(int id, string slot, int amount) {
            var item = new SteamItemRuntime(id, slot, amount);
            Items[id] = item;
            Rows[id] = Inventory.Add(item);
        }

        public void Build() {
            for (int guard = 0; Unlock.Loading && guard < 100; guard++)Unlock.Advance();
            Assert(!Unlock.Loading, "construction completes");
        }

        public void Click(int id) { Rows[id].Button.onClick.Invoke(); }

        public EmoteDonutEntry Temp(int id) {
            foreach (var child in Wheel.emoteEntryParent.Children) {
                foreach (var pair in Wheel.IDtoEmoteEntry)
                    if (System.Object.ReferenceEquals(pair.Value.transform, child) && pair.Key == id)
                        return pair.Value;
            }
            var field = typeof(NativeUnlock).GetField("emotes", BindingFlags.Instance | BindingFlags.NonPublic);
            object entry;
            return ((Dictionary<int, object>)field.GetValue(Unlock)).TryGetValue(id, out entry) ?
                (EmoteDonutEntry)entry : null;
        }

        public int TempCount {
            get {
                return ((Dictionary<int, object>)typeof(NativeUnlock).GetField("emotes",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Unlock)).Count;
            }
        }
    }

    static void Check(string name, Action test) {
        test();
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    static void Main() {
        Check("native rows adopted once, eligibility and filters preserved", () => {
            var f = new Fixture(true);
            Assert(f.Unlock.Added == 11 && f.Inventory._hatsCollection._collectionItems.Count == 2,
                "no duplicate rows");
            Assert(System.Object.ReferenceEquals(f.Rows[7].Button.onClick, f.Rows[7].Saved),
                "unsupported theme stays native");
            Assert(f.Rows[2].gameObject.activeSelf && !f.Rows[2]._preview.activeSelf &&
                !f.Rows[2]._unownedOverlay.enabled, "unowned presentation");
            f.Items[2].PassQuality = false;
            f.Items[2].OnItemUpdated();
            Assert(!f.Rows[2].gameObject.activeSelf, "quality gate");
            f.Items[2].PassQuality = true;
            f.Inventory._hatsCollection.PassSearch = false;
            f.Items[2].OnItemUpdated();
            Assert(!f.Rows[2].gameObject.activeSelf, "search gate");
            f.Inventory._hatsCollection.PassSearch = true;
            f.Items[2].PassEvent = false;
            f.Items[2].OnItemUpdated();
            Assert(!f.Rows[2].gameObject.activeSelf, "event gate");
            f.Items[2].PassEvent = true;
            f.Items[2].IsHiddenInInventory = true;
            f.Items[2].OnItemUpdated();
            Assert(!f.Rows[2].gameObject.activeSelf, "hidden gate");
            f.Unlock.Disable();
        });
        Check("temporary cosmetics avoid native events/prefs and owned reselection repairs", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            f.Click(2);
            Assert(f.Visuals.Chosen == 2 && f.Rows[2].NativeClicks == 0 && FakePrefs.Writes == writes,
                "virtual cosmetic boundary");
            Assert(f.Rows[2]._equippedIcon.enabled && !f.Rows[1]._equippedIcon.enabled, "exclusive badges");
            f.Click(1);
            Assert(f.Rows[1].NativeClicks == 1 && f.Items[1].IsEquipped && f.Visuals.Adopts == 1,
                "owned A temporary B owned A");
            f.Items[2].Quantity = 1;
            f.Click(2);
            Assert(f.Rows[2].NativeClicks == 1 && f.Visuals.Selects == 1, "ownership reevaluated on click");
            f.Unlock.Disable();
        });
        Check("owned search-excluded rows stay hidden across adoption/native refresh/off", () => {
            var f = new Fixture(false);
            f.Inventory._hatsCollection.PassSearch = false;
            f.Rows[1].gameObject.SetActive(false);
            f.Build();
            Assert(!f.Rows[1].gameObject.activeSelf, "owned filtered row adoption");
            f.Items[1].OnItemUpdated();
            Assert(!f.Rows[1].gameObject.activeSelf, "owned native refresh respects collection filter");
            f.Unlock.Disable();
            Assert(!f.Rows[1].gameObject.activeSelf &&
                !f.Inventory._hatsCollection._collectionRoot.gameObject.activeSelf,
                "owned filter and empty root restored on off");
        });
        Check("skin owned temporary clear and owned reselection keep exclusive native state", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            f.Click(12);
            Assert(f.Visuals.Chosen == 12 && f.Rows[12]._equippedIcon.enabled &&
                !f.Rows[11]._equippedIcon.enabled && FakePrefs.Writes == writes, "temporary skin preview");
            f.Click(12);
            Assert(f.Visuals.Clears == 1 && !f.Rows[12]._equippedIcon.enabled &&
                !f.Rows[11]._equippedIcon.enabled, "temporary skin clear");
            f.Click(11);
            Assert(f.Items[11].IsEquipped && f.Rows[11]._equippedIcon.enabled && f.Rows[11].NativeClicks == 1,
                "owned skin native repair after virtual clear");
            f.Click(12);
            f.Click(11);
            Assert(f.Items[11].IsEquipped && f.Visuals.Adopts == 2 && f.Rows[11]._equippedIcon.enabled &&
                !f.Rows[12]._equippedIcon.enabled, "owned skin A virtual B owned A");
            f.Unlock.Disable();
        });
        Check("exchange/trash reject virtual action and preserve owned native dispatch", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            ItemExchange.Instance.IsVisible = true;
            foreach (int id in new[] {2, 4, 5, 6})f.Click(id);
            Assert(f.Visuals.Selects == 0 && f.TempCount == 0 && f.Themes.Previewed == null &&
                FakePrefs.Writes == writes, "exchange blocks virtual");
            f.Click(1);
            Assert(f.Rows[1].Transactions == 1, "owned transaction event");
            ItemExchange.Instance.IsVisible = false;
            Steam.TrashCan.Instance.InTrashMode = true;
            f.Click(6);
            Assert(f.TempCount == 0, "trash blocks virtual");
            f.Click(1);
            Assert(f.Rows[1].Transactions == 2, "native trash dispatch");
            Steam.TrashCan.Instance.InTrashMode = false;
            f.Unlock.Disable();
        });
        Check("theme preview is reversible without prefs/quantity, tab clear repairs once", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            f.Click(4);
            Assert(f.Themes.Previewed.ItemId == 4 && f.Themes.Current.ItemId == 3 &&
                FakePrefs.Writes == writes && !f.Items[4].IsEquipped, "theme preview only");
            int calls = f.Themes.PreviewCalls;
            f.Unlock.MaintainTheme();
            Assert(f.Themes.PreviewCalls == calls, "preview not toggle-looped");
            f.Themes.RestoreEquipped(MenuTabs.MenuTab.Inventory);
            Assert(f.Themes.Previewed != null, "inventory tab enum not mistaken");
            f.Themes.RestoreEquipped(MenuTabs.MenuTab.Bongodecks);
            f.Unlock.MaintainTheme();
            Assert(f.Themes.Previewed.ItemId == 4 && f.Themes.PreviewCalls == calls + 1,
                "BongoDex tab restore reapply");
            f.Click(4);
            f.Unlock.MaintainTheme();
            Assert(f.Themes.Previewed == null && FakePrefs.Writes == writes, "explicit virtual deselect");
            f.Click(4);
            f.Click(3);
            Assert(f.Themes.Current.ItemId == 3 && f.Themes.Previewed == null && f.Themes.EquipCalls == 1,
                "owned current theme re-equip");
            f.Unlock.Disable();
        });
        Check("off preserves unrelated native preview and latest legitimate theme", () => {
            var f = new Fixture(true);
            f.Themes.Preview(f.Items[8]);
            f.Unlock.Disable();
            Assert(f.Themes.Previewed.ItemId == 8, "unrelated preview preserved without temporary theme");
            f = new Fixture(true);
            f.Click(4);
            f.Themes.Preview(f.Items[8]);
            f.Unlock.Disable();
            Assert(f.Themes.Previewed.ItemId == 8, "external preview replacement preserved");
            f = new Fixture(true);
            f.Click(4);
            f.Items[8].Quantity = 1;
            f.Themes.Equip(f.Items[8]);
            int writes = FakePrefs.Writes;
            f.Unlock.Disable();
            Assert(f.Themes.Current.ItemId == 8 && FakePrefs.Writes == writes,
                "latest legitimate Current survives off");
        });
        Check("delayed BongoDex native preview/deselect supersedes inventory temporary theme", () => {
            var f = new Fixture(true);
            f.Click(4);
            var collection = new ItemCollection();
            var dex = new InventoryItem(f.Items[8], collection);
            dex._isPartOfBongoDex = true;
            collection._collectionItems.Add(dex);
            f.DexView.BongoDexCollections.Add(new BongoCat.UI.BongoDexCollection(collection));
            f.Unlock.Tick();
            f.Build();
            int count = f.Unlock.Added;
            f.Unlock.Tick();
            f.Build();
            Assert(f.Unlock.Added == count, "exact identity adoption occurs once");
            dex.Button.onClick.Invoke();
            f.Unlock.MaintainTheme();
            Assert(f.Themes.Previewed.ItemId == 8 && dex.NativeClicks == 1,
                "explicit BongoDex preview takes over");
            dex.Button.onClick.Invoke();
            f.Unlock.MaintainTheme();
            Assert(f.Themes.Previewed == null, "BongoDex explicit deselection sticks");
            f.Click(4);
            f.Unlock.Disable();
            Assert(System.Object.ReferenceEquals(dex.Button.onClick, dex.Saved),
                "BongoDex exact event restored");
        });
        Check("isolated reusable wheel entries cannot contaminate native preference saves", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            f.Click(5);
            Assert(f.TempCount == 1 && f.Wheel.IDtoEmoteEntry.Count == 0 && FakePrefs.Writes == writes,
                "isolated temp dictionary");
            var temp = f.Temp(5);
            Assert(System.Object.ReferenceEquals(f.Unlock.TemporaryReusable(), temp), "Auto Emote fallback");
            Assert(Math.Abs(temp.transform.localPosition.x) < 0.01 && temp.transform.localPosition.y == 100,
                "single wheel geometry");
            f.Click(9);
            Assert(FakePrefs.Get("EQUIPPED_EMOTES_3") == "9" && f.TempCount == 1,
                "native equip saves native ID only");
            Assert(temp.transform.localPosition.x > 0, "merged wheel geometry");
            f.Click(9);
            Assert(FakePrefs.Get("EQUIPPED_EMOTES_3") == "" && !f.Wheel.IDtoEmoteEntry.ContainsKey(5),
                "native remove saves no temporary IDs");
            f.Unlock.Disable();
            Assert(f.Wheel.emoteEntryParent.Children.Count == 0, "temporary children removed");
        });
        Check("temporary consumable playback never consumes; acquisition retires callback", () => {
            var f = new Fixture(true);
            int writes = FakePrefs.Writes;
            f.Click(6);
            var temp = f.Temp(6);
            Assert(temp != null && f.Unlock.TemporaryReusable() == null && !temp.counter.activeSelf,
                "consumable excluded from automation");
            temp.Button.onClick.Invoke();
            Assert(f.Wheel.emoteSpawner.Spawns == 1 && BongoCat.Multiplayer.SteamMultiplayer.Sent == 1 &&
                f.Items[6].Quantity == 0 && f.Items[6].Consumed == 0 && FakePrefs.Writes == writes,
                "manual zero quantity safe playback");
            f.Items[6].Quantity = 1;
            temp.Button.onClick.Invoke();
            Assert(f.TempCount == 0 && f.Wheel.emoteSpawner.Spawns == 1 && f.Items[6].Consumed == 0,
                "acquired item virtual callback retired without free play");
            f.Click(6);
            Assert(f.Wheel.IDtoEmoteEntry.ContainsKey(6), "acquired native equip");
            f.Wheel.IDtoEmoteEntry[6].Button.onClick.Invoke();
            Assert(f.Items[6].Consumed == 1 && f.Items[6].Quantity == 0,
                "owned native consuming playback retained");
            f.Unlock.Disable();
        });
        Check("native entries win acquisition, duplicate and full-wheel handoffs", () => {
            var f = new Fixture(true);
            f.Click(5);
            f.Items[5].Quantity = 1;
            f.Click(5);
            Assert(f.TempCount == 0 && f.Wheel.IDtoEmoteEntry.ContainsKey(5), "real acquisition route");
            f.Items[5].Quantity = 0;
            f.Click(6);
            f.Wheel.maxEmoteCount = 1;
            f.Unlock.Tick();
            Assert(f.TempCount == 0 && f.Wheel.IDtoEmoteEntry.Count == 1, "native entries win cap shrink");
            f.Click(6);
            Assert(f.TempCount == 0 && f.Unlock.Error.Contains("full"), "combined cap rejects add");
            f.Unlock.Disable();
        });
        Check("native capacity trim restores geometry and destroyed virtual entries repair safely", () => {
            var f = new Fixture(true);
            f.Click(5);
            f.Click(9);
            Assert(Math.Abs(f.Wheel.IDtoEmoteEntry[9].transform.localPosition.x) > 1,
                "merged native layout differs");
            f.Wheel.maxEmoteCount = 1;
            f.Unlock.Tick();
            Assert(f.TempCount == 0 && Math.Abs(f.Wheel.IDtoEmoteEntry[9].transform.localPosition.x) < 0.01,
                "last virtual cap trim restores native layout");
            f = new Fixture(true);
            f.Click(5);
            var prior = f.Temp(5);
            prior.Destroyed = true;
            prior.gameObject.Destroyed = true;
            prior.transform.SetParent(null, false);
            int writes = FakePrefs.Writes;
            f.Unlock.Tick();
            Assert(f.TempCount == 1 && !System.Object.ReferenceEquals(prior, f.Temp(5)) &&
                f.Wheel.IDtoEmoteEntry.Count == 0 && FakePrefs.Writes == writes,
                "destroyed temporary child repaired without native keys");
            f.Unlock.Disable();
        });
        Check("failed wheel initialization rolls back and native layout survives", () => {
            var f = new Fixture(true);
            f.Wheel.FailSetItem = true;
            f.Click(5);
            Assert(f.TempCount == 0 && f.Wheel.IDtoEmoteEntry.Count == 0 &&
                f.Wheel.emoteEntryParent.Children.Count == 0, "failed insertion rollback");
            f.Wheel.FailSetItem = false;
            f.Click(9);
            f.Click(6);
            f.Unlock.Disable();
            Assert(f.Wheel.IDtoEmoteEntry.Count == 1 && f.Wheel.emoteEntryParent.Children.Count == 1 &&
                Math.Abs(f.Wheel.IDtoEmoteEntry[9].transform.localPosition.x) < 0.01,
                "native wheel/layout restored");
        });
        Check("off restores exact event objects, callbacks, row presentation and is idempotent", () => {
            var f = new Fixture(true);
            f.Click(2);
            f.Click(4);
            f.Click(5);
            f.Click(6);
            int writes = FakePrefs.Writes;
            f.Unlock.Disable();
            f.Unlock.Disable();
            Assert(f.Visuals.Disables == 1 && f.Themes.Previewed == null && f.TempCount == 0 &&
                FakePrefs.Writes == writes, "independent/idempotent cleanup");
            foreach (var pair in f.Rows) {
                Assert(System.Object.ReferenceEquals(pair.Value.Button.onClick, pair.Value.Saved),
                    "exact event restoration " + pair.Key);
                foreach (var callback in pair.Value.SteamItem.OnItemUpdated.GetInvocationList())
                    Assert(callback.Target is InventoryItem, "only native row callbacks remain");
                if (!pair.Value.SteamItem.IsOwned)
                    Assert(!pair.Value.gameObject.activeSelf && pair.Value._preview.activeSelf,
                        "native unowned visuals restored");
            }
            Assert(Heathen.SteamworksIntegration.LobbyData.Current.MyHat == 1, "owned lobby baseline restored");
        });
        Check("partial construction cancellation restores adopted rows without deleting any", () => {
            var f = new Fixture(false);
            f.Unlock.Advance();
            Assert(f.Unlock.Added > 0 && f.Unlock.Loading, "partial construction fixture");
            f.Unlock.Disable();
            Assert(!f.Unlock.Loading && f.Inventory._hatsCollection._collectionItems.Count == 2,
                "cancel retains native rows");
            foreach (var row in f.Rows.Values)
                Assert(System.Object.ReferenceEquals(row.Button.onClick, row.Saved),
                    "partial original events restored");
        });
        Check("cleanup failure is isolated from events, wheel, themes and native roots", () => {
            var f = new Fixture(true);
            f.Click(2);
            f.Click(4);
            f.Click(5);
            f.Items[9].Quantity = 0;
            f.Visuals.FailDisable = true;
            bool threw = false;
            try {
                f.Unlock.Disable();
            } catch {
                threw = true;
            }
            Assert(threw && f.Themes.Previewed == null && f.TempCount == 0 &&
                !f.Inventory._emoteCollection._collectionRoot.gameObject.activeSelf,
                "independent cleanup executes and empty native root hides");
            foreach (var row in f.Rows.Values)
                Assert(System.Object.ReferenceEquals(row.Button.onClick, row.Saved),
                    "events restored despite renderer failure");
            Assert(!f.Rows[2].gameObject.activeSelf && f.Inventory.Refreshes >= 2,
                "native rows and sections restored despite failure");
            f.Unlock.Disable();
        });
        Check("off skips rows the game already destroyed and restores the rest", () => {
            var f = new Fixture(true);
            f.Click(2);
            f.Rows[12].Destroyed = true;
            f.Rows[12].gameObject.Destroyed = true;
            f.Unlock.Tick();
            f.Unlock.Disable();
            Assert(f.Visuals.Disables == 1 && !f.Rows[2].gameObject.activeSelf &&
                System.Object.ReferenceEquals(f.Rows[2].Button.onClick, f.Rows[2].Saved),
                "live rows restored beside a destroyed row");
            foreach (var callback in f.Items[12].OnItemUpdated.GetInvocationList())
                Assert(callback.Target is InventoryItem, "destroyed row callback removed");
        });
        Check("construction failure restores every recorded event and callback", () => {
            var f = new Fixture(false);
            f.Rows[2].FailInitialization = true;
            bool threw = false;
            try {
                f.Unlock.Advance();
            } catch {
                threw = true;
            }
            Assert(threw && !f.Unlock.Loading, "build failure stopped");
            Assert(System.Object.ReferenceEquals(f.Rows[1].Button.onClick, f.Rows[1].Saved) &&
                System.Object.ReferenceEquals(f.Rows[2].Button.onClick, f.Rows[2].Saved),
                "failure restores events");
        });
        Console.WriteLine("PASS: " + passed +
            " actual NativeUnlock control-flow fixtures (no live game transactions)");
    }
}
