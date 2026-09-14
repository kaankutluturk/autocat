using System;using System.IO;using System.Diagnostics;using System.Reflection;using System.Windows.Forms;using AutoCat.Diagnostics;
static class DiagnosticSession {
 internal static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"AutoCat","logs");
 internal static void Start(string[] args){string level=Environment.GetEnvironmentVariable("AUTOCAT_LOG_LEVEL");for(int i=0;i+1<args.Length;i++)if(args[i]=="--log-level")level=args[i+1];LogLevel parsed;if(!Enum.TryParse<LogLevel>(level,true,out parsed)||!Enum.IsDefined(typeof(LogLevel),parsed))parsed=LogLevel.Info;
  Diag.Session=Guid.NewGuid().ToString("N");Diag.Level=parsed;Diag.Current=new DiagnosticLog("menu");Diag.Current.Configure(Folder,Diag.Session,parsed);
  Diag.Write(LogLevel.Info,"Session","start",Summary());Diag.Write(LogLevel.Info,"Privacy","policy","No raw keys, account IDs, credentials or inventory dumps; paths redacted; settings changes recorded logically.");
  AppDomain.CurrentDomain.UnhandledException+=(s,e)=>{var error=e.ExceptionObject as Exception;if(error!=null)Diag.Fault("Session","fatal",error);Stop();};
 }
 internal static string Summary(){return "AutoCat=1.0.0 runtime=100 build="+Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString()+" session="+Diag.Session+" OS="+Environment.OSVersion.VersionString+" OS64="+Environment.Is64BitOperatingSystem+" processBits="+(IntPtr.Size*8)+" pid="+Process.GetCurrentProcess().Id+" level="+Diag.Level+" attach=Mono";}
 internal static void Stop(){if(Diag.Current==null)return;Diag.Values(LogLevel.Info,"Session","shutdown","sinkFailures={0} peakQueued={1}",Diag.Current.Failures,Diag.Current.PeakQueued);Diag.Current.Dispose();Diag.Current.WaitForDrain(2000);Diag.Current=null;}
}
