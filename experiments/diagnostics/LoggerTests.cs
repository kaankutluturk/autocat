using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.RegularExpressions;
using AutoCat.Diagnostics;

class LoggerTests {
    static string root;
    static void Assert(bool ok, string message) { if (!ok)throw new Exception(message); }

    static string PathFor(string folder, string id) {
        return Path.Combine(folder, "diagnostic-" + id + ".log");
    }

    static int Main(string[] args) {
        try {
            if (args.Length > 0 && args[0] == "--writer") {
                using (var log = new DiagnosticLog(args[3])) {
                    log.Configure(args[1], args[2], LogLevel.Trace);
                    for (int i = 0; i < 100; i++) {
                        log.Values(LogLevel.Info, "Test", "cross-process", "value={0}", i);
                        if (i % 10 == 0)Thread.Sleep(10);
                    }
                    log.Dispose();
                    Assert(log.WaitForDrain(5000), "child drain");
                }
                return 0;
            }
            root = args[0];
            Directory.CreateDirectory(root);
            FormatAndLimits();
            Redaction();
            Backpressure();
            CrossProcess();
            RotationRetention();
            FailureRecovery();
            Benchmarks();
            Console.WriteLine("PASS all diagnostic logger tests");
            return 0;
        } catch (Exception e) {
            Console.WriteLine("FAIL " + e);
            return 1;
        }
    }

