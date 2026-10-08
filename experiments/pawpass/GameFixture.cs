using System;
using System.Collections;
using System.Collections.Generic;
using Heathen.SteamworksIntegration;
using PawPassFixture;
using UnityEngine;

// Offline model of the game types the PawPass claim feature reads, with the names, member kinds, accessibility
// and behavior recorded from the game's metadata, plus the few other types the game component polls. Every
// method and property getter records its invocation; fields cannot, as in the real game. The build symbols
// NO_PAWPASS, NO_ROW_CLAIM, ROWS_RENAMED and UNCLAIMED_CHANGED produce a game whose members have changed.
namespace PawPassFixture {
public sealed class Call {
    public string Name;
    public object Target;
    public bool Direct;
}

// Direct is true when the member was entered from outside the game model, that is by the code under test.
public static class Calls {
    public static readonly List<Call> Log = new List<Call>();
    public static Action<Call> Hook;
    static int depth;

    public static T Get<T>(string name, object target, Func<T> body) {
        var call = new Call {Name = name, Target = target, Direct = depth == 0};
        Log.Add(call);
        depth++;
        try {
            if (Hook != null)Hook(call);
            return body();
        } finally {
            depth--;
        }
    }

    public static void Run(string name, object target, Action body) {
        Get(name, target, () => {
            body();
            return 0;
        });
    }

    public static void Forbidden(string name, object target) {
        Run("FORBIDDEN " + name, target, () => {
        });
    }

    public static int Count(string name) { return Log.FindAll(c => c.Name == name).Count; }
    public static int Direct(string name) { return Log.FindAll(c => c.Name == name && c.Direct).Count; }

    public static void Reset() {
        Log.Clear();
        Hook = null;
        depth = 0;
    }
}

// The Steam side: cached inventory quantities, cached stats, and exchanges waiting for a result.
public static class FakeSteam {
    public sealed class Request {
        public BongoCat.SteamJsonParser.SteamExchange Exchange;
        public Action<bool, ItemDetail[]> Done;
    }

    public static readonly Dictionary<int, long> Items = new Dictionary<int, long>();
    public static readonly Dictionary<string, int> Stats = new Dictionary<string, int>();
    public static readonly List<Request> InFlight = new List<Request>();
    public static readonly List<BongoCat.SteamJsonParser.SteamExchange> Submitted =
        new List<BongoCat.SteamJsonParser.SteamExchange>();
    public static bool StatsReady = true;
    static int nextId = 1000;
    public static int NewId() { return nextId++; }

    public static long Quantity(int id) {
        long value;
        return Items.TryGetValue(id, out value) ? value : 0;
    }

    public static void Complete(bool success) {
        var request = InFlight[0];
        InFlight.RemoveAt(0);
        var granted = new List<ItemDetail>();
        if (success) {
            foreach (var input in request.Exchange.Input)Items[input.Id] = Quantity(input.Id) - 1;
            foreach (var output in request.Exchange.Output) {
                Items[output.Id] = Quantity(output.Id) + 1;
                granted.Add(new ItemDetail {Id = output.Id});
            }
        }
        Calls.Run("Steam result", request.Exchange, () => request.Done(success, success ? granted.ToArray() : null));
    }

    public static void Reset() {
        Items.Clear();
        Stats.Clear();
        InFlight.Clear();
        Submitted.Clear();
        StatsReady = true;
    }
}
}

namespace Heathen.SteamworksIntegration {
public struct ItemDetail {
    public int Id;
}

public struct ItemData {
    public int id;
    public long GetTotalQuantity() { return FakeSteam.Quantity(id); }
    public static implicit operator ItemData(int value) { return new ItemData {id = value}; }
}

public struct StatData {
    string id;
    public int IntValue() {
        string name = id;
        return Calls.Get("StatData.IntValue", name, () => {
            int value;
            return FakeSteam.Stats.TryGetValue(name, out value) ? value : 0;
        });
    }
    public void Set(int value) { Calls.Forbidden("StatData.Set", id); }
    public void Store() { Calls.Forbidden("StatData.Store", id); }
    public static implicit operator StatData(string name) { return new StatData {id = name}; }
}
}

namespace Steam {
public class StatsInitializer : MonoBehaviour {
    public static bool Initialized { get { return FakeSteam.StatsReady; } }
}

public class ChestExchanger : MonoBehaviour {
    private static bool IsExchanging;
    public static void TestSetExchanging(bool value) { IsExchanging = value; }
}
}

namespace SteamTools {
public class Game {
    public class Stats {
        public static StatData EmotesUsed = "EmotesUsed";
    }

    public class Inventory {
        public static ItemData Chest_Token = 1, Emote_Chest_Token = 2;
    }
}
}

public class EmoteDonut : MonoBehaviour {
    public static EmoteDonut Instance;
}

