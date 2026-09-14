using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace AutoCat.Diagnostics {
 public enum LogLevel { Trace, Debug, Info, Warn, Error }
 // Shared by the external menu and the Unity component. Disk/mutex/formatting work
 // lives exclusively on this worker; producers only filter, copy scalars and queue.
 public sealed class DiagnosticLog:IDisposable {
  struct Event { public long Ticks,Sequence,A,B,C,D;public LogLevel Level;public string Subsystem,Action,Message;public bool Numbers;public Exception Error; }
  sealed class Repeat { public string Subsystem,Action,Message;public LogLevel Level;public long Until,Count; }
  readonly object gate=new object();readonly Queue<Event> normal=new Queue<Event>(),warnings=new Queue<Event>(),errors=new Queue<Event>();
  readonly Repeat[] repeats=new Repeat[64];readonly long[] dropped=new long[5];readonly Thread worker;readonly string role;readonly int capacity;readonly long maxBytes;
  string folder,session;volatile LogLevel minimum;volatile bool closed;long sequence,failures,written;int peak;
  public DiagnosticLog(string role,int capacity=512,long maxBytes=4194304){this.role=role;this.capacity=Math.Max(16,capacity);this.maxBytes=Math.Max(1024,maxBytes);minimum=LogLevel.Info;worker=new Thread(Run);worker.IsBackground=true;worker.Start();}
  public bool Enabled(LogLevel level){return level>=minimum&&!closed;}
  public long Failures{get{return Interlocked.Read(ref failures);}}public long Written{get{return Interlocked.Read(ref written);}}public int PeakQueued{get{lock(gate)return peak;}}
  public static bool ValidSession(string id){if(id==null||id.Length!=32)return false;foreach(char c in id)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))return false;return true;}
  public void Configure(string directory,string id,LogLevel level){if(!ValidSession(id))throw new ArgumentException("Invalid diagnostic session");lock(gate){if(folder!=null&&(folder!=directory||session!=id))throw new InvalidOperationException("Diagnostic session already configured");folder=directory;session=id;minimum=level;Monitor.Pulse(gate);}}
  public void Write(LogLevel level,string subsystem,string action,string message){if(!Enabled(level))return;Add(level,subsystem,action,message,false,0,0,0,0,null);}
  public void Values(LogLevel level,string subsystem,string action,string template,long a=0,long b=0,long c=0,long d=0){if(!Enabled(level))return;Add(level,subsystem,action,template,true,a,b,c,d,null);}
  public void Fault(string subsystem,string action,Exception error){if(!Enabled(LogLevel.Error))return;var real=error.InnerException??error;Limited(LogLevel.Error,subsystem,action,real.Message,real);}
  // First occurrence is always queued. Repeated identical conditions are counted;
  // periodic worker summaries survive even if the condition never recurs again.
  public void Limited(LogLevel level,string subsystem,string action,string message,Exception error=null){LimitEvent(level,subsystem,action,message,false,0,0,0,0,error);}
  public void LimitedValues(LogLevel level,string subsystem,string action,string template,long a=0,long b=0,long c=0,long d=0){LimitEvent(level,subsystem,action,template,true,a,b,c,d,null);}
  void LimitEvent(LogLevel level,string subsystem,string action,string message,bool numbers,long a,long b,long c,long d,Exception error){if(!Enabled(level))return;message=Limit(message);long now=DateTime.UtcNow.Ticks;lock(gate){int free=-1;for(int i=0;i<repeats.Length;i++){var r=repeats[i];if(r==null){if(free<0)free=i;continue;}if(r.Subsystem==subsystem&&r.Action==action&&r.Message==message){if(now<r.Until){r.Count++;return;}FlushRepeat(r);r.Until=now+TimeSpan.FromSeconds(10).Ticks;Enqueue(level,subsystem,action,message,numbers,a,b,c,d,error);return;}}
   if(free<0){free=0;for(int i=1;i<repeats.Length;i++)if(repeats[i].Until<repeats[free].Until)free=i;FlushRepeat(repeats[free]);}
   repeats[free]=new Repeat{Subsystem=subsystem,Action=action,Message=message,Level=level,Until=now+TimeSpan.FromSeconds(10).Ticks};Enqueue(level,subsystem,action,message,numbers,a,b,c,d,error);
  }}
  void FlushRepeat(Repeat r){if(r.Count>0){Enqueue(r.Level,r.Subsystem,r.Action+".repeated","suppressed={0}; see initial condition",true,r.Count,0,0,0,null);r.Count=0;}}
  static string Limit(string s){if(s==null)return "";return s.Length>2048?s.Substring(0,2048)+" [truncated]":s;}
  void Add(LogLevel l,string s,string a,string m,bool numbers,long x,long y,long z,long w,Exception error){lock(gate){Enqueue(l,s,a,m,numbers,x,y,z,w,error);}}
  void Enqueue(LogLevel level,string subsystem,string action,string message,bool numbers,long a,long b,long c,long d,Exception error){if(closed)return;var queue=level==LogLevel.Error?errors:level==LogLevel.Warn?warnings:normal;int limit=level>=LogLevel.Warn?capacity/8:capacity*3/4;if(queue.Count>=limit){dropped[(int)level]++;return;}
   queue.Enqueue(new Event{Ticks=DateTime.UtcNow.Ticks,Sequence=++sequence,Level=level,Subsystem=subsystem,Action=action,Message=Limit(message),Numbers=numbers,A=a,B=b,C=c,D=d,Error=error});peak=Math.Max(peak,normal.Count+warnings.Count+errors.Count);if(level>=LogLevel.Warn)Monitor.Pulse(gate);}
  static readonly Regex paths=new Regex(@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n""<>|]*");
  static readonly Regex steamIds=new Regex(@"\b7656119\d{10}\b");
  static readonly Regex addresses=new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}");
  public static string Redact(string value){return addresses.Replace(steamIds.Replace(paths.Replace(value,"<path>"),"<steam-id>"),"<address>");}
  static string Clean(string s){return s.Replace("\r","\\r").Replace("\n","\\n").Replace("\0","");}
  string Format(Event e){string message=e.Message;if(e.Numbers){try{message=String.Format(CultureInfo.InvariantCulture,message,e.A,e.B,e.C,e.D);}catch{message+=" [invalid diagnostic template]";}}if(e.Error!=null)message+=" exception="+(minimum<=LogLevel.Debug?e.Error.ToString():e.Error.GetType().Name+": "+e.Error.Message);message=Clean(Redact(message));if(message.Length>8192)message=message.Substring(0,8192)+" [truncated]";
   return new DateTime(e.Ticks,DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture)+" ["+e.Level.ToString().ToUpperInvariant()+"] ["+role+"] ["+e.Subsystem+"] "+e.Action+" seq="+e.Sequence.ToString(CultureInfo.InvariantCulture)+" "+message+Environment.NewLine;}
  Event Take(){Queue<Event> q=normal.Count>0?normal:null;if(warnings.Count>0&&(q==null||warnings.Peek().Sequence<q.Peek().Sequence))q=warnings;if(errors.Count>0&&(q==null||errors.Peek().Sequence<q.Peek().Sequence))q=errors;return q.Dequeue();}
  void Run(){FileMutex fileGate=null;FileStream lease=null;long reportedFailures=0;try{while(true){Event[] batch;string directory,id;lock(gate){if(!closed&&errors.Count==0&&warnings.Count==0)Monitor.Wait(gate,500);long now=DateTime.UtcNow.Ticks;foreach(var r in repeats)if(r!=null&&(closed||now>=r.Until))FlushRepeat(r);
     if(folder==null){if(closed)return;continue;}directory=folder;id=session;int count=Math.Min(64,normal.Count+warnings.Count+errors.Count);if(count==0&&closed)return;batch=new Event[count];for(int i=0;i<count;i++)batch[i]=Take();}
    var text=new StringBuilder();foreach(var e in batch)text.Append(Format(e));lock(gate){for(int i=0;i<dropped.Length;i++)if(dropped[i]>0){text.Append(Format(new Event{Ticks=DateTime.UtcNow.Ticks,Level=LogLevel.Warn,Subsystem="Logger",Action="backpressure",Message="level="+((LogLevel)i).ToString()+" suppressed={0}",Numbers=true,A=dropped[i]}));dropped[i]=0;}}
    long failureCount=Failures;if(failureCount>reportedFailures)text.Append(Format(new Event{Ticks=DateTime.UtcNow.Ticks,Level=LogLevel.Warn,Subsystem="Logger",Action="sink.recovered",Message="failedBatches={0}; some records lost",Numbers=true,A=failureCount}));
    if(text.Length==0)continue;
    try{if(fileGate==null)fileGate=new FileMutex("Local\\autocat-diagnostics-"+id);bool held=false;try{held=fileGate.WaitOne(1000);if(!held)throw new IOException("Diagnostic file lock timed out");Directory.CreateDirectory(directory);string path=Path.Combine(directory,"diagnostic-"+id+".log");
       if(lease==null){lease=new FileStream(path+"."+role+".active",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);if(role=="menu")Retain(directory,id);}
       if(File.Exists(path)&&Size(path)+Encoding.UTF8.GetByteCount(text.ToString())>maxBytes){if(File.Exists(path+".2"))File.Delete(path+".2");if(File.Exists(path+".1")){File.Copy(path+".1",path+".2",true);File.Delete(path+".1");}File.Copy(path,path+".1",true);File.Delete(path);}
       using(var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text.ToString());Interlocked.Exchange(ref written,written+batch.Length);reportedFailures=failureCount;
      }finally{if(held)fileGate.ReleaseMutex();}
    }catch{Interlocked.Increment(ref failures);}
   }}finally{if(lease!=null)lease.Dispose();if(fileGate!=null)fileGate.Close();}}
  static void Retain(string directory,string active){
#if !AUTOCAT_GAME
   var files=new List<string>(Directory.GetFiles(directory,"diagnostic-*.log"));files.Sort((a,b)=>File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));for(int i=7;i<files.Count;i++){string file=files[i],name=Path.GetFileNameWithoutExtension(file);if(name.Length!=43||!ValidSession(name.Substring(11))||name.Substring(11)==active)continue;FileStream menu=null,game=null;try{menu=new FileStream(file+".menu.active",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);game=new FileStream(file+".game.active",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);foreach(string suffix in new[]{"",".1",".2"})if(File.Exists(file+suffix))File.Delete(file+suffix);}catch(IOException){}finally{if(menu!=null)menu.Dispose();if(game!=null)game.Dispose();}if(!File.Exists(file)){foreach(string suffix in new[]{".menu.active",".game.active"})try{File.Delete(file+suffix);}catch(IOException){}}}