    static void FormatAndLimits() {
        string dir = Path.Combine(root, "format"), id = Guid.NewGuid().ToString("N");
        using (var log = new DiagnosticLog("test")) {
            log.Configure(dir, id, LogLevel.Info);
            log.Write(LogLevel.Debug, "Test", "hidden", "must not appear");
            log.Values(LogLevel.Info, "Test", "values", "old={0} new={1}", 42, 0);
            log.Write(LogLevel.Info, "Test", "redaction",
                "path=C:\\Users\\SecretName\\file.txt\naddress=10.20.30.40 steam=76561198000000000 email=example@example.com");
            for (int i = 0; i < 10000; i++)
                log.Fault("Test", "failure", new InvalidOperationException("harmless synthetic failure"));
            log.Dispose();
            Assert(log.WaitForDrain(5000), "format drain");
        }
        string text = File.ReadAllText(PathFor(dir, id));
        Assert(!text.Contains("must not appear") && text.Contains("old=42 new=0"), "level/numeric formatting");
        Assert(!text.Contains("SecretName") && !text.Contains("76561198000000000") &&
            !text.Contains("example@example.com") && !text.Contains("10.20.30.40"), "redaction");
        Assert(text.Contains("suppressed=9999") && text.Contains("InvalidOperationException"),
            "repeat summary/first error");
        foreach (var line in File.ReadAllLines(PathFor(dir, id)))
            Assert(Regex.IsMatch(line, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[(INFO|WARN|ERROR)\]"),
                "single-line structured timestamp");
        Console.WriteLine(
            "PASS levels, millisecond timestamps, primitive formatting, redaction, initial error + repeated count, shutdown drain");
    }

    // Each row: input, expected output. A path is swallowed to the end of its line unless the rest of the line
    // is one of the fixed endings.
    static void Redaction() {
        string[][] cases = {
            new[] {@"Access to the path 'C:\Users\SecretName\file.txt' is denied.",
                "Access to the path '<path>' is denied."},
            new[] {@"Could not find '\\SecretHost\share\Secret Name\f.txt'.", "Could not find '<path>'."},
            new[] {@"Access to the path 'C:\Users\O'SecretName\Secret' photos.zip' is denied.",
                "Access to the path '<path>' is denied."},
            new[] {@"The process cannot access the file 'C:\Users\SecretName\a b.dll' because it is being used by another process.",
                "The process cannot access the file '<path>' because it is being used by another process."},
            new[] {@"The file 'C:\Users\SecretName\settings.xml' already exists.",
                "The file '<path>' already exists."},
            new[] {@"Could not load file or assembly 'C:\Users\SecretName\a.dll' or one of its dependencies. The system cannot find the file specified.",
                "Could not load file or assembly '<path>' or one of its dependencies. The system cannot find the file specified."},
            new[] {"a 'C:\\Users\\SecretName\\x' failed\nFile name: 'C:\\Users\\SecretName\\x'",
                "a '<path>\nFile name: '<path>'"},
            new[] {@"   at X in C:\src\SecretName\File.cs:line 42", "   at X in <path>:line 42"},
            new[] {"\"C:\\Users\\SecretName\\a b.txt\" is locked", "\"<path>\" is locked"},
            new[] {@"Could not open 'C:\Users\SecretName\My Docs\settings.xml': access denied",
                "Could not open '<path>"},
            new[] {@"open C:\Program Files\SecretName\a b.dll: access denied", "open <path>"},
            new[] {@"\\SecretHost\share\Secret Name\f.txt: network path not found", "<path>"},
            new[] {@"Could not copy 'C:\Users\SecretName\a' to 'D:\SecretName\b': disk full",
                "Could not copy '<path>"},
            new[] {@"C:\Users\SecretName;D:\SecretName\x: bad", "<path>"},
            new[] {@"path=C:\Users\SecretName\file.txt and SecretName tail", "path=<path>"},
            new[] {@"Could not open 'C:\Users\SecretName\My Docs\set", "Could not open '<path>"},
            new[] {@"Could not open 'C:\Users\O' SecretName\file.txt", "Could not open '<path>"},
            new[] {@"Could not open 'C:\Users\Name\Friends' Secret [truncated]", "Could not open '<path>"},
            new[] {@"The process cannot access the file 'C:\Users\SecretName\a.dll' because it is in use.",
                "The process cannot access the file '<path>"},
            new[] {"file:///C:/Users/SecretName/x", "fil<path>"},
            new[] {"C:/Users/SecretName/x.log: steam=76561198000000000 ip=10.20.30.40 mail=example@example.com",
                "<path>"},
            new[] {@"Could not open 'C:\Users\Bob\Friends' Secret", "Could not open '<path>"},
            new[] {@"Could not open 'C:\Users\Bob\Parents'. Secret.txt", "Could not open '<path>"},
            new[] {@"Could not open 'C:\Users\Bob\Rock 'n' SecretRoll.mp3", "Could not open '<path>"},
            new[] {"error=\"Could not find file 'C:\\Users\\Bob\\Friends' Secret.txt'\"",
                "error=\"Could not find file '<path>\""},
            new[] {@"Could not find file 'C:\Users\Bob\Friends' Secret.txt'!", "Could not find file '<path>"},
            new[] {@"path='C:\Users\Bob\Friends' Secret.txt'|next", "path='<path>|next"},
            new[] {@"{file='C:\Users\Bob\Friends' Secret.txt'}", "{file='<path>"},
            new[] {"Could not find 'C:\\Users\\Bob\\x'.\nFile name: 'C:\\Users\\Bob\\Friends' Secret",
                "Could not find '<path>'.\nFile name: '<path>"},
            new[] {@"Loaded C:\Users\Bob\file.txt:SecretStream", "Loaded <path>"},
            new[] {@"Could not move C:\Users\Bob\x.txt: target Documents\Secret\x.txt exists",
                "Could not move <path>"},
            new[] {@"C:\Users\Bob\a;D:Secret\b", "<path>"},
            new[] {"GET http://localhost:8080/users/SecretName failed", "GET htt<path>"}
        };
        foreach (var row in cases) {
            string actual = DiagnosticLog.Redact(row[0]);
            Assert(!actual.Contains("Secret"), "path not redacted: " + actual);
            Assert(actual == row[1], "redaction changed the message: expected [" + row[1] + "] got [" + actual + "]");
        }
        // The earlier rule swallowed every path to the end of its line. Whatever the input, the current rule may
        // only add one fixed ending to what that rule produced.
        var swallow = new Regex(@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n""<>|]*");
        string[] endings = {
            "'", "'.", "' is denied.", "' because it is being used by another process.", "' already exists.",
            "' or one of its dependencies. The system cannot find the file specified."
        };
        string[] tokens = {
            @"C:\", "c:/", @"\\host\share\", @"D:\Users\", "D:", "http://localhost:8080/", "Bob", "Friends", "x.txt",
            " ", " ", "'", "'", "\"", "|", "<", ">", ":", ";", ".", ",", "!", "}", "=", "\\", "/", "-", ")",
            "'n'", "' ", "'.", ": ", ":line 42", ":line ", " is denied.", "' is denied.", "' already exists.",
            "' because it is being used by another process.", " because", " exists", "[truncated]",
            "' or one of its dependencies. The system cannot find the file specified.", "File name: '", "in "
        };
        // A third of the lines are built around the case that matters most: a path, a closing quote, any punctuation
        // after it, then the end of the message or more text. Purely random token sequences reach that shape too
        // rarely to catch a rule that treats some punctuation after the quote as the end of a path.
        string[] starts = {@"C:\", "c:/", @"\\host\share\", @"D:\Users\"};
        string[] names = {"Bob", "Friends", "x.txt", " ", "Secret", "n", "My Docs", "a b"};
        string[] punctuation = {
            ".", ",", ";", ":", "!", "?", ")", "]", "}", "-", "\"", "|", "<", ">", "/", "\\", "=", "'", "%", "&"
        };
        string[] openings = {"", "", "Could not open '", "path='", "error=\"Could not find file '", "{file='", "in "};
        string[] tails = {"", "", "", " Secret.txt", " is denied.", " because", ":line 42", " [truncated]"};
        var random = new Random(20261006);
        var timer = Stopwatch.StartNew();
        const int generatedLines = 50000;
        for (int i = 0; i < generatedLines; i++) {
            var line = new System.Text.StringBuilder();
            if (i % 3 == 0) {
                line.Append(openings[random.Next(openings.Length)]).Append(starts[random.Next(starts.Length)]);
                for (int count = 1 + random.Next(3); count > 0; count--)line.Append(names[random.Next(names.Length)]);
                line.Append('\'');
                for (int count = random.Next(3); count > 0; count--)line.Append(punctuation[random.Next(punctuation.Length)]);
                line.Append(tails[random.Next(tails.Length)]);
            } else
                for (int count = 1 + random.Next(12); count > 0; count--)line.Append(tokens[random.Next(tokens.Length)]);
            string input = line.ToString(), before = swallow.Replace(input, "<path>"),
                after = DiagnosticLog.Redact(input);
            bool allowed = after == before ||
                (after.StartsWith(before, StringComparison.Ordinal) &&
                    Regex.IsMatch(after.Substring(before.Length), @"^:line \d+$"));
            foreach (string ending in endings)
                if (after == before + ending)allowed = true;
            Assert(allowed, "redaction reveals more than swallowing the line: [" + input + "] gave [" + after + "]");
        }
        Console.WriteLine(
            "PASS paths are swallowed to the end of the line; only fixed message endings survive (" + generatedLines +
            " generated lines, " + timer.ElapsedMilliseconds + " ms)");
    }

    static void Backpressure() {
        string dir = Path.Combine(root, "pressure"), id = Guid.NewGuid().ToString("N");
        using (var log = new DiagnosticLog("test", 64)) {
            for (int i = 0; i < 100000; i++)log.Write(LogLevel.Info, "Test", "flood", "normal");
            log.Write(LogLevel.Warn, "Test", "reserved.warn", "preserved warning");
            log.Write(LogLevel.Error, "Test", "reserved.error", "preserved error");
            Assert(log.PeakQueued <= 64, "bounded queue");
            log.Configure(dir, id, LogLevel.Trace);
            log.Dispose();
            Assert(log.WaitForDrain(5000), "pressure drain");
        }
        string text = File.ReadAllText(PathFor(dir, id));
        Assert(text.Contains("preserved warning") && text.Contains("preserved error") &&
            text.Contains("backpressure"), "priority/backlog accounting");
        Console.WriteLine("PASS bounded backlog, reserved WARN/ERROR capacity, reported suppression");
    }

    static void CrossProcess() {
        string dir = Path.Combine(root, "cross"), id = Guid.NewGuid().ToString("N");
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        var a = Process.Start(new ProcessStartInfo(exe, "--writer \"" + dir + "\" " + id + " menu") {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        var b = Process.Start(new ProcessStartInfo(exe, "--writer \"" + dir + "\" " + id + " game") {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert(a.WaitForExit(10000) && b.WaitForExit(10000) && a.ExitCode == 0 && b.ExitCode == 0,
            "writers exit");
        var lines = File.ReadAllLines(PathFor(dir, id));
        Assert(lines.Length == 200 && lines.Count(x => x.Contains("[menu]")) == 100 &&
            lines.Count(x => x.Contains("[game]")) == 100, "cross-process lines intact");
        Console.WriteLine("PASS two real processes share one session without torn/missing lines");
    }

    static void RotationRetention() {
        string dir = Path.Combine(root, "rotation"), id = Guid.NewGuid().ToString("N");
        using (var log = new DiagnosticLog("test", 512, 2048)) {
            log.Configure(dir, id, LogLevel.Info);
            for (int batch = 0; batch < 6; batch++) {
                for (int i = 0; i < 30; i++)log.Write(LogLevel.Info, "Test", "rotate", new string('x', 100));
                Thread.Sleep(550);
            }
            log.Dispose();
            Assert(log.WaitForDrain(5000), "rotate drain");
        }
        Assert(File.Exists(PathFor(dir, id) + ".1") &&
            Directory.GetFiles(dir, "*.log*").Count(x => !x.EndsWith("active")) <= 3, "rotation bounded");
        dir = Path.Combine(root, "retention");
        Directory.CreateDirectory(dir);
        string heldId = Guid.NewGuid().ToString("N");
        string heldPath = PathFor(dir, heldId);
        File.WriteAllText(heldPath, "active remains");
        File.SetLastWriteTimeUtc(heldPath, DateTime.UtcNow.AddDays(-30));
        using (var lease = new FileStream(heldPath + ".game.active", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read)) {
            for (int i = 0; i < 12; i++) {
                string file = PathFor(dir, Guid.NewGuid().ToString("N"));
                File.WriteAllText(file, "old");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-i - 1));
            }
            id = Guid.NewGuid().ToString("N");
            using (var log = new DiagnosticLog("menu")) {
                log.Configure(dir, id, LogLevel.Info);
                log.Write(LogLevel.Info, "Test", "retention", "new");
                log.Dispose();
                Assert(log.WaitForDrain(5000), "retention drain");
            }
            Assert(File.Exists(heldPath), "never delete active session");
            Assert(Directory.GetFiles(dir, "*.log").Length <= 9, "retained sessions bounded except active");
        }
        Console.WriteLine("PASS rotation/oldest-first retention; active session protected");
    }

    static void FailureRecovery() {
        string dir = Path.Combine(root, "blocked");
        File.WriteAllText(dir, "blocks directory creation");
        string id = Guid.NewGuid().ToString("N");
        using (var log = new DiagnosticLog("test")) {
            log.Configure(dir, id, LogLevel.Info);
            log.Write(LogLevel.Error, "Test", "sink.failed", "first event");
            Thread.Sleep(700);
            Assert(log.Failures > 0, "sink failure observable");
            File.Delete(dir);
            log.Write(LogLevel.Info, "Test", "after.recovery", "second event");
            log.Dispose();
            Assert(log.WaitForDrain(5000), "recovery drain");
        }
        Assert(File.ReadAllText(PathFor(dir, id)).Contains("sink.recovered"), "lost batch/recovery reported");
        Console.WriteLine("PASS unwritable sink never throws to producer; recovery reports losses");
    }

    static void Benchmarks() {
        foreach (int level in new[] {-1, 2, 1, 0}) {
            GC.Collect();
            long memory = GC.GetTotalMemory(true);
            var process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            var log = level < 0 ? null : new DiagnosticLog("test");
            if (log != null)
                log.Configure(Path.Combine(root, "bench" + level), Guid.NewGuid().ToString("N"),
                    (LogLevel)level);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1000000; i++) {
                if (log != null) {
                    if (i % 3000 == 0)log.Values(LogLevel.Info, "Polling", "aggregate", "polls={0}", 3000);
                    if (i % 500 == 0 && log.Enabled(LogLevel.Debug))
                        log.Values(LogLevel.Debug, "Polling", "details", "iteration={0}", i);
                    if (i % 100 == 0 && log.Enabled(LogLevel.Trace))
                        log.Values(LogLevel.Trace, "Polling", "sample", "iteration={0}", i);
                }
            }
            watch.Stop();
            if (log != null) {
                log.Dispose();
                Assert(log.WaitForDrain(10000), "benchmark drain");
            }
            process.Refresh();
            Console.WriteLine("BENCH synthetic 1M poll gates level=" +
                (level < 0 ? "disabled" : ((LogLevel)level).ToString()) + " producerMs=" +
                watch.Elapsed.TotalMilliseconds.ToString("F3") + " cpuMs=" +
                (process.TotalProcessorTime - cpu).TotalMilliseconds.ToString("F3") + " retainedBytes=" +
                (GC.GetTotalMemory(true) - memory) + " peakQueue=" + (log == null ? 0 : log.PeakQueued));
        }
    }
}
