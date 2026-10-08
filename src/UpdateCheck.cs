using System;
using System.Net;
using System.Threading;

// One GET to the fixed releases/latest address, once per launch. GitHub answers with a redirect to the tag page of the
// newest release; only the Location header is read, it is reduced to three small integers, and nothing else of the
// reply is kept, logged, opened or run. Nothing is downloaded or installed.
static class UpdateCheck {
    internal const string ReleasePage = "https://github.com/kaankutluturk/autocat/releases/latest";
    const string TagPrefix = "https://github.com/kaankutluturk/autocat/releases/tag/";

    internal sealed class Reply {
        public int Status;
        public string Location;
    }

    internal enum Outcome {
        Newer,
        Current,
        Unreachable,
        Rejected
    }

    // Tests replace these; a normal launch uses the network read and the default browser.
    internal static readonly Func<Reply> Network = Get;
    internal static Func<Reply> Fetch = Network;
    internal static Action<string> Open = address => System.Diagnostics.Process.Start(address);
    internal static int TimeoutMs = 5000;
    static volatile HttpWebRequest pending;

    // TLS 1.2 is the one place the protocol is chosen: the default of a .NET 4.0 program is a protocol GitHub no longer
    // accepts. Certificate validation is left alone, and this program makes no other HTTPS request.
    internal const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;

    internal static void UseTls12() {
        ServicePointManager.SecurityProtocol = Tls12;
    }

    static Reply Get() {
        UseTls12();
        return Send(new Uri(ReleasePage));
    }

    // A plain GET with nothing added: no proxy (so no proxy discovery), credentials, cookies, user agent, other
    // headers or redirects. If the connection drops before a reply the HTTP stack may repeat the request once.
    internal static HttpWebRequest Create(Uri address) {
        var request = (HttpWebRequest)WebRequest.Create(address);
        request.Method = "GET";
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.KeepAlive = false;
        request.Timeout = TimeoutMs;
        request.MaximumResponseHeadersLength = 8;
        return request;
    }

    internal static Reply Send(Uri address) {
        var request = Create(address);
        pending = request;
        try {
            var call = request.BeginGetResponse(null, null);
            if (!call.AsyncWaitHandle.WaitOne(TimeoutMs)) {
                request.Abort();
                return null;
            }
            using (var response = (HttpWebResponse)request.EndGetResponse(call))
                return new Reply {Status = (int)response.StatusCode, Location = response.Headers["Location"]};
        } finally {
            pending = null;
        }
    }

    internal static void Cancel() {
        var request = pending;
        try {
            if (request != null)request.Abort();
        } catch (Exception) {
        }
    }

    // The newest release's version, or null unless the reply is a redirect to exactly <TagPrefix>vMAJOR.MINOR.PATCH.
    internal static Version Parse(Reply reply) {
        int status = reply.Status;
        if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308)return null;
        string location = reply.Location;
        if (location == null || location.Length > 100 || !location.StartsWith(TagPrefix, StringComparison.Ordinal))
            return null;
        string tag = location.Substring(TagPrefix.Length);
        if (tag.StartsWith("v", StringComparison.Ordinal))tag = tag.Substring(1);
        string[] parts = tag.Split('.');
        if (parts.Length != 3)return null;
        int major = Number(parts[0]), minor = Number(parts[1]), patch = Number(parts[2]);
        if (major < 0 || minor < 0 || patch < 0)return null;
        return new Version(major, minor, patch);
    }

    // One to four ASCII digits without a leading zero, else -1.
    static int Number(string text) {
        if (text.Length < 1 || text.Length > 4 || (text.Length > 1 && text[0] == '0'))return -1;
        int value = 0;
        foreach (char c in text) {
            if (c < '0' || c > '9')return -1;
            value = value * 10 + (c - '0');
        }
        return value;
    }

    // The log line for an outcome: fixed words and the validated version only.
    internal static string Describe(Outcome outcome, Version latest) {
        if (outcome == Outcome.Newer)return "result=newer latest=" + latest.ToString(3);
        if (outcome == Outcome.Current)return "result=none";
        return "result=failed reason=" + (outcome == Outcome.Rejected ? "response" : "network");
    }

    // Never throws; anything unexpected is a failed check.
    internal static Outcome Check(Func<Reply> fetch, Version running, out Version latest) {
        latest = null;
        Reply reply;
        try {
            reply = fetch();
        } catch (Exception) {
            return Outcome.Unreachable;
        }
        if (reply == null)return Outcome.Unreachable;
        Version tag = Parse(reply);
        if (tag == null)return Outcome.Rejected;
        if (tag.CompareTo(new Version(running.Major, running.Minor, Math.Max(running.Build, 0))) <= 0)
            return Outcome.Current;
        latest = tag;
        return Outcome.Newer;
    }

    internal static Thread Start(Func<Reply> fetch, Version running, Action<Outcome, Version> done) {
        var worker = new Thread(() => {
            try {
                Version latest;
                var outcome = Check(fetch, running, out latest);
                done(outcome, latest);
            } catch (Exception) {
            }
        }) {Name = "update-check", IsBackground = true};
        worker.Start();
        return worker;
    }
}