namespace BongoCat.SteamJsonParser {
public abstract class SteamItemBase : ScriptableObject {
    public int Id;
    public string Name;
    public ItemData ItemData { get { return Id; } }
    public bool HasItem { get { return Calls.Get("SteamItemBase.get_HasItem", this, () => ItemData.GetTotalQuantity() > 0); } }
}

public class SteamItemBasic : SteamItemBase {
}

public class SteamExchange : SteamItemBase {
    public List<SteamItemBasic> Input = new List<SteamItemBasic>();
    public List<SteamItemBase> Output = new List<SteamItemBase>();

    // The game's version reports a ValueTuple; the pair is passed as two arguments here.
    public void Exchange(Action<bool, ItemDetail[]> callback) {
        Calls.Run("SteamExchange.Exchange", this, () => {
            foreach (var input in Input)
                if (FakeSteam.Quantity(input.Id) <= 0) {
                    if (callback != null)callback(false, null);
                    return;
                }
            FakeSteam.Submitted.Add(this);
            FakeSteam.InFlight.Add(new FakeSteam.Request {Exchange = this, Done = callback});
        });
    }
}
}

namespace BongoCat {
public class Pets : MonoBehaviour {
    public static Pets Instance;
    public bool _init = true;
    public int _currentGained, _currentAchievement, _totalSpent;
}

public class ShopItem : MonoBehaviour {
    public bool _waitingForServer;
    public void Buy() { Calls.Run("ShopItem.Buy", this, () => _waitingForServer = true); }
}

public class Shop : MonoBehaviour {
    public static Shop NormalShop, EmoteShop;
    public ShopItem _shopItem = new ShopItem();
    public bool _openingChest, Ready;
    public int StockRefreshTimeLeft;
    public bool CanGetChest { get { return Ready; } }
}

public class ItemExchange : MonoBehaviour {
    public enum Group { Cosmetic, Emote }
    public static ItemExchange Instance;
    public bool _isExchanging;
    public int _possibleExchanges;
    public List<object> _exchangeSlots = new List<object>();
    public bool HasItemsSlotted { get { return false; } }

    public bool CanFillExchangeWithDuplicates(Group group, object unused) {
        return Calls.Get("ItemExchange.CanFillExchangeWithDuplicates", this, () => false);
    }
}

public class CatInventory : MonoBehaviour {
    public static CatInventory Instance;
    public List<object> Items = new List<object>();
    public bool FetchingItems;
}
}

#if !NO_PAWPASS
namespace BongoCat.PawPass {
using BongoCat.SteamJsonParser;

public enum PawPassItemState { Locked = 0, Claimable = 1, Claimed = 2, PayToUnlock = 3 }

public class PawPassMilestoneItem {
    public SteamItemBase RewardItem;
}

[Serializable]
public class PawPassMilestone {
    public List<PawPassMilestoneItem> Items = new List<PawPassMilestoneItem>();
    public bool IsFullCosmetic;
    public SteamItemBasic ClaimToken;
    public SteamExchange ClaimExchange;
    internal bool Unclaimed { get { return Calls.Get("PawPassMilestone.get_IsUnclaimed", this, () => ClaimToken.HasItem); } }
#if UNCLAIMED_CHANGED
    public int IsUnclaimed { get { return Unclaimed ? 1 : 0; } }
#else
    public bool IsUnclaimed { get { return Unclaimed; } }
#endif

    public void Claim(Action<bool, ItemDetail[]> onCompleted) {
        Calls.Run("PawPassMilestone.Claim", this, () => ClaimExchange.Exchange(onCompleted));
    }
}

public class PawPassEntity : ScriptableObject {
    public string SeasonCode;
    public int RequiredTapsPerMilestone = 10000;
    public SteamItemBasic PassToken, FreeKey, PremiumKey;
    public SteamExchange PremiumUnlockExchange;
    public List<PawPassMilestone> FreePassMilestones = new List<PawPassMilestone>();
    public List<PawPassMilestone> PremiumPassMilestones = new List<PawPassMilestone>();
    public int TotalMilestones { get { return FreePassMilestones.Count; } }
    public int TotalTaps { get { return TotalMilestones * RequiredTapsPerMilestone; } }
    public StatData ProgressStat { get { return "PP_PR_" + SeasonCode; } }
    public bool HasFreePass { get { return Calls.Get("PawPassEntity.get_HasFreePass", this, () => FreeKey.HasItem); } }

    public bool HasPremiumPass {
        get { return Calls.Get("PawPassEntity.get_HasPremiumPass", this, () => PremiumKey.HasItem); }
    }

    public bool IsJoined {
        get { return Calls.Get("PawPassEntity.get_IsJoined", this, () => HasFreePass || HasPremiumPass); }
    }

