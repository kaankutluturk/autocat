using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Collections;
using UnityEngine;
using AutoCat.Diagnostics;

namespace AutocatBridge {
 public static class Entry {
  internal static void Note(string text){}
  static bool installed;static object keyHook;static FieldInfo keyEvent;static Action<int> callback;
  public static void Load(){lock(typeof(Entry)){if(installed)return;Assembly game=null;foreach(var a in AppDomain.CurrentDomain.GetAssemblies())if(a.GetName(false).Name=="Assembly-CSharp")game=a;
   if(game==null)throw new Exception("Bongo Cat not initialized");var type=game.GetType("BongoCat.OSSpecific.GlobalKeyHook",true,false);keyHook=type.GetField("Instance",BindingFlags.Public|BindingFlags.Static).GetValue(null);if(keyHook==null)throw new Exception("Input hook not initialized");
   keyEvent=type.GetField("OnKeyPressed",BindingFlags.Public|BindingFlags.Instance);var current=(Delegate)keyEvent.GetValue(keyHook);if(current!=null)foreach(var d in current.GetInvocationList())if(d.Method.DeclaringType.Namespace=="AutocatBridge")current=Delegate.Remove(current,d);
   callback=Install;keyEvent.SetValue(keyHook,Delegate.Combine(current,callback));installed=true;Note("subscribed");}}
  static void Install(int taps){Note("main callback");keyEvent.SetValue(keyHook,Delegate.Remove((Delegate)keyEvent.GetValue(keyHook),callback));try{foreach(var obj in Resources.FindObjectsOfTypeAll(typeof(GameObject))){var previous=(GameObject)obj;if(previous.name=="autocat.xyz.runtime")UnityEngine.Object.Destroy(previous);}var go=new GameObject("autocat.xyz.runtime");UnityEngine.Object.DontDestroyOnLoad(go);go.AddComponent<Worker>();Note("component created");}catch(Exception e){installed=false;Note(e.ToString());}}
  internal static void Removed(){installed=false;}
 }
 public sealed partial class Worker:MonoBehaviour {
  [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
  internal volatile bool running,gifts,exchange,closed,emoting,instaGift;
  volatile string state="WAIT";string problem="";string lastDiagnosticCommand="";Thread thread;NamedPipeServerStream pipe;readonly object gate=new object();
  long lastContact=DateTime.UtcNow.Ticks;float nextRead,nextGift,nextExchange;double nextEmote;float lastFrame,windowTime,windowWorst,frameAverage,framePeak,tapScale=1,emoteBudget=20;int windowFrames,slowWindows;volatile int emoteRate=1;int claims,trades,emoteCalls,emoteTotal=-1;bool ownSlots;object[] ownedItems;long[] ownedAmounts;int nextGroup;
  NativeUnlock nativeUnlock;volatile bool wantUnlock;bool unlockFailed;float nextNative;CosmeticPreview preview;volatile bool catalogRequested;volatile string previewCatalog="CATALOG|";int previewSelection=(-2147483648);object pendingGift;long pendingTokens;bool pendingEmote,nextGiftEmote;float giftSubmitted;long normalTokens,emoteTokens;
  Assembly game;Type petsType,shopType,exchangeType,donutType,statsType;
  readonly DictionaryCache cache=new DictionaryCache();
  void Awake(){
   DiagnosticStartup();
   Entry.Note("Awake");
   try{foreach(var a in AppDomain.CurrentDomain.GetAssemblies())if(a.GetName(false).Name=="Assembly-CSharp")game=a;if(game==null)throw new Exception("Game assembly unavailable");
    donutType=game.GetType("EmoteDonut",true,false);statsType=game.GetType("SteamTools.Game+Stats",true,false);petsType=game.GetType("BongoCat.Pets",true,false);shopType=game.GetType("BongoCat.Shop",true,false);exchangeType=game.GetType("BongoCat.ItemExchange",true,false);
    thread=new Thread(Server);thread.IsBackground=true;thread.Start();
   }catch(Exception e){problem=e.Message;diagnostics.Fault("Runtime","initialization.failed",e);Entry.Note(e.ToString());}
  }
  object Field(object obj,string name){return cache.Field(obj.GetType(),name).GetValue(obj);}
  object Static(Type type,string name){return cache.Field(type,name).GetValue(null);}
  static readonly object[] NoArgs=new object[0];
  object Call(object obj,string name){return cache.Method(obj.GetType(),name,0).Invoke(obj,NoArgs);}
  object Call(object obj,string name,params object[] args){return cache.Method(obj.GetType(),name,args.Length).Invoke(obj,args);}
  int Number(object obj,string name){return Convert.ToInt32(Field(obj,name));}
  bool Flag(object obj,string name){return (bool)Field(obj,name);}
  void Update(){
   if(closed){Destroy(gameObject);return;}
   if(!running||!emoting)emoteBudget=emoteRate;if(!running){tapScale=1;slowWindows=0;}
   float nowFrame=Time.realtimeSinceStartup;
   if(lastFrame>0){float dt=nowFrame-lastFrame;windowTime+=dt;windowFrames++;if(dt>windowWorst)windowWorst=dt;}
   lastFrame=nowFrame;
   if(windowTime>=0.5f){frameAverage=windowTime/Math.Max(1,windowFrames);framePeak=windowWorst;
    // Bug A's actual symptom (repeated exception/catch overhead) inflated frameAverage far past these numbers. A 17.5ms/17.2ms degrade/recover band is within ordinary frame-time jitter on a healthy machine and ratchets emoteBudget to the floor under normal play (observed: avg 17.6ms/peak 19.5ms -> 0.3/s). Widened so only genuinely bad frame pacing triggers throttling, and recovery is no longer near-unreachable.
    if(frameAverage>0.028f)slowWindows++;else slowWindows=0;
    if(slowWindows>=3||frameAverage>0.045f){emoteBudget=Math.Max(0.25f,emoteBudget*0.65f);tapScale=Math.Max(0.05f,tapScale*0.7f);}
    else if(frameAverage<0.022f&&framePeak<0.04f){if(running&&emoting)emoteBudget=Math.Min(emoteRate,emoteBudget+Math.Max(2,emoteRate*0.1f));tapScale=Math.Min(1,tapScale+0.05f);}
    windowTime=0;windowFrames=0;windowWorst=0;
   }
   if(DateTime.UtcNow.Ticks-Interlocked.Read(ref lastContact)>TimeSpan.FromSeconds(4).Ticks){if(running)diagnostics.Write(LogLevel.Warn,"Connection","watchdog.paused","No contact for 4 seconds");running=false;}
   if(DateTime.UtcNow.Ticks-Interlocked.Read(ref lastContact)>TimeSpan.FromSeconds(30).Ticks){diagnostics.Write(LogLevel.Warn,"Connection","watchdog.closed","No contact for 30 seconds");closed=true;return;}
    try{

     if(problem.StartsWith("Emotes:"))problem="";
     float slice=Time.realtimeSinceStartup;if(!running||!emoting)nextEmote=slice;int burst=0;bool emoteResolved=false;object frameEmote=null;while(running&&emoting&&emoteTotal<2000000000&&Time.realtimeSinceStartup>=nextEmote&&burst++<(emoteRate>200?64:2)&&Time.realtimeSinceStartup-slice<0.001f){
      nextEmote+=1.0/Math.Max(0.25,Math.Min(emoteBudget,emoteRate));
      if(!emoteResolved){emoteResolved=true;var donut=Static(donutType,"Instance");
      if(donut!=null){var entries=(IDictionary)Field(donut,"IDtoEmoteEntry");IList deadKeys=null;foreach(DictionaryEntry pair in entries){var entryComponent=pair.Value as Component;if(entryComponent==null){if(deadKeys==null)deadKeys=new System.Collections.Generic.List<object>();deadKeys.Add(pair.Key);continue;}var item=Field(pair.Value,"_steamItem");if(item!=null&&!(bool)Call(item,"get_IsConsumable")){frameEmote=pair.Value;break;}}
      if(deadKeys!=null){foreach(var key in deadKeys)entries.Remove(key);try{Call(donut,"RearrangeEmotes");}catch(Exception e){diagnostics.Fault("Emotes","wheel.rearrange.failed",e);}EmoteDiagnostic("pruned dead wheel entries="+deadKeys.Count.ToString());}}
      }if(frameEmote==null)problem="Emotes: equip a non-consumable emote";
      else {try{var spawner=(Component)Field(frameEmote,"_emoteSpawner");if(spawner!=null&&spawner.gameObject.activeInHierarchy){Call(frameEmote,"SpawnEmoteParticle");emoteCalls++;}else problem="Emotes: cat is inactive";}catch(Exception spawnEx){frameEmote=null;problem="Emotes: equip a non-consumable emote";EmoteDiagnostic("stale frameEmote at spawn: "+((spawnEx.InnerException??spawnEx).GetType().Name)+" "+((spawnEx.InnerException??spawnEx).Message));}}
     }
     if(nextEmote<Time.realtimeSinceStartup)nextEmote=Time.realtimeSinceStartup;
    }catch(Exception e){var real=e.InnerException??e;problem="Emotes: "+real.Message;diagnostics.Fault("Emotes","update.failed",real);}

   if(Time.realtimeSinceStartup<nextRead)return;nextRead=Time.realtimeSinceStartup+0.1f;
   try{
    var pets=Static(petsType,"Instance");var normal=Static(shopType,"NormalShop");var emote=Static(shopType,"EmoteShop");var ex=Static(exchangeType,"Instance");
    if(pets==null||normal==null||emote==null||ex==null||!Flag(pets,"_init")){state="WAIT";diagnostics.Limited(LogLevel.Warn,"State","not.ready","Required game instances or Pets initialization unavailable");return;}
    bool nReady=(bool)Call(normal,"get_CanGetChest"),eReady=(bool)Call(emote,"get_CanGetChest");
    int gained=Number(pets,"_currentGained"),lifetime=Number(pets,"_currentAchievement"),spent=Number(pets,"_totalSpent");
    normalTokens=Tokens(false);emoteTokens=Tokens(true);
    try{if((catalogRequested||wantUnlock)&&preview==null){string folder=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"autocat-data");Directory.CreateDirectory(folder);string backupValue=PlayerPrefs.GetString("EQUIPPED_ITEMS_3","<absent>");diagnostics.Values(LogLevel.Info,"Persistence","equipmentBackup.requested","source=PlayerPrefs key=EQUIPPED_ITEMS_3 chars={0}; previous backup may be replaced; contents excluded",backupValue.Length);File.WriteAllText(Path.Combine(folder,"cosmetic-equipped-backup.txt"),backupValue);diagnostics.Write(LogLevel.Info,"Persistence","equipmentBackup.written","source=local-backup-file; write returned; no game ownership changed; later persistence not inferred");preview=new CosmeticPreview(game,()=>PlayerPrefs.GetString("EQUIPPED_ITEMS_3","<absent>"),NativeLog,(slot,id)=>{if(nativeUnlock!=null)nativeUnlock.BroadcastSlot(slot,id);});previewCatalog=preview.Catalog;}
    if(!wantUnlock){unlockFailed=false;if(nativeUnlock!=null){nativeUnlock.Disable();nativeUnlock=null;}}else if(!unlockFailed){try{if(nativeUnlock==null)nativeUnlock=new NativeUnlock(game,preview,NativeLog);if(Time.realtimeSinceStartup>=nextNative){nativeUnlock.Tick();if(nativeUnlock.Error!="")problem="Unlock all: "+nativeUnlock.Error;else if(problem.StartsWith("Unlock all:"))problem="";nextNative=Time.realtimeSinceStartup+0.5f;}}catch{unlockFailed=true;throw;}}int choice=Interlocked.Exchange(ref previewSelection,(-2147483648));if(choice!=(-2147483648)&&preview!=null){if(choice<0)preview.Disable();else preview.Select(choice);problem=preview.Active?"Local cosmetic preview":"";}}
    catch(Exception error){diagnostics.Fault("UnlockAll","lifecycle.failed",error);problem="Preview: "+(error.InnerException??error).Message;catalogRequested=false;}
    try{
     if(pendingGift!=null){long tokens=pendingEmote?emoteTokens:normalTokens;if(tokens<pendingTokens){diagnostics.Values(LogLevel.Info,"Gifts","token.consumption.observed","request={0} emote={1} before={2} after={3}; source=Steam-inventory-cache; reward identity unverified",giftRequestId,pendingEmote?1:0,pendingTokens,tokens);pendingGift=null;nextGift=Time.realtimeSinceStartup+2;if(problem=="Gift not confirmed; retry delayed")problem="";}else if(Time.realtimeSinceStartup-giftSubmitted>30){diagnostics.Values(LogLevel.Warn,"Gifts","request.unconfirmed","request={0} retryDelaySeconds=60",giftRequestId);pendingGift=null;nextGift=Time.realtimeSinceStartup+60;problem="Gift not confirmed; retry delayed";}}
     if((gifts||instaGift)&&pendingGift==null&&Time.realtimeSinceStartup>=nextGift      &&!(bool)Static(game.GetType("Steam.ChestExchanger",true,false),"IsExchanging")){
      nextGift=Time.realtimeSinceStartup+2;
      for(int attempt=0;attempt<2;attempt++){bool isEmote=attempt==0?nextGiftEmote:!nextGiftEmote;var shop=isEmote?emote:normal;var item=Field(shop,"_shopItem");long tokens=isEmote?emoteTokens:normalTokens;
       if(Flag(shop,"_openingChest")||Flag(item,"_waitingForServer"))continue;
       if(instaGift&&tokens>0&&Number(shop,"StockRefreshTimeLeft")>0){int oldTimer=Number(shop,"StockRefreshTimeLeft");cache.Field(shop.GetType(),"StockRefreshTimeLeft").SetValue(shop,0);diagnostics.Values(LogLevel.Info,"InstaGift","timer.applied","emote={0} oldSeconds={1} readback={2}; source=process-memory",isEmote?1:0,oldTimer,Number(shop,"StockRefreshTimeLeft"));}
       if(gifts&&tokens>0&&(bool)Call(shop,"get_CanGetChest")){pendingGift=shop;pendingEmote=isEmote;pendingTokens=tokens;giftSubmitted=Time.realtimeSinceStartup;nextGiftEmote=!isEmote;giftRequestId++;diagnostics.Values(LogLevel.Info,"Gifts","request.started","request={0} emote={1} tokensBefore={2}",giftRequestId,isEmote?1:0,tokens);Call(item,"Buy");diagnostics.Values(LogLevel.Info,"Gifts","request.submitted","request={0}; native Buy returned; outcome pending",giftRequestId);claims++;break;}
      }
     }
    }catch(Exception e){diagnostics.Fault("Gifts","request.failed",e);problem="Gifts: "+(e.InnerException??e).Message;}    try{
     if(problem.StartsWith("Manual exchange")&&!(bool)Call(ex,"get_HasItemsSlotted"))problem="";
     if(running&&exchange&&Time.realtimeSinceStartup>=nextExchange&&!Flag(ex,"_isExchanging")){
      nextExchange=Time.realtimeSinceStartup+5;if(problem.StartsWith("Exchange:"))problem="";
      bool has=(bool)Call(ex,"get_HasItemsSlotted");
      if(!has)ownSlots=false;
      if(has&&(!ownSlots||!SameSelection(ex))){ownSlots=false;problem="Manual exchange selection: clear slots to resume";}
      else {
       // Clean up only our unchanged selection after completion or a failed attempt.
       if(has){foreach(var slot in (IEnumerable)Field(ex,"_exchangeSlots"))Call(slot,"SetItem",null,1L);Call(ex,"UpdateInteractable");ownSlots=false;}
       PruneDestroyedInventoryCallbacks();int group=nextGroup;nextGroup=nextGroup==0?1:0;
       var ready=cache.Method(ex.GetType(),"CanFillExchangeWithDuplicates",2);
       object[] arguments={Enum.ToObject(ready.GetParameters()[0].ParameterType,group),null};
       if((bool)ready.Invoke(ex,arguments)){
        Call(ex,"OpenTab",group==0?0:2);
        // Recheck after changing category, before touching slots.
        if((bool)ready.Invoke(ex,arguments)){
         try{Call(ex,"SlotDuplicates");}finally{RememberSelection(ex);}ExchangeLog("slotted="+ownSlots.ToString()+" possible="+Number(ex,"_possibleExchanges").ToString()+" group="+group.ToString());
         Call(ex,"UpdateInteractable");
         if(ownSlots&&Number(ex,"_possibleExchanges")>0&&!Flag(ex,"_isExchanging")){
          StartBackgroundExchange(ex);
          // Keep ownership until the game finishes; do not label leftovers manual.
         }else if(ownSlots&&SameSelection(ex)){
          foreach(var slot in (IEnumerable)Field(ex,"_exchangeSlots"))Call(slot,"SetItem",null,1L);
          Call(ex,"UpdateInteractable");ownSlots=false;
         }
        }
       }
      }
     }
    }catch(Exception e){problem="Exchange: "+(e.InnerException??e).Message;diagnostics.Fault("Exchange","operation.failed",e);}    var stat=Static(statsType,"EmotesUsed");emoteTotal=(int)Call(stat,"IntValue");DiagnosticState(nReady,eReady,lifetime);state=String.Join("|",new[]{"OK",gained.ToString(),lifetime.ToString(),spent.ToString(),nReady?"1":"0",eReady?"1":"0",Number(normal,"StockRefreshTimeLeft").ToString(),Number(emote,"StockRefreshTimeLeft").ToString(),Number(ex,"_possibleExchanges").ToString(),claims.ToString(),trades.ToString(),Convert.ToBase64String(Encoding.UTF8.GetBytes(problem)),emoteTotal.ToString(),emoteCalls.ToString(),tapScale.ToString(System.Globalization.CultureInfo.InvariantCulture),(frameAverage*1000).ToString(System.Globalization.CultureInfo.InvariantCulture),(framePeak*1000).ToString(System.Globalization.CultureInfo.InvariantCulture),emoteBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),normalTokens.ToString(),emoteTokens.ToString()});
   }catch(Exception e){diagnostics.Fault("State","read.failed",e);problem=(e.InnerException??e).Message;state="ERROR|"+Convert.ToBase64String(Encoding.UTF8.GetBytes(problem));running=false;}
  }
  void PruneDestroyedInventoryCallbacks(){var inventory=Static(game.GetType("BongoCat.CatInventory",true,false),"Instance");int removed=0;foreach(var item in (IEnumerable)Field(inventory,"Items")){var field=cache.Field(item.GetType(),"OnItemUpdated");var callbacks=(Delegate)field.GetValue(item);if(callbacks==null)continue;foreach(var callback in callbacks.GetInvocationList()){var target=callback.Target as Component;if(!System.Object.ReferenceEquals(target,null)&&target==null){callbacks=Delegate.Remove(callbacks,callback);removed++;}}field.SetValue(item,callbacks);}if(removed>0)ExchangeLog("removed destroyed inventory callbacks="+removed.ToString());}
  void ExchangeLog(string message){diagnostics.Limited(message.StartsWith("slotted=")||message.StartsWith("removed destroyed")?LogLevel.Info:LogLevel.Error,"Exchange","state",message);}
  void EmoteDiagnostic(string message){diagnostics.Limited(LogLevel.Warn,"Emotes","condition",message);}
  void StartBackgroundExchange(object ex){var method=cache.Method(ex.GetType(),"ExchangeRoutine",2);var items=(IList)Activator.CreateInstance(method.GetParameters()[0].ParameterType);foreach(var slot in (IEnumerable)Field(ex,"_exchangeSlots")){var item=Field(slot,"ItemSlotted");if(item!=null)items.Add(item);}int possible=Number(ex,"_possibleExchanges");if(items.Count==0||possible<=0||Flag(ex,"_isExchanging"))return;var routine=(IEnumerator)method.Invoke(ex,new object[]{items,possible});var go=new GameObject("autocat.exchange.request");UnityEngine.Object.DontDestroyOnLoad(go);var observation=ObserveExchangeStart(routine,items,possible);go.AddComponent<ExchangeRunner>().Begin(routine,()=>ObserveExchangeEnd(observation));}
  long Tokens(bool emote){return (long)Call(Static(game.GetType("SteamTools.Game+Inventory",true,false),emote?"Emote_Chest_Token":"Chest_Token"),"GetTotalQuantity");}
  void RememberSelection(object ex){var slots=(IList)Field(ex,"_exchangeSlots");ownedItems=new object[slots.Count];ownedAmounts=new long[slots.Count];ownSlots=false;for(int i=0;i<slots.Count;i++){ownedItems[i]=Field(slots[i],"ItemSlotted");ownedAmounts[i]=(long)Field(slots[i],"_slottedAmount");if(ownedItems[i]!=null)ownSlots=true;}}
  bool SameSelection(object ex){var slots=(IList)Field(ex,"_exchangeSlots");if(ownedItems==null||slots.Count!=ownedItems.Length)return false;for(int i=0;i<slots.Count;i++)if(!System.Object.ReferenceEquals(ownedItems[i],Field(slots[i],"ItemSlotted"))||ownedAmounts[i]!=(long)Field(slots[i],"_slottedAmount"))return false;return true;}
  void Server(){Entry.Note("Server starting");while(!closed){try{
   using(var p=new NamedPipeServerStream("autocat-control-"+GetCurrentProcessId().ToString(),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)){
    lock(gate){if(closed)return;pipe=p;}p.WaitForConnection();
    using(var reader=new StreamReader(p,Encoding.UTF8,false,1024,true))using(var writer=new StreamWriter(p,new UTF8Encoding(false),1024,true)){writer.AutoFlush=true;
     while(!closed&&p.IsConnected){var line=reader.ReadLine();if(line==null)break;Interlocked.Exchange(ref lastContact,DateTime.UtcNow.Ticks);
      if(line.StartsWith("LOG|")){try{var logArgs=line.Split('|');int level;if(logArgs.Length!=4||!int.TryParse(logArgs[2],out level))throw new ArgumentException("Invalid LOG command");ConfigureDiagnostics(logArgs[1],level,logArgs[3]);writer.WriteLine("LOGOK");}catch(Exception e){diagnostics.Fault("Logger","configuration.failed",e);writer.WriteLine("LOGERROR");}continue;}
      if(line=="CATALOG"){diagnostics.Write(LogLevel.Debug,"UnlockAll","catalog.requested","Existing catalog response");catalogRequested=true;writer.WriteLine(previewCatalog);continue;}if(line.StartsWith("PREVIEW|")){int choice;if(int.TryParse(line.Substring(8),out choice))Interlocked.Exchange(ref previewSelection,choice);writer.WriteLine("QUEUED");continue;}if(line=="UNLOAD"){diagnostics.Write(LogLevel.Info,"Runtime","unload.requested","Control command");running=false;closed=true;writer.WriteLine("BYE");break;}
      bool changedDiagnosticCommand=line!=lastDiagnosticCommand;var values=line.Split('|');if(values.Length==8&&values[0]=="SET"){running=values[1]=="1";gifts=values[2]=="1";exchange=values[3]=="1";emoting=values[4]=="1";instaGift=values[6]=="1";wantUnlock=values[7]=="1";int parsed;if(int.TryParse(values[5],out parsed))emoteRate=Math.Max(1,Math.Min(100000000,parsed));if(changedDiagnosticCommand){diagnostics.Values(LogLevel.Info,"Configuration","applied","running={0} featureFlags={1} emoteRate={2}; flags: collect=1 exchange=2 emote=4 insta=8 unlock=16",running?1:0,(gifts?1:0)|(exchange?2:0)|(emoting?4:0)|(instaGift?8:0)|(wantUnlock?16:0),emoteRate);lastDiagnosticCommand=line;}}
      writer.WriteLine(state);
     }
    }
   }
  }catch(Exception e){if(!closed){diagnostics.Fault("Connection","server.failed",e);problem="Connection: "+e.Message;Entry.Note(e.ToString());}}finally{lock(gate){pipe=null;}running=false;}if(!closed)Thread.Sleep(250);}}
  void LateUpdate(){if(nativeUnlock!=null&&wantUnlock&&preview!=null){try{nativeUnlock.Advance();preview.Maintain();}catch(Exception e){diagnostics.Fault("UnlockAll","maintenance.failed",e);problem="Unlock all: "+(e.InnerException??e).Message;unlockFailed=true;if(nativeUnlock!=null){nativeUnlock.Disable();nativeUnlock=null;}}}}
  void OnDestroy(){diagnostics.Write(LogLevel.Info,"Runtime","cleanup.started","Restoring temporary state");bool clean=true;try{if(nativeUnlock!=null)nativeUnlock.Disable();diagnostics.Write(LogLevel.Info,"Runtime","cleanup.native","Native temporary state cleanup returned");}catch(Exception e){clean=false;diagnostics.Fault("Runtime","cleanup.native.failed",e);}finally{nativeUnlock=null;}try{if(preview!=null)preview.Disable();diagnostics.Write(LogLevel.Info,"Runtime","cleanup.renderer","Renderer cleanup returned");}catch(Exception e){clean=false;diagnostics.Fault("Runtime","cleanup.renderer.failed",e);}finally{preview=null;}FlushRepairDiagnostics(true);foreach(var o in exchangeObservations)diagnostics.Values(LogLevel.Warn,"Exchange","shutdown.unverified","request={0}; outcome still pending",o.Id);running=false;closed=true;try{lock(gate){if(pipe!=null)pipe.Dispose();}}catch(Exception e){clean=false;diagnostics.Fault("Runtime","cleanup.pipe.failed",e);}diagnostics.Write(clean?LogLevel.Info:LogLevel.Warn,"Runtime",clean?"cleanup.restored":"cleanup.partial",clean?"All cleanup units returned":"One or more cleanup units failed; remaining units were still attempted");diagnostics.Write(LogLevel.Info,"Runtime","stopped","Worker destroyed; queued diagnostics drain asynchronously");diagnostics.Dispose();Entry.Removed();}
 }
 public sealed class ExchangeRunner:MonoBehaviour {
  public void Begin(IEnumerator routine,Action ended){StartCoroutine(Finish(routine,ended));}
  IEnumerator Finish(IEnumerator routine,Action ended){try{yield return routine;}finally{try{ended();}catch{}UnityEngine.Object.Destroy(gameObject);}}
 }
 sealed class DictionaryCache {
  readonly System.Collections.Generic.Dictionary<Type,System.Collections.Generic.Dictionary<string,FieldInfo>> fields=new System.Collections.Generic.Dictionary<Type,System.Collections.Generic.Dictionary<string,FieldInfo>>();
  readonly System.Collections.Generic.Dictionary<Type,System.Collections.Generic.Dictionary<string,MethodInfo[]>> methods=new System.Collections.Generic.Dictionary<Type,System.Collections.Generic.Dictionary<string,MethodInfo[]>>();
  public FieldInfo Field(Type type,string name){System.Collections.Generic.Dictionary<string,FieldInfo> entries;if(!fields.TryGetValue(type,out entries)){entries=new System.Collections.Generic.Dictionary<string,FieldInfo>();fields[type]=entries;}FieldInfo f;if(entries.TryGetValue(name,out f))return f;f=type.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);if(f==null)throw new MissingFieldException(name);entries[name]=f;return f;}
  public MethodInfo Method(Type type,string name,int count){System.Collections.Generic.Dictionary<string,MethodInfo[]> entries;if(!methods.TryGetValue(type,out entries)){entries=new System.Collections.Generic.Dictionary<string,MethodInfo[]>();methods[type]=entries;}MethodInfo[] overloads;if(!entries.TryGetValue(name,out overloads)){overloads=new MethodInfo[8];entries[name]=overloads;}if(count>=overloads.Length)throw new MissingMethodException(name);if(overloads[count]!=null)return overloads[count];foreach(var m in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static))if(m.Name==name&&m.GetParameters().Length==count){overloads[count]=m;return m;}throw new MissingMethodException(name);}
 }
}





