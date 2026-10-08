using System;
using System.Collections;
using System.Reflection;
using AutoCat.Diagnostics;

namespace AutocatBridge {
// Claims PawPass rewards the player has already earned, one at a time, through the game's own reward row.
// Eligibility (pass joined, milestone reached, track owned, claim token held) is evaluated here, from the game's
// own data, immediately before every claim.
sealed class PawPassClaim {
    const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly object[] NoArgs = new object[0];
    readonly Assembly game;
    int resolved;
    FieldInfo instance, initialized, claimingAll, selectedPass, freeRows, premiumRows, requiredTaps,
        freeMilestones, premiumMilestones, rowMilestone, revealing, inventory, fetching;
    MethodInfo tapsFor, joined, hasFree, hasPremium, unclaimed;
    object pending, pendingPass, lapsed, lapsedPass, candidate, candidateRow;
    bool pendingPremium, lapsedPremium;
    int pendingIndex, lapsedIndex, requests, unconfirmed;
    float submitted, next, candidateSince, lapsedRead, lapsedUntil;
#if AUTOCAT_PAWPASS_DRYRUN
    readonly System.Collections.Generic.List<object> reported = new System.Collections.Generic.List<object>();
#else
    MethodInfo claim;
#endif
    // A milestone has to stay claimable, on the same row, for this long before it is claimed, and the wait
    // starts over after any inventory request seen in the game. A claim the player started just before, whose
    // token is still held until Steam answers, is then normally over; the evaluation runs every two seconds.
    const float Settle = 6;
    const int Attempts = 2;
    // How long a late answer to a timed-out request is still looked for: ten times the timeout.
    const float LateWindow = 300;

    public string Status = "";
    public bool Pending { get { return pending != null; } }
    public PawPassClaim(Assembly assembly) { game = assembly; }

    sealed class Progress {
        public bool Joined, HasFree, HasPremium;
        public int Reached;
        public IList Free, Premium;
    }

    // busy: a gift claim or duplicate exchange is in flight. A claim already started is followed to its end
    // even when the feature is no longer active.
    public void Tick(bool active, bool busy, float now) {
        if (pending != null) {
            bool left = true;
            try {
                left = (bool)unclaimed.Invoke(pending, NoArgs);
            } catch (Exception e) {
                Diag.CurrentLimited("PawPass", "confirmation.read.failed", (e.InnerException ?? e).Message);
            }
            if (!left) {
                Diag.Values(LogLevel.Info, "PawPass", "claim.confirmed",
                    "request={0} premium={1} index={2}; claim token no longer held; source=Steam-inventory-cache",
                    requests, pendingPremium ? 1 : 0, pendingIndex);
                pending = null;
                unconfirmed = 0;
                next = now + 2;
                Status = "";
            } else if (now - submitted > 30) {
                Diag.Values(LogLevel.Warn, "PawPass", "claim.unconfirmed",
                    "request={0} premium={1} index={2} retryDelaySeconds=60", requests, pendingPremium ? 1 : 0,
                    pendingIndex);
                lapsed = pending;
                lapsedPass = pendingPass;
                lapsedPremium = pendingPremium;
                lapsedIndex = pendingIndex;
                lapsedUntil = now + LateWindow;
                pending = null;
                next = now + 60;
                Status = "pawpass claim not confirmed; retry delayed";
                // Requests that are never confirmed are not repeated without end.
                if (++unconfirmed >= Attempts) {
                    Diag.Values(LogLevel.Warn, "PawPass", "claim.halted",
                        "unconfirmed={0}; no further claims until the feature is switched off or paused and on again",
                        unconfirmed);
                    Status = Halted;
                }
            }
            return;
        }
        if (active && lapsed != null && now >= lapsedRead) {
            // The answer to a request that timed out can still arrive. It is looked for only while the
            // feature is active, for a limited time, and while the milestone is still where it was.
            lapsedRead = now + 2;
            bool left = true;
            try {
                if (now > lapsedUntil || !LapsedInPlace())lapsed = null;
                else left = (bool)unclaimed.Invoke(lapsed, NoArgs);
            } catch (Exception e) {
                lapsed = null;
                Diag.CurrentLimited("PawPass", "confirmation.read.failed", (e.InnerException ?? e).Message);
            }
            if (!left) {
                Diag.Values(LogLevel.Info, "PawPass", "claim.confirmed.late",
                    "request={0}; claim token no longer held after the timeout", requests);
                lapsed = null;
                unconfirmed = 0;
                Status = "";
            }
        }
        if (!active) {
            Status = "";
            candidate = null;
#if AUTOCAT_PAWPASS_DRYRUN
            reported.Clear();
#endif
            return;
        }
        if (resolved == 0)Resolve();
        if (resolved < 0) {
            Status = "pawpass unavailable";
            return;
        }
        if (unconfirmed >= Attempts) {
            Status = Halted;
            return;
        }
        try {
            if (busy || Occupied())candidate = null;
            if (now < next)return;
            next = now + 2;
            Status = Evaluate(busy, now);
        } catch (Exception e) {
            if (pending == null)candidate = null;
            Status = pending != null ? "claiming pawpass" : "pawpass not ready";
            Diag.CurrentLimited("PawPass", pending != null ? "claim.call.failed" : "evaluation.failed",
                (e.InnerException ?? e).Message);
        }
#if AUTOCAT_PAWPASS_DRYRUN
        if (pending == null)Status = Status == "" ? "pawpass dry run" : "pawpass dry run: " + Status;
#endif
    }

