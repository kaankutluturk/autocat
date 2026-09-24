using System;using System.IO;using System.Reflection;using AutoCat.Diagnostics;
static class EmbeddedRuntime {
 // Embedded because Mono needs a real file path to load into the game process; this keeps the public download to one file.
 // Mono can't reload an assembly name a game process has already loaded. Releases use the version digits (1.0.1 -> 101,
 // 1.1.0 -> 110); live-testing a runtime change under an ID the game already loaded needs a Bongo Cat restart.
 // build.ps1 reads the value from this line.
 internal const string RuntimeId="110";
 internal static string FileName{get{return "autocat.runtime."+RuntimeId+".dll";}}
 internal static string ResourceName{get{return "AutoCat.Runtime."+FileName;}}
 internal static string Extract(){string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AutoCat","runtime");Directory.CreateDirectory(dir);string target=Path.Combine(dir,FileName);
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)){if(stream==null)throw new InvalidOperationException("Embedded runtime resource missing: "+ResourceName);var bytes=new byte[stream.Length];int offset=0;while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);if(read<=0)break;offset+=read;}string temp=target+".tmp";File.WriteAllBytes(temp,bytes);if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);}
  int kept;int removed=RemoveStale(dir,FileName,out kept);if(removed+kept>0)Diag.Values(LogLevel.Info,"Runtime","stale.cleanup","removed={0} inUse={1}",removed,kept);
  return target;
 }
 // Older runtimes a running Bongo Cat still has loaded can't be deleted; they're skipped and retried on a later launch.
 internal static int RemoveStale(string dir,string current,out int inUse){int removed=0;inUse=0;
  foreach(var file in Directory.GetFiles(dir,"autocat.runtime.*.dll")){if(String.Equals(Path.GetFileName(file),current,StringComparison.OrdinalIgnoreCase))continue;try{File.Delete(file);removed++;}catch(IOException){inUse++;}catch(UnauthorizedAccessException){inUse++;}}
  return removed;
 }
}
