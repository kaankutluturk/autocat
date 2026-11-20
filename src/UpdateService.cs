using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Diagnostics;

static class UpdateService {
    // The signed metadata lives in the public repository, so a GitHub release holds only autocat.exe.
    internal const string Channel = "https://raw.githubusercontent.com/kaankutluturk/autocat/master/update/";
    internal static bool GameRunning() {
        foreach (var process in Process.GetProcessesByName("BongoCat")) {
            process.Dispose();
            return true;
        }
        return false;
    }
    internal static ReleaseManifest Check() {
        if (!ReleaseTrust.Ready)return null;
        CleanupCompleted();
        byte[] manifest = Download(Channel + "manifest.txt", 4096, null);
        byte[] signature = Download(Channel + "manifest.sig", 384, null);
        var release = UpdateEnvelope.Verify(manifest, signature, ReleaseTrust.PublicKeyXml,
            UpdateState.Load().HighestSequence, Assembly.GetExecutingAssembly().GetName().Version);
        UpdateState.Observe(manifest, release);
        return release.IsNewer ? release : null;
    }
    internal static string Stage(ReleaseManifest offered, Func<bool> cancelled) {
        if (!ReleaseTrust.Ready)throw new InvalidOperationException("Update verification is not configured.");
        // Re-verify so the channel cannot swap the offer.
        byte[] manifest = Download(Channel + "manifest.txt", 4096, cancelled);
        byte[] signature = Download(Channel + "manifest.sig", 384, cancelled);
        var release = UpdateEnvelope.Verify(manifest, signature, ReleaseTrust.PublicKeyXml,
            UpdateState.Load().HighestSequence, Assembly.GetExecutingAssembly().GetName().Version);
        if (!release.IsNewer || release.Sequence != offered.Sequence || release.Sha256 != offered.Sha256)
            throw new InvalidDataException("The available release changed. Check for updates again.");
        UpdateState.Observe(manifest, release);
        Directory.CreateDirectory(UpdateState.Root);
        string folder = Path.Combine(UpdateState.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            File.WriteAllBytes(Path.Combine(folder, "manifest.txt"), manifest);
            File.WriteAllBytes(Path.Combine(folder, "manifest.sig"), signature);
            byte[] payload = Download(release.PayloadUrl, release.PayloadBytes, cancelled);
            string path = Path.Combine(folder, "autocat.exe.part");
            File.WriteAllBytes(path, payload);
            UpdateEnvelope.VerifyPayload(release, path);
            File.Move(path, Path.Combine(folder, "autocat.exe"));
            if (cancelled())throw new OperationCanceledException();
            return folder;
        } catch {
            foreach (string file in Directory.GetFiles(folder))File.Delete(file);
            Directory.Delete(folder);
            throw;
        }
    }
    internal static bool Allowed(Uri uri) {
        return uri != null && uri.Scheme == "https" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" ||
             uri.Host == "raw.githubusercontent.com");
    }
    internal static void CleanupCompleted() {
        UpdateState.ValidatePath();
        if (!Directory.Exists(UpdateState.Root))return;
        string selfFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        foreach (string folder in Directory.GetDirectories(UpdateState.Root)) {
            Guid operation;
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out operation) ||
                String.Equals(folder, selfFolder, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)continue;
            string result = Path.Combine(folder, "apply-result");
            if (!File.Exists(result) || new FileInfo(result).Length > 32 ||
                File.GetLastWriteTimeUtc(result) > DateTime.UtcNow.AddDays(-1))continue;
            try {
                if (File.ReadAllText(result) != "healthy")continue;
                string[] owned = {"manifest.txt", "manifest.sig", "autocat.exe", "apply-helper.exe", "apply-request.xml", "apply-ready", "startup-health", "apply-result"};
                string[] files = Directory.GetFiles(folder);
                bool recognized = Directory.GetDirectories(folder).Length == 0;
                foreach (string file in files)
                    if (Array.IndexOf(owned, Path.GetFileName(file)) < 0 ||
                        (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)recognized = false;
                if (!recognized)continue;
                foreach (string file in files)File.Delete(file);
                Directory.Delete(folder);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    internal static byte[] Download(string address, long maximum, Func<bool> cancelled) {
        if (maximum < 1 || maximum > 20 * 1024 * 1024)throw new InvalidDataException("Update exceeds the size limit.");
        UpdateCheck.UseTls12();
        var uri = new Uri(address);
        var deadline = Stopwatch.StartNew();
        for (int redirects = 0; redirects <= 5; redirects++) {
            if (!Allowed(uri))throw new InvalidDataException("Update address was rejected.");
            if (cancelled != null && cancelled())throw new OperationCanceledException();
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.MaximumResponseHeadersLength = 16;
            using (var response = (HttpWebResponse)request.GetResponse()) {
                int status = (int)response.StatusCode;
                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308) {
                    string location = response.Headers["Location"];
                    if (String.IsNullOrEmpty(location))throw new InvalidDataException("Update redirect is invalid.");
                    uri = new Uri(uri, location);
                    continue;
                }
                if (status != 200 || response.ContentLength > maximum)
                    throw new InvalidDataException("Update response was rejected.");
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream()) {
                    byte[] buffer = new byte[32768];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                        if (deadline.Elapsed.TotalSeconds > 60 || cancelled != null && cancelled())
                            throw new OperationCanceledException();
                        if (output.Length + count > maximum)throw new InvalidDataException("Update exceeds the size limit.");
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
            }
        }
        throw new InvalidDataException("Too many update redirects.");
    }
}