    const string Halted = "pawpass claim failed; toggle to retry";

    // The menu paused automation or switched the feature off. Only this lifts a stop; the component
    // clearing its own flags when the menu goes silent does not.
    public void Release() {
        lapsed = null;
        if (unconfirmed < Attempts)return;
        unconfirmed = 0;
        Diag.Write(LogLevel.Info, "PawPass", "claim.halt.cleared", "paused or switched off from the menu");
    }

    bool LapsedInPlace() {
        object manager = instance.GetValue(null);
        if (!Live(manager) || !Live(lapsedPass) ||
                !Object.ReferenceEquals(selectedPass.GetValue(manager), lapsedPass))return false;
        var list = (IList)(lapsedPremium ? premiumMilestones : freeMilestones).GetValue(lapsedPass);
        return list != null && lapsedIndex < list.Count && Object.ReferenceEquals(list[lapsedIndex], lapsed);
    }

    // The game is fetching the inventory or running its own claim-all.
    bool Occupied() {
        object manager = instance.GetValue(null), items = inventory.GetValue(null);
        return Live(manager) && Live(items) &&
            ((bool)fetching.GetValue(items) || (bool)claimingAll.GetValue(manager));
    }

    string Evaluate(bool busy, float now) {
        object held = candidate, heldRow = candidateRow;
        candidate = null;
        object manager = instance.GetValue(null), items = inventory.GetValue(null);
        if (!Live(manager) || !Live(items) || !(bool)initialized.GetValue(manager))return "pawpass not ready";
        if ((bool)fetching.GetValue(items) || (bool)claimingAll.GetValue(manager))return "pawpass waiting";
        if (busy)return "";
        object pass = selectedPass.GetValue(manager);
        if (!Live(pass))return "pawpass not ready";
        var progress = Read(pass);
        bool premium = false;
        int index = -1;
        int count = Math.Max(progress.Free == null ? 0 : progress.Free.Count,
            progress.Premium == null ? 0 : progress.Premium.Count);
        for (int i = 0; i < count && index < 0; i++)
            for (int track = 0; track < 2 && index < 0; track++)
                if (Claimable(progress, track == 1, i)) {
                    premium = track == 1;
                    index = i;
                }
        if (index < 0)return "";
        object milestone = (premium ? progress.Premium : progress.Free)[index];
        var rows = (IList)(premium ? premiumRows : freeRows).GetValue(manager);
        if (rows == null || index >= rows.Count)return "open pawpass once";
        object row = rows[index];
        if (!Live(row) || !Object.ReferenceEquals(rowMilestone.GetValue(row), milestone))
            return "open pawpass once";
        if ((bool)revealing.GetValue(row))return "pawpass waiting";
        // Read again right before the call: the selected pass, the milestone at this place, and that it is
        // still earned, owned and unclaimed.
        if (!Object.ReferenceEquals(selectedPass.GetValue(manager), pass))return "";
        var fresh = Read(pass);
        if (!Claimable(fresh, premium, index) ||
                !Object.ReferenceEquals((premium ? fresh.Premium : fresh.Free)[index], milestone))return "";
        candidate = milestone;
        candidateRow = row;
        if (!Object.ReferenceEquals(held, milestone) || !Object.ReferenceEquals(heldRow, row))candidateSince = now;
        if (now - candidateSince < Settle)return "";
#if AUTOCAT_PAWPASS_DRYRUN
        if (!reported.Contains(milestone)) {
            reported.Add(milestone);
            Diag.Values(LogLevel.Info, "PawPass", "claim.would_start",
                "premium={0} index={1}; dry-run build, nothing is claimed", premium ? 1 : 0, index);
        }
        return "would claim";
#else
        candidate = null;
        lapsed = null;
        pending = milestone;
        pendingPass = pass;
        pendingPremium = premium;
        pendingIndex = index;
        submitted = now;
        requests++;
        Diag.Values(LogLevel.Info, "PawPass", "claim.started", "request={0} premium={1} index={2}", requests,
            premium ? 1 : 0, index);
        claim.Invoke(row, NoArgs);
        Diag.Values(LogLevel.Info, "PawPass", "claim.submitted",
            "request={0}; native row claim returned; outcome pending", requests);
        return "claiming pawpass";
#endif
    }

