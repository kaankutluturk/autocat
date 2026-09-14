using System;using System.Collections.Generic;using System.IO;using System.Threading;
namespace AutocatBridge {
// Bounded, background-only disk writes; callers never wait for disk I/O.
public sealed class BufferedLog:IDisposable {
 readonly Queue<string[]> pending=new Queue<string[]>();readonly object gate=new object();readonly Thread worker;bool closed;public int Dropped{get;private set;}public int Failures{get;private set;}
 public BufferedLog(){worker=new Thread(Run);worker.IsBackground=true;worker.Start();}
 public void Write(string path,string message){lock(gate){if(closed)return;if(pending.Count>=256){Dropped++;return;}pending.Enqueue(new[]{path,message});Monitor.Pulse(gate);}}
 void Run(){while(true){string[][] batch;lock(gate){while(pending.Count==0&&!closed)Monitor.Wait(gate,1000);if(pending.Count==0&&closed)return;batch=pending.ToArray();pending.Clear();}var grouped=new Dictionary<string,System.Text.StringBuilder>();foreach(var entry in batch){System.Text.StringBuilder text;if(!grouped.TryGetValue(entry[0],out text)){text=new System.Text.StringBuilder();grouped.Add(entry[0],text);}text.Append(entry[1]);}foreach(var pair in grouped)try{if(File.Exists(pair.Key)&&FileSize(pair.Key)>2097152){string backup=pair.Key+".previous";if(File.Exists(backup))File.Delete(backup);File.Copy(pair.Key,backup,true);File.Delete(pair.Key);}using(var writer=new StreamWriter(pair.Key,true))writer.Write(pair.Value.ToString());}catch{lock(gate){Failures++;}}}}
 static long FileSize(string path){using(var stream=File.OpenRead(path))return stream.Length;} public void Dispose(){lock(gate){closed=true;Monitor.Pulse(gate);}}
 public bool WaitForDrain(int milliseconds){return worker.Join(milliseconds);}
}}

