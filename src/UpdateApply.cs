using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

static class UpdateApply {
    internal static int HealthTimeoutMs = 30000;
    internal static Action<string> Checkpoint = delegate { };
    internal static string StageRootForTests;
    internal static string TrustedPublicKeyXml;
    static string healthStage, healthNonce;
    static string activationStage;
    internal static bool IsHealthStartup { get { return activationStage != null; } }
    internal static bool WaitForStartupCommit() {
        string stage = activationStage;
        if (stage == null)return true;
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000) {
            string result = Path.Combine(stage, "apply-result");
            if (File.Exists(result)) {
                return Encoding.UTF8.GetString(ReadBounded(result, 2048)) == "healthy";
            }
            Thread.Sleep(100);
        }
        return false;
    }
    static bool originalRestarted;
    sealed class Plan {
        internal string Target, OriginalHash, Nonce;
        internal int ParentId;
        internal long ParentStarted;
    }
    internal static string StageRoot {
        get { return StageRootForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCat", "updates"); }
    }
    internal static string Quote(string value) {
        if (value == null || value.IndexOf('\0') >= 0)throw new ArgumentException("Invalid argument");
        var b = new StringBuilder("\""); int slashes = 0;
        foreach(char c in value) {
            if(c == '\\') { slashes++; continue; }
            if(c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); }
            else { b.Append('\\', slashes); b.Append(c); }
            slashes = 0;
        }
        b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
    }
    static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }
    static string Hash(string file) {
        using(var stream = File.OpenRead(file))using(var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    static bool Same(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
    static string LocalPath(string path) {
        if(String.IsNullOrEmpty(path) || !Path.IsPathRooted(path))throw new IOException("Update path must be absolute");
        string full = Path.GetFullPath(path);
        if(full.Length < 4 || full[1] != ':' || full[2] != '\\' || full.StartsWith("\\\\"))throw new IOException("Update requires a local drive");
        var drive = new DriveInfo(Path.GetPathRoot(full));
        if(drive.DriveType != DriveType.Fixed)throw new IOException("Update requires a fixed local drive");
        for(string p = full; !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) {
            if((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update cannot use linked paths");
        }
        return full;
    }
    static string Stage(string stage) {
        string full = LocalPath(stage), root = LocalPath(StageRoot);
        Guid id;
        if(!Same(Path.GetDirectoryName(full),root) || Path.GetFileName(full).Length != 32 ||
                !Guid.TryParseExact(Path.GetFileName(full),"N",out id) || !Directory.Exists(full))
            throw new IOException("Invalid update staging directory");
        return full;
    }
    static bool GameRunning() { return Process.GetProcessesByName("BongoCat").Length != 0; }
    static void RequireGameClosed() { if(GameRunning())throw new IOException("Close Bongo Cat before applying this update"); }
    static ReleaseManifest Verify(string stage) {
        byte[] bytes = ReadBounded(Path.Combine(stage,"manifest.txt"),8192);
        byte[] signature = ReadBounded(Path.Combine(stage,"manifest.sig"),1024);
        var manifest = UpdateEnvelope.Verify(bytes,signature,TrustedPublicKeyXml ?? ReleaseTrust.PublicKeyXml,
            UpdateState.Load().HighestSequence,Assembly.GetExecutingAssembly().GetName().Version);
        if(!manifest.IsNewer)throw new IOException("Update is not newer than this executable");
        UpdateEnvelope.VerifyPayload(manifest,LocalPath(Path.Combine(stage,"autocat.exe")));
        var candidate=Assembly.ReflectionOnlyLoadFrom(Path.Combine(stage,"autocat.exe"));
        var version=candidate.GetName().Version;
        if(version.Major != manifest.Version.Major || version.Minor != manifest.Version.Minor ||
                version.Build != manifest.Version.Build || version.Revision != 0)
            throw new IOException("Signed version does not match the executable");
        string runtime="AutoCat.Runtime.autocat.runtime."+manifest.RuntimeId+".dll";
        var resources=candidate.GetManifestResourceNames();
        if(Array.IndexOf(resources,runtime) < 0)throw new IOException("Signed runtime does not match the executable");
        UpdateState.Observe(bytes,manifest);
        return manifest;
    }
    static byte[] ReadBounded(string file,int max) {
        LocalPath(file);
        var f = new FileInfo(file);
        if(!f.Exists || f.Length < 1 || f.Length > max)throw new IOException("Invalid update input length");
        byte[] bytes = File.ReadAllBytes(file);
        if(bytes.Length < 1 || bytes.Length > max)throw new IOException("Invalid update input length");
        return bytes;
    }
    static void WriteNew(string path,string text) {
        using(var stream = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        using(var writer = new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);
    }
    static void CheckBackup(string target) {
        string backup=LocalPath(target+".autocat-backup.exe"), receipt=LocalPath(target+".autocat-backup.receipt");
        if(!File.Exists(backup) && !File.Exists(receipt))return;
        if(!File.Exists(backup) || !File.Exists(receipt))throw new IOException("Unrecognized update backup; manual recovery required");
        string expected=target+"\n"+Hash(backup)+"\n";
        if(Encoding.UTF8.GetString(ReadBounded(receipt,8192)) != expected)throw new IOException("Backup receipt does not match; manual recovery required");
    }
    // A recognized backup is removed a day after the update that made it.
    internal static void PruneBackup(string targetPath,DateTime now) {
        if(IsHealthStartup)return;
        string target=LocalPath(targetPath), backup=target+".autocat-backup.exe", receipt=target+".autocat-backup.receipt";
        if(!File.Exists(receipt) || File.GetLastWriteTimeUtc(receipt) > now.AddDays(-1))return;
        CheckBackup(target);
        File.Delete(backup);
        File.Delete(receipt);
    }
    static void Receipt(string target,string hash) {
        string receipt=target+".autocat-backup.receipt", temp=receipt+".tmp";
        WriteNew(temp,target+"\n"+hash+"\n");
        if(File.Exists(receipt))File.Replace(temp,receipt,null); else File.Move(temp,receipt);
    }
    static void SavePlan(string stage,Plan plan) {
        var b = new StringBuilder();
        using(var w = XmlWriter.Create(b,new XmlWriterSettings {OmitXmlDeclaration=true})) {
            w.WriteStartElement("apply");
            w.WriteAttributeString("target",plan.Target); w.WriteAttributeString("sha256",plan.OriginalHash);
            w.WriteAttributeString("nonce",plan.Nonce); w.WriteAttributeString("pid",plan.ParentId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("started",plan.ParentStarted.ToString(CultureInfo.InvariantCulture)); w.WriteEndElement();
        }
        WriteNew(Path.Combine(stage,"apply-request.xml"),b.ToString());
    }
    static Plan ReadPlan(string stage) {
        var settings = new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
        var bytes = ReadBounded(Path.Combine(stage,"apply-request.xml"),4096);
        using(var r = XmlReader.Create(new MemoryStream(bytes),settings)) {
            r.MoveToContent();
            if(r.Name != "apply")throw new IOException("Invalid apply request");
            var plan = new Plan {Target=LocalPath(r.GetAttribute("target")),OriginalHash=r.GetAttribute("sha256"),
                Nonce=r.GetAttribute("nonce"),ParentId=Int32.Parse(r.GetAttribute("pid"),CultureInfo.InvariantCulture),
                ParentStarted=Int64.Parse(r.GetAttribute("started"),CultureInfo.InvariantCulture)};
            if(plan.OriginalHash == null || plan.OriginalHash.Length != 64 || plan.Nonce == null || plan.Nonce.Length != 64 ||
                    plan.ParentId <= 0 || plan.ParentStarted <= 0)throw new IOException("Invalid apply identity");
            return plan;
        }
    }
    static Process Launch(string exe,string args) {
        return Process.Start(new ProcessStartInfo(exe,args) {UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe),CreateNoWindow=true});
    }
    static string RandomNonce() { var bytes = new byte[32]; using(var r=RandomNumberGenerator.Create())r.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-",""); }
    internal static void Start(string stageDirectory,string targetPath) {
        string stage=Stage(stageDirectory), target=LocalPath(targetPath);
        if(!Same(target,LocalPath(Self)))throw new IOException("Update target must be the running executable");
        RequireGameClosed(); Verify(stage);
        CheckBackup(target);
        var current = Process.GetCurrentProcess();
        var plan = new Plan {Target=target,OriginalHash=Hash(target),Nonce=RandomNonce(),ParentId=current.Id,
            ParentStarted=current.StartTime.ToUniversalTime().Ticks};
        string helper=Path.Combine(stage,"apply-helper.exe");
        File.Copy(target,helper,false);
        if(!Same(Hash(helper),plan.OriginalHash))throw new IOException("Trusted helper copy verification failed");
        SavePlan(stage,plan);
        var process=Launch(helper,"--update-apply "+Quote(stage));
        if(process == null)throw new IOException("Update helper did not start");
        var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds < 5000 && !process.HasExited) {
            string ready=Path.Combine(stage,"apply-ready");
            if(File.Exists(ready) && Encoding.UTF8.GetString(ReadBounded(ready,128)) == plan.Nonce) { process.Dispose(); return; }
            Thread.Sleep(50);
        }
        process.Dispose();
        throw new IOException("Update helper was not ready; current executable has not been replaced");
    }
    internal static bool TryRun(string[] args) {
        if(args.Length == 0)return false;
        if(args[0] == "--update-apply") {
            try { if(args.Length != 2)throw new IOException("Invalid apply arguments"); Apply(Stage(args[1])); }
            catch(Exception e) {
                Debug.WriteLine("Update apply failed: "+e.Message);
                try {
                    string stage=Stage(args[1]);
                    string reason=e.Message.Length > 1000 ? e.Message.Substring(0,1000) : e.Message;
                    WriteNew(Path.Combine(stage,"apply-result"),"Update could not finish. Your previous version was kept or a recovery backup was retained.\n"+reason);
                    RestartUnchangedOriginal(stage);
                } catch { }
            }
            return true;
        }
        if(args[0] == "--update-health") {
            if(args.Length != 3)throw new IOException("Invalid health arguments");
            string stage=Stage(args[1]); var plan=ReadPlan(stage);
            if(args[2] != plan.Nonce || !Same(LocalPath(Self),plan.Target))throw new IOException("Invalid startup-health identity");
            RequireGameClosed();
            healthStage=stage; healthNonce=plan.Nonce; activationStage=stage;
            return false;
        }
        return false;
    }
    static void RestartUnchangedOriginal(string stage) {
        if(originalRestarted)return;
        var plan=ReadPlan(stage);
        if(!Same(LocalPath(Self),Path.Combine(stage,"apply-helper.exe")) ||
                !Same(Hash(Self),plan.OriginalHash) || !Same(Hash(plan.Target),plan.OriginalHash))return;
        string ready=Path.Combine(stage,"apply-ready");
        if(!File.Exists(ready) || Encoding.UTF8.GetString(ReadBounded(ready,128)) != plan.Nonce)return;
        try {
            using(var parent=Process.GetProcessById(plan.ParentId))
                if(parent.StartTime.ToUniversalTime().Ticks == plan.ParentStarted && !parent.HasExited)return;
        } catch(ArgumentException) { }
        var restored=Launch(plan.Target,"");
        originalRestarted=restored != null;
        if(restored != null)restored.Dispose();
    }
    internal static void AcknowledgeStartup() {
        if(healthStage == null)return;
        RequireGameClosed();
        var p=Process.GetCurrentProcess();
        WriteNew(Path.Combine(healthStage,"startup-health"),healthNonce+"|"+p.Id.ToString(CultureInfo.InvariantCulture)+"|"+
            p.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        healthStage=null; healthNonce=null;
    }
    static void Apply(string stage) {
        var plan=ReadPlan(stage);
        if(!Same(LocalPath(Self),Path.Combine(stage,"apply-helper.exe")) || !Same(Hash(Self),plan.OriginalHash))
            throw new IOException("Apply must use the trusted helper copy");
        var parent=Process.GetProcessById(plan.ParentId);
        if(parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted || !Same(LocalPath(parent.MainModule.FileName),plan.Target))
            throw new IOException("Original process identity changed");
        if(!Same(Hash(plan.Target),plan.OriginalHash))throw new IOException("Original executable changed");
        RequireGameClosed(); Verify(stage);
        WriteNew(Path.Combine(stage,"apply-ready"),plan.Nonce);
        if(!parent.WaitForExit(60000))throw new IOException("Original application did not exit");
        // Staging or target may have changed while the parent was exiting.
        RequireGameClosed(); var manifest=Verify(stage);
        if(!Same(Hash(plan.Target),plan.OriginalHash))throw new IOException("Original executable changed before activation");
        string pending=plan.Target+".autocat-pending-"+Path.GetFileName(stage);
        string backup=plan.Target+".autocat-backup.exe";
        LocalPath(pending); LocalPath(backup);
        CheckBackup(plan.Target);
        if(File.Exists(pending))throw new IOException("Update pending file already exists");
        string previous=Path.Combine(stage,"previous-backup.exe");
        bool replaced=false, archived=false, committed=false; Process candidate=null;
        try {
            File.Copy(Path.Combine(stage,"autocat.exe"),pending,false);
            UpdateEnvelope.VerifyPayload(manifest,pending);
            Checkpoint("before-replace"); RequireGameClosed();
            CheckBackup(plan.Target);
            if(File.Exists(backup)) {
                File.Copy(backup,previous,false);
                if(!Same(Hash(previous),Hash(backup)))throw new IOException("Previous backup archive verification failed");
                archived=true; File.Delete(backup);
            }
            File.Replace(pending,plan.Target,backup); replaced=true;
            if(!Same(Hash(backup),plan.OriginalHash))throw new IOException("Backup identity mismatch");
            UpdateEnvelope.VerifyPayload(manifest,plan.Target);
            Checkpoint("after-replace"); RequireGameClosed();
            candidate=Launch(plan.Target,"--update-health "+Quote(stage)+" "+Quote(plan.Nonce));
            if(candidate == null)throw new IOException("Updated executable did not start");
            long started=candidate.StartTime.ToUniversalTime().Ticks;
            var timer=Stopwatch.StartNew(); bool healthy=false;
            while(timer.ElapsedMilliseconds < HealthTimeoutMs && !candidate.HasExited) {
                string health=Path.Combine(stage,"startup-health");
                if(File.Exists(health)) {
                    string expected=plan.Nonce+"|"+candidate.Id.ToString(CultureInfo.InvariantCulture)+"|"+started.ToString(CultureInfo.InvariantCulture);
                    healthy=Encoding.UTF8.GetString(ReadBounded(health,256)) == expected;
                    if(healthy)break;
                }
                Thread.Sleep(100);
            }
            if(!healthy)throw new IOException("Updated desktop did not acknowledge startup");
            committed=true; // Cleanup failures after a healthy start must not roll back.
            Receipt(plan.Target,plan.OriginalHash);
            if(archived)File.Delete(previous);
            WriteNew(Path.Combine(stage,"apply-result"),"healthy");
        } catch {
            if(committed) {
                try { WriteNew(Path.Combine(stage,"apply-result"),"Updated desktop started, but recovery-file cleanup needs attention. Backup files were retained."); } catch { }
                return;
            }
            if(replaced) {
                if(candidate != null && !candidate.HasExited) { candidate.Kill(); if(!candidate.WaitForExit(5000))throw new IOException("Candidate did not stop; backup retained"); }
                if(!Same(Hash(backup),plan.OriginalHash))throw new IOException("Recovery backup changed; manual restore required");
                if(!Same(Hash(plan.Target),manifest.Sha256))throw new IOException("Activation target changed; manual restore required");
                string failed=plan.Target+".autocat-failed-"+Path.GetFileName(stage);
                if(File.Exists(failed))throw new IOException("Recovery destination exists; backup retained");
                File.Replace(backup,plan.Target,failed);
                if(!Same(Hash(plan.Target),plan.OriginalHash))throw new IOException("Restored executable identity mismatch");
                if(Same(Hash(failed),manifest.Sha256))File.Delete(failed);
                var restored=Launch(plan.Target,"");
                originalRestarted=restored != null;
                if(restored != null)restored.Dispose();
            }
            if(archived && !File.Exists(backup))File.Copy(previous,backup,false);
            if(!replaced)RestartUnchangedOriginal(stage);
            throw;
        } finally {
            if(File.Exists(pending))File.Delete(pending);
            if(candidate != null)candidate.Dispose();
            parent.Dispose();
        }
    }
}
