using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
internal static class SessionStateUiTests {
 internal static void Run(DeviceStore store){
  string evidence=Environment.GetEnvironmentVariable("REMSUP_WORKSPACE_UI_EVIDENCE")??Environment.GetEnvironmentVariable("REMSUP_LIVE_UI_EVIDENCE");bool wasDark=Appearance.Dark;
  foreach(bool dark in string.IsNullOrEmpty(evidence)?new[]{false}:new[]{false,true}){
   var global=new RelayConfiguration("first.example.test",8443,RelayTls.DefaultPin);var entered=new ManualResetEventSlim();var checking=new ManualResetEventSlim();int blockedCalls=0;Exception error=null;
   using(var form=new DesktopForm(store,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="111222333"}),()=>global,false,(endpoint,device,password,command,seconds,bytes,cancel)=>{
    if(command=="block"){Interlocked.Increment(ref blockedCalls);entered.Set();cancel.WaitHandle.WaitOne(15000);cancel.ThrowIfCancellationRequested();}
    if(device.DeviceId=="111444777"){checking.Set();cancel.WaitHandle.WaitOne(15000);cancel.ThrowIfCancellationRequested();throw new IOException("Synthetic identity check unavailable");}
    if(device.DeviceId=="111444778")throw new IOException("Synthetic target check failed before a tab was created");
    if(command=="unknown"||command=="timeout")throw new IOException(command=="timeout"?"Timeout; dispatched command result unknown.":"Connection ended after dispatch; result unknown.");
    return new CommandResult{ExitCode=command=="nonzero"?3:0,Stdout="DEMO-PC\\demo\r\n"+command+"\r\n"+endpoint.Display,Stderr=command=="nonzero"?"Synthetic command failure":"",DurationMs=42,Truncated=command=="truncate"};
   },_=>true)){
    Appearance.Dark=dark;Appearance.Apply(form);form.StartPosition=FormStartPosition.Manual;form.Location=new Point(8,8);form.TopMost=!string.IsNullOrEmpty(evidence);
    form.Shown+=async(s,e)=>{try{
     var pending=Connect(form,Target("Pending check","111444777"));var wait=System.Diagnostics.Stopwatch.StartNew();while(!checking.IsSet&&wait.ElapsedMilliseconds<3000)await Task.Delay(10);Check(checking.IsSet&&form.Workspace.Sessions.Count==0&&Field<Button>(form,"cancelCheck").Enabled,"Home exposes cancellable check without a fake ready tab");Snap(form,evidence,"home-checking",dark);form.Workspace.CancelChecks();await pending;Check(form.Workspace.Sessions.Count==0,"cancelled check does not create a tab");await Connect(form,Target("Failed check","111444778"));Check(form.Workspace.Sessions.Count==0&&Field<Label>(form,"connectionStatus").Text.Contains("failed"),"Home failure is visible without a checked tab");Snap(form,evidence,"home-check-failed",dark);
     var a=Target("History workstation","123456789");var b=Target("Background laptop","987654321");await Connect(form,a);await Connect(form,b);var one=form.Workspace.Sessions[0];var two=form.Workspace.Sessions[1];var tabs=Field<TabControl>(form,"tabs");var panel=PanelFor(tabs,one);tabs.SelectedIndex=1;
     global=new RelayConfiguration("next.example.test",9443,RelayTls.DefaultPin);await form.Workspace.RunAsync(one,"echo next endpoint",30,1024);panel.ViewLatest();Check(panel.output.Height>=50,"output remains visible beside labelled history at working-area height");Check(one.History[0].Endpoint.Display=="first.example.test:8443"&&one.History[1].Endpoint.Display=="next.example.test:9443","each historical entry retains its actual immutable attempted endpoint");
     panel.command.Text="deliberate unsent draft";panel.history.SelectedIndex=0;Check(panel.viewing.Text.Contains("first.example.test:8443")&&panel.output.Text.Contains("first.example.test:8443")&&panel.command.Text=="deliberate unsent draft","older endpoint/command/output labelled without changing draft");Snap(form,evidence,"older-endpoint",dark);
     var operation=form.Workspace.RunAsync(one,"block",30,1024);var watch=System.Diagnostics.Stopwatch.StartNew();while(!entered.IsSet&&watch.ElapsedMilliseconds<3000)await Task.Delay(10);Check(entered.IsSet&&panel.cancel.Enabled&&panel.history.SelectedIndex==0&&panel.state.Text.Contains("Running"),"historical selection and current operation/cancel are independent");Check(tabs.TabPages[1].Text.Contains("Running"),"native caption shows actual running device");Snap(form,evidence,"older-history-running",dark);
     tabs.SelectedIndex=2;Snap(form,evidence,"background-running",dark);Field<RadioButton>(form,"settingsNav").PerformClick();Check(Field<Button>(form,"more").Text.Contains("running"),"Settings retains discoverable background operation indicator");Snap(form,evidence,"settings-background-running",dark);Field<RadioButton>(form,"homeNav").Checked=true;tabs.SelectedIndex=2;using(var menu=form.CreateSessionMenu()){Check(menu.Items[0].Text.Contains("Running")&&((ToolStripMenuItem)menu.Items[1]).Checked&&menu.Items[0].AccessibleName.Contains("123456789"),"More announces states and selected session");}
     for(int i=2;i<9;i++)await Connect(form,Target("Synthetic device "+i,(222333440+i).ToString()));Check(tabs.TabPages.Cast<TabPage>().Sum(page=>tabs.GetTabRect(tabs.TabPages.IndexOf(page)).Width)>tabs.ClientSize.Width,"real native tab overflow fixture");using(var menu=form.CreateSessionMenu()){menu.Show(form,new Point(420,105));await Task.Delay(200);Snap(form,evidence,"overflow-more-running",dark);menu.Close();}
     tabs.SelectedIndex=1;form.Workspace.Cancel(one);await operation;panel.ViewLatest();Check(blockedCalls==1&&one.Result==null&&one.NavigationState=="Failed"&&tabs.TabPages[1].Text.Contains("Failed"),"cancel records unknown once and failed caption without retry");Snap(form,evidence,"cancel-unknown",dark);
     foreach(string command in new[]{"nonzero","truncate","unknown","timeout"}){await form.Workspace.RunAsync(one,command,30,1024);panel.ViewLatest();if(command=="nonzero")Check(panel.metadata.Text.Contains("Exit: 3")&&one.NavigationState=="Failed","actual nonzero result metadata/state");if(command=="truncate")Check(panel.metadata.Text.Contains("shortened")&&one.NavigationState=="Idle","actual truncation metadata");Snap(form,evidence,command,dark);}
     using(var warning=new ActiveSessionCloseForm("History workstation")){Check(((Button)warning.AcceptButton).Text=="Keep open"&&warning.AcceptButton==warning.CancelButton,"active close defaults Enter/Escape to Keep open");}
     form.Close();
    }catch(Exception ex){error=ex;}finally{form.Close();}};
    Application.Run(form);if(error!=null)throw error;
   }
   entered.Dispose();checking.Dispose();
  }
  Appearance.Dark=wasDark;EditorCaptures(evidence);Console.WriteLine("PASS immutable historical endpoint/selection/current operation/cancel/tab states/More/overflow/unknown/nonzero/truncated");
 }
 static DeviceRecord Target(string name,string id){return new DeviceRecord{DisplayName=name,DeviceId=id,UseDefaultRelay=true,SavePermanentPassword=true,PermanentPassword="synthetic-session"};}
 static Task Connect(DesktopForm form,DeviceRecord target){return (Task)typeof(DesktopForm).GetMethod("Connect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{target});}
 static SessionPanel PanelFor(TabControl tabs,RemoteSession session){return tabs.TabPages.Cast<TabPage>().SelectMany(page=>page.Controls.OfType<SessionPanel>()).First(panel=>panel.Session==session);}
 static void Snap(Form form,string evidence,string name,bool dark){if(string.IsNullOrEmpty(evidence))return;Directory.CreateDirectory(evidence);FriendlyUiTests.Capture(form,Path.Combine(evidence,"state-"+name+"-"+(dark?"dark":"light")+".png"));}
 static void EditorCaptures(string evidence){if(string.IsNullOrEmpty(evidence))return;bool wasDark=Appearance.Dark;
  foreach(bool dark in new[]{false,true})foreach(string view in new[]{"collapsed","custom","lan","expanded","quick"}){
   var record=new DeviceRecord{DisplayName="Optional workstation",DeviceId="123456789",UseDefaultRelay=true,SavePermanentPassword=view=="expanded"||view=="quick",PermanentPassword="synthetic-session",Note=view=="expanded"?"Synthetic local note":""};
   using(var form=new DeviceEditorForm(record,view=="quick",()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin)))using(var timer=new System.Windows.Forms.Timer{Interval=350}){
    if(view=="custom"){Field<CheckBox>(form,"custom").Checked=true;Field<TextBox>(form,"host").Text="custom.example.test";Field<NumericUpDown>(form,"port").Value=10443;}
    if(view=="lan"){Field<ComboBox>(form,"transport").SelectedIndex=1;Field<TextBox>(form,"host").Text="192.168.1.25";Field<NumericUpDown>(form,"port").Value=8444;}
    Appearance.Dark=dark;Appearance.Apply(form);form.StartPosition=FormStartPosition.Manual;form.Location=new Point(8,8);form.TopMost=true;Exception error=null;
    form.Shown+=(s,e)=>timer.Start();timer.Tick+=(s,e)=>{try{if(view=="expanded"){Field<TextBox>(form,"note").Focus();Check(Field<CheckBox>(form,"optionalToggle").Checked,"existing optional fields auto-expand");}else if(view=="quick")Check(!Field<CheckBox>(form,"save").Checked&&Field<TextBox>(form,"password").Text=="","Quick discards saved credential context");if(view=="custom"||view=="lan"){form.Size=form.MinimumSize;Field<TextBox>(form,"host").Focus();}Snap(form,evidence,"editor-"+view+"-focus",dark);}catch(Exception ex){error=ex;}finally{timer.Stop();form.Close();}};Application.Run(form);if(error!=null)throw error;
   }
  }
  foreach(bool dark in new[]{false,true})using(var form=new RemsupClientForm(false,new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="111222333"})))using(var timer=new System.Windows.Forms.Timer{Interval=350}){Appearance.Dark=dark;Appearance.Apply(form);form.Size=form.MinimumSize;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(8,8);form.TopMost=true;Exception error=null;form.Shown+=(s,e)=>timer.Start();timer.Tick+=(s,e)=>{try{var button=AllControls(form).OfType<Button>().First(b=>b.Text=="Settings");button.Focus();Check(button.Focused&&button.Visible,"compact portable Settings receives keyboard focus");Snap(form,evidence,"portable-compact-focus",dark);}catch(Exception ex){error=ex;}finally{timer.Stop();form.Close();}};Application.Run(form);if(error!=null)throw error;}
  Appearance.Dark=wasDark;
 }
 static System.Collections.Generic.IEnumerable<Control> AllControls(Control root){foreach(Control c in root.Controls){yield return c;foreach(var child in AllControls(c))yield return child;}}
 static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
 static void Check(bool ok,string text){if(!ok)throw new Exception("SessionStateUiTests: "+text);}
}
