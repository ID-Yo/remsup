using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
internal static class CurrentStoreTests {
 public static void Run(){
  var root=Path.Combine(Path.GetTempPath(),"RemsupCurrentStore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   var legacy=Path.Combine(root,"devices.json");var fresh=Path.Combine(root,"devices-v2.json");
   File.WriteAllText(legacy,"{\"version\":1,\"revision\":0,\"records\":[]}");var before=File.ReadAllText(legacy);
   var selected=SelectPath(root);var store=new DeviceStore(selected);var data=store.Load();
   Check(selected==fresh&&data.Records.Count==0,"v1 does not block fresh current store");
   data.Upsert(new DeviceRecord{DisplayName="New current profile",DeviceId="123456789",UseDefaultRelay=true,SavePermanentPassword=true,PermanentPassword="synthetic-current"});store.Save(data);var currentData=data;var validCurrent=File.ReadAllText(fresh);
   Check(store.Load().Records.Count==1&&File.ReadAllText(legacy)==before,"Add/Save/reopen preserves legacy file");
   Directory.CreateDirectory(Path.Combine(root,"existing"));var existing=Path.Combine(root,"existing","devices.json");var oldCurrent=new DeviceStore(existing);data=oldCurrent.Load();data.Upsert(new DeviceRecord{DisplayName="Earlier v2 profile",DeviceId="987654321",UseDefaultRelay=true,SavePermanentPassword=true,PermanentPassword="synthetic-current"});oldCurrent.Save(data);before=File.ReadAllText(existing);
   Check(SelectPath(Path.GetDirectoryName(existing))==existing,"earlier valid v2 filename remains current");Check(new DeviceStore(SelectPath(Path.GetDirectoryName(existing))).Load().Records[0].SavePermanentPassword,"earlier v2 credentials retained");Check(File.ReadAllText(existing)==before,"earlier current v2 file not rewritten");
   var unknown=validCurrent.Replace("\"relayMode\":\"Default\"","\"relayMode\":\"Unknown\"");File.WriteAllText(fresh,unknown);Fail(()=>store.Load(),"mode");Fail(()=>store.Save(currentData),"mode");Check(File.ReadAllText(fresh)==unknown,"unknown current mode preserved");
   var unreadable=System.Text.RegularExpressions.Regex.Replace(validCurrent,"\"protectedPassword\":\"[^\"]+\"","\"protectedPassword\":\"AAAA\"");Check(unreadable!=validCurrent,"credential corruption fixture");File.WriteAllText(fresh,unreadable);Fail(()=>store.Load(),"credential");Fail(()=>store.Save(currentData),"credential");Check(File.ReadAllText(fresh)==unreadable,"unreadable current credential preserved");
   File.WriteAllText(fresh,"not-json");Fail(()=>new DeviceStore(SelectPath(root)).Load(),"corrupt");Fail(()=>store.Save(currentData),"corrupt");Check(File.ReadAllText(fresh)=="not-json"&&File.ReadAllText(legacy).Contains("\"version\":1"),"corrupt current store preserved");
   using(var broken=new DesktopForm(store,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="222333444"}),()=>RelayConfiguration.Parse(new string[0]),false)){
    var message=Field<Label>(broken,"emptyState").Text;Check(message.Contains("devices-v2.json")&&message.Contains("corrupt"),"GUI displays exact current-store cause");
    Check(!FindButton(broken,"Add device").Enabled&&!message.Contains("Restore a valid"),"Add is disabled for corrupt current store with its exact cause");
   }
   File.WriteAllText(fresh,"");Fail(()=>new DeviceStore(SelectPath(root)).Load(),"empty");Check(File.ReadAllText(fresh)=="","empty current file is not missing");
   File.WriteAllText(existing,"not-json");Check(SelectPath(Path.GetDirectoryName(existing))==existing,"ambiguous earlier candidate preserved as error, not reset");Fail(()=>new DeviceStore(SelectPath(Path.GetDirectoryName(existing))).Load(),"corrupt");
   var emptyDirectory=Path.Combine(root,"fresh-install");var newPath=SelectPath(emptyDirectory);Check(newPath.EndsWith("devices-v2.json")&&new DeviceStore(newPath).Load().Records.Count==0&&!File.Exists(newPath),"missing current file is an empty valid snapshot");
   CheckGui(root);Console.WriteLine("PASS versioned current store, legacy-v1 independence, earlier-v2 preservation and actual GUI Add/Save/reopen");
  }finally{Directory.Delete(root,true);}
 }
 static void CheckGui(string root){
  string evidence=Environment.GetEnvironmentVariable("REMSUP_LIVE_UI_EVIDENCE");bool wasDark=Appearance.Dark;
  try{foreach(bool dark in new[]{false,true})foreach(bool legacy in new[]{false,true}){
   Appearance.Dark=dark;var folder=Path.Combine(root,"gui-"+dark+"-"+legacy);Directory.CreateDirectory(folder);var old=Path.Combine(folder,"devices.json");
   if(legacy)File.WriteAllText(old,"{\"version\":1,\"revision\":0,\"records\":[]}");
   var store=new DeviceStore(SelectPath(folder));var relay=RelayConfiguration.Parse(new string[0]);
   using(var form=new DesktopForm(store,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="222333444"}),()=>relay,false))using(var timer=new Timer{Interval=string.IsNullOrEmpty(evidence)?100:500}){
    int step=0;Exception failure=null;var watch=System.Diagnostics.Stopwatch.StartNew();
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(8,8);form.TopMost=!string.IsNullOrEmpty(evidence);
    form.Shown+=(s,e)=>timer.Start();timer.Tick+=(s,e)=>{try{
     Check(watch.ElapsedMilliseconds<10000,"bounded Add dialog test");
     if(step==0){Check(Field<DeviceStoreSnapshot>(form,"data")!=null,"GUI has valid empty current store");step=1;FindButton(form,"Add device").PerformClick();}
     else if(step==1){var editor=Application.OpenForms.OfType<DeviceEditorForm>().FirstOrDefault();if(editor==null)return;
      Field<TextBox>(editor,"name").Text="New review workstation";Field<TextBox>(editor,"id").Text="123456789";
      Check(Field<Label>(editor,"defaultEndpoint").Text.Contains("relay.example.invalid:443"),"actual Add dialog shows configured default");
      if(!string.IsNullOrEmpty(evidence)){Directory.CreateDirectory(evidence);FriendlyUiTests.Capture(editor,Path.Combine(evidence,"current-store-"+(legacy?"legacy":"fresh")+"-"+(dark?"dark":"light")+"-editor.png"));}
      step=2;FindButton(editor,"Save").PerformClick();
     }else{
      var rows=Field<ListView>(form,"devices");Check(rows.Items.Count==1&&((DeviceRecord)rows.Items[0].Tag).UseDefaultRelay&&DeviceEndpoint.Resolve((DeviceRecord)rows.Items[0].Tag,()=>relay).Display=="relay.example.invalid:443","native saved card has effective default");
      var saved=store.Load().Records;Check(saved.Count==1&&saved[0].UseDefaultRelay&&saved[0].Host==""&&saved[0].Port==0&&!saved[0].SavePermanentPassword,"GUI save/reopen retains mode without endpoint/OTP snapshot");
      if(!string.IsNullOrEmpty(evidence))FriendlyUiTests.Capture(form,Path.Combine(evidence,"current-store-"+(legacy?"legacy":"fresh")+"-"+(dark?"dark":"light")+"-saved.png"));
      if(legacy)Check(File.ReadAllText(old)=="{\"version\":1,\"revision\":0,\"records\":[]}","GUI leaves old v1 untouched");timer.Stop();form.Close();
     }
    }catch(Exception ex){failure=ex;timer.Stop();foreach(Form dialog in Application.OpenForms.Cast<Form>().Where(item=>item.Owner==form).ToArray())dialog.Close();form.Close();}};
    Application.Run(form);if(failure!=null)throw failure;
   }
  }}finally{Appearance.Dark=wasDark;}
 }
 static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
 static Button FindButton(Control root,string text){if(root is Button&&root.Text==text)return (Button)root;foreach(Control c in root.Controls){var found=FindButton(c,text);if(found!=null)return found;}return null;}
 static string SelectPath(string root){return DeviceStore.ResolveStorePath(root);}
 static void Check(bool value,string message){if(!value)throw new Exception("CurrentStoreTests: "+message);}
 static void Fail(Action action,string expected){try{action();}catch(Exception ex){if(expected!=null)Check(ex.Message.IndexOf(expected,StringComparison.OrdinalIgnoreCase)>=0,"classified error: "+expected);return;}throw new Exception("Expected current store failure");}
}
