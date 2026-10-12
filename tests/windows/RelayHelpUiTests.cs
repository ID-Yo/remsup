using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
internal static class RelayHelpUiTests {
 public static void Run(){
  var configured=new RelayConfiguration("global.example.test",9443,RelayTls.DefaultPin);
  using(var editor=new DeviceEditorForm(null,false,()=>configured)){
   editor.TopLevel=false;editor.Show();Application.DoEvents();
   var custom=Field<CheckBox>(editor,"custom");var host=Field<TextBox>(editor,"host");var port=Field<NumericUpDown>(editor,"port");var transport=Field<ComboBox>(editor,"transport");
   Check(!custom.Checked&&!host.Enabled&&Field<Label>(editor,"defaultEndpoint").Text.Contains("global.example.test:9443"),"new Relay UI is Default with effective read-only endpoint");
   custom.Checked=true;host.Text="custom.example.test";port.Value=9445;custom.Checked=false;custom.Checked=true;Check(host.Text=="custom.example.test"&&port.Value==9445,"custom toggle preserves typed values");
   transport.SelectedIndex=1;Application.DoEvents();Check(!custom.Visible&&host.Enabled&&host.AccessibleName.Contains("Target LAN"),"LAN hides override and describes target");
   editor.Size=editor.MinimumSize;Check(FindButton(editor,"Save").Visible,"minimum editor keeps Save footer visible");Check(!string.IsNullOrEmpty(host.AccessibleDescription),"critical endpoint has accessible inline help");
   Field<TextBox>(editor,"password").Text="synthetic-help-secret";CheckHelp(editor);
   Check(editor.AcceptButton==FindButton(editor,"Save")&&editor.CancelButton==FindButton(editor,"Cancel"),"keyboard Enter/Escape actions");
   transport.SelectedIndex=0;custom.Checked=false;Field<TextBox>(editor,"name").Text="Synthetic";Field<TextBox>(editor,"id").Text="123456789";
   FindButton(editor,"Save").PerformClick();Check(editor.Record!=null&&editor.Record.UseDefaultRelay&&editor.Record.Host==""&&editor.Record.Port==0,"editor saves Default without hidden override");
  }
  var prior=new DeviceRecord{DisplayName="Custom",DeviceId="123456789",Host="custom.example.test",Port=8443};
  using(var editor=new DeviceEditorForm(prior,false,()=>configured)){Field<ComboBox>(editor,"transport").SelectedIndex=1;Check(Field<NumericUpDown>(editor,"port").Value==8443,"existing custom port is not overwritten by transport selection");}
  using(var settings=new RemsupSettingsForm(new RemsupSettings{RelayHost=RelayConfiguration.DefaultHost},_=>{throw new IOException();})){
   CheckHelp(settings);
   int changed=0;settings.SettingsSaved+=()=>changed++;settings.TopLevel=false;settings.Show();FindButton(settings,"Save changes").PerformClick();Check(changed==0&&!settings.IsDisposed&&Field<Label>(settings,"saveStatus").Text.Contains("could not"),"failed settings save does not reconfigure/close");
  }
  AttemptEndpoints();CaptureScreens();Console.WriteLine("PASS Default/Custom/LAN UI/help/settings failure and confirmed execution endpoint");
 }
 static void CaptureScreens(){
  string evidence=Environment.GetEnvironmentVariable("REMSUP_LIVE_UI_EVIDENCE");if(string.IsNullOrEmpty(evidence))return;
  Directory.CreateDirectory(evidence);bool wasDark=Appearance.Dark;
  try{foreach(bool dark in new[]{false,true})foreach(string view in new[]{"default","fresh","custom","lan","missing","quick","password","settings-general","settings-security","settings-network","settings-about"}){
   Appearance.Dark=dark;Form form;
  var configured=new RelayConfiguration("relay.example.test",9443,RelayTls.DefaultPin);
   if(view.StartsWith("settings"))form=new RemsupSettingsForm(new RemsupSettings{RelayHost=RelayConfiguration.DefaultHost,RelayPort=RelayConfiguration.DefaultPort});
   else if(view=="password")form=new PasswordForm();
   else {var editor=new DeviceEditorForm(null,view=="quick",()=>view=="fresh"?RelayConfiguration.Parse(new string[0]):view=="missing"?new RelayConfiguration("",0):configured);Field<TextBox>(editor,"name").Text="Demo workstation";Field<TextBox>(editor,"id").Text="123456789";
    if(view=="custom"){Field<CheckBox>(editor,"custom").Checked=true;Field<TextBox>(editor,"host").Text="custom.example.test";Field<NumericUpDown>(editor,"port").Value=10443;}
    if(view=="lan"){Field<ComboBox>(editor,"transport").SelectedIndex=1;Field<TextBox>(editor,"host").Text="192.168.1.25";}
    form=editor;
   }
   using(form)using(var timer=new Timer{Interval=500}){
    form.TopMost=true;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(8,8);Exception failure=null;int step=0;
    form.Shown+=(s,e)=>{if(view.StartsWith("settings"))((RemsupSettingsForm)form).Editor.SelectCategory(view=="settings-general"?0:view=="settings-security"?1:view=="settings-network"?2:3);form.Activate();timer.Start();};
    timer.Tick+=(s,e)=>{try{FriendlyUiTests.Capture(form,Path.Combine(evidence,"editor-"+view+"-"+(dark?"dark":"light")+(step==1?"-small":"")+".png"));if(step++==0&&view!="password"&&!view.StartsWith("settings")){form.Size=form.MinimumSize;form.Refresh();}else{timer.Stop();form.Close();}}catch(Exception ex){failure=ex;timer.Stop();form.Close();}};
    Application.Run(form);if(failure!=null)throw new Exception("Capture "+view+"/"+(dark?"dark":"light")+": "+failure.Message,failure);
   }
  }}finally{Appearance.Dark=wasDark;}
 }
 static T Find<T>(Control root)where T:Control{if(root is T)return (T)root;foreach(Control c in root.Controls){var found=Find<T>(c);if(found!=null)return found;}return null;}
 static void AttemptEndpoints(){
  var root=Path.Combine(Path.GetTempPath(),"RemsupAttempt-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
  var global=new RelayConfiguration("first.example.test",9443,RelayTls.DefaultPin);string preview=null;DeviceEndpoint attempted=null;int calls=0;
   var store=new DeviceStore(Path.Combine(root,"devices.json"));var data=store.Load();data.Upsert(new DeviceRecord{DisplayName="Inherited",DeviceId="111222333",UseDefaultRelay=true,SavePermanentPassword=true,PermanentPassword="synthetic-only"});store.Save(data);
   using(var form=new DesktopForm(store,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="222333444"}),()=>global,false,(endpoint,device,password,text,seconds,bytes,cancel)=>{calls++;attempted=endpoint;return new CommandResult{ExitCode=0,Stdout="DEMO-PC\\demo"};},message=>{preview=message;global=new RelayConfiguration("next.example.test",10443,RelayTls.DefaultPin);return true;})){
   Exception failure=null;form.Shown+=async(s,e)=>{try{CheckHelp(form);await (Task)typeof(DesktopForm).GetMethod("Connect",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{data.Records[0]});
    Check(calls==1&&preview.Contains("first.example.test:9443")&&attempted.Host=="first.example.test"&&attempted.Port==9443,"global change after confirm does not reroute execution");
    await (Task)typeof(DesktopForm).GetMethod("Execute",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{"hostname"});Check(calls==2&&preview.Contains("next.example.test:10443")&&attempted.Port==10443,"next run uses new global endpoint");
    global=new RelayConfiguration("",0);await (Task)typeof(DesktopForm).GetMethod("Execute",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{"hostname"});Check(calls==2&&form.Workspace.Sessions[0].History.Count==2,"missing global fails before command provider or a result is recorded");
   }catch(Exception ex){failure=ex;}finally{form.Close();}};Application.Run(form);if(failure!=null)throw failure;
   }
  }finally{Directory.Delete(root,true);}
 }
 static void Pump(Task task){var watch=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<5000){Application.DoEvents();System.Threading.Thread.Sleep(10);}Check(task.IsCompleted,"async attempt bounded");task.GetAwaiter().GetResult();}
 static void CheckHelp(Control control){
  var tips=(ConditionalWeakTable<Control,ToolTip>)typeof(GuiHelp).GetField("tips",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  if(control is ButtonBase||control is TextBox||control is ComboBox||control is NumericUpDown){
   Check(!string.IsNullOrEmpty(control.AccessibleDescription),"help coverage: "+control.Text);ToolTip tip=null;
   for(Control root=control;root!=null;root=root.Parent)if(tips.TryGetValue(root,out tip))break;
   Check(tip!=null&&!string.IsNullOrEmpty(tip.GetToolTip(control)),"owned tooltip coverage: "+control.AccessibleName);
   Check(!control.AccessibleDescription.Contains("synthetic-help-secret")&&!tip.GetToolTip(control).Contains("synthetic-help-secret"),"credential excluded from help");
  }
  if(!(control is UpDownBase))foreach(Control child in control.Controls)CheckHelp(child);
 }
 static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
 static Button FindButton(Control root,string text){var b=root as Button;if(b!=null&&b.Text==text)return b;foreach(Control c in root.Controls){var found=FindButton(c,text);if(found!=null)return found;}return null;}
 static void Check(bool ok,string message){if(!ok)throw new Exception("RelayHelpUiTests: "+message);}
}
