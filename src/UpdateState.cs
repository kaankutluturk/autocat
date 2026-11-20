using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

sealed class UpdateState {
    internal long HighestSequence;
    internal string Fingerprint = "";
#if AUTOCAT_UPDATE_TESTS
    internal static string RootForTests;
#endif
    internal static string Root {
        get {
#if AUTOCAT_UPDATE_TESTS
            if (RootForTests != null)return RootForTests;
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCat", "updates");
        }
    }
    internal static UpdateState Load() {
        ValidatePath();
        var result = new UpdateState();
        string path = Path.Combine(Root, "seen-release.txt");
        if (!File.Exists(path))return result;
        var info = new FileInfo(path);
        if (info.Length > 128)throw new InvalidDataException("Update history is invalid.");
        var lines = File.ReadAllLines(path);
        if (lines.Length != 2 || !Int64.TryParse(lines[0], out result.HighestSequence) ||
                result.HighestSequence < 1 || lines[1].Length != 64)
            throw new InvalidDataException("Update history is invalid.");
        result.Fingerprint = lines[1];
        return result;
    }
    internal static void Observe(byte[] bytes, ReleaseManifest manifest) {
        using (var mutex = new Mutex(false, "Local\\AutoCat.SignedUpdateHistory")) {
            bool acquired = false;
            try {
                try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)throw new IOException("Another update is recording its verified release.");
                Record(bytes, manifest);
            } finally { if (acquired)mutex.ReleaseMutex(); }
        }
    }
    static void Record(byte[] bytes, ReleaseManifest manifest) {
        ValidatePath();
        string fingerprint;
        using (var sha = SHA256.Create())fingerprint = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        var previous = Load();
        if (manifest.Sequence < previous.HighestSequence ||
            manifest.Sequence == previous.HighestSequence && fingerprint != previous.Fingerprint)
            throw new InvalidDataException("The update conflicts with an already verified release.");
        if (manifest.Sequence == previous.HighestSequence)return;
        Directory.CreateDirectory(Root);
        string target = Path.Combine(Root, "seen-release.txt"), temp = target + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, manifest.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + fingerprint + "\n", Encoding.ASCII);
        try {
            if (File.Exists(target))File.Replace(temp, target, null);
            else File.Move(temp, target);
        } finally { if (File.Exists(temp))File.Delete(temp); }
    }
    internal static void ValidatePath() {
        FileSystemInfo item = new FileInfo(Path.Combine(Root, "seen-release.txt"));
        while (item != null) {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update storage cannot use symbolic links or junctions.");
            item = item is FileInfo ? ((FileInfo)item).Directory : ((DirectoryInfo)item).Parent;
        }
    }
}
