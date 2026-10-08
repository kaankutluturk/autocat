using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using AutoCat.Diagnostics;

// Offline stubs for unrelated Worker observations; only the production
// diagnostics startup/configuration methods are exercised.
namespace UnityEngine {
public static class Time {
    public static float realtimeSinceStartup;
}
}

namespace AutocatBridge {
public sealed partial class Worker {
    string problem = "";
    bool running, emoting;
    float emoteBudget, tapScale, frameAverage, framePeak;
    int emoteRate, emoteCalls, emoteTotal, trades;
    static uint GetCurrentProcessId() { return (uint)System.Diagnostics.Process.GetCurrentProcess().Id; }
    static object Field(object target, string name) { throw new NotSupportedException(); }
    static object Call(object target, string name) { throw new NotSupportedException(); }

    public void TestSession(string folder, string session) {
        ConfigureDiagnostics(session, (int)LogLevel.Info, Convert.ToBase64String(Encoding.UTF8.GetBytes(folder)));
        DiagnosticStartup();
        diagnostics.Dispose();
        if (!diagnostics.WaitForDrain(5000))throw new Exception("Worker diagnostics did not drain");
    }
}
}

class WorkerDiagnosticsTests {
    static void Main(string[] args) {
        string folder = Path.GetFullPath(args[0]);
        string session = Guid.NewGuid().ToString("N");
        new AutocatBridge.Worker().TestSession(folder, session);
        string expected = "runtime=" +
            Assembly.GetExecutingAssembly().GetName().Name.Replace("autocat.runtime.", "");
        string[] lines = File.ReadAllLines(Path.Combine(folder, "diagnostic-" + session + ".log"));
        bool linked = false, initialized = false;
        foreach (string line in lines) {
            if (line.Contains("Session") && line.Contains("linked")) {
                if (!line.Contains(expected + " pid="))
                    throw new Exception("Session.linked runtime label is stale: " + line);
                linked = true;
            }
            if (line.Contains("Runtime") && line.Contains("initialized")) {
                if (!line.Contains(expected + " source="))
                    throw new Exception("Runtime.initialized identity mismatch: " + line);
                initialized = true;
            }
        }
        if (!linked || !initialized)throw new Exception("Missing runtime diagnostic identity records");
        Console.WriteLine("PASS: startup and Session.linked labels follow loaded assembly identity (" +
            expected + ")");
    }
}