    Progress Read(object pass) {
        var p = new Progress {
            Joined = (bool)joined.Invoke(pass, NoArgs)
        };
        int required = (int)requiredTaps.GetValue(pass);
        if (!p.Joined || required <= 0)return p;
        p.Reached = (int)tapsFor.Invoke(null, new[] {pass}) / required;
        p.HasFree = (bool)hasFree.Invoke(pass, NoArgs);
        p.HasPremium = (bool)hasPremium.Invoke(pass, NoArgs);
        p.Free = (IList)freeMilestones.GetValue(pass);
        p.Premium = (IList)premiumMilestones.GetValue(pass);
        return p;
    }

    // The game's own rule for a claimable reward: the pass is joined, the milestone is reached, its track is
    // owned, and its claim token is still held.
    bool Claimable(Progress p, bool premium, int index) {
        var list = premium ? p.Premium : p.Free;
        return p.Joined && index >= 0 && index < p.Reached && (premium ? p.HasPremium : p.HasFree) &&
            list != null && index < list.Count && list[index] != null &&
            (bool)unclaimed.Invoke(list[index], NoArgs);
    }

    static bool Live(object value) { return value as UnityEngine.Object != null; }

    void Resolve() {
        try {
            var manager = game.GetType("BongoCat.PawPass.PawPassManager", true, false);
            var pass = game.GetType("BongoCat.PawPass.PawPassEntity", true, false);
            var milestone = game.GetType("BongoCat.PawPass.PawPassMilestone", true, false);
            var row = game.GetType("BongoCat.PawPass.PawPassItem", true, false);
            var items = game.GetType("BongoCat.CatInventory", true, false);
            foreach (var type in new[] {manager, pass, row, items})
                if (!typeof(UnityEngine.Object).IsAssignableFrom(type))throw new MissingMemberException(type.Name);
            instance = Member(manager, "Instance", manager, true);
            initialized = Member(manager, "IsInitialized", typeof(bool), false);
            claimingAll = Member(manager, "_isClaimingAll", typeof(bool), false);
            selectedPass = Member(manager, "SelectedPass", pass, false);
            freeRows = Members(manager, "_freeItems", row);
            premiumRows = Members(manager, "_premiumItems", row);
            tapsFor = Method(manager, "TapsFor", typeof(int), true, pass);
            requiredTaps = Member(pass, "RequiredTapsPerMilestone", typeof(int), false);
            freeMilestones = Members(pass, "FreePassMilestones", milestone);
            premiumMilestones = Members(pass, "PremiumPassMilestones", milestone);
            joined = Method(pass, "get_IsJoined", typeof(bool), false);
            hasFree = Method(pass, "get_HasFreePass", typeof(bool), false);
            hasPremium = Method(pass, "get_HasPremiumPass", typeof(bool), false);
            unclaimed = Method(milestone, "get_IsUnclaimed", typeof(bool), false);
            rowMilestone = Member(row, "Milestone", milestone, false);
            revealing = Member(row, "IsRevealing", typeof(bool), false);
            var rowClaim = Method(row, "Claim", typeof(void), false);
#if !AUTOCAT_PAWPASS_DRYRUN
            claim = rowClaim;
#endif
            inventory = Member(items, "Instance", items, true);
            fetching = Member(items, "FetchingItems", typeof(bool), false);
            resolved = 1;
            Diag.Write(LogLevel.Info, "PawPass", "resolved", "game members found; claims use the game's own row");
        } catch (Exception e) {
            resolved = -1;
            Diag.Write(LogLevel.Warn, "PawPass", "unavailable",
                "game member missing or changed: " + (e.InnerException ?? e).Message +
                "; feature disabled for this session");
        }
    }

    static FieldInfo Member(Type type, string name, Type expected, bool isStatic) {
        var field = type.GetField(name, Any);
        if (field == null || field.FieldType != expected || field.IsStatic != isStatic)
            throw new MissingFieldException(type.Name + "." + name);
        return field;
    }

    static FieldInfo Members(Type type, string name, Type element) {
        var field = type.GetField(name, Any);
        if (field == null || field.IsStatic || !typeof(IList).IsAssignableFrom(field.FieldType) ||
                !field.FieldType.IsGenericType || field.FieldType.GetGenericArguments().Length != 1 ||
                field.FieldType.GetGenericArguments()[0] != element)
            throw new MissingFieldException(type.Name + "." + name);
        return field;
    }

    // Overloads are told apart by their exact parameter list.
    static MethodInfo Method(Type type, string name, Type returns, bool isStatic, params Type[] arguments) {
        foreach (var method in type.GetMethods(Any)) {
            if (method.Name != name || method.ReturnType != returns || method.IsStatic != isStatic)continue;
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length)continue;
            bool same = true;
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType != arguments[i])same = false;
            if (same)return method;
        }
        throw new MissingMethodException(type.Name + "." + name);
    }
}
}
