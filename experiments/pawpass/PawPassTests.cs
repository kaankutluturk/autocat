using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using AutocatBridge;
using BongoCat;
using BongoCat.SteamJsonParser;
using PawPassFixture;
using UnityEngine;
#if !NO_PAWPASS
using BongoCat.PawPass;
#endif

// Unlock All is not part of these tests; the game component only needs the types to exist.
namespace AutocatBridge {
public sealed class CosmeticPreview {
    public CosmeticPreview(Assembly assembly, Func<string> saved, Action<string> logger,
        Action<string, int> broadcaster) { }
    public bool Active { get { return false; } }
    public string Catalog { get { return "CATALOG|"; } }
    public void Select(int id) { }
    public void Disable() { }
    public void Maintain() { }
}

public sealed class NativeUnlock {
    public string Error = "";
    public NativeUnlock(Assembly assembly, CosmeticPreview renderer, Action<string> logger) { }
    public void Tick() { }
    public void Advance() { }
    public void MaintainTheme() { }
    public void Disable() { }
    public void BroadcastSlot(string slot, int id) { }
    public object TemporaryReusable() { return null; }
}
}

// Drives the real game component (Bridge.cs, PawPassClaim.cs) against the game model, one poll at a time.
sealed class Rig : IDisposable {
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    public Worker Worker;
    public Shop Normal, Emote;
    public ItemExchange Exchange;
#if !NO_PAWPASS
    public PawPassManager Manager;
    public PawPassEntity Pass;
#endif

    public Rig(int milestones = 4, int taps = 0) {
        Calls.Reset();
        FakeSteam.Reset();
        Resources.Objects.Clear();
        Time.realtimeSinceStartup = 100;
        Pets.Instance = new Pets();
        Shop.NormalShop = Normal = new Shop();
        Shop.EmoteShop = Emote = new Shop();
        ItemExchange.Instance = Exchange = new ItemExchange();
        CatInventory.Instance = new CatInventory();
        Steam.ChestExchanger.TestSetExchanging(false);
#if !NO_PAWPASS
        PawPassManager.TestTapsWithoutJoin = false;
        Pass = NewPass("S1", milestones, taps);
        PawPassManager.Instance = Manager = new PawPassManager();
        Manager.PlayablePasses.Add(Pass);
        Manager.IsInitialized = true;
        Manager.SelectPass(Pass);
#endif
        Calls.Reset();
        Worker = new GameObject("rig").AddComponent<Worker>();
        Invoke("Awake");
        Last = this;
    }

#if !NO_PAWPASS
    // A joined free pass: the free key is owned, the premium key is not, every claim token is still held.
    public static PawPassEntity NewPass(string season, int milestones, int taps) {
        var pass = new PawPassEntity {
            SeasonCode = season,
            RequiredTapsPerMilestone = 100,
            FreeKey = Item(1),
            PremiumKey = Item(0),
            PassToken = Item(0)
        };
        for (int i = 0; i < milestones * 2; i++) {
            var milestone = new PawPassMilestone {
                ClaimToken = Item(1),
                ClaimExchange = new SteamExchange {Id = FakeSteam.NewId()}
            };
            milestone.ClaimExchange.Input.Add(milestone.ClaimToken);
            milestone.ClaimExchange.Output.Add(Item(0));
            (i % 2 == 0 ? pass.FreePassMilestones : pass.PremiumPassMilestones).Add(milestone);
        }
        FakeSteam.Stats["PP_PR_" + season] = taps;
        return pass;
    }

    public static SteamItemBasic Item(long quantity) {
        var item = new SteamItemBasic {Id = FakeSteam.NewId()};
        FakeSteam.Items[item.Id] = quantity;
        return item;
    }

    public static void Hold(SteamItemBase item, long quantity) { FakeSteam.Items[item.Id] = quantity; }
    public PawPassMilestone Free(int index) { return Pass.FreePassMilestones[index]; }
    public PawPassMilestone Premium(int index) { return Pass.PremiumPassMilestones[index]; }
    public PawPassItem Row(bool premium, int index) { return Manager.TestRows(premium)[index]; }
    public void Taps(int value) { FakeSteam.Stats["PP_PR_" + Pass.SeasonCode] = value; }
#endif

    object Invoke(string name) {
        return typeof(Worker).GetMethod(name, Any).Invoke(Worker, new object[0]);
    }

    public T Get<T>(string name) { return (T)typeof(Worker).GetField(name, Any).GetValue(Worker); }
    public void Set(string name, object value) { typeof(Worker).GetField(name, Any).SetValue(Worker, value); }

    public void On() {
        Worker.running = true;
        Worker.pawPass = true;
    }

    // One state poll with the menu in contact.
    public void Poll(float seconds = 0.11f) {
        Time.realtimeSinceStartup += seconds;
        Set("lastContact", DateTime.UtcNow.Ticks);
        Invoke("Update");
    }

    public void Silent(float seconds = 0.11f) {
        Time.realtimeSinceStartup += seconds;
        Invoke("Update");
    }

    // Long enough for a claimable milestone to have stayed so for the required time.
    public void Settle() { Run(7); }

    public void Run(float seconds) {
        for (float t = 0; t < seconds; t += 0.5f)Poll(0.5f);
    }

    public string[] State() { return Get<string>("state").Split('|'); }

    public string Status() {
        var state = State();
        if (state[0] != "OK" || state.Length != 21)throw new Exception("state line: " + String.Join("|", state));
        return Encoding.UTF8.GetString(Convert.FromBase64String(state[20]));
    }

    public List<Call> PawPassCalls() {
        return Calls.Log.FindAll(c => c.Direct && c.Name.StartsWith("PawPass"));
    }

    public int Claims() { return Calls.Direct("PawPassItem.Claim()"); }

    public void LogTo(string folder, string session) {
        typeof(Worker).GetMethod("ConfigureDiagnostics", Any).Invoke(Worker, new object[] {
            session, (int)AutoCat.Diagnostics.LogLevel.Info, Convert.ToBase64String(Encoding.UTF8.GetBytes(folder))
        });
    }

    NamedPipeClientStream menu;
    StreamReader menuReader;
    StreamWriter menuWriter;