    public bool HasUnclaimedRewards {
        get {
            return Calls.Get("PawPassEntity.get_HasUnclaimedRewards", this,
                () => HasFreePass && FreePassMilestones.Exists(m => m.Unclaimed) ||
                    HasPremiumPass && PremiumPassMilestones.Exists(m => m.Unclaimed));
        }
    }

    public List<PawPassMilestone> Milestones(bool premium) {
        return premium ? PremiumPassMilestones : FreePassMilestones;
    }
}

public class PawPassItem : MonoBehaviour {
    public PawPassMilestone Milestone;
    public PawPassItemState ItemState;
    public bool IsRevealing;
    public bool TestClaimThrows;
    public void SetMilestone(PawPassMilestone milestone) { Milestone = milestone; }
#if !NO_ROW_CLAIM
    public void Claim() { Calls.Run("PawPassItem.Claim()", this, () => Claim(null)); }
#endif

    public void Claim(Action onCompleted) {
        Calls.Run("PawPassItem.Claim(Action)", this, () => {
            if (TestClaimThrows)throw new InvalidOperationException("row claim failed");
            var milestone = Milestone;
            milestone.Claim((success, granted) => {
                if (success && milestone == Milestone)ItemState = PawPassItemState.Claimed;
                PawPassManager.Instance.Refresh();
                if (onCompleted != null)onCompleted();
            });
        });
    }

    public void UnlockPremiumPass() { Calls.Forbidden("PawPassItem.UnlockPremiumPass", this); }
}

public class PawPassWindow : MonoBehaviour {
    public void ConfirmRedeem() { Calls.Forbidden("PawPassWindow.ConfirmRedeem", this); }
    public void RequestPremiumUnlock() { Calls.Forbidden("PawPassWindow.RequestPremiumUnlock", this); }
    public void ClaimAll() { Calls.Forbidden("PawPassWindow.ClaimAll", this); }
}

public class PawPassManager : MonoBehaviour {
    private PawPassWindow _pawPassWindow = new PawPassWindow();
    private List<PawPassEntity> _passes = new List<PawPassEntity>();
#if ROWS_RENAMED
    private List<PawPassItem> _freeRows = new List<PawPassItem>();
    private List<PawPassItem> _freeItems { get { return _freeRows; } }
#else
    private List<PawPassItem> _freeItems = new List<PawPassItem>();
#endif
    private List<PawPassItem> _premiumItems = new List<PawPassItem>();
    public PawPassEntity SelectedPass, ActivePass;
    private bool _isClaimingAll;
    public bool IsInitialized;
    public static PawPassManager Instance;
    // Not the game's behavior: lets a test show the join check does not lean on the tap count.
    public static bool TestTapsWithoutJoin;
    public List<PawPassEntity> PlayablePasses { get { return _passes; } }
    public List<PawPassItem> TestRows(bool premium) { return premium ? _premiumItems : _freeItems; }
    public void TestSetClaimingAll(bool value) { _isClaimingAll = value; }

    public void SelectPass(PawPassEntity pass) {
        Calls.Run("PawPassManager.SelectPass", this, () => {
            SelectedPass = pass;
            CreateMilestones();
            Refresh();
        });
    }

    private void CreateMilestones() {
        GrowTo(_freeItems);
        GrowTo(_premiumItems);
        for (int i = 0; i < SelectedPass.TotalMilestones; i++) {
            _freeItems[i].SetMilestone(SelectedPass.FreePassMilestones[i]);
            _premiumItems[i].SetMilestone(SelectedPass.PremiumPassMilestones[i]);
        }
    }

    private void GrowTo(List<PawPassItem> rows) {
        while (rows.Count < SelectedPass.TotalMilestones)rows.Add(new PawPassItem());
        for (int i = 0; i < rows.Count; i++)rows[i].gameObject.SetActive(i < SelectedPass.TotalMilestones);
    }

    public void Refresh() { Calls.Run("PawPassManager.Refresh", this, () => { }); }

    public static int TapsFor(PawPassEntity pass) {
        return Calls.Get("PawPassManager.TapsFor", pass, () => {
            if (TestTapsWithoutJoin)return pass.ProgressStat.IntValue();
            if (!Steam.StatsInitializer.Initialized || !pass.IsJoined)return 0;
            return Mathf.Clamp(pass.ProgressStat.IntValue(), 0, pass.TotalTaps);
        });
    }

    public void AddTaps(int amount) { Calls.Forbidden("PawPassManager.AddTaps", this); }
    public void UnlockPremiumPass() { Calls.Forbidden("PawPassManager.UnlockPremiumPass", this); }
    public void UnlockPremiumPass(PawPassEntity pass) { Calls.Forbidden("PawPassManager.UnlockPremiumPass", this); }
    public void RedeemPassToken() { Calls.Forbidden("PawPassManager.RedeemPassToken", this); }
    public void ClaimAll() { Calls.Forbidden("PawPassManager.ClaimAll", this); }
}
}
#endif
