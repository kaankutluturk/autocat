using System;using System.Drawing;using System.Windows.Forms;using System.Runtime.InteropServices;
namespace AutoCat {
 static class KeyNames {
  public static string Name(int key,int mods=0){string name;
   switch(key){case 0:return "none";case 1:name="mouse 1";break;case 2:name="mouse 2";break;case 4:name="mouse 3";break;case 5:name="mouse 4";break;case 6:name="mouse 5";break;case 16:name="shift";break;case 17:name="ctrl";break;case 18:name="alt";break;case 256:name="wheel up";break;case 257:name="wheel down";break;case 258:name="wheel left";break;case 259:name="wheel right";break;default:name=((Keys)key).ToString().ToLowerInvariant();break;}
   return ((mods&2)!=0?"ctrl + ":"")+((mods&4)!=0?"shift + ":"")+((mods&1)!=0?"alt + ":"")+((mods&8)!=0?"win + ":"")+name;
  }
 }
 sealed class KeyMonitor:IDisposable {
  delegate IntPtr Hook(int code,IntPtr w,IntPtr l);
  [StructLayout(LayoutKind.Sequential)]struct Keyboard {public uint Key,Scan,Flags,Time;public UIntPtr Extra;}
  [StructLayout(LayoutKind.Sequential)]struct Mouse {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
  [DllImport("user32.dll")]static extern IntPtr SetWindowsHookEx(int type,Hook callback,IntPtr module,uint thread);
  [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr h);
  [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr h,int c,IntPtr w,IntPtr l);
  [DllImport("kernel32.dll")]static extern IntPtr GetModuleHandle(string name);
  readonly Hook keyboard,mouse;IntPtr kh,mh;readonly bool[] down=new bool[256];public Action<int,int,Point> Pressed,Released;
  public KeyMonitor(){keyboard=KeyboardEvent;mouse=MouseEvent;kh=SetWindowsHookEx(13,keyboard,GetModuleHandle(null),0);mh=SetWindowsHookEx(14,mouse,GetModuleHandle(null),0);if(kh==IntPtr.Zero||mh==IntPtr.Zero){Dispose();throw new Exception("Cannot monitor bindings; relaunch autocat to reopen the menu");}}
  int Mods(int key){int m=0;if((down[162]||down[163]||down[17])&&key!=17)m|=2;if((down[160]||down[161]||down[16])&&key!=16)m|=4;if((down[164]||down[165]||down[18])&&key!=18)m|=1;if((down[91]||down[92])&&key!=91&&key!=92)m|=8;return m;}
  void Record(int key,bool held,Point point){if(key<=0||key>=256)return;bool was=down[key];down[key]=held;if(held==was)return;int normalized=key==160||key==161?16:key==162||key==163?17:key==164||key==165?18:key;var handler=held?Pressed:Released;if(handler!=null)handler(normalized,Mods(normalized),point);}
  IntPtr KeyboardEvent(int c,IntPtr w,IntPtr l){if(c>=0){var k=(Keyboard)Marshal.PtrToStructure(l,typeof(Keyboard));if((k.Flags&16)==0)Record((int)k.Key,(k.Flags&128)==0,Cursor.Position);}return CallNextHookEx(kh,c,w,l);}
  IntPtr MouseEvent(int c,IntPtr w,IntPtr l){if(c>=0){var m=(Mouse)Marshal.PtrToStructure(l,typeof(Mouse));if((m.Flags&1)==0){int msg=w.ToInt32(),key=0;bool held=false;var point=new Point(m.X,m.Y);
   if(msg==0x201||msg==0x202){key=1;held=msg==0x201;}else if(msg==0x204||msg==0x205){key=2;held=msg==0x204;}else if(msg==0x207||msg==0x208){key=4;held=msg==0x207;}else if(msg==0x20b||msg==0x20c){key=(m.Data>>16)==1?5:6;held=msg==0x20b;}
   if(key!=0)Record(key,held,point);else if(msg==0x20a||msg==0x20e){int delta=(short)(m.Data>>16);key=msg==0x20a?(delta>0?256:257):(delta>0?259:258);if(Pressed!=null)Pressed(key,Mods(key),point);}
  }}return CallNextHookEx(mh,c,w,l);}
  public void Dispose(){if(kh!=IntPtr.Zero){UnhookWindowsHookEx(kh);kh=IntPtr.Zero;}if(mh!=IntPtr.Zero){UnhookWindowsHookEx(mh);mh=IntPtr.Zero;}}
 }
 sealed class BindingEditor:Control {
  readonly Func<int> key,mods;readonly Action<int,int> set;readonly KeyMonitor monitor;
  static BindingEditor active;public static bool Capturing{get{return active!=null;}}int modifier;bool finishing;
  public BindingEditor(Func<int> getKey,Func<int> getMods,Action<int,int> setter,KeyMonitor input){key=getKey;mods=getMods;set=setter;monitor=input;Height=23;TabStop=true;AccessibleRole=AccessibleRole.PushButton;SetStyle(ControlStyles.Selectable|ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);Cursor=Cursors.Hand;}
  protected override void OnPaint(PaintEventArgs e){using(var p=new Pen(active==this?Skin.Accent:Skin.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);Skin.TextAt(e.Graphics,active==this?"":KeyNames.Name(key(),mods()),ClientRectangle,Skin.Text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
  void Edit(){if(active!=null)active.End();Focus();active=this;modifier=0;finishing=false;if(monitor!=null){monitor.Pressed+=OnPress;monitor.Released+=OnRelease;}Invalidate();}
  void End(){if(monitor!=null){monitor.Pressed-=OnPress;monitor.Released-=OnRelease;}if(active==this)active=null;modifier=0;Invalidate();}
  static bool IsModifier(int k){return k==16||k==17||k==18||k==91||k==92;}
  void OnPress(int k,int m,Point point){if(active!=this||finishing)return;if(IsModifier(k)){modifier=k;return;}Accept(k==27?0:k,k==27?0:m);}
  void OnRelease(int k,int m,Point point){if(active==this&&!finishing&&k==modifier)Accept(k,0);}
  void Accept(int k,int m){finishing=true;BeginInvoke(new Action(()=>{if(active!=this)return;set(k,m);End();}));}
  protected override void OnMouseDown(MouseEventArgs e){if(active!=this&&e.Button==MouseButtons.Left)Edit();base.OnMouseDown(e);}
  protected override bool ProcessCmdKey(ref Message message,Keys data){if(active==this){if(monitor==null)OnPress((int)(data&Keys.KeyCode),((data&Keys.Control)!=0?2:0)|((data&Keys.Shift)!=0?4:0)|((data&Keys.Alt)!=0?1:0),Cursor.Position);return true;}if(data==Keys.Enter||data==Keys.Space){Edit();return true;}return base.ProcessCmdKey(ref message,data);}
  protected override void OnLostFocus(EventArgs e){if(active==this&&!finishing)End();base.OnLostFocus(e);}
  protected override void OnVisibleChanged(EventArgs e){if(!Visible&&active==this)End();base.OnVisibleChanged(e);}
  protected override void Dispose(bool disposing){if(disposing)End();base.Dispose(disposing);}
 }
}