    // A command from the menu over the real control pipe, as the desktop sends it.
    public void Send(string line) {
        if (menu == null) {
            menu = new NamedPipeClientStream(".",
                "autocat-control-" + System.Diagnostics.Process.GetCurrentProcess().Id, PipeDirection.InOut);
            menu.Connect(5000);
            menuReader = new StreamReader(menu, Encoding.UTF8, false, 1024, true);
            menuWriter = new StreamWriter(menu, new UTF8Encoding(false), 1024, true) {AutoFlush = true};
        }
        menuWriter.WriteLine(line);
        menuReader.ReadLine();
    }

    // The menu goes away without a word; the component clears its flags itself.
    public void Drop() {
        if (menu == null)return;
        menu.Dispose();
        menu = null;
        for (int i = 0; i < 100 && Worker.running; i++)Thread.Sleep(50);
        Thread.Sleep(100);
    }

    // A new component in the same game, as after a reinjection.
    public void Reinject() {
        Drop();
        Dispose();
        disposed = false;
        Worker = new GameObject("rig").AddComponent<Worker>();
        Invoke("Awake");
    }

    // The component of the test that ran last, so a failed test cannot leave its control pipe open.
    public static Rig Last;
    bool disposed;

    public void Dispose() {
        if (disposed)return;
        disposed = true;
        if (menu != null)menu.Dispose();
        menu = null;
        Invoke("OnDestroy");
        var thread = Get<Thread>("thread");
        if (thread != null && !thread.Join(5000))throw new Exception("control server did not stop");
    }
}

static class PawPassTests {
    static int passed, failed;
    static string sourceRoot, logFolder;
    static void Assert(bool value, string name) { if (!value)throw new Exception(name); }

    static readonly string[] Reads = {
        "PawPassManager.TapsFor", "PawPassEntity.get_IsJoined", "PawPassEntity.get_HasFreePass",
        "PawPassEntity.get_HasPremiumPass", "PawPassMilestone.get_IsUnclaimed"
    };

    static readonly string[] Forbidden = {
        "UnlockPremiumPass", "RequestPremiumUnlock", "ConfirmRedeem", "RedeemPassToken", "AddTaps", "PP_PR_",
        "ClaimAll", "SetStat", "StoreStats", "SelectPass", "SetActivePass", "Has_PPP", "PP_BL_"
    };

    // Holds for every test: the only game members the component enters are the listed reads and the
    // parameterless row claim, and nothing that grants, buys, redeems or writes is ever touched.
    static void Invariants() {
        foreach (var call in Calls.Log) {
            Assert(!call.Name.StartsWith("FORBIDDEN"), "forbidden member invoked: " + call.Name);
            if (!call.Direct)continue;
            if (call.Name.StartsWith("PawPass"))
                Assert(Array.IndexOf(Reads, call.Name) >= 0 || call.Name == "PawPassItem.Claim()",
                    "unexpected PawPass member invoked: " + call.Name);
            Assert(call.Name != "SteamExchange.Exchange", "exchange invoked outside the game's own row");
        }
    }

    static void Check(string name, Action body) {
        try {
            body();
            Invariants();
        } catch (Exception e) {
            var real = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            Console.WriteLine("FAIL: " + name + ": " + real.Message);
            failed++;
            try {
                if (Rig.Last != null)Rig.Last.Dispose();
            } catch (Exception) {
            }
            return;
        }
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    static string[] LogLines(Rig rig, string session) {
        rig.Dispose();
        if (!AutoCat.Diagnostics.Diag.Current.WaitForDrain(5000))throw new Exception("log did not drain");
        var lines = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(logFolder, "diagnostic-" + session + ".log")))
            if (line.Contains("[PawPass]"))lines.Add(line);
        return lines.ToArray();
    }

    static int Main(string[] args) {
        sourceRoot = Path.GetFullPath(args[0]);
        logFolder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(logFolder);
#if VARIANT
        Check("a missing or changed game member disables only this feature, with one status and one log line",
            Unavailable);
#else
        Standard();
        Check("the forbidden member names do not occur in the bridge, desktop or shared sources", SourceScan);
#endif
        if (failed > 0) {
            Console.WriteLine("FAILED: " + failed + " of " + (passed + failed) + " PawPass claim fixtures");
            return 1;
        }
        Console.WriteLine("PASS: " + passed + " PawPass claim fixtures (no live game transactions)");
        return 0;
    }

    // Everything is in place for a claim, so only the changed member can be what stops it.
    static void Unavailable() {
        string session = Guid.NewGuid().ToString("N");
        var rig = new Rig(4, 250);
        rig.LogTo(logFolder, session);
        rig.On();
        rig.Worker.gifts = true;
        rig.Normal.Ready = true;
        FakeSteam.Items[1] = 1;
        rig.Run(10);
        Assert(rig.Status() == "pawpass unavailable", "status: " + rig.Status());
        Assert(rig.PawPassCalls().Count == 0 && FakeSteam.Submitted.Count == 0, "no PawPass member is entered");
        Assert(Calls.Count("PawPassItem.Claim(Action)") == 0, "the other claim overload is never a fallback");
        Assert(Calls.Direct("ShopItem.Buy") == 1 && rig.Get<string>("problem") == "",
            "gift collection is unaffected");
        var lines = LogLines(rig, session);
        Assert(lines.Length == 1 && lines[0].Contains("unavailable"), "one log line: " + lines.Length);
    }

    static void SourceScan() {
        int files = 0;
        foreach (string folder in new[] {"bridge", "src", "shared"})
            foreach (string file in Directory.GetFiles(Path.Combine(sourceRoot, folder), "*.cs")) {
                files++;
                string text = File.ReadAllText(file);
                foreach (string name in Forbidden)
                    Assert(text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0,
                        Path.GetFileName(file) + " contains " + name);
            }
        Assert(files > 11, "sources found");
        string claim = File.ReadAllText(Path.Combine(sourceRoot, "bridge", "PawPassClaim.cs"));
        Assert(claim.IndexOf("SetValue", StringComparison.Ordinal) < 0, "the feature writes no game field");
        Assert(claim.Split(new[] {".Invoke(row"}, StringSplitOptions.None).Length == 2 &&
            claim.Split(new[] {"claim.Invoke("}, StringSplitOptions.None).Length == 2,
            "the row claim is invoked in exactly one place");
    }

