using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Diagnostics;
using AutoCat;
using AutoCat.Diagnostics;

static class Tests {
    public static void Run(string path) {
        var report = new StringBuilder();
        int fetches = 0;
        UpdateCheck.Fetch = () => {
            fetches++;
            return null;
        };
        try {
#if AUTOCAT_PAWPASS_DRYRUN
            report.AppendLine("DRY RUN: pawpass dry-run build; it claims nothing and must not be released");
#endif
            var f = TapEngine.Frame("10000000");
            if (f[0] != 0 || f[1] != 16 || Encoding.Unicode.GetString(f, 2, f.Length - 2) != "10000000")
                throw new Exception("Framing");
            report.AppendLine("PASS: tap protocol framing");
            if (TapEngine.Grant(25000000, 7) != 7 || TapEngine.Grant(1, 0) != 0 ||
                    TapEngine.Grant(100, -2) != 0)throw new Exception("Counter budget");
            using (var guard = new TapEngine("unused")) {
                guard.Observe(1999999995, 100);
                if (guard.Remaining != 5)throw new Exception("Cutoff");
                guard.Observe(100, 100);
                if (guard.Remaining != 5)throw new Exception("Budget rollback");
            }
            report.AppendLine("PASS: counter cutoff, budget clamp, stale lower totals cannot enlarge budget");
            var state = GameState.Parse("OK|100|101|10|1|0|0|12|2|3|4|");
            if (!state.Ready || state.Lifetime != 101 || !state.NormalReady || state.EmoteTimer != 12)
                throw new Exception("Bridge state");
            var tokens = GameState.Parse("OK|100|101|10|1|0|0|12|2|3|4||5|6|1|16.7|20|20|6|12");
            if (tokens.NormalTokens != 6 || tokens.EmoteTokens != 12 || state.NormalTokens != -1 ||
                    new Settings().InstaGift)throw new Exception("Token telemetry/defaults");
            report.AppendLine("PASS: token telemetry, legacy unknown balances and insta gift off by default");
            var claiming = GameState.Parse("OK|100|101|10|1|0|0|12|2|3|4||5|6|1|16.7|20|20|6|12|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("open pawpass once")));
            using (var client = new BridgeClient("unused", null)) {
                if (claiming.PawPass != "open pawpass once" || claiming.EmoteTokens != 12 || !claiming.Ready ||
                        tokens.PawPass != "" || state.PawPass != "" || new Settings().PawPass ||
                        client.Command() != "SET|0|0|0|0|1|0|0|0")throw new Exception("PawPass protocol/defaults");
                client.PawPass = true;
                if (client.Command() != "SET|0|0|0|0|1|0|0|1")throw new Exception("PawPass command field");
            }
            report.AppendLine(
                "PASS: pawpass claim is off by default, is the ninth command field, and its status is read from the state line when present");
            if (!BridgeClient.StartupNotReady(new Exception("Input hook not initialized")) ||
                    !BridgeClient.StartupNotReady(new Exception("Bongo Cat not initialized")) ||
                    BridgeClient.StartupNotReady(new Exception("Access denied")))
                throw new Exception("Startup readiness classification");
            if (!BridgeClient.PipeDisconnected(new IOException("Pipe is broken.")) ||
                    BridgeClient.PipeDisconnected(new IOException("Bridge response timed out")) ||
                    BridgeClient.PipeDisconnected(new Exception("Pipe is broken.")))
                throw new Exception("Pipe disconnect classification");
            report.AppendLine("PASS: startup readiness and expected pipe disconnects are classified narrowly");
            if (!MainForm.Matches(5, 2, 5, 2) || MainForm.Matches(5, 2, 5, 0) ||
                    MainForm.Matches(0, 0, 0, 0) || !MainForm.Matches(256, 0, 256, 0))
                throw new Exception("Bindings");
            report.AppendLine("PASS: mouse / wheel / modifier bindings and unbound state");
            if (!MainForm.IsMinimize(0xF020) || !MainForm.IsMinimize(0xF022) || MainForm.IsMinimize(0xF030) ||
                    MainForm.IsMinimize(0xF120) || MainForm.IsMinimize(0xF060) || MainForm.IsMinimize(0xF010))
                throw new Exception("Taskbar minimize request");
            report.AppendLine("PASS: only the minimize system command is taken as the taskbar button on the front menu");
            BindingEdges();
            report.AppendLine(
                "PASS: a press is one action, a held key never repeats, and a key held at start or when the bindings " +
                "are applied is not a press");
            BindingModifiers();
            report.AppendLine(
                "PASS: left/right modifiers normalize, extra or late modifiers do not match, all four combine");
            BindingModifierOnly();
            report.AppendLine(
                "PASS: modifier-only bindings fire on press of either side and not while another modifier is held");
            BindingMouseButtons();
            report.AppendLine(
                "PASS: mouse buttons 1-5 are polled with their pointer position, following swapped buttons");
            BindingReads();
            report.AppendLine(
                "PASS: binding polling reads only the bound keys, and the modifier keys when one of them goes down");
            WheelHook();
            report.AppendLine(
                "PASS: the wheel hook exists only while a wheel binding does, and only reads state for a bound direction");
            var settings = new Settings {
                Taps = 100000000,
                MenuKey = 5,
                MenuModifiers = 2,
                PauseKey = 257,
                Clicker = true,
                Gifts = true,
                Exchange = true,
                Emoting = true,
                InstaGift = true,
                EmoteRate = 7,
                AccentColor = 0x1A2B3C,
                PawPass = true
            };
            string tmp = Path.Combine(Path.GetTempPath(), "autocat-settings-" + Guid.NewGuid() + ".xml");
            try {
                settings.Save(tmp);
                var copy = Settings.Read(tmp);
                if (copy.MenuKey != 5 || copy.MenuModifiers != 2 || copy.PauseKey != 257 || !copy.Gifts ||
                        !copy.InstaGift || !copy.Emoting || copy.EmoteRate != 7 || copy.Taps != 200 ||
                        settings.Taps != 100000000 || copy.AccentColor != 0x1A2B3C || !copy.PawPass)
                    throw new Exception("Persistence");
                settings.ExtremeRates = true;
                settings.EmoteRate = 1000;
                settings.Save(tmp);
                var extreme = Settings.Read(tmp);
                var restored = new RatePolicy();
                restored.Restore(extreme.ExtremeRates);
                if (!restored.Enabled || restored.Pending || extreme.Taps != 100000000 ||
                        extreme.EmoteRate != 1000)throw new Exception("Confirmed rates persistence");
                settings.ExtremeRates = false;
                settings.Save(tmp);
                if (Settings.Read(tmp).ExtremeRates || Settings.Read(tmp).EmoteRate != 100)
                    throw new Exception("Disabled rate persistence");
                settings.Taps = 3;
                settings.Save(tmp);
                if (Settings.Read(tmp).Taps != 3)throw new Exception("Atomic overwrite");
            } finally {
                File.Delete(tmp);
            }
            report.AppendLine("PASS: settings round trip and atomic overwrite");
            System.Drawing.Color hexColor;
            if (!AutoCat.Skin.TryParseHex("#1a2B3c", out hexColor) ||
                    hexColor.ToArgb() != System.Drawing.Color.FromArgb(0x1a, 0x2b, 0x3c).ToArgb() ||
                    AutoCat.Skin.TryParseHex("bogus", out hexColor) ||
                    AutoCat.Skin.Hex(System.Drawing.Color.FromArgb(0xE0, 0xA1, 0x89)) != "E0A189")
                throw new Exception("Hex parsing");
            var hsvRound = AutoCat.Skin.HsvToRgb(24, 0.395f, 0.878f);
            float rh, rs, rv;
            AutoCat.Skin.RgbToHsv(hsvRound, out rh, out rs, out rv);
            var hsvBack = AutoCat.Skin.HsvToRgb(rh, rs, rv);
            if (Math.Abs(hsvRound.R - hsvBack.R) > 2 || Math.Abs(hsvRound.G - hsvBack.G) > 2 ||
                    Math.Abs(hsvRound.B - hsvBack.B) > 2)throw new Exception("HSV round trip");
            report.AppendLine("PASS: accent hex parsing/formatting and HSV round trip");
            if (Math.Abs(EntryPoint.ComputeScale(1f, new System.Drawing.Size(1920, 1080)) - 1f) > 0.001f)
                throw new Exception("Scale: 1080p/100% baseline must be 1x");
            if (Math.Abs(EntryPoint.ComputeScale(1f, new System.Drawing.Size(3840, 2160)) - 2f) > 0.001f)
                throw new Exception("Scale: 4K/100% must reach 2x from resolution alone");
            if (Math.Abs(EntryPoint.ComputeScale(1.5f, new System.Drawing.Size(1920, 1080)) - 1.5f) > 0.001f)
                throw new Exception("Scale: DPI-only scaling must not be reduced by resolution");
            if (Math.Abs(EntryPoint.ComputeScale(1.5f, new System.Drawing.Size(3840, 2160)) - 2f) > 0.001f)
                throw new Exception(
                    "Scale: DPI and resolution must take the larger signal, not multiply/stack");
            if (Math.Abs(EntryPoint.ComputeScale(1f, new System.Drawing.Size(7680, 4320)) - 3f) > 0.001f)
                throw new Exception("Scale: must clamp to the 3x ceiling");
            if (Math.Abs(EntryPoint.ComputeScale(1f, new System.Drawing.Size(1280, 720)) - 1f) > 0.001f)
                throw new Exception("Scale: below-baseline resolution must not shrink below 1x");
            report.AppendLine(
                "PASS: DPI/resolution combined scale formula (1080p baseline, larger-signal-wins, clamped 1x-3x)");
            var self = System.Reflection.Assembly.GetExecutingAssembly();
            var fileVersion =
                (System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(self,
                    typeof(System.Reflection.AssemblyFileVersionAttribute));
            var assemblyVersion = self.GetName().Version;
            if (fileVersion == null || fileVersion.Version != assemblyVersion.ToString())
                throw new Exception("Runtime identity: AssemblyVersion and AssemblyFileVersion differ");
            if (EmbeddedRuntime.RuntimeId != assemblyVersion.Major.ToString() + assemblyVersion.Minor +
                    assemblyVersion.Build)
                throw new Exception("Runtime identity: RuntimeId " + EmbeddedRuntime.RuntimeId +
                    " does not match version " + assemblyVersion.ToString(3));
            using (var embedded = self.GetManifestResourceStream(EmbeddedRuntime.ResourceName)) {
                if (embedded == null || embedded.Length == 0)
                    throw new Exception("Runtime identity: no embedded runtime named " +
                        EmbeddedRuntime.ResourceName);
            }
            string summary = DiagnosticSession.Summary();
            if (summary.IndexOf("runtime=" + EmbeddedRuntime.RuntimeId + " ", StringComparison.Ordinal) < 0 ||
                    summary.IndexOf("AutoCat=" + assemblyVersion.ToString(3), StringComparison.Ordinal) < 0)
                throw new Exception(
                    "Runtime identity: diagnostic summary does not report version and runtime id");
            report.AppendLine(
                "PASS: embedded runtime matches RuntimeId and version digits, version attributes agree, summary reports both (runtime" +
                EmbeddedRuntime.RuntimeId + ")");
            Metadata(self, assemblyVersion);
            report.AppendLine(
                "PASS: executable and embedded runtime carry the version, product and copyright resource; the manifest is asInvoker, lists Windows 10 and leaves DPI awareness to the code; the runtime keeps its name and assembly version");
            string runtimeDir = Path.Combine(Path.GetTempPath(),
                "autocat-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtimeDir);
            try {
                foreach (var n in new[] {
                    "autocat.runtime.100.dll",
                    "autocat.runtime.101.dll",
                    EmbeddedRuntime.FileName,
                    "other.dll"
                })File.WriteAllText(Path.Combine(runtimeDir, n), "x");
                int inUse, removed;
                using (new FileStream(Path.Combine(runtimeDir, "autocat.runtime.101.dll"), FileMode.Open,
                        FileAccess.Read, FileShare.None))
                    removed = EmbeddedRuntime.RemoveStale(runtimeDir, EmbeddedRuntime.FileName, out inUse);
                if (removed != 1 || inUse != 1 ||
                        File.Exists(Path.Combine(runtimeDir, "autocat.runtime.100.dll")) ||
                        !File.Exists(Path.Combine(runtimeDir, "autocat.runtime.101.dll")) ||
                        !File.Exists(Path.Combine(runtimeDir, EmbeddedRuntime.FileName)) ||
                        !File.Exists(Path.Combine(runtimeDir, "other.dll")))
                    throw new Exception("Stale runtime cleanup");
            } finally {
                Directory.Delete(runtimeDir, true);
            }
            report.AppendLine(
                "PASS: stale extracted runtimes are removed; current, in-use, and unrelated files are kept");
            var policy = new RatePolicy();
            if (new Settings().Taps != 10 || new Settings().EmoteRate != 10 || policy.Enabled ||
                    policy.Clamp(int.MaxValue, false) != 200 || policy.Clamp(int.MaxValue, true) != 100)
                throw new Exception("Safe defaults");
            policy.Confirm();
            if (policy.Enabled)throw new Exception("Unrequested confirmation");
            policy.Request(true);
            if (policy.Enabled || !policy.Pending || policy.Clamp(1000, false) != 200 ||
                    policy.Clamp(1000, true) != 100)throw new Exception("Pending bypass");
            policy.Request(false);
            policy.Confirm();
            if (policy.Enabled)throw new Exception("Cancelled confirmation");
            policy.Request(true);
            policy.Confirm();
            if (!policy.Enabled || policy.Pending || policy.Clamp(1000, false) != 1000 ||
                    policy.Clamp(int.MaxValue, false) != TapEngine.MaximumRate ||
                    policy.Clamp(1000, true) != 1000 || policy.Clamp(int.MinValue, false) != 1)
                throw new Exception("Danger bounds");
            policy.Request(false);
            if (policy.Clamp(1000, false) != 200 || policy.Clamp(1000, true) != 100 || policy.Pending ||
                    policy.Enabled)throw new Exception("Disable clamp");
            report.AppendLine(
                "PASS: danger confirmation, cancellation, independent normal caps and hard ceilings");
            string legacy = Path.Combine(Path.GetTempPath(), "autocat-legacy-" + Guid.NewGuid() + ".xml");
            try {
                File.WriteAllText(legacy,
                    "<Settings><Taps>100000000</Taps><EmoteRate>2147483647</EmoteRate></Settings>");
                var loaded = Settings.Read(legacy);
                if (loaded.Taps != 200 || loaded.EmoteRate != 100)throw new Exception("Legacy unsafe rates");
            } finally {
                File.Delete(legacy);
            }
            report.AppendLine(
                "PASS: legacy rates use independent normal clamps; saving does not alter live session rates");
            SettingsVersions();
            report.AppendLine(
                "PASS: a 1.1.1 settings file loads unchanged with pawpass claim off, a saved file adds one element after the ones 1.1.1 wrote, and elements a build does not know are skipped");
            DiagnosticsHandoff();
            report.AppendLine("PASS: menu and runtime diagnostics handoff writes one shared session file");
            Transport();
            report.AppendLine("PASS: connected tap delivery, pause, resume, stale-counter stop and unload");
            ReconnectWait();
            report.AppendLine("PASS: reconnect wait ends promptly once closing and still times out otherwise");
            UpdateReplies();
            report.AppendLine(
                "PASS: update check accepts only a redirect to a vMAJOR.MINOR.PATCH tag of this project on github.com, compares versions numerically, and treats any problem as a quiet check with no result");
            UpdateRequest();
            report.AppendLine(
                "PASS: update request carries only the request line, Host and Connection: Close, does not follow redirects or read the body, and ends on timeout or close");
            UpdateRequestShape();
            report.AppendLine(
                "PASS: update request is a bare GET with no proxy, credentials, cookies, certificates, user agent or other headers, uses TLS 1.2 only with certificate validation untouched, never consults the system proxy, repeats at most once after a dropped connection, and refuses oversized headers");
            if (fetches != 0)throw new Exception("Update: the self-test attempted a network read");
            if (TrayText.Tooltip("attached", true) != "autocat\nattached · running" ||
                    TrayText.Tooltip("a message that is not a connection state", false) !=
                    "autocat\nconnection problem · paused" || TrayText.Tooltip("waiting for game initialization", true).Length > 63)
                throw new Exception("Tray tooltip");
            report.AppendLine("PASS: the notification area tooltip names autocat and the connection and pause state, in at most 63 characters, and never carries an error message");
            if (NotifyTray.Created != 0)throw new Exception("Tray: the self-test created a notification area icon");
            report.AppendLine("PASS: the self-test creates no notification area icon");
            report.AppendLine("PASS: the self-test makes no update request");
            File.WriteAllText(path, report.ToString());
        } catch (Exception e) {
            File.WriteAllText(path, report + "FAIL: " + e);
            Environment.ExitCode = 1;
        }
    }

    // The file 1.1.1 wrote has no PawPass element; a file with elements a build does not know is what an older
    // build sees when it reads a newer file.
    static void SettingsVersions() {
        const string head = "<?xml version=\"1.0\"?>\r\n<Settings xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n";
        const string body = "  <Taps>25</Taps>\r\n  <MenuKey>46</MenuKey>\r\n  <PauseKey>35</PauseKey>\r\n" +
            "  <MenuModifiers>2</MenuModifiers>\r\n  <PauseModifiers>0</PauseModifiers>\r\n" +
            "  <Clicker>true</Clicker>\r\n  <Gifts>true</Gifts>\r\n  <Exchange>false</Exchange>\r\n" +
            "  <Emoting>true</Emoting>\r\n  <InstaGift>true</InstaGift>\r\n  <ExtremeRates>false</ExtremeRates>\r\n";
        const string tail = "  <EmoteRate>7</EmoteRate>\r\n  <AccentColor>1715004</AccentColor>\r\n";
        string path = Path.Combine(Path.GetTempPath(), "autocat-versions-" + Guid.NewGuid() + ".xml");
        Func<string, Settings> read = text => {
            File.WriteAllText(path, text);
            var loaded = Settings.Read(path);
            if (loaded.Taps != 25 || loaded.MenuKey != 46 || loaded.PauseKey != 35 || loaded.MenuModifiers != 2 ||
                    !loaded.Clicker || !loaded.Gifts || loaded.Exchange || !loaded.Emoting || !loaded.InstaGift ||
                    loaded.ExtremeRates || loaded.EmoteRate != 7 || loaded.AccentColor != 0x1A2B3C)
                throw new Exception("Settings versions: known fields changed");
            return loaded;
        };
        try {
            var old = read(head + body + tail + "</Settings>");
            if (old.PawPass)throw new Exception("Settings versions: a 1.1.1 file must leave PawPass off");
            old.PawPass = true;
            old.Save(path);
            string saved = File.ReadAllText(path);
            if (!saved.StartsWith(head + body + tail + "  <PawPass>true</PawPass>\r\n</Settings>"))
                throw new Exception("Settings versions: saved shape " + saved);
            if (!read(saved).PawPass)throw new Exception("Settings versions: PawPass not kept");
            if (read(head + body + "  <Later>true</Later>\r\n" + tail + "  <Newer><A>1</A></Newer>\r\n</Settings>")
                    .PawPass)throw new Exception("Settings versions: unknown elements");
        } finally {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    const string TagAddress = "https://github.com/kaankutluturk/autocat/releases/tag/";

    static UpdateCheck.Reply Redirect(string location, int status = 302) {
        return new UpdateCheck.Reply {Status = status, Location = location};
    }

    static string Latest(UpdateCheck.Reply reply) {
        var version = UpdateCheck.Parse(reply);
        return version == null ? "-" : version.ToString();
    }

    // What a reply may become: three small integers, and only for a redirect to the tag page of this project.
    static void UpdateReplies() {
        foreach (var valid in new[] {
            new[] {TagAddress + "v1.2.1", "1.2.1"},
            new[] {TagAddress + "1.2.1", "1.2.1"},
            new[] {TagAddress + "v0.0.0", "0.0.0"},
            new[] {TagAddress + "v10.20.30", "10.20.30"},
            new[] {TagAddress + "v9999.9999.9999", "9999.9999.9999"}
        })
            if (Latest(Redirect(valid[0])) != valid[1])throw new Exception("Update reply: rejected " + valid[0]);
        foreach (int status in new[] {301, 302, 303, 307, 308})
            if (Latest(Redirect(TagAddress + "v1.2.1", status)) != "1.2.1")
                throw new Exception("Update reply: redirect status " + status);
        foreach (string tag in new[] {
            "v1.2", "v1", "v", "", "1.2", "v1.2.3.4", "v1.2.3.", "v.1.2", "v1..3", "v...", "v1.2.x", "va.b.c",
            "v1.2.3-beta", "v1.2.3+1", "v1.2.3 ", " v1.2.3", "v1.2.3\n", "v1.2.3\r\n", "v1.2.3?x=1", "v1.2.3#x",
            "v1.2.3/", "v1.2.3/../x", "v1.2.3%00", "v-1.2.3", "v1.-2.3", "v1.2.-3", "v+1.2.3", "vv1.2.3", "V1.2.3",
            "v01.2.3", "v1.02.3", "v1.2.03", "v00.2.3", "v10000.2.3", "v1.10000.3", "v1.2.10000",
            "v99999999999999999999.2.3", "v1.2.4294967296", "v1.2.2147483648", "v\u0661.2.3", "v1.2.\uff13",
            "v0x1.2.3", "v1e1.2.3", "latest", "tag/v1.2.3", "releases"
        })
            if (Latest(Redirect(TagAddress + tag)) != "-")
                throw new Exception("Update reply: accepted tag '" + tag + "'");
        foreach (string location in new[] {
            null,
            "",
            "/kaankutluturk/autocat/releases/tag/v1.2.3",
            "kaankutluturk/autocat/releases/tag/v1.2.3",
            "//github.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "http://github.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://github.com:444/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://github.com:443/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://GitHub.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://www.github.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://github.com.example.org/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://example.org/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://github.com@example.org/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://example.org/https://github.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "https://github.com/someone/autocat/releases/tag/v1.2.3",
            "https://github.com/kaankutluturk/autocat2/releases/tag/v1.2.3",
            "https://github.com/kaankutluturk/Autocat/releases/tag/v1.2.3",
            "https://github.com/kaankutluturk/autocat/releases/v1.2.3",
            "https://github.com/kaankutluturk/autocat/releases/download/v1.2.3",
            "https://github.com/kaankutluturk/autocat/releases/tag/",
            "https://github.com/kaankutluturk/autocat/releases/tag",
            "https://github.com/kaankutluturk/autocat/releases",
            "https://github.com/kaankutluturk/autocat/releases/latest",
            "https://github.com/kaankutluturk/autocat/releases/tag/v1.2.3/extra",
            "https://github.com/kaankutluturk/autocat/releases/tag/" + new string('1', 4000) + ".2.3",
            "https://github.com/login?return_to=https://github.com/kaankutluturk/autocat/releases/tag/v1.2.3",
            "file:///C:/Windows/System32/cmd.exe",
            "javascript:alert(1)"
        })
            if (Latest(Redirect(location)) != "-")
                throw new Exception("Update reply: accepted location '" + location + "'");
        foreach (int status in new[] {0, 100, 200, 204, 304, 400, 404, 429, 500, 503})
            if (Latest(Redirect(TagAddress + "v1.2.1", status)) != "-")
                throw new Exception("Update reply: accepted status " + status);
        var running = new Version(1, 2, 0, 0);
        Func<string, UpdateCheck.Outcome> outcome = tag => {
            Version latest;
            var result = UpdateCheck.Check(() => Redirect(TagAddress + tag), running, out latest);
            if ((latest != null) != (result == UpdateCheck.Outcome.Newer))
                throw new Exception("Update: version kept for " + tag);
            return result;
        };
        foreach (string newer in new[] {"v1.2.1", "v1.3.0", "v2.0.0", "v1.2.10", "v1.10.0", "v1.100.0", "v10.0.0", "1.2.1"})
            if (outcome(newer) != UpdateCheck.Outcome.Newer)
                throw new Exception("Update: " + newer + " is newer than 1.2.0");
        foreach (string notNewer in new[] {"v1.2.0", "v1.1.1", "v1.1.9999", "v1.0.0", "v0.9.9", "v0.99.99", "v1.1.99"})
            if (outcome(notNewer) != UpdateCheck.Outcome.Current)
                throw new Exception("Update: " + notNewer + " is not newer than 1.2.0");
        Version found;
        if (UpdateCheck.Check(() => Redirect(TagAddress + "v1.2.0"), new Version(1, 2, 0, 7), out found) !=
                UpdateCheck.Outcome.Current ||
                UpdateCheck.Check(() => Redirect(TagAddress + "v1.2.1"), new Version(1, 2, 0, 7), out found) !=
                UpdateCheck.Outcome.Newer || found.ToString() != "1.2.1" ||
                UpdateCheck.Check(() => Redirect(TagAddress + "v1.9.0"), new Version(1, 10, 0, 0), out found) !=
                UpdateCheck.Outcome.Current ||
                UpdateCheck.Check(() => Redirect(TagAddress + "v1.2.10"), new Version(1, 2, 9, 0), out found) !=
                UpdateCheck.Outcome.Newer)
            throw new Exception("Update: versions compare numerically on major, minor and patch");
        if (outcome("v1.2.x") != UpdateCheck.Outcome.Rejected)throw new Exception("Update: malformed tag");
        if (UpdateCheck.Check(() => Redirect(null), running, out found) != UpdateCheck.Outcome.Rejected ||
                UpdateCheck.Check(() => Redirect(TagAddress + "v1.2.1", 200), running, out found) !=
                UpdateCheck.Outcome.Rejected || found != null)throw new Exception("Update: non-redirect reply");
        foreach (var failing in new Func<UpdateCheck.Reply>[] {
            () => { throw new TimeoutException(); },
            () => { throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout); },
            () => { throw new InvalidOperationException(@"D:\private\secret"); },
            () => { throw new OutOfMemoryException(); },
            () => { throw new Unexpected(); },
            () => null
        })
            if (UpdateCheck.Check(failing, running, out found) != UpdateCheck.Outcome.Unreachable || found != null)
                throw new Exception("Update: a failing read must be a quiet failed check");
        if (UpdateCheck.Describe(UpdateCheck.Outcome.Newer, new Version(1, 2, 1)) != "result=newer latest=1.2.1" ||
                UpdateCheck.Describe(UpdateCheck.Outcome.Current, null) != "result=none" ||
                UpdateCheck.Describe(UpdateCheck.Outcome.Unreachable, null) != "result=failed reason=network" ||
                UpdateCheck.Describe(UpdateCheck.Outcome.Rejected, null) != "result=failed reason=response")
            throw new Exception("Update: log line");
        // Nothing on the check thread, whatever it throws, may end the process.
        UpdateCheck.Start(() => { throw new Unexpected(); }, running, (o, l) => { throw new Unexpected(); }).Join();
        UpdateCheck.Start(() => Redirect(TagAddress + "v1.2.1"), running, (o, l) => { throw new Unexpected(); }).Join();
    }

    sealed class Unexpected : Exception {
        public Unexpected() : base(@"D:\private\secret") {
        }
    }

    sealed class SlowProxy : IWebProxy {
        public int Asked;
        public ICredentials Credentials { get; set; }

        public Uri GetProxy(Uri destination) {
            Asked++;
            Thread.Sleep(4000);
            return null;
        }

        public bool IsBypassed(Uri host) {
            Asked++;
            Thread.Sleep(4000);
            return false;
        }
    }

    // Everything the request is allowed to carry, as set up before it is sent.
    static void UpdateRequestShape() {
        var request = UpdateCheck.Create(new Uri(UpdateCheck.ReleasePage));
        if (request.Method != "GET" || request.RequestUri.AbsoluteUri != UpdateCheck.ReleasePage)
            throw new Exception("Update request: method or address");
        if (request.UseDefaultCredentials || request.Credentials != null || request.PreAuthenticate ||
                request.CookieContainer != null || request.ClientCertificates.Count != 0)
            throw new Exception("Update request: credentials, cookies or certificates");
        if (request.Proxy != null)throw new Exception("Update request: a proxy is configured");
        if (request.AllowAutoRedirect || request.KeepAlive)throw new Exception("Update request: redirects or keep-alive");
        if (request.Timeout != UpdateCheck.TimeoutMs || request.MaximumResponseHeadersLength != 8)
            throw new Exception("Update request: time and size bounds");
        if (request.UserAgent != null || request.Accept != null || request.Referer != null ||
                request.Expect != null || request.ContentType != null || request.Headers.Count != 0 ||
                request.AutomaticDecompression != DecompressionMethods.None)
            throw new Exception("Update request: an added header");
        request.Abort();
        var callback = ServicePointManager.ServerCertificateValidationCallback;
        var protocol = ServicePointManager.SecurityProtocol;
        try {
            UpdateCheck.UseTls12();
            if (ServicePointManager.SecurityProtocol != (SecurityProtocolType)3072)
                throw new Exception("Update request: protocol is " + ServicePointManager.SecurityProtocol);
            if (ServicePointManager.ServerCertificateValidationCallback != null || callback != null)
                throw new Exception("Update request: certificate validation was replaced");
        } finally {
            ServicePointManager.SecurityProtocol = protocol;
        }
        // A configured system proxy is never consulted, so discovery cannot run or delay the request.
        var defaultProxy = WebRequest.DefaultWebProxy;
        var slow = new SlowProxy();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int saved = UpdateCheck.TimeoutMs;
        try {
            WebRequest.DefaultWebProxy = slow;
            UpdateCheck.TimeoutMs = 500;
            var timer = Stopwatch.StartNew();
            UpdateCheck.Send(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"));
            if (slow.Asked != 0 || timer.ElapsedMilliseconds > 2500)
                throw new Exception("Update request: the system proxy was consulted");
            if (!ReferenceEquals(WebRequest.DefaultWebProxy, slow))
                throw new Exception("Update request: the process-wide proxy was changed");
        } finally {
            UpdateCheck.TimeoutMs = saved;
            WebRequest.DefaultWebProxy = defaultProxy;
            listener.Stop();
        }
        // A connection dropped before any reply costs at most one repeat of the request, and ends quietly.
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int connections = 0;
        var drop = new Thread(() => {
            try {
                while (true) {
                    using (var client = listener.AcceptTcpClient()) {
                        Interlocked.Increment(ref connections);
                        ReadHead(client.GetStream());
                    }
                }
            } catch (Exception) {
            }
        }) {IsBackground = true};
        drop.Start();
        try {
            try {
                UpdateCheck.Send(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"));
            } catch (WebException) {
            }
            Thread.Sleep(300);
            if (connections < 1 || connections > 2)throw new Exception("Update request: " + connections + " connections for a dropped one");
        } finally {
            listener.Stop();
        }
        // More header than the cap is refused before its Location is looked at.
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var big = new Thread(() => {
            try {
                using (var client = listener.AcceptTcpClient()) {
                    var stream = client.GetStream();
                    ReadHead(stream);
                    byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nX-Pad: " + new string('a', 12000) +
                        "\r\nLocation: " + TagAddress + "v9.9.9\r\nContent-Length: 0\r\n\r\n");
                    stream.Write(reply, 0, reply.Length);
                    Thread.Sleep(500);
                }
            } catch (Exception) {
            }
        }) {IsBackground = true};
        big.Start();
        try {
            UpdateCheck.Reply oversized = null;
            try {
                oversized = UpdateCheck.Send(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"));
            } catch (WebException) {
            }
            if (oversized != null)throw new Exception("Update request: a reply with oversized headers was accepted");
        } finally {
            listener.Stop();
        }
    }

    static string ReadHead(Stream stream) {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n") && head.Length < 4096 && stream.Read(one, 0, 1) == 1)
            head.Append((char)one[0]);
        return head.ToString();
    }

    // The request as it leaves the program (against a local listener), and that it is bounded in time and size.
    static void UpdateRequest() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int saved = UpdateCheck.TimeoutMs;
        try {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string mode = "redirect", seen = null;
            var served = new List<TcpClient>();
            var server = new Thread(() => {
                try {
                    while (true) {
                        var client = listener.AcceptTcpClient();
                        served.Add(client);
                        var stream = client.GetStream();
                        seen = ReadHead(stream);
                        if (mode != "redirect")continue;
                        byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nSet-Cookie: a=b\r\n" +
                            "Location: http://127.0.0.1:" + port +
                            "/followed\r\nContent-Length: 1000000\r\nConnection: close\r\n\r\n");
                        stream.Write(reply, 0, reply.Length);
                    }
                } catch (Exception) {
                }
            }) {IsBackground = true};
            server.Start();
            var timer = Stopwatch.StartNew();
            var answer = UpdateCheck.Send(new Uri("http://127.0.0.1:" + port +
                "/kaankutluturk/autocat/releases/latest"));
            if (answer == null || answer.Status != 302 || answer.Location != "http://127.0.0.1:" + port + "/followed")
                throw new Exception("Update request: the redirect is not returned to the caller");
            if (timer.ElapsedMilliseconds > 3000)throw new Exception("Update request: waited for the unread body");
            string expected = "GET /kaankutluturk/autocat/releases/latest HTTP/1.1\r\nHost: 127.0.0.1:" + port +
                "\r\nConnection: Close\r\n\r\n";
            if (seen != expected)throw new Exception("Update request: on the wire was " + seen.Replace("\r\n", "|"));
            Thread.Sleep(500);
            if (served.Count != 1 || listener.Pending())throw new Exception("Update request: a redirect was followed");
            mode = "silent";
            UpdateCheck.TimeoutMs = 300;
            timer.Restart();
            if (UpdateCheck.Send(new Uri("http://127.0.0.1:" + port + "/")) != null)
                throw new Exception("Update request: timeout");
            if (timer.ElapsedMilliseconds > 3000)throw new Exception("Update request: timeout not bounded");
            UpdateCheck.TimeoutMs = 20000;
            Exception cancelled = null;
            var call = new Thread(() => {
                try {
                    UpdateCheck.Send(new Uri("http://127.0.0.1:" + port + "/"));
                } catch (Exception e) {
                    cancelled = e;
                }
            }) {IsBackground = true};
            seen = null;
            call.Start();
            for (int i = 0; i < 200 && seen == null; i++)Thread.Sleep(10);
            timer.Restart();
            UpdateCheck.Cancel();
            if (!call.Join(3000) || cancelled == null || timer.ElapsedMilliseconds > 2000)
                throw new Exception("Update request: closing must end a request in flight");
            foreach (var client in served)client.Close();
        } finally {
            UpdateCheck.TimeoutMs = saved;
            listener.Stop();
        }
    }

    static void Metadata(System.Reflection.Assembly self, Version assemblyVersion) {
        string version = assemblyVersion.ToString();
        var exe = FileVersionInfo.GetVersionInfo(self.Location);
        if (exe.FileVersion != version || exe.ProductName != "AutoCat" || exe.FileDescription != "AutoCat" ||
                String.IsNullOrEmpty(exe.LegalCopyright) || !exe.LegalCopyright.StartsWith("Copyright (c) "))
            throw new Exception("Metadata: executable version resource is " + exe.FileVersion + " / " +
                exe.ProductName + " / " + exe.LegalCopyright);
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(self.Location));
        int start = text.IndexOf("<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\"", StringComparison.Ordinal);
        int end = start < 0 ? -1 : text.IndexOf("</assembly>", start, StringComparison.Ordinal);
        if (end < 0)throw new Exception("Metadata: no manifest in the executable");
        text = text.Substring(start, end - start);
        foreach (string expected in new[] {
            "name=\"AutoCat\"",
            "version=\"" + version + "\"",
            "level=\"asInvoker\"",
            "uiAccess=\"false\"",
            "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"
        })
            if (text.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new Exception("Metadata: executable manifest lacks " + expected);
        foreach (string unexpected in new[] {"dpiAware", "dpiAwareness", "longPathAware"})
            if (text.IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Metadata: executable manifest declares " + unexpected);
        string temp = Path.Combine(Path.GetTempPath(), "autocat-meta-" + Guid.NewGuid().ToString("N") + ".dll");
        try {
            using (var embedded = self.GetManifestResourceStream(EmbeddedRuntime.ResourceName))
                using (var file = File.Create(temp))embedded.CopyTo(file);
            var runtime = FileVersionInfo.GetVersionInfo(temp);
            if (runtime.FileVersion != version || runtime.ProductName != "AutoCat" ||
                    runtime.LegalCopyright != exe.LegalCopyright || String.IsNullOrEmpty(runtime.FileDescription))
                throw new Exception("Metadata: runtime version resource is " + runtime.FileVersion + " / " +
                    runtime.ProductName + " / " + runtime.LegalCopyright);
            var name = System.Reflection.AssemblyName.GetAssemblyName(temp);
            if (name.Name != Path.GetFileNameWithoutExtension(EmbeddedRuntime.FileName) ||
                    name.Version != new Version(0, 0, 0, 0))
                throw new Exception("Metadata: runtime identity changed to " + name.FullName);
        } finally {
            File.Delete(temp);
        }
    }

    static string Joined(List<string> log) {
        string text = String.Join(",", log.ToArray());
        log.Clear();
        return text;
    }

    static void Expect(List<string> log, string expected, string step) {
        string actual = Joined(log);
        if (actual != expected)
            throw new Exception("Bindings, " + step + ": expected [" + expected + "] but saw [" + actual + "]");
    }

    static BindingMonitor Watch(FakeInput input, int menu, int pause, List<string> log) {
        var monitor = new BindingMonitor(input);
        monitor.Pressed = (key, mods, point) => log.Add(key + "/" + mods);
        if (!monitor.Configure(menu, pause))throw new Exception("Bindings: configure failed");
        return monitor;
    }

    static void Press(FakeInput input, BindingMonitor monitor, params int[] keys) {
        foreach (int key in keys)input.Down.Add(key);
        monitor.Poll();
    }

    static void Release(FakeInput input, BindingMonitor monitor, params int[] keys) {
        foreach (int key in keys)input.Down.Remove(key);
        monitor.Poll();
    }

    static void BindingEdges() {
        var input = new FakeInput();
        var log = new List<string>();
        var monitor = Watch(input, 45, 36, log);
        for (int i = 0; i < 5; i++)monitor.Poll();
        Expect(log, "", "idle");
        Press(input, monitor, 45);
        Expect(log, "45/0", "one press, one action");
        for (int i = 0; i < 20; i++)monitor.Poll();
        Expect(log, "", "a held key does not repeat");
        Release(input, monitor, 45);
        Expect(log, "", "release is not an action");
        Press(input, monitor, 45);
        Expect(log, "45/0", "a second press");
        Release(input, monitor, 45);
        Press(input, monitor, 36);
        Expect(log, "36/0", "the other binding");
        Press(input, monitor, 45);
        Expect(log, "45/0", "a press while the other key is still held");
        Press(input, monitor, 65);
        Release(input, monitor, 45, 36, 65);
        Expect(log, "", "unbound keys do nothing");
        input.Down.Add(45);
        monitor = Watch(input, 45, 36, log);
        monitor.Poll();
        Expect(log, "", "a key already down when watching starts is not a press");
        Release(input, monitor, 45);
        Press(input, monitor, 45);
        Expect(log, "45/0", "press after a start with the key held");
        Release(input, monitor, 45);
        monitor.Configure(36, 45);
        Press(input, monitor, 45, 36);
        Expect(log, "36/0,45/0", "both bindings in one poll");
        Release(input, monitor, 45, 36);
        monitor.Configure(45, 45);
        Press(input, monitor, 45);
        Expect(log, "45/0", "a key bound twice is reported once");
        Release(input, monitor, 45);
        input.Down.Add(36);
        monitor.Configure(45, 36);
        monitor.Poll();
        Expect(log, "", "a newly bound key that is down is not a press");
        Release(input, monitor, 36);
        input.Down.Add(36);
        monitor.Configure(45, 36);
        monitor.Poll();
        Expect(log, "", "a watched key pressed just before the bindings are applied is not a press");
        Release(input, monitor, 36);
        Press(input, monitor, 36);
        Expect(log, "36/0", "it fires again once released and pressed");
        Release(input, monitor, 36);
        input.Down.Add(36);
        monitor.Configure(256, 36);
        monitor.Poll();
        Expect(log, "", "adding a wheel binding does not press a watched key");
        Release(input, monitor, 36);
        monitor.Configure(1, 4);
        Press(input, monitor, 1);
        Expect(log, "1/0", "mouse 1 once watched");
        Release(input, monitor, 1);
        input.Down.Add(1);
        monitor.Configure(1, 4);
        monitor.Poll();
        Expect(log, "", "a mouse button pressed before the bindings are applied is not a press");
        Release(input, monitor, 1);
        input.Swapped = true;
        input.Down.Add(2);
        monitor.Configure(1, 4);
        monitor.Poll();
        Expect(log, "", "a swapped mouse button pressed before the bindings are applied is not a press");
        input.Swapped = false;
    }

    static void BindingModifiers() {
        var input = new FakeInput();
        var log = new List<string>();
        var monitor = Watch(input, 45, 36, log);
        Press(input, monitor, 162, 45);
        Expect(log, "45/2", "left ctrl");
        Release(input, monitor, 162, 45);
        Press(input, monitor, 163, 45);
        Expect(log, "45/2", "right ctrl is ctrl");
        Release(input, monitor, 163, 45);
        Press(input, monitor, 160, 45);
        Expect(log, "45/4", "left shift");
        Release(input, monitor, 160, 45);
        Press(input, monitor, 161, 45);
        Expect(log, "45/4", "right shift");
        Release(input, monitor, 161, 45);
        Press(input, monitor, 165, 45);
        Expect(log, "45/1", "right alt");
        Release(input, monitor, 165, 45);
        Press(input, monitor, 92, 45);
        Expect(log, "45/8", "right win");
        Release(input, monitor, 92, 45);
        Press(input, monitor, 162, 161, 164, 91, 45);
        Expect(log, "45/15", "all four modifiers");
        Release(input, monitor, 162, 161, 164, 91, 45);
        Press(input, monitor, 45);
        Expect(log, "45/0", "bare key");
        Press(input, monitor, 162);
        Release(input, monitor, 162, 45);
        Expect(log, "", "a modifier pressed after the key is not an action");
        Press(input, monitor, 162, 160, 45);
        string seen = Joined(log);
        if (seen != "45/6" || MainForm.Matches(45, 2, 45, 6) || !MainForm.Matches(45, 6, 45, 6) ||
                MainForm.Matches(45, 0, 45, 6))
            throw new Exception("Bindings, extra modifier: " + seen);
        Release(input, monitor, 162, 160, 45);
        Press(input, monitor, 162);
        Press(input, monitor, 45);
        Expect(log, "45/2", "modifier held before the key");
    }

    static void BindingModifierOnly() {
        var input = new FakeInput();
        var log = new List<string>();
        var monitor = Watch(input, 16, 17, log);
        Press(input, monitor, 160);
        Expect(log, "16/0", "left shift alone");
        for (int i = 0; i < 5; i++)monitor.Poll();
        Expect(log, "", "a held modifier does not repeat");
        Press(input, monitor, 161);
        Expect(log, "16/0", "the second shift key is a press of its own");
        Release(input, monitor, 160, 161);
        Press(input, monitor, 161);
        Expect(log, "16/0", "right shift alone");
        Release(input, monitor, 161);
        Press(input, monitor, 163);
        Expect(log, "17/0", "right ctrl alone");
        Release(input, monitor, 163);
        Press(input, monitor, 162);
        Expect(log, "17/0", "ctrl alone");
        Press(input, monitor, 160);
        Expect(log, "16/2", "shift with ctrl held");
        if (MainForm.Matches(16, 0, 16, 2))throw new Exception("Bindings: shift with ctrl held matched");
        Release(input, monitor, 162, 160);
        Press(input, monitor, 164, 160);
        Expect(log, "16/1", "shift with alt held");
        Release(input, monitor, 164, 160);
        monitor.Configure(18, 91);
        Press(input, monitor, 165);
        Expect(log, "18/0", "right alt alone");
        Release(input, monitor, 165);
        Press(input, monitor, 91);
        Expect(log, "91/0", "left win alone");
        Release(input, monitor, 91);
        Press(input, monitor, 92);
        Expect(log, "", "the other win key is not bound");
        Release(input, monitor, 92);
        Press(input, monitor, 164, 91);
        Expect(log, "18/8,91/1", "alt and win keep the other modifiers");
    }

    static void BindingMouseButtons() {
        var input = new FakeInput();
        var log = new List<string>();
        var point = Point.Empty;
        var monitor = new BindingMonitor(input);
        monitor.Pressed = (key, mods, at) => {
            log.Add(key + "/" + mods);
            point = at;
        };
        monitor.Configure(1, 4);
        input.Cursor = new Point(12, 34);
        Press(input, monitor, 1);
        Expect(log, "1/0", "mouse 1");
        if (point != new Point(12, 34))throw new Exception("Bindings: a mouse press reports the pointer");
        Press(input, monitor, 1);
        Expect(log, "", "a held button does not repeat");
        Release(input, monitor, 1);
        Press(input, monitor, 4, 162);
        Expect(log, "4/2", "mouse 3 with ctrl");
        Release(input, monitor, 4, 162);
        monitor.Configure(5, 6);
        Press(input, monitor, 5);
        Expect(log, "5/0", "mouse 4");
        Release(input, monitor, 5);
        Press(input, monitor, 6);
        Expect(log, "6/0", "mouse 5");
        Release(input, monitor, 6);
        monitor.Configure(1, 2);
        input.Swapped = true;
        Press(input, monitor, 2);
        Expect(log, "1/0", "swapped: the physical right button is the primary one");
        Release(input, monitor, 2);
        Press(input, monitor, 1);
        Expect(log, "2/0", "swapped: the physical left button is the secondary one");
        Release(input, monitor, 1);
        input.Swapped = false;
        monitor.Configure(2, 1);
        Press(input, monitor, 2);
        Expect(log, "2/0", "unswapped buttons");
    }

    static void BindingReads() {
        var input = new FakeInput();
        var log = new List<string>();
        var monitor = Watch(input, 45, 36, log);
        for (int i = 0; i < 10; i++)monitor.Poll();
        if (input.AskedFor() != "36,45" || input.SwapReads != 0 || input.CursorReads != 0)
            throw new Exception("Bindings: idle polling reads only the two bound keys, read " + input.AskedFor());
        input.Down.UnionWith(new[] {65, 66, 13, 27, 1});
        for (int i = 0; i < 3; i++)monitor.Poll();
        if (input.AskedFor() != "36,45")throw new Exception("Bindings: other keys must not be read");
        Press(input, monitor, 45);
        string[] allowed = "36,45,91,92,160,161,162,163,164,165".Split(',');
        foreach (string asked in input.AskedFor().Split(','))
            if (Array.IndexOf(allowed, asked) < 0)
                throw new Exception("Bindings: read " + asked + ", which is neither bound nor a modifier");
        input = new FakeInput();
        monitor = Watch(input, 0, 0, log);
        for (int i = 0; i < 5; i++)monitor.Poll();
        if (input.AskedFor() != "" || input.SwapReads != 0)
            throw new Exception("Bindings: no binding must read nothing");
        monitor.Configure(16, 0);
        input.Asked.Clear();
        for (int i = 0; i < 5; i++)monitor.Poll();
        if (input.AskedFor() != "160,161")throw new Exception("Bindings: a modifier-only binding reads its keys");
        monitor.Configure(160, 255);
        input.Asked.Clear();
        monitor.Poll();
        if (input.AskedFor() != "255")throw new Exception("Bindings: the left-shift code is not a binding");
        monitor.Configure(1, 259);
        input.Asked.Clear();
        monitor.Poll();
        if (input.AskedFor() != "1" || input.SwapReads == 0)throw new Exception("Bindings: mouse button read");
    }

    static void WheelHook() {
        var input = new FakeInput();
        var log = new List<string>();
        var monitor = Watch(input, 45, 36, log);
        if (input.Installs != 0 || input.Wheel != null)throw new Exception("Wheel: a default setup has no hook");
        if (!monitor.Configure(256, 36) || input.Installs != 1 || input.Wheel == null)
            throw new Exception("Wheel: a wheel binding installs the hook");
        monitor.Configure(257, 256);
        monitor.Configure(36, 258);
        if (input.Installs != 1 || input.Removals != 0)throw new Exception("Wheel: the hook is installed once");
        monitor.Configure(45, 36);
        if (input.Wheel != null || input.Removals != 1)throw new Exception("Wheel: unbinding removes the hook");
        monitor.Configure(45, 259);
        if (input.Wheel == null || input.Installs != 2)throw new Exception("Wheel: installed again");
        input.Asked.Clear();
        input.Wheel(259, new Point(1, 2));
        Expect(log, "259/0", "wheel right");
        input.Wheel(256, new Point(1, 2));
        input.Wheel(257, new Point(1, 2));
        Expect(log, "", "other wheel directions");
        input.Down.UnionWith(new[] {162, 160});
        input.Wheel(259, new Point(1, 2));
        Expect(log, "259/6", "wheel with modifiers");
        input.Down.Clear();
        input.Asked.Clear();
        input.Wheel(256, Point.Empty);
        if (input.Asked.Count != 0)throw new Exception("Wheel: an unbound direction reads no state");
        monitor.Poll();
        if (input.AskedFor() != "45")throw new Exception("Wheel: a wheel binding is not polled");
        monitor.Dispose();
        if (input.Wheel != null || input.Removals != 2)throw new Exception("Wheel: dispose removes the hook");
        input = new FakeInput {FailHook = true};
        monitor = Watch(input, 45, 36, log);
        if (monitor.Configure(256, 36) || input.Wheel != null)
            throw new Exception("Wheel: a failed install is reported");
        Press(input, monitor, 36);
        Expect(log, "36/0", "polling continues without the hook");
        input.FailHook = false;
        if (!monitor.Configure(256, 36) || input.Wheel == null)throw new Exception("Wheel: install retried");
        monitor.Configure(0, 0);
        if (input.Wheel != null)throw new Exception("Wheel: cleared bindings remove the hook");
    }

    static int ReadTap(NamedPipeClientStream p) {
        var head = Read(p, 2);
        int count = head[0] * 256 + head[1];
        return int.Parse(Encoding.Unicode.GetString(Read(p, count)));
    }

    static byte[] Read(Stream p, int n) {
        var b = new byte[n];
        int offset = 0;
        while (offset < n) {
            var a = p.BeginRead(b, offset, n - offset, null, null);
            using (a.AsyncWaitHandle) {
                if (!a.AsyncWaitHandle.WaitOne(3000))throw new Exception("Read timeout");
                int got = p.EndRead(a);
                if (got == 0)throw new Exception("EOF");
                offset += got;
            }
        }
        return b;
    }

    static void DiagnosticsHandoff() {
        string priorSession = Diag.Session;
        LogLevel priorLevel = Diag.Level;
        string session = Guid.NewGuid().ToString("N"),
            folder = Path.Combine(Path.GetTempPath(), "autocat-diagnostics-" + Guid.NewGuid().ToString("N"));
        try {
            Diag.Session = session;
            Diag.Level = LogLevel.Info;
            string[] command = DiagnosticSession.Handshake().Split('|');
            if (command.Length != 4 || command[0] != "LOG" || command[1] != session ||
                    Encoding.UTF8.GetString(Convert.FromBase64String(command[3])) != DiagnosticSession.Folder)
                throw new Exception("Diagnostic handoff command");
            using (var menu = new DiagnosticLog("menu"))
                using (var game = new DiagnosticLog("game")) {
                    menu.Configure(folder, session, LogLevel.Info);
                    game.Configure(folder, session, LogLevel.Info);
                    menu.Write(LogLevel.Info, "Integration", "menu.record", "menu marker");
                    game.Write(LogLevel.Info, "Integration", "game.record", "game marker");
                    menu.Dispose();
                    game.Dispose();
                    if (!menu.WaitForDrain(3000) || !game.WaitForDrain(3000))
                        throw new Exception("Diagnostic drain");
                }
            string path = Path.Combine(folder, "diagnostic-" + session + ".log");
            if (!File.Exists(path) || Directory.GetFiles(folder, "diagnostic-*.log").Length != 1)
                throw new Exception("Shared diagnostic session file");
            string text = File.ReadAllText(path);
            if (text.IndexOf("[menu] [Integration] menu.record", StringComparison.Ordinal) < 0 ||
                    text.IndexOf("[game] [Integration] game.record", StringComparison.Ordinal) < 0)
                throw new Exception("Shared diagnostic records");
        } finally {
            Diag.Session = priorSession;
            Diag.Level = priorLevel;
            if (Directory.Exists(folder))Directory.Delete(folder, true);
        }
    }

    static void ReconnectWait() {
        using (var p = new NamedPipeClientStream(".", "autocat-test-" + Guid.NewGuid().ToString("N"),
                PipeDirection.InOut, PipeOptions.Asynchronous)) {
            var timer = Stopwatch.StartNew();
            if (BridgeClient.ConnectUntil(p, 6000, () => timer.ElapsedMilliseconds >= 400) ||
                    timer.ElapsedMilliseconds > 2000)
                throw new Exception("Reconnect wait must end promptly once closing");
            timer.Restart();
            bool timedOut = false;
            try {
                BridgeClient.ConnectUntil(p, 600, () => false);
            } catch (TimeoutException) {
                timedOut = true;
            }
            if (!timedOut || timer.ElapsedMilliseconds < 500)
                throw new Exception("Reconnect wait must still time out when not closing");
        }
    }

    static void Transport() {
        string name = "autocat-test-" + Guid.NewGuid().ToString("N");
        using (var engine = new TapEngine(name)) {
            engine.Rate = 100;
            engine.Enabled = true;
            engine.Observe(0, 0);
            engine.Start();
            using (var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                p.Connect(3000);
                int total = 0;
                for (int i = 0; i < 4; i++)total += ReadTap(p);
                if (total < 25 || total > 100)throw new Exception("Rate");
                engine.Enabled = false;
                ReadTap(p);
                if (ReadTap(p) != 0 || ReadTap(p) != 0)throw new Exception("Pause");
                engine.Enabled = true;
                engine.Observe(100, 100);
                if (ReadTap(p) == 0 && ReadTap(p) == 0)throw new Exception("Resume");
                for (int i = 0; i < 25; i++)ReadTap(p);
                if (ReadTap(p) != 0)throw new Exception("Stale counter must stop taps");
                engine.Dispose();
            }
        }
    }
}

// Stands in for the keyboard, mouse and wheel hook, and records what was asked of them.
sealed class FakeInput : IInputSource {
    public readonly HashSet<int> Down = new HashSet<int>();
    public readonly List<int> Asked = new List<int>();
    public bool Swapped, FailHook, FailRemoval;
    public Point Cursor;
    public int SwapReads, CursorReads, Installs, Removals;
    public Action<int, Point> Wheel;

    public bool IsDown(int key) {
        Asked.Add(key);
        return Down.Contains(key);
    }

    public bool ButtonsSwapped {
        get {
            SwapReads++;
            return Swapped;
        }
    }

    public Point CursorPosition {
        get {
            CursorReads++;
            return Cursor;
        }
    }

    public bool WatchWheel(Action<int, Point> handler) {
        if (handler == null) {
            if (FailRemoval)throw new InvalidOperationException("hook");
            if (Wheel != null)Removals++;
            Wheel = null;
            return true;
        }
        if (FailHook)return false;
        if (Wheel == null)Installs++;
        Wheel = handler;
        return true;
    }

    public string AskedFor() {
        var keys = new List<int>();
        foreach (int key in Asked)if (!keys.Contains(key))keys.Add(key);
        keys.Sort();
        return String.Join(",", keys.ConvertAll(k => k.ToString()).ToArray());
    }
}

// Stands in for the notification area icon, and records what the menu asked of it.
sealed class FakeTray : ITray {
    readonly Action toggle, unload;
    public string ToolTip { get; set; }
    public string ToggleText { get; set; }
    public Font MenuFont { get; set; }
    public int Disposed, DisplayChanges;

    public FakeTray(Action onToggle, Action onUnload) {
        toggle = onToggle;
        unload = onUnload;
    }

    public void Click() { toggle(); }
    public void ChooseToggle() { toggle(); }
    public void ChooseUnload() { unload(); }
    public void DisplayChanged() { DisplayChanges++; }
    public void Dispose() { Disposed++; }
}