#endif
  }
  static long Size(string path){using(var stream=File.OpenRead(path))return stream.Length;}
  public void Dispose(){lock(gate){if(closed)return;foreach(var r in repeats)if(r!=null)FlushRepeat(r);closed=true;Monitor.Pulse(gate);}}
  public bool WaitForDrain(int milliseconds){return worker.Join(milliseconds);}
 }
 sealed class FileMutex {
  [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateMutex(IntPtr attributes,bool owner,string name);
  [System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
  [System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern bool ReleaseMutex(IntPtr handle);
  [System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
  IntPtr handle;public FileMutex(string name){handle=CreateMutex(IntPtr.Zero,false,name);if(handle==IntPtr.Zero)throw new IOException("Cannot create diagnostic file mutex");}
  public bool WaitOne(uint ms){uint result=WaitForSingleObject(handle,ms);return result==0||result==0x80;}public void ReleaseMutex(){ReleaseMutex(handle);}public void Close(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
 }
 // A disabled default keeps offline UI/tests isolated and never touches game logs.
 public static class Diag {
  public static DiagnosticLog Current;public static string Session="";public static LogLevel Level=LogLevel.Info;
  public static bool Enabled(LogLevel l){return Current!=null&&Current.Enabled(l);}
  public static void Write(LogLevel l,string s,string a,string m){var log=Current;if(log!=null)log.Write(l,s,a,m);}
  public static void Values(LogLevel l,string s,string a,string t,long x=0,long y=0,long z=0,long w=0){var log=Current;if(log!=null)log.Values(l,s,a,t,x,y,z,w);}
  public static void Fault(string s,string a,Exception e){var log=Current;if(log!=null)log.Fault(s,a,e);}
  public static void CurrentLimited(string s,string a,string m){var log=Current;if(log!=null)log.Limited(LogLevel.Warn,s,a,m);}
 }
}