#if !VARIANT
    // The game's own rule for one reward row, written out from PawPassManager.Refresh and ApplyState and
    // PawPassItem.Claim, independently of the code under test. Null means the game would offer this row's
    // claim button and the click would claim the milestone the rule was evaluated for.
    static class GameRule {
        public static string Refuses(PawPassItem row) {
            var manager = PawPassManager.Instance;
            var selected = manager.SelectedPass;
            if (!(bool)selected)return "no selected pass";
            if (!manager.IsInitialized)return "manager not initialised";
            int index = -1;
            bool premium = false;
            for (int track = 0; track < 2; track++) {
                var rows = manager.TestRows(track == 1);
                for (int i = 0; i < rows.Count; i++)
                    if (System.Object.ReferenceEquals(rows[i], row)) {
                        index = i;
                        premium = track == 1;
                    }
            }
            if (index < 0)return "row is not in the manager's lists";
            if (index >= selected.TotalMilestones)return "row beyond the selected pass";
            if (row.Destroyed)return "row destroyed";
            if (row.IsRevealing)return "row revealing";
            int taps = PawPassManager.TapsFor(selected), required = selected.RequiredTapsPerMilestone;
            bool reached = !(taps < unchecked((index + 1) * required));
            if (!(premium ? selected.HasPremiumPass : selected.HasFreePass))return "track not owned";
            var list = selected.Milestones(premium);
            if (index >= list.Count)return "no milestone at the index";
            if (!list[index].IsUnclaimed)return "already claimed";
            if (!reached)return "not reached";
            if (!System.Object.ReferenceEquals(row.Milestone, list[index]))return "row holds another milestone";
            return null;
        }
    }

    // Random game states, including damaged ones. calls counts the row claims made; the return value counts
    // those the game's rule would not have offered.
    static int RandomStates(int trials, int seed, out int calls) {
        var random = new System.Random(seed);
        int claims = 0, violations = 0;
        for (int trial = 0; trial < trials; trial++)
            using (var rig = new Rig(Math.Max(1, random.Next(0, 7)), 0)) {
                var passes = new List<PawPassEntity> {rig.Pass};
                for (int extra = random.Next(0, 3); extra > 0; extra--) {
                    var other = Rig.NewPass("X" + trial + "_" + extra, random.Next(1, 8), 0);
                    rig.Manager.PlayablePasses.Add(other);
                    passes.Add(other);
                }
                Calls.Run("player", null, () => {
                    for (int s = random.Next(0, 4); s > 0; s--)
                        rig.Manager.SelectPass(passes[random.Next(passes.Count)]);
                });
                if (random.Next(8) == 0)rig.Manager.SelectedPass = passes[random.Next(passes.Count)];
                foreach (var pass in passes) {
                    int[] requireds = {100, 100, 100, 100, 1, 0, -100, int.MaxValue, 1 << 30, 7};
                    pass.RequiredTapsPerMilestone = requireds[random.Next(requireds.Length)];
                    long taps = (long)pass.RequiredTapsPerMilestone *
                        random.Next(-1, pass.FreePassMilestones.Count + 3) + random.Next(-1, 2);
                    if (random.Next(10) == 0)taps = random.Next(int.MinValue, int.MaxValue);
                    FakeSteam.Stats["PP_PR_" + pass.SeasonCode] =
                        (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, taps));
                    Rig.Hold(pass.FreeKey, random.Next(4) == 0 ? 0 : 1);
                    Rig.Hold(pass.PremiumKey, random.Next(2));
                    foreach (var m in pass.FreePassMilestones)
                        Rig.Hold(m.ClaimToken, random.Next(3) == 0 ? 0 : random.Next(1, 3));
                    foreach (var m in pass.PremiumPassMilestones)Rig.Hold(m.ClaimToken, random.Next(3) == 0 ? 0 : 1);
                    if (random.Next(12) == 0 && pass.PremiumPassMilestones.Count > 0)
                        pass.PremiumPassMilestones.RemoveAt(pass.PremiumPassMilestones.Count - 1);
                    if (random.Next(12) == 0)
                        pass.PremiumPassMilestones.Add(random.Next(2) == 0 ? null :
                            passes[random.Next(passes.Count)].FreePassMilestones[0]);
                    if (random.Next(15) == 0 && pass.FreePassMilestones.Count > 1) {
                        var first = pass.FreePassMilestones[0];
                        pass.FreePassMilestones[0] = pass.FreePassMilestones[1];
                        pass.FreePassMilestones[1] = first;
                    }
                    if (random.Next(20) == 0 && pass.FreePassMilestones.Count > 0)pass.FreePassMilestones[0] = null;
                }
                for (int track = 0; track < 2; track++) {
                    var rows = rig.Manager.TestRows(track == 1);
                    if (rows.Count > 1 && random.Next(10) == 0) {
                        var first = rows[0].Milestone;
                        rows[0].Milestone = rows[1].Milestone;
                        rows[1].Milestone = first;
                    }
                    if (rows.Count > 1 && random.Next(15) == 0) {
                        var first = rows[0];
                        rows[0] = rows[1];
                        rows[1] = first;
                    }
                    if (rows.Count > 0 && random.Next(15) == 0)rows[random.Next(rows.Count)].Destroyed = true;
                    if (rows.Count > 0 && random.Next(15) == 0)rows[random.Next(rows.Count)].IsRevealing = true;
                    if (rows.Count > 0 && random.Next(15) == 0)rows[random.Next(rows.Count)].Milestone = null;
                    if (rows.Count > 0 && random.Next(15) == 0)rows.RemoveAt(rows.Count - 1);
                    if (rows.Count > 0 && random.Next(15) == 0)
                        rows[random.Next(rows.Count)].Milestone =
                            passes[random.Next(passes.Count)].FreePassMilestones[0];
                }
                if (random.Next(12) == 0)FakeSteam.StatsReady = false;
                if (random.Next(20) == 0)rig.Manager.IsInitialized = false;
                if (random.Next(25) == 0)CatInventory.Instance.FetchingItems = true;
                Calls.Hook = call => {
                    if (call.Name != "PawPassItem.Claim()")return;
                    claims++;
                    string reason = GameRule.Refuses((PawPassItem)call.Target);
                    if (reason == null)return;
                    violations++;
                    Console.WriteLine("claim the game would not offer, trial " + trial + ": " + reason);
                };
                rig.On();
                for (int step = 0; step < 14; step++) {
                    rig.Run(2.5f);
                    if (FakeSteam.InFlight.Count > 0 && random.Next(3) != 0)FakeSteam.Complete(random.Next(5) != 0);
                    if (step == 6 && random.Next(4) == 0)
                        Calls.Run("player", null, () => {
                            try {
                                rig.Manager.SelectPass(passes[random.Next(passes.Count)]);
                            } catch (Exception) {
                            }
                        });
                }
                Calls.Hook = null;
                Invariants();
            }
        calls = claims;
        return violations;
    }
