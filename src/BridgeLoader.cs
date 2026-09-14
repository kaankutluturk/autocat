using System;using System.IO;using System.Linq;using System.Text;using System.Collections.Generic;using System.Diagnostics;using System.Runtime.InteropServices;
static class BridgeLoader {
 [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint rights,bool inherit,int pid);
 [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr VirtualAllocEx(IntPtr p,IntPtr a,UIntPtr n,uint type,uint protect);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool VirtualFreeEx(IntPtr p,IntPtr a,UIntPtr n,uint type);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool WriteProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr got);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr got);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool VirtualProtectEx(IntPtr p,IntPtr a,UIntPtr n,uint protect,out uint old);
 [DllImport("kernel32.dll")]static extern bool FlushInstructionCache(IntPtr p,IntPtr a,UIntPtr n);
 [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr CreateRemoteThread(IntPtr p,IntPtr attrs,UIntPtr stack,IntPtr start,IntPtr arg,uint flags,out uint id);
 [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(IntPtr h,uint ms);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
 static Exception Error(string task){return new IOException(task+": Windows error "+Marshal.GetLastWin32Error());}
 public static void Load(int pid,string bridgePath){
  ProcessModule mono;using(var process=Process.GetProcessById(pid)){if(process.ProcessName!="BongoCat")throw new Exception("Target is not BongoCat");mono=process.Modules.Cast<ProcessModule>().Single(m=>m.ModuleName.Equals("mono-2.0-bdwgc.dll",StringComparison.OrdinalIgnoreCase));}
  var exports=Exports(mono.FileName,mono.BaseAddress.ToInt64());
  IntPtr p=OpenProcess(0x43a,false,pid);if(p==IntPtr.Zero)throw Error("Open Bongo Cat");IntPtr memory=IntPtr.Zero,thread=IntPtr.Zero;bool finished=false;
  try{
   memory=VirtualAllocEx(p,IntPtr.Zero,new UIntPtr(8192),0x3000,4);if(memory==IntPtr.Zero)throw Error("Allocate loader");long data=memory.ToInt64()+4096;
   var strings=new byte[4096];Action<int,string> put=(offset,value)=>{var bytes=Encoding.UTF8.GetBytes(value+"\0");if(offset+bytes.Length>strings.Length)throw new Exception("Bridge path too long");Buffer.BlockCopy(bytes,0,strings,offset,bytes.Length);};
   put(256,Path.GetFullPath(bridgePath));put(2304,"AutocatBridge");put(2368,"Entry");put(2432,"Load");
   UIntPtr written;if(!WriteProcessMemory(p,new IntPtr(data),strings,new UIntPtr(4096),out written))throw Error("Write loader data");
   var code=new List<byte>();var exits=new List<int>();Action<byte[]> emit=b=>code.AddRange(b);Action<long> imm=v=>emit(BitConverter.GetBytes(v));
   Action<long> rax=v=>{emit(new byte[]{0x48,0xb8});imm(v);};Action<long> rcx=v=>{emit(new byte[]{0x48,0xb9});imm(v);};Action<long> rdx=v=>{emit(new byte[]{0x48,0xba});imm(v);};Action<long> r8=v=>{emit(new byte[]{0x49,0xb8});imm(v);};Action<long> r9=v=>{emit(new byte[]{0x49,0xb9});imm(v);};
   Action<string> call=name=>{rax(exports[name]);emit(new byte[]{0xff,0xd0});};
   Action<int> store=offset=>{emit(new byte[]{0x48,0xa3});imm(data+offset);};
   Action check=()=>{emit(new byte[]{0x48,0x85,0xc0,0x0f,0x84});exits.Add(code.Count);emit(new byte[4]);};
   Action<int> stage=n=>{rax(data+64);emit(new byte[]{0xc7,0x00});emit(BitConverter.GetBytes(n));};
   emit(new byte[]{0x48,0x83,0xec,0x28});call("mono_get_root_domain");store(0);emit(new byte[]{0x48,0x89,0xc1});call("mono_thread_attach");store(8);stage(1);
   rax(data);emit(new byte[]{0x48,0x8b,0x08});rdx(data+256);call("mono_domain_assembly_open");store(16);check();stage(2);
   rax(data+16);emit(new byte[]{0x48,0x8b,0x08});call("mono_assembly_get_image");store(24);check();stage(3);
   rax(data+24);emit(new byte[]{0x48,0x8b,0x08});rdx(data+2304);r8(data+2368);call("mono_class_from_name");store(32);check();stage(4);
   rax(data+32);emit(new byte[]{0x48,0x8b,0x08});rdx(data+2432);r8(0);call("mono_class_get_method_from_name");store(40);check();stage(5);
   rax(data+40);emit(new byte[]{0x48,0x8b,0x08});rdx(0);r8(0);r9(data+56);call("mono_runtime_invoke");store(48);stage(6);
   rax(data+56);emit(new byte[]{0x48,0x8b,0x08,0x48,0x85,0xc9,0x0f,0x84});int noException=code.Count;emit(new byte[4]);rdx(data+88);call("mono_object_to_string");store(80);
   var skipError=BitConverter.GetBytes(code.Count-noException-4);for(int i=0;i<4;i++)code[noException+i]=skipError[i];
   int finish=code.Count;foreach(int jump in exits){var bytes=BitConverter.GetBytes(finish-jump-4);for(int i=0;i<4;i++)code[jump+i]=bytes[i];}
   rax(data+8);emit(new byte[]{0x48,0x8b,0x08});call("mono_thread_detach");emit(new byte[]{0x31,0xc0,0x48,0x83,0xc4,0x28,0xc3});
   var blob=code.ToArray();if(!WriteProcessMemory(p,memory,blob,new UIntPtr((uint)blob.Length),out written))throw Error("Write loader");uint old;if(!VirtualProtectEx(p,memory,new UIntPtr(4096),0x20,out old))throw Error("Protect loader");FlushInstructionCache(p,memory,new UIntPtr((uint)blob.Length));
   uint id;thread=CreateRemoteThread(p,IntPtr.Zero,UIntPtr.Zero,memory,IntPtr.Zero,0,out id);if(thread==IntPtr.Zero)throw Error("Start bridge loader");
   if(WaitForSingleObject(thread,10000)!=0)throw new Exception("Bridge loader did not finish; do not retry until game restart");finished=true;
   var result=new byte[96];if(!ReadProcessMemory(p,new IntPtr(data),result,new UIntPtr(96),out written))throw Error("Read loader result");int completed=BitConverter.ToInt32(result,64);long exception=BitConverter.ToInt64(result,56);string message="";long str=BitConverter.ToInt64(result,80);
   if(str!=0){var length=new byte[4];if(ReadProcessMemory(p,new IntPtr(str+16),length,new UIntPtr(4),out written)){int count=BitConverter.ToInt32(length,0);if(count>0&&count<8192){var text=new byte[count*2];if(ReadProcessMemory(p,new IntPtr(str+20),text,new UIntPtr((uint)text.Length),out written))message=Encoding.Unicode.GetString(text);}}}
   if(completed!=6||exception!=0)throw new Exception("Bridge initialization failed (stage "+completed+"): "+message);
  }finally{if(thread!=IntPtr.Zero)CloseHandle(thread);if(memory!=IntPtr.Zero&&(thread==IntPtr.Zero||finished))VirtualFreeEx(p,memory,UIntPtr.Zero,0x8000);CloseHandle(p);}
 }
 static Dictionary<string,long> Exports(string path,long moduleBase){var b=File.ReadAllBytes(path);int pe=BitConverter.ToInt32(b,0x3c),sections=BitConverter.ToUInt16(b,pe+6),optional=pe+24,sectionTable=optional+BitConverter.ToUInt16(b,pe+20);if(BitConverter.ToUInt16(b,optional)!=0x20b)throw new Exception("Expected x64 Mono");
  Func<int,int> map=rva=>{for(int i=0;i<sections;i++){int s=sectionTable+40*i,va=BitConverter.ToInt32(b,s+12),size=Math.Max(BitConverter.ToInt32(b,s+8),BitConverter.ToInt32(b,s+16));if(rva>=va&&rva<va+size)return rva-va+BitConverter.ToInt32(b,s+20);}throw new Exception("Invalid export RVA");};
  int er=BitConverter.ToInt32(b,optional+112),sizeE=BitConverter.ToInt32(b,optional+116),e=map(er),n=BitConverter.ToInt32(b,e+24),functions=map(BitConverter.ToInt32(b,e+28)),names=map(BitConverter.ToInt32(b,e+32)),ordinals=map(BitConverter.ToInt32(b,e+36));var result=new Dictionary<string,long>();
  for(int i=0;i<n;i++){int nameAt=map(BitConverter.ToInt32(b,names+4*i)),end=Array.IndexOf(b,(byte)0,nameAt);string name=Encoding.ASCII.GetString(b,nameAt,end-nameAt);int ordinal=BitConverter.ToUInt16(b,ordinals+2*i),rva=BitConverter.ToInt32(b,functions+ordinal*4);if(rva>=er&&rva<er+sizeE)continue;result[name]=moduleBase+rva;}return result;
 }
}
