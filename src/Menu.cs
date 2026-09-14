using System;using System.IO;using System.Drawing;using System.Threading;using System.Windows.Forms;using AutoCat;using AutoCat.Diagnostics;
sealed class MainForm:Form {
 Settings settings;readonly RatePolicy rates=new RatePolicy();Label dangerWarning;MicroButton dangerConfirm;readonly bool inspection;readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"AutoCat","settings.xml");
 readonly TapEngine taps=new TapEngine();BridgeClient bridge;KeyMonitor keys;bool running,closing;IntPtr previous;
 readonly Panel[] pages=new Panel[4];readonly MenuTabs tabs=new MenuTabs();readonly Label footer=new Label();
 readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};readonly EventWaitHandle reopen;MicroButton start,emoteStart;string notice="";DateTime noticeUntil;
 public MainForm(bool inspect=false){inspection=inspect;if(!inspect)reopen=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\autocat.xyz-show-menu");settings=inspection?new Settings():Settings.Read(settingsPath);rates.Restore(settings.ExtremeRates);taps.Rate=settings.Taps;
  Text="autocat.xyz";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(480,274);BackColor=Skin.Background;ForeColor=Skin.Text;Font=Skin.Font;DoubleBuffered=true;
  if(!inspection){try{keys=new KeyMonitor();keys.Pressed+=OnBinding;}catch(Exception e){Notice(e.Message);}}
  tabs.SetBounds(158,5,307,40);tabs.Change=SelectPage;Controls.Add(tabs);for(int i=0;i<4;i++){pages[i]=new Panel{Location=new Point(16,57),Size=new Size(448,179),BackColor=Skin.Background,Visible=i==0};Controls.Add(pages[i]);}
  BuildClicker();BuildAutomation();BuildExploits();BuildMisc();footer.SetBounds(18,245,444,17);footer.AutoEllipsis=false;footer.ForeColor=Skin.Muted;Controls.Add(footer);footer.MouseDown+=(s,e)=>DragMenu(e);
  if(!inspection){
   bridge=new BridgeClient(EmbeddedRuntime.Extract(),taps);taps.Start();bridge.Start();timer.Tick+=(s,e)=>Tick();timer.Start();
   var diagnostics=new ContextMenuStrip();diagnostics.Items.Add("open logs folder",null,(s,e)=>{try{Directory.CreateDirectory(DiagnosticSession.Folder);System.Diagnostics.Process.Start(DiagnosticSession.Folder);}catch(Exception ex){Diag.Fault("Diagnostics","open.failed",ex);Notice("Could not open logs folder");}});diagnostics.Items.Add("copy session ID",null,(s,e)=>CopyDiagnostic(Diag.Session));diagnostics.Items.Add("copy diagnostic summary",null,(s,e)=>CopyDiagnostic(DiagnosticSession.Summary()));footer.ContextMenuStrip=diagnostics;
   LogConfiguration();Diag.Write(LogLevel.Info,"Menu","opened","startup visible; automation paused");
  }
  FormClosing+=(s,e)=>Shutdown();UpdateText();
 }
 protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle=(p.ExStyle&~0x40000)|0x80;return p;}}
 protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;using(var p=new Pen(Color.Black))g.DrawRectangle(p,0,0,Width-1,Height-1);using(var p=new Pen(Skin.Border)){g.DrawRectangle(p,3,3,Width-7,Height-7);g.DrawRectangle(p,10,50,Width-20,Height-88);}using(var p=new Pen(Color.FromArgb(39,39,44)))g.DrawRectangle(p,5,5,Width-11,Height-11);
  TextRenderer.DrawText(g,"autocat",Skin.Logo,new Point(24,17),Color.FromArgb(238,235,232),TextFormatFlags.NoPadding);int word=TextRenderer.MeasureText(g,"autocat",Skin.Logo,Size.Empty,TextFormatFlags.NoPadding).Width;TextRenderer.DrawText(g,".xyz",Skin.Logo,new Point(23+word,17),Skin.Accent,TextFormatFlags.NoPadding);
 }
 void DragMenu(MouseEventArgs e){if(e.Button==MouseButtons.Left){Native.ReleaseCapture();Native.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}}
 protected override void OnMouseDown(MouseEventArgs e){if(e.X<158&&e.Y<50)DragMenu(e);base.OnMouseDown(e);}
 public void SelectPage(int index){if(index<0||index>=4)return;tabs.Selected=index;tabs.Invalidate();for(int i=0;i<4;i++)pages[i].Visible=i==index;Invalidate(true);}
 FieldGroup Group(int page,string title,int x,int w){var g=new FieldGroup(title,x,0,w,148);pages[page].Controls.Add(g);return g;}
 Label Label(Control parent,string text,int x,int y,int w,int h=18){var l=new Label{Text=text,Location=new Point(x,y),Size=new Size(w,h),ForeColor=Skin.Muted,Font=Skin.Font,BackColor=Color.Transparent};parent.Controls.Add(l);return l;}
 MicroButton Button(Control p,string text,int x,int y,int w,Action action){var b=new MicroButton(text,()=>{try{Diag.Write(LogLevel.Debug,"Menu","action",text);action();}catch(Exception e){Diag.Fault("Menu",text,e);Notice(e.Message);}}){Location=new Point(x,y),Size=new Size(w,24)};p.Controls.Add(b);return b;}
 void Check(Control p,string text,int y,Func<bool> getter,Action<bool> setter){var c=new MicroCheck(text,getter,v=>{setter(v);Sync();p.Invalidate(true);}){Location=new Point(14,y),Width=p.Width-28};p.Controls.Add(c);}
 void Dynamic(Control p,Label l,int y,int h){l.SetBounds(14,y,p.Width-28,h);l.ForeColor=Skin.Muted;l.Font=Skin.Font;p.Controls.Add(l);}
 void BuildClicker(){
  var click=RateGroup(false);var emote=RateGroup(true);emote.Visible=false;
  Button(pages[0],"auto clicking",0,0,218,()=>{click.Visible=true;emote.Visible=false;});
  Button(pages[0],"auto emoting",230,0,218,()=>{click.Visible=false;emote.Visible=true;});
 }
 FieldGroup RateGroup(bool emote){
  var c=new FieldGroup(emote?"auto emoting":"auto clicking",0,30,448,144);pages[0].Controls.Add(c);
  Func<int> rate=()=>emote?settings.EmoteRate:settings.Taps;
  Action<int> set=v=>{v=rates.Clamp(v,emote);if(emote)settings.EmoteRate=v;else settings.Taps=v;Sync();};
  Check(c,"enabled",24,()=>emote?settings.Emoting:settings.Clicker,v=>{if(emote)settings.Emoting=v;else settings.Clicker=v;});
  int normalMaximum=emote?RatePolicy.NormalEmoteMaximum:RatePolicy.NormalClickMaximum;
  var slider=new MicroSlider(emote?"emote rate":"click rate",1,normalMaximum,1,"0.0","",()=>Math.Min(normalMaximum,rate()),v=>{set((int)v);c.Invalidate(true);}){Location=new Point(12,57),Width=420,Display=()=>rate().ToString("N0")+" / s"};c.Controls.Add(slider);

  var exact=new TextBox{Visible=false,TextAlign=HorizontalAlignment.Right,BackColor=Skin.Panel,ForeColor=Skin.Text,Font=Skin.Font,BorderStyle=BorderStyle.FixedSingle,Location=new Point(250,55),Size=new Size(170,23)};c.Controls.Add(exact);exact.BringToFront();bool ending=false;
  Action<bool> finish=commit=>{if(!exact.Visible||ending)return;ending=true;int value;if(commit&&int.TryParse(exact.Text,System.Globalization.NumberStyles.Integer|System.Globalization.NumberStyles.AllowThousands,System.Globalization.CultureInfo.CurrentCulture,out value))set(value);exact.Hide();slider.Invalidate();ending=false;};
  slider.EditValue=()=>{exact.Text=rate().ToString();exact.Show();exact.Focus();exact.SelectAll();};exact.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter||e.KeyCode==Keys.Escape){finish(e.KeyCode==Keys.Enter);e.SuppressKeyPress=true;slider.Focus();}};exact.LostFocus+=(s,e)=>finish(true);
  var button=Button(c,"resume automation",14,108,420,ToggleRunning);if(emote)emoteStart=button;else start=button;return c;
 } void BuildAutomation(){var gifts=Group(1,"gifts",0,218);Check(gifts,"auto collect",24,()=>settings.Gifts,v=>settings.Gifts=v);
  var ex=Group(1,"duplicate exchanges",230,218);Check(ex,"auto-slot + exchange",24,()=>settings.Exchange,v=>settings.Exchange=v);
 }
 long shownNormalTokens=long.MinValue,shownEmoteTokens=long.MinValue;Label tokenCounts;bool unlockAll;
 void BuildExploits(){var gifts=Group(2,"insta gift",0,218);Check(gifts,"enabled",24,()=>settings.InstaGift,v=>settings.InstaGift=v);tokenCounts=Label(gifts,"normal: —\nemote: —",14,58,190,36);var unlock=Group(2,"unlock all",230,218);Check(unlock,"enabled",24,()=>unlockAll,v=>unlockAll=v);Label(unlock,"temporarily unlocks cosmetics\nand emotes",14,58,190,36);}
 void BuildMisc(){var binds=Group(3,"bindings",0,218);binds.Height=100;Label(binds,"menu",14,29,87);var menu=new BindingEditor(()=>settings.MenuKey,()=>settings.MenuModifiers,(k,m)=>SetBinding(true,k,m),keys){Location=new Point(105,24),Width=99};binds.Controls.Add(menu);Label(binds,"pause/resume",14,69,87);var pause=new BindingEditor(()=>settings.PauseKey,()=>settings.PauseModifiers,(k,m)=>SetBinding(false,k,m),keys){Location=new Point(105,64),Width=99};binds.Controls.Add(pause);

  var app=Group(3,"app",230,218);app.Height=100;Button(app,"save settings",14,24,92,()=>{Save();Notice("settings saved");});Button(app,"reset settings",112,24,92,ResetSettings);Button(app,"unload autocat.xyz",14,64,190,Close);
  var danger=new FieldGroup("danger zone",0,106,448,70);pages[3].Controls.Add(danger);
  Check(danger,"allow extreme rates",23,()=>rates.Enabled||rates.Pending,RequestDanger);danger.Controls[0].Width=163;
  dangerWarning=Label(danger,"High rates may reduce FPS and\nunlock achievements immediately.",182,22,252,34);dangerWarning.ForeColor=Skin.Accent;
  dangerConfirm=Button(danger,"enable",14,44,150,ConfirmDanger);
  UpdateDanger();
 }
 void RequestDanger(bool value){Diag.Write(LogLevel.Info,"DangerZone",value?"extreme.requested":"extreme.cancelled_or_disabled","Two-action confirmation; pending state not persisted");rates.Request(value);settings.ExtremeRates=rates.Enabled;Sync();if(!value)Save();UpdateDanger();Invalidate(true);}
 void ConfirmDanger(){Diag.Write(LogLevel.Info,"DangerZone","extreme.confirmed","Second deliberate action");rates.Confirm();settings.ExtremeRates=rates.Enabled;Save();UpdateDanger();Invalidate(true);LogConfiguration();}
 void UpdateDanger(){if(dangerWarning!=null){dangerWarning.Text=rates.Pending?"High rates may reduce FPS and\nunlock achievements immediately.":"allows custom click and emote rates";dangerWarning.ForeColor=rates.Pending?Skin.Accent:Skin.Muted;}if(dangerConfirm!=null)dangerConfirm.Visible=rates.Pending;} void SetBinding(bool menu,int key,int mods){Diag.Values(LogLevel.Info,"Bindings","requested","menu={0} keyCode={1} modifiers={2}",menu?1:0,key,mods);if(key!=0&&(menu?settings.PauseKey:settings.MenuKey)==key&&(menu?settings.PauseModifiers:settings.MenuModifiers)==mods){Diag.Write(LogLevel.Warn,"Bindings","rejected","Menu and pause bindings conflict");Notice("Choose a different binding for each action");return;}if(menu){settings.MenuKey=key;settings.MenuModifiers=mods;}else{settings.PauseKey=key;settings.PauseModifiers=mods;}Diag.Write(LogLevel.Info,"Bindings","applied",menu?"menu":"pause/resume");UpdateText();}
 public static bool Matches(int assigned,int modifiers,int key,int observed){return assigned!=0&&assigned==key&&modifiers==observed;}
 void OnBinding(int key,int mods,Point point){if(closing||BindingEditor.Capturing)return;bool isMouse=key<7||key>=256;if(isMouse&&Visible&&Bounds.Contains(point))return;
  bool menu=Matches(settings.MenuKey,settings.MenuModifiers,key,mods),pause=Matches(settings.PauseKey,settings.PauseModifiers,key,mods);if(!menu&&!pause)return;BeginInvoke(new Action(()=>{if(closing||BindingEditor.Capturing)return;if(menu){if(Visible)HideMenu();else ShowMenu();}else ToggleRunning();}));
 }
 void ToggleRunning(){running=!running;Diag.Write(LogLevel.Info,"Automation",running?"resume.requested":"pause.requested","Gifts and Unlock All retain independent toggles");Sync();UpdateText();}
 void Sync(){settings.Taps=rates.Clamp(settings.Taps,false);settings.EmoteRate=rates.Clamp(settings.EmoteRate,true);taps.Rate=settings.Taps;taps.Enabled=running&&settings.Clicker;if(bridge!=null){bridge.UnlockAll=unlockAll;bridge.InstaGift=settings.InstaGift;bridge.Emoting=settings.Emoting;bridge.EmoteRate=settings.EmoteRate;bridge.Gifts=settings.Gifts;bridge.Exchange=settings.Exchange;bridge.Running=running;}LogConfiguration();}
 void Tick(){if(reopen!=null&&reopen.WaitOne(0))ShowMenu();if(Visible)UpdateText();}
 string Countdown(int seconds){return TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(@"mm\:ss");}
 void UpdateText(){var s=bridge==null?new GameState():bridge.State;string connection=bridge==null?"waiting for game":bridge.Connection;
  if(tokenCounts!=null&&pages[2].Visible&&(s.NormalTokens!=shownNormalTokens||s.EmoteTokens!=shownEmoteTokens)){shownNormalTokens=s.NormalTokens;shownEmoteTokens=s.EmoteTokens;string counts="normal: "+(s.NormalTokens<0?"—":s.NormalTokens.ToString("N0"))+"\nemote: "+(s.EmoteTokens<0?"—":s.EmoteTokens.ToString("N0"));if(tokenCounts.Text!=counts)tokenCounts.Text=counts;}
  string buttonText=running?"pause automation":"resume automation";if(start.Text!=buttonText){start.Text=buttonText;start.Invalidate();}if(emoteStart!=null&&emoteStart.Text!=buttonText){emoteStart.Text=buttonText;emoteStart.Invalidate();}
  bool emoteThrottled=running&&settings.Emoting&&s.EmoteBudget>0&&s.EmoteBudget<settings.EmoteRate-0.01;
  string footerText=connection+" · "+(running?"running":"paused")+(DateTime.UtcNow<noticeUntil?" · "+notice:"")+(!String.IsNullOrEmpty(s.Message)&&s.Message!=connection&&s.Message!=notice?" · "+s.Message:"")+(emoteThrottled?" · emote throttled: "+s.EmoteBudget.ToString("0.0")+"/s (avg "+s.FrameMs.ToString("0.0")+"ms, peak "+s.PeakMs.ToString("0.0")+"ms)":"");if(footer.Text!=footerText)footer.Text=footerText;
 }
 void Notice(string message){notice=message;noticeUntil=DateTime.UtcNow.AddSeconds(6);if(start!=null)UpdateText();}
 void ResetSettings(){Diag.Write(LogLevel.Info,"Settings","reset.requested","Replacing saved preferences with defaults; lifetime/achievements untouched");rates.Request(false);UpdateDanger();running=false;unlockAll=false;settings=new Settings();Sync();Save();UpdateText();Invalidate(true);Notice("settings reset");Diag.Write(LogLevel.Info,"Settings","reset.applied","features off; rates=10; automation paused");}
 void Save(){settings.ExtremeRates=rates.Enabled;if(!inspection)settings.Save(settingsPath);}
 void HideMenu(){Save();Hide();Diag.Write(LogLevel.Info,"Menu","hidden","Automation state unchanged");if(Native.IsWindow(previous))Native.SetForegroundWindow(previous);}
 void ShowMenu(){UpdateText();previous=Native.GetForegroundWindow();var area=Screen.FromHandle(previous).WorkingArea;if(!area.IntersectsWith(Bounds))Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);Show();Activate();Native.SetForegroundWindow(Handle);Diag.Write(LogLevel.Info,"Menu","shown","Automation state unchanged");}
 void Shutdown(){if(closing)return;Diag.Write(LogLevel.Info,"Session","shutdown.requested","Stopping automation, hooks and bridge");closing=true;running=false;timer.Stop();taps.Enabled=false;if(keys!=null)keys.Dispose();if(bridge!=null)bridge.Dispose();taps.Dispose();try{Save();}catch(Exception e){Diag.Fault("Settings","shutdown.save.failed",e);}if(reopen!=null)reopen.Dispose();}
 void CopyDiagnostic(string text){try{Clipboard.SetText(text);Notice("copied");}catch(Exception e){Diag.Fault("Diagnostics","clipboard.failed",e);Notice("Could not copy diagnostics");}}
 int lastFlags=-1,lastTaps=-1,lastEmoteRate=-1;
 void LogConfiguration(){if(inspection)return;int flags=(settings.Clicker?1:0)|(settings.Emoting?2:0)|(settings.Gifts?4:0)|(settings.Exchange?8:0)|(settings.InstaGift?16:0)|(unlockAll?32:0)|(settings.ExtremeRates?64:0)|(running?128:0);if(flags!=lastFlags){string[] names={"Clicks","Emotes","AutoCollect","Exchange","InstaGift","UnlockAll","ExtremeRates","Automation"};for(int i=0;i<names.Length;i++)if(lastFlags<0||((lastFlags^flags)&(1<<i))!=0)Diag.Write(LogLevel.Info,names[i],(flags&(1<<i))!=0?"configured.on":"configured.off","source=menu; game application recorded separately");lastFlags=flags;}if(lastTaps!=settings.Taps||lastEmoteRate!=settings.EmoteRate){Diag.Values(LogLevel.Info,"Rates","validated","clickBefore={0} clickAfter={1} emoteBefore={2} emoteAfter={3}",lastTaps,settings.Taps,lastEmoteRate,settings.EmoteRate);lastTaps=settings.Taps;lastEmoteRate=settings.EmoteRate;}}
 public void Render(string directory){Directory.CreateDirectory(directory);Opacity=0;Show();for(int i=0;i<4;i++){SelectPage(i);Application.DoEvents();using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,ClientRectangle);b.Save(Path.Combine(directory,new[]{"auto-clicker.png","automation.png","exploits.png","misc.png"}[i]));}}SelectPage(3);RequestDanger(true);Application.DoEvents();using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,ClientRectangle);b.Save(Path.Combine(directory,"misc-confirmation.png"));}ConfirmDanger();Application.DoEvents();using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,ClientRectangle);b.Save(Path.Combine(directory,"misc-enabled.png"));}Hide();}
 public void Verify(string path){if(ShowInTaskbar||FormBorderStyle!=FormBorderStyle.None||!TopMost||pages.Length!=4)throw new Exception("Window style");settings.Clicker=settings.Gifts=settings.Exchange=settings.Emoting=settings.InstaGift=true;ToggleRunning();if(!taps.Enabled)throw new Exception("Shared-feature blocker");Show();Sync();if(!taps.Enabled)throw new Exception("Open menu pauses clicks");Hide();ToggleRunning();if(taps.Enabled)throw new Exception("Pause failed");ResetSettings();if(settings.Taps!=10||settings.MenuKey!=45||settings.PauseKey!=36||settings.Clicker||settings.Gifts||settings.Exchange||settings.Emoting||settings.InstaGift||settings.EmoteRate!=10||running||taps.Enabled)throw new Exception("Reset defaults");RequestDanger(true);if(rates.Enabled||!rates.Pending)throw new Exception("Danger confirmation");ConfirmDanger();settings.Taps=1000;settings.EmoteRate=1000;Sync();if(taps.Rate!=1000||settings.EmoteRate!=1000||!settings.ExtremeRates)throw new Exception("Custom rate");RequestDanger(false);if(taps.Rate!=200||settings.Taps!=200||settings.EmoteRate!=100||settings.ExtremeRates||rates.Enabled)throw new Exception("Immediate clamp");ResetSettings();File.WriteAllText(path,"PASS: inline danger confirmation and independent 200 click / 100 emote clamps\nPASS: reset restores clean defaults and pauses automation\nPASS: borderless four-section menu without taskbar entry\nPASS: all feature toggles coexist\nPASS: taps remain enabled with menu open\nPASS: pause and resume\n");}
}
static class EntryPoint {
 [STAThread]static void Main(string[] args){Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  if(args.Length>=2&&args[0]=="--self-test"){Tests.Run(args[1]);return;}if(args.Length>=2&&(args[0]=="--render"||args[0]=="--ui-test")){using(var form=new MainForm(true)){if(args[0]=="--render")form.Render(args[1]);else form.Verify(args[1]);}return;}
  bool created;using(var mutex=new Mutex(true,"Local\\autocat.xyz-single-instance",out created)){if(!created){try{using(var signal=EventWaitHandle.OpenExisting("Local\\autocat.xyz-show-menu"))signal.Set();}catch(WaitHandleCannotBeOpenedException){MessageBox.Show("Unload the older autocat version before opening this update.");}return;}DiagnosticSession.Start(args);try{Application.Run(new MainForm());}catch(Exception e){Diag.Fault("Session","startup_or_loop.failed",e);throw;}finally{DiagnosticSession.Stop();}}
 }
}