#endif

#if DRYRUN
    static void Standard() {
        Check("dry-run build: the claim it would make is logged once per activation and nothing is started", () => {
            string session = Guid.NewGuid().ToString("N");
            var rig = new Rig(4, 200);
            rig.LogTo(logFolder, session);
            rig.On();
            rig.Worker.gifts = true;
            rig.Poll();
            Assert(rig.Status() == "pawpass dry run", "names itself with nothing to report: " + rig.Status());
            rig.Run(60);
            Assert(rig.Status() == "pawpass dry run: would claim", "status: " + rig.Status());
            Assert(!rig.Get<PawPassClaim>("pawPassClaim").Pending, "nothing outstanding");
            rig.Worker.running = rig.Worker.pawPass = false;
            rig.Run(3);
            Assert(rig.Status() == "", "no status while paused");
            rig.On();
            rig.Run(20);
            Rig.Hold(rig.Free(0).ClaimToken, 0);
            rig.Run(20);
            rig.Normal.Ready = true;
            FakeSteam.Items[1] = 1;
            rig.Run(5);
            Assert(Calls.Direct("ShopItem.Buy") == 1, "gift collection is not held back");
            Assert(Calls.Count("PawPassItem.Claim()") == 0 && Calls.Count("PawPassItem.Claim(Action)") == 0 &&
                Calls.Count("PawPassMilestone.Claim") == 0 && FakeSteam.Submitted.Count == 0, "nothing claimed");
            Invariants();
            var lines = LogLines(rig, session);
            string[] expected = {"resolved", "claim.would_start", "claim.would_start", "claim.would_start"};
            Assert(lines.Length == expected.Length, "lines: " + lines.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert(lines[i].Contains("] " + expected[i] + " "), "line " + i + ": " + lines[i]);
            Assert(lines[1].Contains("premium=0 index=0") && lines[2].Contains("premium=0 index=0") &&
                lines[3].Contains("premium=0 index=1"), "track and index");
        });
        Check("dry-run build: no claim member is entered in any of 400 random game states", () => {
            int calls, violations = RandomStates(400, 20261007, out calls);
            Assert(calls == 0 && violations == 0 && FakeSteam.Submitted.Count == 0, "claims: " + calls);
        });
    }
#elif !VARIANT
    static void Standard() {
        Check("toggle off: no PawPass member is entered and nothing is claimed", () => {
            using (var rig = new Rig(4, 400)) {
                rig.Worker.running = true;
                rig.Run(20);
                Assert(rig.PawPassCalls().Count == 0 && FakeSteam.Submitted.Count == 0, "nothing entered");
                Assert(rig.Status() == "", "no status");
            }
        });
        Check("paused: the flag alone, without running, claims nothing", () => {
            using (var rig = new Rig(4, 400)) {
                rig.Worker.pawPass = true;
                rig.Run(20);
                Assert(rig.PawPassCalls().Count == 0 && FakeSteam.Submitted.Count == 0, "nothing entered");
            }
        });
        Check("on with nothing earned: reads only, no claim, no status", () => {
            using (var rig = new Rig(4, 99)) {
                rig.On();
                rig.Run(20);
                Assert(rig.PawPassCalls().Count > 0 && rig.Claims() == 0 && FakeSteam.Submitted.Count == 0,
                    "reads without a claim");
                Assert(rig.Status() == "", "status: " + rig.Status());
            }
        });
        Check("pass not joined: no claim with taps and tokens present, even if the game still counted taps", () => {
            for (int mode = 0; mode < 2; mode++)
                using (var rig = new Rig(4, 400)) {
                    Rig.Hold(rig.Pass.FreeKey, 0);
                    PawPassManager.TestTapsWithoutJoin = mode == 1;
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0 && rig.Status() == "",
                        "no claim, mode " + mode);
                }
        });
        Check("required taps per milestone zero or negative: no claim", () => {
            foreach (int required in new[] {0, -100})
                using (var rig = new Rig(4, 400)) {
                    rig.Pass.RequiredTapsPerMilestone = required;
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim, required " + required);
                }
        });
        Check("milestone already claimed: no claim", () => {
            using (var rig = new Rig(4, 100)) {
                Rig.Hold(rig.Free(0).ClaimToken, 0);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim");
            }
        });
        Check("free pass missing: a reached, unclaimed free milestone is not claimed", () => {
            using (var rig = new Rig(4, 400)) {
                Rig.Hold(rig.Pass.FreeKey, 0);
                Rig.Hold(rig.Pass.PremiumKey, 1);
                for (int i = 0; i < 4; i++)Rig.Hold(rig.Premium(i).ClaimToken, 0);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim");
            }
        });
        Check("premium milestone is never claimed without the premium key, token present and index reached",
            () => {
                using (var rig = new Rig(4, 400)) {
                    for (int i = 0; i < 4; i++)Rig.Hold(rig.Free(i).ClaimToken, 0);
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim");
                    Assert(Calls.Log.FindAll(c => c.Target is PawPassMilestone &&
                        rig.Pass.PremiumPassMilestones.Contains((PawPassMilestone)c.Target)).Count == 0,
                        "premium milestones are not even read without the key");
                }
            });
        Check("exactly the lowest eligible milestone is claimed, once, through its own row", () => {
            using (var rig = new Rig(4, 350)) {
                Rig.Hold(rig.Free(0).ClaimToken, 0);
                rig.On();
                rig.Settle();
                var claims = Calls.Log.FindAll(c => c.Name == "PawPassItem.Claim()");
                Assert(claims.Count == 1 && claims[0].Direct &&
                    System.Object.ReferenceEquals(claims[0].Target, rig.Row(false, 1)), "row of free milestone 1");
                var inner = Calls.Log.FindAll(c => c.Name == "PawPassMilestone.Claim");
                Assert(inner.Count == 1 && !inner[0].Direct &&
                    System.Object.ReferenceEquals(inner[0].Target, rig.Free(1)), "claimed by the row itself");
                Assert(FakeSteam.Submitted.Count == 1 &&
                    System.Object.ReferenceEquals(FakeSteam.Submitted[0], rig.Free(1).ClaimExchange),
                    "one exchange, for that milestone");
                Assert(rig.Status() == "claiming pawpass", "status: " + rig.Status());
            }
        });
        Check("order: lower index first, free before premium at the same index, never beyond the reached tier",
            () => {
                using (var rig = new Rig(4, 250)) {
                    Rig.Hold(rig.Pass.PremiumKey, 1);
                    Rig.Hold(rig.Free(0).ClaimToken, 0);
                    rig.On();
                    var expected = new[] {rig.Premium(0), rig.Free(1), rig.Premium(1)};
                    foreach (var milestone in expected) {
                        rig.Run(9);
                        Assert(FakeSteam.InFlight.Count == 1 && System.Object.ReferenceEquals(
                            FakeSteam.InFlight[0].Exchange, milestone.ClaimExchange), "next in order");
                        FakeSteam.Complete(true);
                    }
                    rig.Run(20);
                    Assert(rig.Claims() == 3 && FakeSteam.Submitted.Count == 3, "tier 3 and 4 are not reached");
                }
            });
        Check("only the selected pass: another playable pass with earned rewards is never read or claimed", () => {
            using (var rig = new Rig(4, 0)) {
                var other = Rig.NewPass("S0", 4, 400);
                rig.Manager.PlayablePasses.Add(other);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim");
                Assert(Calls.Log.FindAll(c => System.Object.ReferenceEquals(c.Target, other)).Count == 0,
                    "other pass untouched");
            }
        });
        Check("row holds another milestone: no claim, status asks to open the PawPass", () => {
            using (var rig = new Rig(4, 100)) {
                rig.Row(false, 0).Milestone = rig.Free(1);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "open pawpass once", "status: " + rig.Status());
            }
        });
        Check("selected pass changed without its rows: no claim", () => {
            using (var rig = new Rig(4, 0)) {
                rig.Manager.SelectedPass = Rig.NewPass("S2", 4, 400);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "open pawpass once", "status: " + rig.Status());
            }
        });
        Check("row missing, row lists empty, row destroyed: no claim, same status", () => {
            using (var rig = new Rig(4, 200)) {
                Rig.Hold(rig.Free(0).ClaimToken, 0);
                rig.Manager.TestRows(false).RemoveRange(1, 3);
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "open pawpass once", "missing: " + rig.Status());
            }
            using (var rig = new Rig(4, 200)) {
                rig.Manager.TestRows(false).Clear();
                rig.Manager.TestRows(true).Clear();
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "open pawpass once", "empty: " + rig.Status());
            }
            using (var rig = new Rig(4, 200)) {
                rig.Row(false, 0).Destroyed = true;
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "open pawpass once", "destroyed: " + rig.Status());
                Assert(Calls.Count("PawPassItem.Claim(Action)") == 0, "a destroyed row is never entered");
            }
        });
        Check("manager absent, destroyed or not initialised, or selected pass absent or destroyed: no claim", () => {
            for (int mode = 0; mode < 5; mode++)
                using (var rig = new Rig(4, 200)) {
                    if (mode == 0)PawPassManager.Instance = null;
                    if (mode == 1)rig.Manager.Destroyed = true;
                    if (mode == 2)rig.Manager.IsInitialized = false;
                    if (mode == 3)rig.Manager.SelectedPass = null;
                    if (mode == 4)rig.Pass.Destroyed = true;
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && rig.PawPassCalls().Count == 0, "nothing entered, mode " + mode);
                    Assert(rig.Status() == "pawpass not ready", "mode " + mode + ": " + rig.Status());
                }
        });
        Check("inventory fetch, native claim-all or a revealing row: no claim, waiting", () => {
            for (int mode = 0; mode < 3; mode++)
                using (var rig = new Rig(4, 200)) {
                    if (mode == 0)CatInventory.Instance.FetchingItems = true;
                    if (mode == 1)rig.Manager.TestSetClaimingAll(true);
                    if (mode == 2)rig.Row(false, 0).IsRevealing = true;
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim, mode " + mode);
                    Assert(rig.Status() == "pawpass waiting", "mode " + mode + ": " + rig.Status());
                    CatInventory.Instance.FetchingItems = false;
                    rig.Manager.TestSetClaimingAll(false);
                    rig.Row(false, 0).IsRevealing = false;
                    rig.Run(9);
                    Assert(rig.Claims() == 1, "claims once the game is idle, mode " + mode);
                }
        });
        Check("a gift request or exchange in flight, the game's or AutoCat's own: no claim until it ends", () => {
            for (int mode = 0; mode < 6; mode++)
                using (var rig = new Rig(4, 200)) {
                    if (mode == 0)Steam.ChestExchanger.TestSetExchanging(true);
                    if (mode == 1)rig.Normal._openingChest = true;
                    if (mode == 2)rig.Emote._shopItem._waitingForServer = true;
                    if (mode == 3)rig.Exchange._isExchanging = true;
                    if (mode == 4)rig.Set("pendingGift", rig.Normal);
                    if (mode == 5) {
                        var type = typeof(Worker).GetNestedType("ExchangeObservation", BindingFlags.NonPublic);
                        object running = Activator.CreateInstance(type);
                        type.GetField("Deadline").SetValue(running, Single.MaxValue);
                        ((System.Collections.IList)rig.Get<object>("exchangeObservations")).Add(running);
                    }
                    rig.Set("giftSubmitted", Time.realtimeSinceStartup + 1000);
                    rig.On();
                    rig.Run(20);
                    Assert(rig.Claims() == 0 && rig.PawPassCalls().Count == 0, "no claim, mode " + mode);
                    Steam.ChestExchanger.TestSetExchanging(false);
                    rig.Normal._openingChest = rig.Emote._shopItem._waitingForServer = false;
                    rig.Exchange._isExchanging = false;
                    rig.Set("pendingGift", null);
                    ((System.Collections.IList)rig.Get<object>("exchangeObservations")).Clear();
                    rig.Run(9);
                    Assert(rig.Claims() == 1, "claims afterwards, mode " + mode);
                }
        });
        Check("gift and PawPass both ready: the gift goes first and the PawPass claim waits for it", () => {
            using (var rig = new Rig(4, 200)) {
                rig.On();
                rig.Worker.gifts = true;
                rig.Normal.Ready = true;
                FakeSteam.Items[1] = 1;
                rig.Poll();
                Assert(Calls.Direct("ShopItem.Buy") == 1 && rig.Claims() == 0, "gift first");
                rig.Normal._shopItem._waitingForServer = false;
                rig.Run(20);
                Assert(rig.Claims() == 0, "waits for the gift request AutoCat started");
                rig.Normal.Ready = false;
                FakeSteam.Items[1] = 0;
                rig.Run(5);
                Assert(rig.Claims() == 0, "and for the settling time after it");
                rig.Run(5);
                Assert(rig.Claims() == 1, "claims after the gift was confirmed");
            }
        });
        Check("a PawPass claim in flight holds back gift collection and exchanges until it ends", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Settle();
                Assert(rig.Claims() == 1, "claim started");
                rig.Worker.gifts = rig.Worker.exchange = true;
                rig.Normal.Ready = true;
                FakeSteam.Items[1] = 1;
                rig.Run(12);
                Assert(Calls.Direct("ShopItem.Buy") == 0, "no gift request while the claim is outstanding");
                Assert(Calls.Count("ItemExchange.CanFillExchangeWithDuplicates") == 0, "no exchange attempt either");
                FakeSteam.Complete(true);
                rig.Run(6);
                Assert(Calls.Direct("ShopItem.Buy") == 1, "gift collected afterwards");
                Assert(Calls.Count("ItemExchange.CanFillExchangeWithDuplicates") > 0, "exchange checks resume");
            }
        });
        Check("a failing read of the gift and exchange state: nothing new starts, a claim in flight still ends", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Settle();
                Assert(rig.Claims() == 1, "claim started");
                rig.Normal._shopItem = null;
                rig.Worker.exchange = true;
                rig.Run(40);
                Assert(!rig.Get<PawPassClaim>("pawPassClaim").Pending, "timed out although the read fails");
                Assert(Calls.Count("ItemExchange.CanFillExchangeWithDuplicates") > 0, "exchange is released");
                rig.Run(120);
                Assert(rig.Claims() == 1 && rig.Status() == "pawpass not ready", "no new claim: " + rig.Status());
                Assert(rig.State()[0] == "OK" && rig.Get<string>("problem") == "", "other state unaffected");
            }
        });
        Check("a milestone is claimed only after staying claimable on the same row for six seconds", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Run(6);
                Assert(rig.Claims() == 0, "not before");
                rig.Run(1);
                Assert(rig.Claims() == 1, "then once");
            }
            // Each of these, seen for a moment, starts the wait again.
            for (int mode = 0; mode < 6; mode++)
                using (var rig = new Rig(4, 100)) {
                    rig.On();
                    rig.Run(4);
                    if (mode == 0)Rig.Hold(rig.Free(0).ClaimToken, 0);
                    if (mode == 1)rig.Manager.TestRows(false)[0] = new PawPassItem {Milestone = rig.Free(0)};
                    if (mode == 2)CatInventory.Instance.FetchingItems = true;
                    if (mode == 3)rig.Manager.TestSetClaimingAll(true);
                    if (mode == 4)rig.Exchange._isExchanging = true;
                    if (mode == 5)rig.Worker.running = rig.Worker.pawPass = false;
                    rig.Poll(mode < 2 ? 0.5f : 0.2f);
                    Rig.Hold(rig.Free(0).ClaimToken, 1);
                    CatInventory.Instance.FetchingItems = false;
                    rig.Manager.TestSetClaimingAll(false);
                    rig.Exchange._isExchanging = false;
                    rig.On();
                    rig.Run(5.5f);
                    Assert(rig.Claims() == 0, "the earlier seconds do not count, mode " + mode);
                    rig.Run(5);
                    Assert(rig.Claims() == 1, "claimed after a full wait, mode " + mode);
                }
        });
        Check("a claim the player has just started on the row is not submitted a second time", () => {
            for (int mode = 0; mode < 2; mode++)
                using (var rig = new Rig(4, 100)) {
                    if (mode == 0)rig.On();
                    if (mode == 0)rig.Run(3);
                    Calls.Run("player click", null, () => rig.Row(false, 0).Claim(null));
                    rig.On();
                    rig.Run(3);
                    FakeSteam.Complete(true);
                    rig.Run(30);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 1, "one exchange only, mode " + mode);
                }
        });
        Check("eligibility lost between choosing the milestone and the call: no claim", () => {
            for (int mode = 0; mode < 5; mode++)
                using (var rig = new Rig(4, 200)) {
                    Rig.Hold(rig.Free(0).ClaimToken, 0);
                    var target = rig.Free(1);
                    var other = Rig.NewPass("S3", 4, 400);
                    int reads = 0;
                    // The fourth evaluation is the one that would call; each evaluation reads twice.
                    Calls.Hook = call => {
                        if (!call.Direct)return;
                        if (mode == 0 && call.Name == "PawPassMilestone.get_IsUnclaimed" &&
                                System.Object.ReferenceEquals(call.Target, target) && ++reads == 8)
                            Rig.Hold(target.ClaimToken, 0);
                        if (mode == 1 && call.Name == "PawPassManager.TapsFor" && ++reads == 8)rig.Taps(199);
                        if (mode == 2 && call.Name == "PawPassEntity.get_HasFreePass" && ++reads == 8) {
                            Rig.Hold(rig.Pass.FreeKey, 0);
                            Rig.Hold(rig.Pass.PremiumKey, 1);
                        }
                        if (mode == 3 && call.Name == "PawPassMilestone.get_IsUnclaimed" &&
                                System.Object.ReferenceEquals(call.Target, target) && ++reads == 7)
                            rig.Manager.SelectedPass = other;
                        if (mode == 4 && call.Name == "PawPassManager.TapsFor" && ++reads == 8)
                            rig.Pass.FreePassMilestones[1] = other.FreePassMilestones[1];
                    };
                    rig.On();
                    rig.Settle();
                    Assert(reads >= (mode == 3 ? 7 : 8), "the state changed mid-evaluation, mode " + mode);
                    Assert(rig.Claims() == 0 && FakeSteam.Submitted.Count == 0, "no claim, mode " + mode);
                }
        });
        Check("one claim outstanding at a time; the token disappearing confirms it; then the next one", () => {
            using (var rig = new Rig(4, 300)) {
                rig.On();
                rig.Settle();
                rig.Run(25);
                Assert(rig.Claims() == 1 && FakeSteam.Submitted.Count == 1, "one outstanding for 25 seconds");
                Assert(rig.Status() == "claiming pawpass", "status: " + rig.Status());
                FakeSteam.Complete(true);
                rig.Poll();
                Assert(rig.Status() == "" && rig.Claims() == 1, "confirmed, nothing new in the same poll");
                rig.Run(9);
                Assert(rig.Claims() == 2 && System.Object.ReferenceEquals(FakeSteam.InFlight[0].Exchange,
                    rig.Free(1).ClaimExchange), "next milestone");
            }
        });
        Check("no confirmation: times out after 30 seconds, waits 60 more, then tries once more", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Settle();
                rig.Run(22);
                Assert(rig.Claims() == 1 && rig.Status() == "claiming pawpass", "still outstanding");
                rig.Run(9);
                Assert(rig.Status() == "pawpass claim not confirmed; retry delayed", "status: " + rig.Status());
                rig.Run(55);
                Assert(rig.Claims() == 1, "no attempt during the delay");
                rig.Run(12);
                Assert(rig.Claims() == 2, "one new attempt after the delay");
            }
        });
        Check("two unconfirmed requests in a row stop the feature; only a pause or switch-off from the menu " +
            "lifts the stop, not the menu going silent or the pipe dropping", () => {
                const string on = "SET|1|0|0|0|10|0|0|1", halted = "pawpass claim failed; toggle to retry";
                string session = Guid.NewGuid().ToString("N");
                var rig = new Rig(4, 100);
                rig.LogTo(logFolder, session);
                rig.Send(on);
                for (float t = 0; t < 3600; t += 0.5f) {
                    rig.Poll(0.5f);
                    if (FakeSteam.InFlight.Count > 0)FakeSteam.Complete(false);
                }
                Assert(rig.Claims() == 2 && FakeSteam.Submitted.Count == 2, "two attempts in an hour");
                Assert(rig.Status() == halted, "status: " + rig.Status());
                Assert(!rig.Get<PawPassClaim>("pawPassClaim").Pending, "gifts and exchanges are not held back");
                // The watchdog clears the flags; the menu comes back with the same settings.
                rig.Set("lastContact", DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(5).Ticks);
                rig.Silent(0.5f);
                Assert(!rig.Worker.pawPass && rig.Status() == "", "inactive after the watchdog");
                rig.Send(on);
                rig.Run(300);
                Assert(rig.Claims() == 2 && rig.Status() == halted, "still stopped after a watchdog pause");
                // The pipe drops and the menu reconnects with the same settings.
                rig.Drop();
                rig.Poll();
                Assert(!rig.Worker.pawPass, "inactive after the drop");
                rig.Send(on);
                rig.Run(300);
                Assert(rig.Claims() == 2 && rig.Status() == halted, "still stopped after a dropped pipe");
                rig.Send("SET|0|0|0|0|10|0|0|1");
                rig.Poll();
                rig.Send(on);
                rig.Run(300);
                Assert(rig.Claims() == 4 && rig.Status() == halted, "pause and resume: one more round of two");
                rig.Send("SET|1|0|0|0|10|0|0|0");
                rig.Poll();
                rig.Send(on);
                rig.Run(300);
                Assert(rig.Claims() == 6 && rig.Status() == halted, "toggle off and on: one more round of two");
                var lines = new List<string>(LogLines(rig, session));
                Assert(lines.FindAll(l => l.Contains("] claim.halted ")).Count == 3 &&
                    lines.FindAll(l => l.Contains("] claim.halt.cleared ")).Count == 2,
                    "three stops, two of them lifted, each logged once");
            });
        Check("a late answer is looked for only while active, for five minutes, while the milestone is in place, " +
            "and not after a pause from the menu; a later answer cannot lead to a second claim", () => {
                for (int mode = 0; mode < 4; mode++)
                    using (var rig = new Rig(4, 100)) {
                        var target = rig.Free(0);
                        int reads = 0;
                        Calls.Hook = call => {
                            if (call.Direct && call.Name == "PawPassMilestone.get_IsUnclaimed" &&
                                    System.Object.ReferenceEquals(call.Target, target))reads++;
                        };
                        rig.Send("SET|1|0|0|0|10|0|0|1");
                        rig.Run(40);
                        Assert(rig.Claims() == 1 && rig.Status() == "pawpass claim not confirmed; retry delayed",
                            "one lapsed request");
                        // Nothing new can start, so every further read of the milestone is the late read.
                        rig.Exchange._isExchanging = true;
                        reads = 0;
                        rig.Run(20);
                        Assert(reads >= 8, "read while active: " + reads);
                        if (mode == 0)rig.Run(290);
                        if (mode == 1)rig.Manager.SelectedPass = Rig.NewPass("S4", 4, 0);
                        if (mode == 2)rig.Send("SET|0|0|0|0|10|0|0|0");
                        if (mode == 3) {
                            rig.Set("lastContact", DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(5).Ticks);
                            rig.Silent(0.5f);
                        }
                        rig.Poll(2.5f);
                        if (mode == 2)rig.Send("SET|1|0|0|0|10|0|0|1");
                        reads = 0;
                        if (mode == 3)for (int i = 0; i < 200; i++)rig.Silent(0.5f);
                        else rig.Run(100);
                        Assert(reads == 0, "no late read, mode " + mode + ": " + reads);
                        // Steam answers now; the reward is claimed, and it must not be requested again.
                        FakeSteam.Complete(true);
                        rig.Manager.SelectedPass = rig.Pass;
                        rig.Exchange._isExchanging = false;
                        rig.Send("SET|1|0|0|0|10|0|0|1");
                        rig.Run(200);
                        Assert(rig.Claims() == 1 && FakeSteam.Submitted.Count == 1, "no second claim, mode " + mode);
                    }
            });
        Check("a new component after a reinjection waits the full six seconds again", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Run(5);
                rig.Reinject();
                rig.On();
                rig.Run(6);
                Assert(rig.Claims() == 0, "the first component's seconds do not count");
                rig.Run(1.5f);
                Assert(rig.Claims() == 1, "then once");
            }
        });
        Check("a confirmed claim clears the count of unconfirmed ones", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Run(40);
                Assert(rig.Claims() == 1 && rig.Status() == "pawpass claim not confirmed; retry delayed", "one lapsed");
                FakeSteam.InFlight.Clear();
                rig.Run(70);
                Assert(rig.Claims() == 2 && FakeSteam.InFlight.Count == 1, "second attempt");
                FakeSteam.Complete(true);
                rig.Taps(200);
                rig.Run(300);
                Assert(rig.Claims() == 4 && rig.Status() == "pawpass claim failed; toggle to retry",
                    "two more attempts for the next milestone: " + rig.Claims());
            }
        });
        Check("a confirmation arriving after the timeout is recognized and does not count as a failure", () => {
            using (var rig = new Rig(4, 200)) {
                rig.On();
                rig.Settle();
                rig.Run(33);
                Assert(rig.Status() == "pawpass claim not confirmed; retry delayed", "timed out: " + rig.Status());
                FakeSteam.Complete(true);
                rig.Run(3);
                Assert(rig.Status() == "" && rig.Claims() == 1, "recognized: " + rig.Status());
                rig.Run(600);
                Assert(rig.Claims() == 3 && FakeSteam.Submitted.Count == 3 &&
                    System.Object.ReferenceEquals(FakeSteam.Submitted[1], rig.Free(1).ClaimExchange) &&
                    System.Object.ReferenceEquals(FakeSteam.Submitted[2], rig.Free(1).ClaimExchange),
                    "the next milestone gets its own two attempts");
                Assert(rig.Status() == "pawpass claim failed; toggle to retry", "status: " + rig.Status());
            }
        });
        Check("a failed exchange reported by the game is not retried before the timeout and delay", () => {
            using (var rig = new Rig(4, 100)) {
                rig.On();
                rig.Settle();
                FakeSteam.Complete(false);
                rig.Run(85);
                Assert(rig.Claims() == 1, "no early retry");
            }
        });
        Check("the row claim throwing counts as an outstanding request, not as a reason to call again", () => {
            using (var rig = new Rig(4, 100)) {
                rig.Row(false, 0).TestClaimThrows = true;
                rig.On();
                rig.Settle();
                rig.Run(20);
                Assert(rig.Claims() == 1 && rig.State()[0] == "OK", "one call, state still reported");
                Assert(rig.Status() == "claiming pawpass", "status: " + rig.Status());
            }
        });
        Check("a game read failing: no claim, not ready, other state unaffected", () => {
            using (var rig = new Rig(4, 100)) {
                rig.Pass.FreeKey = null;
                rig.On();
                rig.Run(20);
                Assert(rig.Claims() == 0 && rig.Status() == "pawpass not ready", "status: " + rig.Status());
                Assert(rig.Get<string>("problem") == "", "no other condition raised");
            }
        });
        Check("pausing stops new claims and leaves the one in flight to be confirmed or time out", () => {
            using (var rig = new Rig(4, 300)) {
                rig.On();
                rig.Settle();
                rig.Worker.running = rig.Worker.pawPass = false;
                rig.Run(5);
                FakeSteam.Complete(true);
                rig.Run(20);
                Assert(rig.Claims() == 1 && !rig.Get<PawPassClaim>("pawPassClaim").Pending,
                    "confirmed while paused, nothing new");
                rig.On();
                rig.Settle();
                rig.Worker.running = rig.Worker.pawPass = false;
                rig.Run(40);
                Assert(rig.Claims() == 2 && !rig.Get<PawPassClaim>("pawPassClaim").Pending,
                    "timed out while paused");
                rig.Run(80);
                Assert(rig.Claims() == 2 && rig.Status() == "", "nothing more while paused");
            }
        });
        Check("watchdog: four seconds without the menu clear the flag and nothing is claimed", () => {
            using (var rig = new Rig(4, 300)) {
                rig.On();
                rig.Set("lastContact", DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(5).Ticks);
                for (int i = 0; i < 40; i++)rig.Silent(0.5f);
                Assert(!rig.Worker.pawPass && !rig.Worker.running, "flag cleared");
                Assert(rig.Claims() == 0 && rig.PawPassCalls().Count == 0, "nothing entered");
            }
        });
        Check("control pipe: the ninth SET field switches the feature on and off, the old eight-field shape " +
            "leaves it off, and a dropped connection clears it", () => {
                using (var rig = new Rig(4, 0)) {
                    string name = "autocat-control-" + System.Diagnostics.Process.GetCurrentProcess().Id;
                    using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                        pipe.Connect(5000);
                        var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) {AutoFlush = true};
                        Action<string> send = line => {
                            writer.WriteLine(line);
                            reader.ReadLine();
                        };
                        send("SET|1|0|0|0|10|0|0|1");
                        Assert(rig.Worker.running && rig.Worker.pawPass, "ninth field 1: on");
                        send("SET|1|0|0|0|10|0|0|0");
                        Assert(rig.Worker.running && !rig.Worker.pawPass, "ninth field 0: off");
                        send("SET|1|0|0|0|10|0|0|1");
                        send("SET|1|0|0|0|10|0|0");
                        Assert(rig.Worker.running && !rig.Worker.pawPass, "eight fields: off, rest applied");
                        send("SET|0|0|0|0|10|0|0|1|1");
                        Assert(rig.Worker.running && !rig.Worker.pawPass, "other shapes are ignored");
                        send("SET|1|0|0|0|10|0|0|1");
                        rig.Poll();
                        Assert(rig.Worker.pawPass && rig.Status() == "", "on again; state line carries the status");
                    }
                    for (int i = 0; i < 100 && rig.Worker.running; i++)Thread.Sleep(50);
                    Thread.Sleep(100);
                    Assert(!rig.Worker.running && !rig.Worker.pawPass, "cleared when the menu went away");
                }
            });
        Check("log volume: one line when resolved, three per confirmed claim, none per poll", () => {
            string session = Guid.NewGuid().ToString("N");
            var rig = new Rig(4, 0);
            rig.LogTo(logFolder, session);
            rig.On();
            rig.Run(60);
            rig.Taps(200);
            rig.Run(9);
            FakeSteam.Complete(true);
            rig.Run(10);
            rig.Run(40);
            rig.Run(30);
            var lines = LogLines(rig, session);
            string[] expected = {
                "resolved", "claim.started", "claim.submitted", "claim.confirmed", "claim.started",
                "claim.submitted", "claim.unconfirmed"
            };
            Assert(lines.Length == expected.Length, "lines: " + lines.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert(lines[i].Contains("] " + expected[i] + " "), "line " + i + ": " + lines[i]);
            foreach (string line in lines)
                Assert(!line.Contains("itemId") && !line.Contains("7656119"), "no item or Steam IDs");
            Assert(lines[1].Contains("premium=0 index=0") && lines[4].Contains("premium=0 index=1"),
                "track and index are logged");
        });
        Check("3000 random game states: every row claim is one the game's own rule would offer", () => {
            int calls, violations = RandomStates(3000, 20261007, out calls);
            Assert(violations == 0, violations + " claims the game would not offer");
            Assert(calls > 300, "the run made claims: " + calls);
        });
    }
#else
    static void Standard() { }
#endif
}
