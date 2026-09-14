using System;using System.IO;using System.Reflection;
static class EmbeddedRuntime {
 // Embedded because Mono needs a real file path to load into the game process; this keeps the public download to one file.
 internal const string ResourceName="AutoCat.Runtime.autocat.runtime.101.dll";
 internal static string Extract(){string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AutoCat","runtime");Directory.CreateDirectory(dir);string target=Path.Combine(dir,"autocat.runtime.101.dll");
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)){if(stream==null)throw new InvalidOperationException("Embedded runtime resource missing: "+ResourceName);var bytes=new byte[stream.Length];int offset=0;while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);if(read<=0)break;offset+=read;}string temp=target+".tmp";File.WriteAllBytes(temp,bytes);if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);}
  return target;
 }
}
